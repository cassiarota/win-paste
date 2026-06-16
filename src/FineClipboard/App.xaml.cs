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
    // Hotkey ids (mirror PasteNow): popup history + paste-recent-as-plain-text + paste-next-from-stack.
    private const int HotkeyPopup = 1;
    private const int HotkeyPlain = 2;
    private const int HotkeyStack = 3;
    private const int HotkeyShot = 4;
    private const uint VkV = 0x56;
    private const uint VkB = 0x42;
    private const uint VkX = 0x58;
    private const uint VkA = 0x41;

    private static readonly HotkeyCombo DefaultPopup = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkV);
    private static readonly HotkeyCombo DefaultPlain = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkB);
    private static readonly HotkeyCombo DefaultStack = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkX);
    private static readonly HotkeyCombo DefaultShot = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkA);

    private readonly PasteStack _pasteStack = new();

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
    private System.Windows.Forms.ToolStripMenuItem _openMenuItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _pauseMenuItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _startupMenuItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _updateMenuItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _stackMenuItem = null!;
    private System.Drawing.Icon? _trayIcon;

    private System.Windows.Threading.DispatcherTimer? _purgeTimer;

    private readonly UpdateService _update = new();
    private string? _updateUrl;

    private PopupWindow? _popup;
    private SettingsWindow? _settings;
    private IntPtr _lastForeground;

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
        UpdateInfo? info = await _update.CheckAsync();
        if (info != null)
        {
            _updateUrl = info.Url;
            _updateMenuItem.Text = $"⬇ 下载新版本 {info.Version}";
            ShowBalloon("FineClipboard 有新版本", $"{info.Version} 可用,点击下载更新。");
        }
        else if (!silent)
        {
            ShowBalloon("FineClipboard", "当前已是最新版本。");
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

        if (_store.GetSetting(HistoryStore.SoundEnabledKey) == "1")
        {
            System.Media.SystemSounds.Asterisk.Play();
        }

        if (_popup is { IsVisible: true })
        {
            _popup.LoadItems();
        }
    }

    /// <summary>
    /// Re-registers both global hotkeys from the saved (or default) combinations and updates
    /// the tray label. Returns whether each one registered successfully.
    /// </summary>
    internal (bool popupOk, bool plainOk) ReloadHotkeys()
    {
        _hotkeys.UnregisterAll();
        HotkeyCombo popup = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        HotkeyCombo plain = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPlainKey), DefaultPlain);
        HotkeyCombo stack = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyStackKey), DefaultStack);
        HotkeyCombo shot = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyShotKey), DefaultShot);
        bool popupOk = _hotkeys.Register(HotkeyPopup, popup.Modifiers, popup.VirtualKey);
        bool plainOk = _hotkeys.Register(HotkeyPlain, plain.Modifiers, plain.VirtualKey);
        _hotkeys.Register(HotkeyStack, stack.Modifiers, stack.VirtualKey);
        _hotkeys.Register(HotkeyShot, shot.Modifiers, shot.VirtualKey);

        if (_openMenuItem != null)
        {
            _openMenuItem.Text = $"打开历史 ({popup.ToDisplayString()})";
        }
        return (popupOk, plainOk);
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
        string key = isPopup ? HistoryStore.HotkeyPopupKey : HistoryStore.HotkeyPlainKey;
        string previous = _store.GetSetting(key) ?? (isPopup ? DefaultPopup : DefaultPlain).Serialize();

        _store.SetSetting(key, combo.Serialize());
        (bool popupOk, bool plainOk) = ReloadHotkeys();
        if (popupOk && plainOk)
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
            case HotkeyStack:
                PasteNextFromStack();
                break;
            case HotkeyShot:
                ScreenshotService.CaptureInteractive();
                break;
        }
    }

    /// <summary>Adds an item to the paste stack (called from the popup) and refreshes the tray label.</summary>
    internal void AddToPasteStack(ClipboardItem item)
    {
        _pasteStack.Enqueue(item);
        UpdateStackMenu();
    }

    /// <summary>Pastes the next stacked item into the current foreground window (FIFO), then advances.</summary>
    private void PasteNextFromStack()
    {
        ClipboardItem? next = _pasteStack.Dequeue();
        UpdateStackMenu();
        if (next != null)
        {
            _ = _paste.PasteItemAsync(next, NativeMethods.GetForegroundWindow(), plainText: false);
        }
    }

    private void UpdateStackMenu()
    {
        if (_stackMenuItem != null)
        {
            int n = _pasteStack.Count;
            _stackMenuItem.Text = $"粘贴堆栈:{n} 项";
            _stackMenuItem.Enabled = n > 0;
        }
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

    private void SetupTray()
    {
        _trayIcon = TrayIconFactory.Create();

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "FineClipboard",
        };
        _tray.DoubleClick += (_, _) => ShowPopup();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_updateUrl != null)
            {
                OpenUrl(_updateUrl);
            }
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();

        HotkeyCombo popupCombo = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        _openMenuItem = new System.Windows.Forms.ToolStripMenuItem(
            $"打开历史 ({popupCombo.ToDisplayString()})", null, (_, _) => ShowPopup());
        menu.Items.Add(_openMenuItem);

        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add("云同步…", null, (_, _) => ShowSyncWindow());

        _pauseMenuItem = new System.Windows.Forms.ToolStripMenuItem("暂停记录(隐私模式)") { CheckOnClick = true };
        _pauseMenuItem.CheckedChanged += (_, _) =>
        {
            _monitor.Paused = _pauseMenuItem.Checked;
            _tray.Text = _pauseMenuItem.Checked ? "FineClipboard(已暂停记录)" : "FineClipboard";
        };
        menu.Items.Add(_pauseMenuItem);
        menu.Items.Add("锁定密码", null, (_, _) => _vault.Lock());

        HotkeyCombo stackCombo = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyStackKey), DefaultStack);
        _stackMenuItem = new System.Windows.Forms.ToolStripMenuItem("粘贴堆栈:0 项") { Enabled = false };
        menu.Items.Add(_stackMenuItem);
        menu.Items.Add($"清空粘贴堆栈 (粘贴下一项 {stackCombo.ToDisplayString()})", null, (_, _) =>
        {
            _pasteStack.Clear();
            UpdateStackMenu();
        });

        HotkeyCombo shotCombo = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyShotKey), DefaultShot);
        var shotMenu = new System.Windows.Forms.ToolStripMenuItem("截图");
        shotMenu.DropDownItems.Add($"截取区域 / 窗口 ({shotCombo.ToDisplayString()})", null, (_, _) => ScreenshotService.CaptureInteractive());
        shotMenu.DropDownItems.Add("全屏截图", null, (_, _) => ScreenshotService.CaptureFullscreen());
        menu.Items.Add(shotMenu);

        _startupMenuItem = new System.Windows.Forms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled(),
        };
        _startupMenuItem.CheckedChanged += (_, _) => StartupManager.SetEnabled(_startupMenuItem.Checked);
        menu.Items.Add(_startupMenuItem);

        _updateMenuItem = new System.Windows.Forms.ToolStripMenuItem("检查更新…", null, (_, _) => OnUpdateMenuClick());
        menu.Items.Add(_updateMenuItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Shutdown());

        _tray.ContextMenuStrip = menu;
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
