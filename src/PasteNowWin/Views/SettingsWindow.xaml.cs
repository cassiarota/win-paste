using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PasteNowWin.Interop;
using PasteNowWin.Services;

namespace PasteNowWin.Views;

public partial class SettingsWindow : Window
{
    private static readonly HotkeyCombo DefaultPopup = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x56);
    private static readonly HotkeyCombo DefaultPlain = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x42);

    private readonly HistoryStore _store;
    private readonly App _app;
    private bool _initializing;

    /// <summary>"popup" / "plain" while capturing a new combo for that hotkey; null otherwise.</summary>
    private string? _recordingTarget;

    public SettingsWindow(HistoryStore store, App app)
    {
        _initializing = true;
        InitializeComponent();
        _store = store;
        _app = app;

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = $"版本 {version}";
        StartupCheck.IsChecked = StartupManager.IsEnabled();
        SoundCheck.IsChecked = _store.GetSetting(HistoryStore.SoundEnabledKey) == "1";
        LoadExpirySelection();
        ExclusionsBox.Text = _store.GetSetting(HistoryStore.ExclusionsKey) ?? string.Empty;
        LoadHotkeyDisplay();
        RefreshCount();
        _initializing = false;

        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            // If the window is closed mid-recording, make sure the hotkeys come back.
            if (_recordingTarget != null)
            {
                _recordingTarget = null;
                _app.ResumeHotkeys();
            }
        };
    }

    private void RefreshCount() => CountText.Text = $"已保存 {_store.Count()} 条历史记录";

    // ---- Sound ----
    private void SoundCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        _store.SetSetting(HistoryStore.SoundEnabledKey, SoundCheck.IsChecked == true ? "1" : "0");
    }

    // ---- Custom hotkeys ----
    private void PopupHotkeyButton_Click(object sender, RoutedEventArgs e) => StartRecording("popup");

    private void PlainHotkeyButton_Click(object sender, RoutedEventArgs e) => StartRecording("plain");

    private void StartRecording(string target)
    {
        LoadHotkeyDisplay(); // clear any stale "按下快捷键…" on the other button
        _recordingTarget = target;
        // Suspend global hotkeys so their own combos reach this window and can be re-recorded.
        _app.SuspendHotkeys();
        (target == "popup" ? PopupHotkeyButton : PlainHotkeyButton).Content = "按下快捷键…";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingTarget == null)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _recordingTarget = null;
            _app.ResumeHotkeys();
            LoadHotkeyDisplay();
            return;
        }
        if (IsModifierKey(key))
        {
            return; // wait for the non-modifier key
        }

        uint mods = 0;
        ModifierKeys m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;

        if (mods == 0)
        {
            return; // require at least one modifier; keep waiting
        }

        bool isPopup = _recordingTarget == "popup";
        _recordingTarget = null;

        var combo = new HotkeyCombo(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        bool ok = _app.TrySetHotkey(isPopup, combo);
        LoadHotkeyDisplay();

        if (!ok)
        {
            MessageBox.Show(
                "该组合键无法注册,可能已被其它程序占用,或与另一个快捷键冲突,请换一个。",
                "PasteNowWin", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadHotkeyDisplay()
    {
        HotkeyCombo popup = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        HotkeyCombo plain = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPlainKey), DefaultPlain);
        PopupHotkeyButton.Content = popup.ToDisplayString();
        PlainHotkeyButton.Content = plain.ToDisplayString();
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or
        Key.System;

    // ---- Expiry ----
    private void LoadExpirySelection()
    {
        string current = _store.GetSetting(HistoryStore.ExpiryDaysKey) ?? "0";
        foreach (object obj in ExpiryCombo.Items)
        {
            if (obj is ComboBoxItem item && (string?)item.Tag == current)
            {
                ExpiryCombo.SelectedItem = item;
                return;
            }
        }
        ExpiryCombo.SelectedIndex = 0;
    }

    private void ExpiryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        if (ExpiryCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _store.SetSetting(HistoryStore.ExpiryDaysKey, tag);
            if (int.TryParse(tag, out int days))
            {
                _store.PurgeExpired(days);
            }
            RefreshCount();
        }
    }

    // ---- Exclusion rules ----
    private void ExclusionsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        _store.SetSetting(HistoryStore.ExclusionsKey, ExclusionsBox.Text);
    }

    // ---- History management ----
    private void StartupCheck_Click(object sender, RoutedEventArgs e) =>
        StartupManager.SetEnabled(StartupCheck.IsChecked == true);

    private void ClearKeepPinned_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("确定清空历史吗?(置顶项会保留)"))
        {
            _store.Clear(keepPinned: true);
            RefreshCount();
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm("确定清空全部历史吗?(包括置顶项,不可恢复)"))
        {
            _store.Clear(keepPinned: false);
            RefreshCount();
        }
    }

    private static bool Confirm(string message) =>
        MessageBox.Show(message, "PasteNowWin", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK;
}
