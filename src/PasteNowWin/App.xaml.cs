using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PasteNowWin.Interop;
using PasteNowWin.Models;
using PasteNowWin.Services;
using PasteNowWin.Views;

namespace PasteNowWin;

public partial class App : Application
{
    // Hotkey ids (mirror PasteNow): popup history + paste-recent-as-plain-text.
    private const int HotkeyPopup = 1;
    private const int HotkeyPlain = 2;
    private const uint VkV = 0x56;
    private const uint VkB = 0x42;

    private static readonly HotkeyCombo DefaultPopup = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkV);
    private static readonly HotkeyCombo DefaultPlain = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, VkB);

    private Mutex? _singleInstance;

    private HistoryStore _store = null!;
    private NativeMessageWindow _msg = null!;
    private ClipboardMonitor _monitor = null!;
    private PasteService _paste = null!;
    private HotkeyManager _hotkeys = null!;

    private System.Windows.Forms.NotifyIcon _tray = null!;
    private System.Windows.Forms.ToolStripMenuItem _startupMenuItem = null!;
    private System.Windows.Forms.ToolStripMenuItem _updateMenuItem = null!;
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
        _singleInstance = new Mutex(initiallyOwned: true, "PasteNowWin.SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        _store = new HistoryStore();
        PurgeExpiredNow();
        _purgeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _purgeTimer.Tick += (_, _) => PurgeExpiredNow();
        _purgeTimer.Start();

        _msg = new NativeMessageWindow();

        _monitor = new ClipboardMonitor(_msg);
        _monitor.ItemCaptured += OnItemCaptured;
        _monitor.ShouldSkipSource = IsExcludedSource;

        _paste = new PasteService(_monitor, _store);

        _hotkeys = new HotkeyManager(_msg);
        _hotkeys.HotkeyPressed += OnHotkey;
        ReloadHotkeys();

        SetupTray();

        // Check for a newer release in the background (silent if none / on error).
        _ = CheckForUpdatesAsync(silent: true);
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
            ShowBalloon("PasteNowWin 有新版本", $"{info.Version} 可用,点击下载更新。");
        }
        else if (!silent)
        {
            ShowBalloon("PasteNowWin", "当前已是最新版本。");
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
        _store.Add(item);

        if (_store.GetSetting(HistoryStore.SoundEnabledKey) == "1")
        {
            System.Media.SystemSounds.Asterisk.Play();
        }

        if (_popup is { IsVisible: true })
        {
            _popup.LoadItems();
        }
    }

    /// <summary>Re-registers both global hotkeys from the saved (or default) combinations.</summary>
    internal void ReloadHotkeys()
    {
        _hotkeys.UnregisterAll();
        HotkeyCombo popup = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        HotkeyCombo plain = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPlainKey), DefaultPlain);
        _hotkeys.Register(HotkeyPopup, popup.Modifiers, popup.VirtualKey);
        _hotkeys.Register(HotkeyPlain, plain.Modifiers, plain.VirtualKey);
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
        }
    }

    private void PurgeExpiredNow()
    {
        int days = int.TryParse(_store.GetSetting(HistoryStore.ExpiryDaysKey), out int d) ? d : 0;
        _store.PurgeExpired(days);
    }

    private void ShowPopup()
    {
        // Remember the window we'll paste back into, before our popup steals focus.
        _lastForeground = NativeMethods.GetForegroundWindow();

        _popup ??= new PopupWindow(_store, OnPasteRequested);
        _popup.LoadItems();
        _popup.ShowAtCursor();
    }

    private void OnPasteRequested(ClipboardItem item, bool plainText)
    {
        _popup?.Hide();
        _ = _paste.PasteItemAsync(item, _lastForeground, plainText);
    }

    private void ShowSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_store, ReloadHotkeys);
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
            Text = "PasteNowWin",
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
        menu.Items.Add("打开历史 (Ctrl+Shift+V)", null, (_, _) => ShowPopup());
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());

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
