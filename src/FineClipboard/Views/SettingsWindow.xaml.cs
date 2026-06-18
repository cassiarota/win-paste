using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FineClipboard.Interop;
using FineClipboard.Services;

namespace FineClipboard.Views;

public partial class SettingsWindow : Window
{
    private static readonly HotkeyCombo DefaultPopup = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x56);
    private static readonly HotkeyCombo DefaultPlain = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x42);
    private static readonly HotkeyCombo DefaultShot = new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x41);

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

        Icon = AppIconFactory.CreateImageSource();
        AppIconImage.Source = AppIconFactory.CreateImageSource(44);
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = $"版本 {version}";
        StartupCheck.IsChecked = StartupManager.IsEnabled();
        SoundCheck.IsChecked = _store.GetSetting(HistoryStore.SoundEnabledKey) == "1";
        PauseCheck.IsChecked = _app.IsRecordingPaused;
        LoadExpirySelection();
        ExclusionsBox.Text = _store.GetSetting(HistoryStore.ExclusionsKey) ?? string.Empty;
        SelectByTag(MaxItemsCombo, _store.GetSetting(HistoryStore.MaxItemsKey) ?? "1000");
        SelectByTag(ThemeCombo, _store.GetSetting(HistoryStore.ThemeKey) ?? "system");
        SelectByTag(PopupSizeCombo, _store.GetSetting(HistoryStore.PopupSizeKey) ?? "medium");
        RefreshPasswordState();
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

    private void PauseCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        _app.SetRecordingPaused(PauseCheck.IsChecked == true);
    }

    // ---- Custom hotkeys ----
    private void PopupHotkeyButton_Click(object sender, RoutedEventArgs e) => StartRecording("popup");

    private void PlainHotkeyButton_Click(object sender, RoutedEventArgs e) => StartRecording("plain");

    private void ShotHotkeyButton_Click(object sender, RoutedEventArgs e) => StartRecording("shot");

    private void StartRecording(string target)
    {
        LoadHotkeyDisplay(); // clear any stale "按下快捷键…" on the other button
        _recordingTarget = target;
        // Suspend global hotkeys so their own combos reach this window and can be re-recorded.
        _app.SuspendHotkeys();
        ButtonFor(target).Content = "按下快捷键…";
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

        string target = _recordingTarget;
        _recordingTarget = null;

        var combo = new HotkeyCombo(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        bool ok = _app.TrySetHotkey(target, combo);
        LoadHotkeyDisplay();

        if (!ok)
        {
            MessageBox.Show(
                "该组合键无法注册,可能已被其它程序占用,或与另一个快捷键冲突,请换一个。",
                "FineClipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadHotkeyDisplay()
    {
        HotkeyCombo popup = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPopupKey), DefaultPopup);
        HotkeyCombo plain = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyPlainKey), DefaultPlain);
        HotkeyCombo shot = HotkeyCombo.Parse(_store.GetSetting(HistoryStore.HotkeyShotKey), DefaultShot);
        PopupHotkeyButton.Content = popup.ToDisplayString();
        PlainHotkeyButton.Content = plain.ToDisplayString();
        ShotHotkeyButton.Content = shot.ToDisplayString();
    }

    private Button ButtonFor(string target) => target switch
    {
        "popup" => PopupHotkeyButton,
        "plain" => PlainHotkeyButton,
        _ => ShotHotkeyButton,
    };

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

    // ---- Appearance / capacity / snippets ----
    private void MaxItemsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SaveTag(MaxItemsCombo, HistoryStore.MaxItemsKey);

    private void PopupSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SaveTag(PopupSizeCombo, HistoryStore.PopupSizeKey);

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        if (ThemeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _store.SetSetting(HistoryStore.ThemeKey, tag);
            ThemeManager.Apply(tag);
        }
    }

    private void OpenSync_Click(object sender, RoutedEventArgs e) => _app.ShowSyncSettings();

    // ---- Password protection ----
    private void MasterPw_Click(object sender, RoutedEventArgs e)
    {
        if (_app.Vault.IsConfigured)
        {
            return;
        }
        var window = new SetMasterPasswordWindow(_app.Vault) { Owner = this };
        if (window.ShowDialog() == true)
        {
            RefreshPasswordState();
        }
    }

    private void RefreshPasswordState()
    {
        bool configured = _app.Vault.IsConfigured;
        MasterPwButton.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        PasswordConfiguredText.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveTag(ComboBox combo, string settingKey)
    {
        if (_initializing)
        {
            return;
        }
        if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _store.SetSetting(settingKey, tag);
        }
    }

    private static void SelectByTag(ComboBox combo, string value)
    {
        foreach (object obj in combo.Items)
        {
            if (obj is ComboBoxItem item && (string?)item.Tag == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    // ---- History management ----
    private void StartupCheck_Click(object sender, RoutedEventArgs e) =>
        StartupManager.SetEnabled(StartupCheck.IsChecked == true);
}
