using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using PasteNowWin.Models;
using PasteNowWin.Services;

namespace PasteNowWin.Views;

public partial class PopupWindow : Window
{
    private readonly HistoryStore _store;
    private readonly Action<ClipboardItem, bool> _onPaste;
    private List<PopupItemVm> _all = new();
    private bool _contextMenuOpen;

    public PopupWindow(HistoryStore store, Action<ClipboardItem, bool> onPaste)
    {
        InitializeComponent();
        _store = store;
        _onPaste = onPaste;

        // Close when the user clicks away (matches PasteNow behaviour),
        // but keep open while the right-click context menu is showing.
        Deactivated += (_, _) =>
        {
            if (!_contextMenuOpen)
            {
                Hide();
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Reloads items from the store and re-applies the current filter.</summary>
    public void LoadItems()
    {
        _all = _store.GetAll(200).Select(i => new PopupItemVm(i)).ToList();
        ApplyFilter(SearchBox.Text);
    }

    public void FocusSearch()
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    /// <summary>Shows the popup on the monitor under the cursor, then focuses the search box.</summary>
    public void ShowAtCursor()
    {
        // Park off-screen first so the initial frame never flashes at the old location,
        // then place precisely (in physical pixels) via Win32 once the HWND exists.
        Left = -32000;
        Top = -32000;
        Show();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.PositionWindowAtCursor(hwnd, Width, Height);

        Activate();
        FocusSearch();
    }

    private void ApplyFilter(string query)
    {
        IEnumerable<PopupItemVm> source = _all;
        if (!string.IsNullOrWhiteSpace(query))
        {
            source = _all.Where(v =>
                v.PreviewText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (v.Item.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        ItemsList.ItemsSource = source.ToList();
        if (ItemsList.Items.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
        }

        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                PasteSelected(plainText: ctrl);
                e.Handled = true;
                break;
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteSelected();
                e.Handled = true;
                break;
            case Key.P when ctrl:
                PinSelected();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        int count = ItemsList.Items.Count;
        if (count == 0)
        {
            return;
        }
        int index = Math.Clamp(ItemsList.SelectedIndex + delta, 0, count - 1);
        ItemsList.SelectedIndex = index;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PasteSelected(plainText: false);

    private void PasteSelected(bool plainText)
    {
        if (ItemsList.SelectedItem is PopupItemVm vm)
        {
            _onPaste(vm.Item, plainText);
        }
    }

    private void PinSelected()
    {
        if (ItemsList.SelectedItem is PopupItemVm vm)
        {
            _store.TogglePin(vm.Item.Id);
            LoadItems();
        }
    }

    private void DeleteSelected()
    {
        if (ItemsList.SelectedItem is PopupItemVm vm)
        {
            int index = ItemsList.SelectedIndex;
            _store.Delete(vm.Item.Id);
            LoadItems();
            if (ItemsList.Items.Count > 0)
            {
                ItemsList.SelectedIndex = Math.Clamp(index, 0, ItemsList.Items.Count - 1);
            }
        }
    }

    // ---- Right-click context menu ----
    private void ListBoxItem_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    private void ItemMenu_Opened(object sender, RoutedEventArgs e) => _contextMenuOpen = true;

    private void ItemMenu_Closed(object sender, RoutedEventArgs e) => _contextMenuOpen = false;

    private void MenuPaste_Click(object sender, RoutedEventArgs e) => PasteSelected(plainText: false);

    private void MenuPastePlain_Click(object sender, RoutedEventArgs e) => PasteSelected(plainText: true);

    private void MenuPin_Click(object sender, RoutedEventArgs e) => PinSelected();

    private void MenuDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
}
