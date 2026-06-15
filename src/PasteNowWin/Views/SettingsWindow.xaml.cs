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
    private readonly Action _reloadHotkeys;
    private bool _initializing;

    /// <summary>"popup" / "plain" while capturing a new combo for that hotkey; null otherwise.</summary>
    private string? _recordingTarget;

    public SettingsWindow(HistoryStore store, Action reloadHotkeys)
    {
        _initializing = true;
        InitializeComponent();
        _store = store;
        _reloadHotkeys = reloadHotkeys;

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
        _recordingTarget = target;
        Button button = target == "popup" ? PopupHotkeyButton : PlainHotkeyButton;
        button.Content = "按下快捷键…";
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

        var combo = new HotkeyCombo(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        string storeKey = _recordingTarget == "popup" ? HistoryStore.HotkeyPopupKey : HistoryStore.HotkeyPlainKey;
        _store.SetSetting(storeKey, combo.Serialize());

        _recordingTarget = null;
        LoadHotkeyDisplay();
        _reloadHotkeys();
    }

    private void LoadHotkeyDisplay()
    {
        HotkeyCombo popup = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        HotkeyCombo plain = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPlainKey), DefaultPlain);
        PopupHotkeyButton.Content = FormatCombo(popup);
        PlainHotkeyButton.Content = FormatCombo(plain);
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin or
        Key.System;

    private static string FormatCombo(HotkeyCombo c)
    {
        var parts = new System.Collections.Generic.List<string>(4);
        if ((c.Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((c.Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((c.Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((c.Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(c.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint vk)
    {
        string s = KeyInterop.KeyFromVirtualKey((int)vk).ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1]))
        {
            return s[1].ToString(); // D1 -> 1
        }
        return s;
    }

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
