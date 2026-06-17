using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FineClipboard.Interop;
using FineClipboard.Models;
using FineClipboard.Services;
using FineClipboard.Views;

namespace FineClipboard;

public partial class App : Application
{
    // Hotkey ids: popup history + paste-recent-as-plain-text + screenshot.
    private const int HotkeyPopup = 1;
    private const int HotkeyPlain = 2;
    private const int HotkeyShot = 4;
    private const uint VkV = 0x56;
    private const uint VkB = 0x42;
    private const uint VkA = 0x41;

    private static readonly HotkeyCombo DefaultPopup = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkV);
    private static readonly HotkeyCombo DefaultPlain = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkB);
    private static readonly HotkeyCombo DefaultShot = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkA);

    private Mutex? _singleInstance;

    private HistoryStore _store = null!;
    private NativeMessageWindow _msg = null!;
    private ClipboardMonitor _monitor = null!;
    private PasteService _paste = null!;
    private HotkeyManager _hotkeys = null!;
    private PasswordVault _vault = null!;
    private SyncEngine _sync = null!;
    private System.Windows.Threading.DispatcherTimer? _syncTimer;
    private bool _syncing;
    private SyncWindow? _syncWindow;

    internal PasswordVault Vault => _vault;

    private System.Windows.Forms.NotifyIcon _tray = null!;
    private System.Windows.Forms.ToolStripMenuItem _updateMenuItem = null!;
    private System.Drawing.Icon? _trayIcon;

    private System.Windows.Threading.DispatcherTimer? _purgeTimer;

    private readonly UpdateService _update = new();
    private string? _updateUrl;

    private PopupWindow? _popup;
    private SettingsWindow? _settings;
    private ScreenshotPreviewWindow? _screenshotPreview;
    private IntPtr _lastForeground;
    private bool _pendingScreenshotPreview;
    private DateTime _pendingScreenshotPreviewUntil;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: bail out if another copy is already running.
        _singleInstance = new Mutex(initiallyOwned: true, "FineClipboard.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        _store = new HistoryStore();
        _vault = new PasswordVault(_store);
        ThemeManager.Apply(_store.GetSetting(HistoryStore.ThemeKey));
        PurgeExpiredNow();
        _purgeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _purgeTimer.Tick += (_, _) => PurgeExpiredNow();
        _purgeTimer.Start();

        _msg = new NativeMessageWindow();

        _monitor = new ClipboardMonitor(_msg);
        _monitor.ItemCaptured += OnItemCaptured;
        _monitor.ShouldSkipSource = IsExcludedSource;

        _paste = new PasteService(_monitor, _store);

        _sync = new SyncEngine(_store);
        _syncTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _syncTimer.Tick += async (_, _) => await RunSyncAsync();
        _syncTimer.Start();

        _hotkeys = new HotkeyManager(_msg);
        _hotkeys.HotkeyPressed += OnHotkey;
        ReloadHotkeys();

        SetupTray();
        ShowFirstRunWelcome();

        // Check for a newer release in the background (silent if none / on error).
        _ = CheckForUpdatesAsync(silent: true);
    }

    private void ShowFirstRunWelcome()
    {
        if (_store.GetSetting(HistoryStore.FirstRunKey) == "1")
        {
            return;
        }
        _store.SetSetting(HistoryStore.FirstRunKey, "1");
        HotkeyCombo popup = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        ShowBalloon("FineClipboard 已启动",
            $"按 {popup.ToDisplayString()} 打开剪贴板历史。程序在屏幕右下角托盘运行,右键图标可打开设置。");
    }

    private bool IsExcludedSource(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }
        string? raw = _store.GetSetting(HistoryStore.ExclusionsKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        foreach (string line in raw.Split('\n'))
        {
            string rule = line.Trim();
            if (rule.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                rule = rule[..^4];
            }
            if (rule.Length > 0 && string.Equals(rule, source, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        UpdateCheckResult result = await _update.CheckAsync();
        switch (result.Status)
        {
            case UpdateStatus.UpdateAvailable:
                UpdateInfo info = result.Update!;
                _updateUrl = info.Url;
                _updateMenuItem.Text = $"⬇ 下载新版本 {info.Version}";
                ShowBalloon("FineClipboard 有新版本", $"{info.Version} 可用,点击下载更新。");
                break;
            case UpdateStatus.UpToDate:
                if (!silent) ShowBalloon("FineClipboard", "当前已是最新版本。");
                break;
            case UpdateStatus.Failed:
                if (!silent) ShowBalloon("FineClipboard", "检查更新失败,请检查网络后重试。");
                break;
        }
    }

    private void OnUpdateMenuClick()
    {
        if (_updateUrl != null)
        {
            OpenUrl(_updateUrl);
        }
        else
        {
            _ = CheckForUpdatesAsync(silent: false);
        }
    }

    private void ShowBalloon(string title, string text)
    {
        if (_trayIcon != null)
        {
            _tray.Icon = _trayIcon;
        }
        _tray.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.None;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(5000);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore: no default browser / blocked.
        }
    }

    private void OnItemCaptured(ClipboardItem item)
    {
        long id = _store.Add(item);

        // Run OCR on captured images in the background, then store the recognized text so the
        // screenshot becomes searchable. On-device (Windows OCR), never blocks capture.
        if (item.Type == ClipItemType.Image && item.ImageData is { Length: > 0 } png && OcrService.IsAvailable)
        {
            _ = Task.Run(async () =>
            {
                string? text = await OcrService.RecognizeAsync(png).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(text))
                {
                    Dispatcher.Invoke(() =>
                    {
                        _store.UpdateOcrText(id, text);
                        if (_popup is { IsVisible: true })
                        {
                            _popup.LoadItems();
                        }
                    });
                }
            });
        }

        if (_pendingScreenshotPreview && DateTime.UtcNow > _pendingScreenshotPreviewUntil)
        {
            _pendingScreenshotPreview = false;
        }

        if (_pendingScreenshotPreview && item.Type == ClipItemType.Image && item.ImageData is { Length: > 0 } previewPng)
        {
            _pendingScreenshotPreview = false;
            ShowScreenshotPreview(previewPng);
        }

        if (_store.GetSetting(HistoryStore.SoundEnabledKey) == "1")
        {
            System.Media.SystemSounds.Asterisk.Play();
        }

        if (_popup is { IsVisible: true })
        {
            _popup.LoadItems();
        }
    }

    /// <summary>Re-registers global hotkeys and returns whether editable hotkeys registered successfully.</summary>
    internal (bool popupOk, bool plainOk, bool shotOk) ReloadHotkeys()
    {
        _hotkeys.UnregisterAll();
        HotkeyCombo popup = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        HotkeyCombo plain = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPlainKey), DefaultPlain);
        HotkeyCombo shot = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyShotKey), DefaultShot);
        bool popupOk = _hotkeys.Register(HotkeyPopup, popup.Modifiers, popup.VirtualKey);
        bool plainOk = _hotkeys.Register(HotkeyPlain, plain.Modifiers, plain.VirtualKey);
        bool shotOk = _hotkeys.Register(HotkeyShot, shot.Modifiers, shot.VirtualKey);
        return (popupOk, plainOk, shotOk);
    }

    /// <summary>Temporarily removes the global hotkeys (so the settings recorder can capture them).</summary>
    internal void SuspendHotkeys() => _hotkeys.UnregisterAll();

    /// <summary>Re-applies the saved hotkeys after a suspended/cancelled recording.</summary>
    internal void ResumeHotkeys() => ReloadHotkeys();

    /// <summary>
    /// Saves a new combo for one hotkey and re-registers. If either hotkey then fails to
    /// register (e.g. the combo is taken), reverts to the previous value and returns false.
    /// </summary>
    internal bool TrySetHotkey(bool isPopup, HotkeyCombo combo)
    {
        return TrySetHotkey(isPopup ? "popup" : "plain", combo);
    }

    /// <summary>
    /// Saves a new combo for one hotkey and re-registers all global shortcuts. If any then fails
    /// to register (e.g. the combo is taken), reverts the edited shortcut and returns false.
    /// </summary>
    internal bool TrySetHotkey(string target, HotkeyCombo combo)
    {
        (string key, HotkeyCombo def) = target switch
        {
            "popup" => (HistoryStore.HotkeyPopupKey, DefaultPopup),
            "plain" => (HistoryStore.HotkeyPlainKey, DefaultPlain),
            "shot" => (HistoryStore.HotkeyShotKey, DefaultShot),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };
        string previous = _store.GetSetting(key) ?? def.Serialize();

        _store.SetSetting(key, combo.Serialize());
        (bool popupOk, bool plainOk, bool shotOk) = ReloadHotkeys();
        if (popupOk && plainOk && shotOk)
        {
            return true;
        }

        _store.SetSetting(key, previous);
        ReloadHotkeys();
        return false;
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyPopup:
                ShowPopup();
                break;
            case HotkeyPlain:
                _ = _paste.PasteRecentPlainAsync();
                break;
            case HotkeyShot:
                CaptureInteractiveScreenshot();
                break;
        }
    }

    internal string ScreenshotHotkeyDisplay()
    {
        HotkeyCombo shot = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyShotKey), DefaultShot);
        return shot.ToDisplayString();
    }

    internal bool IsRecordingPaused => _monitor.Paused;

    internal void SetRecordingPaused(bool paused)
    {
        _monitor.Paused = paused;
        _tray.Text = paused ? "FineClipboard(已暂停记录)" : "FineClipboard";
    }

    internal void ShowSyncSettings() => ShowSyncWindow();

    internal void LockVault() => _vault.Lock();

    internal void CaptureInteractiveScreenshot()
    {
        _pendingScreenshotPreview = true;
        _pendingScreenshotPreviewUntil = DateTime.UtcNow.AddMinutes(2);
        ScreenshotService.CaptureInteractive();
    }

    internal void CaptureFullscreenScreenshot()
    {
        _pendingScreenshotPreview = true;
        _pendingScreenshotPreviewUntil = DateTime.UtcNow.AddMinutes(2);
        ScreenshotService.CaptureFullscreen();
    }

    private void PurgeExpiredNow()
    {
        int days = int.TryParse(_store.GetSetting(HistoryStore.ExpiryDaysKey), out int d) ? d : 0;
        _store.PurgeExpired(days);
    }

    /// <summary>Runs a background sync round if enabled; refreshes the popup if it pulled changes.</summary>
    private async Task RunSyncAsync()
    {
        if (_syncing || !_sync.Ready)
        {
            return;
        }
        _syncing = true;
        try
        {
            await _sync.SyncNowAsync();
            if (_popup is { IsVisible: true })
            {
                _popup.LoadItems();
            }
        }
        catch
        {
            // Network hiccups are non-fatal; the next tick retries.
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ShowSyncWindow()
    {
        if (_syncWindow == null)
        {
            _syncWindow = new SyncWindow(_sync);
            _syncWindow.Closed += (_, _) => _syncWindow = null;
            _syncWindow.Show();
        }
        else
        {
            _syncWindow.Activate();
        }
    }

    private void ShowPopup()
    {
        // Remember the window we'll paste back into, before our popup steals focus.
        _lastForeground = NativeMethods.GetForegroundWindow();

        _popup ??= new PopupWindow(_store, this);
        _popup.LoadItems();
        _popup.ShowAtCursor();
    }

    // Called by the popup (which has already hidden itself) to act on the previous window.
    internal void PasteItem(ClipboardItem item, bool plainText) =>
        _ = _paste.PasteItemAsync(item, _lastForeground, plainText);

    internal void PasteText(string text) =>
        _ = _paste.PasteTextAsync(text, _lastForeground);

    internal void CopyItem(ClipboardItem item) => _paste.CopyToClipboard(item);

    private void ShowSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_store, this);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        else
        {
            _settings.Activate();
        }
    }

    private void ShowScreenshotPreview(byte[] png)
    {
        _screenshotPreview?.Close();
        _screenshotPreview = new ScreenshotPreviewWindow(png);
        _screenshotPreview.Closed += (_, _) => _screenshotPreview = null;
        _screenshotPreview.Show();
        _screenshotPreview.Activate();
    }

    private void SetupTray()
    {
        _trayIcon = TrayIconFactory.Create();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "FineClipboard",
            BalloonTipIcon = System.Windows.Forms.ToolTipIcon.None,
        };
        _tray.DoubleClick += (_, _) => ShowPopup();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_updateUrl != null)
            {
                OpenUrl(_updateUrl);
            }
        };

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
        };

        menu.Items.Add("设置…", null, (_, _) => ShowSettings());

        HotkeyCombo shotCombo = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyShotKey), DefaultShot);
        var shotMenu = new System.Windows.Forms.ToolStripMenuItem("截图");
        shotMenu.DropDownItems.Add($"截取区域 / 窗口 ({shotCombo.ToDisplayString()})", null, (_, _) => CaptureInteractiveScreenshot());
        shotMenu.DropDownItems.Add("全屏截图", null, (_, _) => CaptureFullscreenScreenshot());
        menu.Items.Add(shotMenu);

        _updateMenuItem = new System.Windows.Forms.ToolStripMenuItem("检查更新…", null, (_, _) => OnUpdateMenuClick());
        menu.Items.Add(_updateMenuItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        _tray.ContextMenuStrip = menu;
    }

    private sealed class TrayMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayMenuColors()) { }
    }

    private sealed class TrayMenuColors : System.Windows.Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(245, 248, 252);
        public override System.Drawing.Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(224, 238, 255);
        public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(137, 184, 255);
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(195, 207, 224);
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(218, 226, 237);
        public override System.Drawing.Color SeparatorLight => System.Drawing.Color.White;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _purgeTimer?.Stop();
        _syncTimer?.Stop();
        _hotkeys?.Dispose();
        _monitor?.Dispose();
        _msg?.Dispose();

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();
        _store?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
