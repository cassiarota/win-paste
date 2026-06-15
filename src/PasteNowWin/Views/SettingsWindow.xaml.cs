using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PasteNowWin.Services;

namespace PasteNowWin.Views;

public partial class SettingsWindow : Window
{
    private readonly HistoryStore _store;
    private bool _initializing;

    public SettingsWindow(HistoryStore store)
    {
        _initializing = true;
        InitializeComponent();
        _store = store;

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = $"版本 {version}";
        StartupCheck.IsChecked = StartupManager.IsEnabled();
        LoadExpirySelection();
        RefreshCount();
        _initializing = false;
    }

    private void RefreshCount() => CountText.Text = $"已保存 {_store.Count()} 条历史记录";

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
