using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FineClipboard.Models;
using FineClipboard.Services;

namespace FineClipboard.Views;

public partial class PopupWindow : Window
{
    private readonly HistoryStore _store;
    private readonly App _app;
    private List<PopupItemVm> _clip = new();
    private List<PopupItemVm> _snippets = new();
    private List<PopupItemVm> _passwords = new();
    private Point _dragStart;
    private bool _contextMenuOpen;
    private bool _suppressHide;
    private bool _initialized;

    public PopupWindow(HistoryStore store, App app)
    {
        InitializeComponent();
        _store = store;
        _app = app;

        Deactivated += (_, _) =>
        {
            if (!_contextMenuOpen && !_suppressHide)
            {
                Hide();
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        _initialized = true;
    }

    // Fixed tabs: 全部 / 文本 / 图片 / 文件 / 置顶 / 片段 / 密码. User lists are appended after these.
    private const int FixedTabCount = 7;

    /// <summary>Reloads history + snippets and resets to the "全部" tab.</summary>
    public void LoadItems()
    {
        LoadData();
        RefreshTabs();
        FilterTabs.SelectedIndex = 0;
        ApplyFilter();
    }

    private void LoadData()
    {
        _clip = _store.GetAll(200).Select(i => new PopupItemVm(i)).ToList();
        _snippets = _store.GetSnippets().Select(s => new PopupItemVm(s)).ToList();
        _passwords = _app.Vault.GetEntries().Select(p => new PopupItemVm(p)).ToList();
    }

    private void RefreshTabs()
    {
        while (FilterTabs.Items.Count > FixedTabCount)
        {
            FilterTabs.Items.RemoveAt(FilterTabs.Items.Count - 1);
        }
        foreach (ClipList list in _store.GetLists())
        {
            FilterTabs.Items.Add(new ListBoxItem { Content = list.Name, Tag = list.Id });
        }
    }

    public void FocusSearch()
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    /// <summary>Shows the popup on the monitor under the cursor, then focuses the search box.</summary>
    public void ShowAtCursor()
    {
        ApplySize();
        Left = -32000;
        Top = -32000;
        Show();

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.PositionWindowAtCursor(hwnd, Width, Height);

        Activate();
        FocusSearch();
    }

    private void ApplySize()
    {
        switch (_store.GetSetting(HistoryStore.PopupSizeKey))
        {
            case "small": Width = 380; Height = 460; break;
            case "large": Width = 520; Height = 680; break;
            default: Width = 440; Height = 560; break;
        }
    }

    private bool SnippetTab => FilterTabs.SelectedIndex == 5;
    private bool PasswordTab => FilterTabs.SelectedIndex == 6;

    private void ApplyFilter()
    {
        if (!_initialized || ItemsList == null)
        {
            return;
        }

        string query = SearchBox.Text;
        IEnumerable<PopupItemVm> source;
        bool listTab = FilterTabs.SelectedItem is ListBoxItem { Tag: long };

        if (listTab)
        {
            long listId = (long)((ListBoxItem)FilterTabs.SelectedItem).Tag;
            source = _store.GetByList(listId).Select(i => new PopupItemVm(i));
            if (!string.IsNullOrWhiteSpace(query))
            {
                source = source.Where(v =>
                    v.PreviewText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (v.Item?.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            }
        }
        else if (SnippetTab || PasswordTab)
        {
            source = PasswordTab ? _passwords : _snippets;
            if (!string.IsNullOrWhiteSpace(query))
            {
                source = source.Where(v =>
                    v.PreviewText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (v.Snippet?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            }
        }
        else
        {
            source = FilterTabs.SelectedIndex switch
            {
                1 => _clip.Where(v => v.Item!.Type == ClipItemType.Text),
                2 => _clip.Where(v => v.Item!.Type == ClipItemType.Image),
                3 => _clip.Where(v => v.Item!.Type == ClipItemType.Files),
                4 => _clip.Where(v => v.Item!.Pinned),
                _ => _clip,
            };
            if (!string.IsNullOrWhiteSpace(query))
            {
                source = source.Where(v =>
                    v.PreviewText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (v.Item?.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            }
        }

        var list = source.ToList();
        bool showBadges = string.IsNullOrEmpty(query);
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Badge = showBadges && i < 9 ? (i + 1).ToString() : string.Empty;
        }

        ItemsList.ItemsSource = list;
        if (list.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
        }

        SearchPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = !string.IsNullOrEmpty(query) ? "没有匹配的内容"
            : listTab ? "这个列表还没有内容,右键历史条目可「移到列表」"
            : SnippetTab ? "还没有常用片段,可在「设置 → 常用片段」里添加"
            : PasswordTab ? (_app.Vault.IsConfigured
                ? "还没有密码,可在「设置 → 密码管理」里添加"
                : "请先在「设置 → 密码保护」里设置主密码")
            : "还没有历史记录,复制点东西试试";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void FilterTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
        SearchBox?.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // 1-9 quick paste — only when not typing a search query.
        if (string.IsNullOrEmpty(SearchBox.Text) && !ctrl && TryNumber(e.Key, out int n))
        {
            PasteByNumber(n);
            e.Handled = true;
            return;
        }

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

    private static bool TryNumber(Key key, out int n)
    {
        if (key >= Key.D1 && key <= Key.D9) { n = key - Key.D1 + 1; return true; }
        if (key >= Key.NumPad1 && key <= Key.NumPad9) { n = key - Key.NumPad1 + 1; return true; }
        n = 0;
        return false;
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

    // ---- Drag out to other apps ----
    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(null);

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        Point pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        if (ItemsList.SelectedItem is not PopupItemVm vm || vm.IsPassword)
        {
            return; // never drag passwords
        }

        DataObject? data = BuildDragData(vm);
        if (data == null)
        {
            return;
        }

        _suppressHide = true;
        DragDropEffects effect;
        try
        {
            effect = DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy);
        }
        finally
        {
            _suppressHide = false;
        }
        if (effect == DragDropEffects.Copy)
        {
            Hide();
        }
    }

    private static DataObject? BuildDragData(PopupItemVm vm)
    {
        var data = new DataObject();
        if (vm.IsSnippet)
        {
            data.SetText(vm.Snippet!.Text);
            return data;
        }

        ClipboardItem? item = vm.Item;
        if (item == null)
        {
            return null;
        }
        switch (item.Type)
        {
            case ClipItemType.Text when !string.IsNullOrEmpty(item.Text):
                data.SetText(item.Text);
                break;
            case ClipItemType.Files:
                var paths = new System.Collections.Specialized.StringCollection();
                paths.AddRange(item.FilePaths);
                data.SetFileDropList(paths);
                break;
            case ClipItemType.Image when item.ImageData != null:
                BitmapImage? bmp = LoadBitmap(item.ImageData);
                if (bmp != null)
                {
                    data.SetImage(bmp);
                }
                break;
            default:
                return null;
        }
        return data;
    }

    private static BitmapImage? LoadBitmap(byte[] png)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(png);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ---- Paste paths ----
    private void PasteByNumber(int n)
    {
        if (n >= 1 && n <= ItemsList.Items.Count && ItemsList.Items[n - 1] is PopupItemVm vm)
        {
            PasteVm(vm, plainText: false);
        }
    }

    private void PasteSelected(bool plainText)
    {
        if (ItemsList.SelectedItem is PopupItemVm vm)
        {
            PasteVm(vm, plainText);
        }
    }

    private void PasteVm(PopupItemVm vm, bool plainText)
    {
        if (vm.IsPassword)
        {
            PastePassword(vm.Password!);
            return;
        }

        Hide();
        if (vm.IsSnippet)
        {
            _app.PasteText(vm.Snippet!.Text);
        }
        else if (vm.Item != null)
        {
            _app.PasteItem(vm.Item, plainText);
        }
    }

    private void PastePassword(Models.PasswordEntry entry)
    {
        if (!EnsureUnlocked())
        {
            return;
        }
        string? secret = _app.Vault.Reveal(entry.Id);
        if (secret == null)
        {
            return;
        }
        Hide();
        _app.PasteText(secret);
    }

    /// <summary>Prompts for the master password until the vault unlocks or the user cancels.</summary>
    private bool EnsureUnlocked()
    {
        if (_app.Vault.IsUnlocked)
        {
            return true;
        }
        if (!_app.Vault.IsConfigured)
        {
            MessageBox.Show("请先在「设置 → 密码保护」里设置主密码。", "FineClipboard",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        _suppressHide = true;
        try
        {
            while (true)
            {
                var dialog = new MasterPasswordDialog("解锁密码", "输入主密码以查看 / 粘贴密码:") { Owner = this };
                if (dialog.ShowDialog() != true)
                {
                    return false;
                }
                if (_app.Vault.Unlock(dialog.Password))
                {
                    return true;
                }
                MessageBox.Show("主密码不正确。", "FineClipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _suppressHide = false;
        }
    }

    // ---- Pin / delete ----
    private void PinSelected()
    {
        if (ItemsList.SelectedItem is PopupItemVm vm && vm.Item != null)
        {
            _store.TogglePin(vm.Item.Id);
            LoadData();
            ApplyFilter();
        }
    }

    private void DeleteSelected()
    {
        if (ItemsList.SelectedItem is not PopupItemVm vm)
        {
            return;
        }
        int index = ItemsList.SelectedIndex;
        if (vm.IsPassword)
        {
            _app.Vault.DeleteEntry(vm.Password!.Id);
        }
        else if (vm.IsSnippet)
        {
            _store.DeleteSnippet(vm.Snippet!.Id);
        }
        else if (vm.Item != null)
        {
            _store.Delete(vm.Item.Id);
        }
        LoadData();
        ApplyFilter();
        if (ItemsList.Items.Count > 0)
        {
            ItemsList.SelectedIndex = Math.Clamp(index, 0, ItemsList.Items.Count - 1);
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

    private void ItemMenu_Opened(object sender, RoutedEventArgs e)
    {
        _contextMenuOpen = true;

        var vm = ItemsList.SelectedItem as PopupItemVm;
        bool isSnippet = vm?.IsSnippet == true;
        ClipItemType? type = vm?.Item?.Type;
        bool isText = isSnippet || type == ClipItemType.Text;
        bool isClip = vm?.Item != null;

        foreach (object obj in ((ContextMenu)sender).Items)
        {
            if (obj is not MenuItem mi || mi.Tag is not string tag)
            {
                continue;
            }
            mi.Visibility = tag switch
            {
                "pasteplain" => Vis(type == ClipItemType.Text || type == ClipItemType.Files),
                "edit" => Vis(isText),
                "transform" => Vis(isText),
                "movelist" => Vis(isClip),
                "copy" => Vis(isClip),
                "openurl" => Vis(isText && LooksLikeUrl(vm?.PasteText)),
                "locate" => Vis(type == ClipItemType.Files),
                "saveimage" => Vis(type == ClipItemType.Image),
                "pin" => Vis(isClip),
                "delete" => Vis(vm != null),
                _ => mi.Visibility,
            };
            if (tag == "movelist" && isClip)
            {
                PopulateMoveList(mi, vm!.Item!);
            }
        }
    }

    private void PopulateMoveList(MenuItem parent, ClipboardItem item)
    {
        parent.Items.Clear();
        foreach (ClipList list in _store.GetLists())
        {
            long listId = list.Id;
            var sub = new MenuItem { Header = list.Name };
            sub.Click += (_, _) => AssignTo(item, listId);
            parent.Items.Add(sub);
        }
        parent.Items.Add(new Separator());
        var create = new MenuItem { Header = "新建并加入…" };
        create.Click += (_, _) => CreateListAndAssign(item);
        parent.Items.Add(create);
        var remove = new MenuItem { Header = "移出列表" };
        remove.Click += (_, _) => AssignTo(item, null);
        parent.Items.Add(remove);
    }

    private void AssignTo(ClipboardItem item, long? listId)
    {
        _store.AssignToList(item.Id, listId);
        LoadData();
        ApplyFilter();
    }

    private void CreateListAndAssign(ClipboardItem item)
    {
        _suppressHide = true;
        try
        {
            var dialog = new InputDialog("新建列表", "列表名称:") { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Text.Length > 0)
            {
                long id = _store.AddList(dialog.Text);
                _store.AssignToList(item.Id, id);
                RefreshTabs();
                LoadData();
                ApplyFilter();
            }
        }
        finally
        {
            _suppressHide = false;
        }
    }

    private void ItemMenu_Closed(object sender, RoutedEventArgs e) => _contextMenuOpen = false;

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private static bool LooksLikeUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        s = s.Trim();
        return (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
               !s.Contains(' ') && !s.Contains('\n');
    }

    private void MenuPaste_Click(object sender, RoutedEventArgs e) => PasteSelected(plainText: false);
    private void MenuPastePlain_Click(object sender, RoutedEventArgs e) => PasteSelected(plainText: true);
    private void MenuPin_Click(object sender, RoutedEventArgs e) => PinSelected();
    private void MenuDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is PopupItemVm vm && vm.Item != null)
        {
            Hide();
            _app.CopyItem(vm.Item);
        }
    }

    private void MenuOpenUrl_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is PopupItemVm { PasteText: { } url })
        {
            Hide();
            TryRun(() => Process.Start(new ProcessStartInfo(url.Trim()) { UseShellExecute = true }));
        }
    }

    private void MenuLocate_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is PopupItemVm vm && vm.Item?.FilePaths.FirstOrDefault() is { } path)
        {
            Hide();
            TryRun(() => Process.Start("explorer.exe", $"/select,\"{path}\""));
        }
    }

    private void MenuSaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not PopupItemVm vm || vm.Item?.ImageData is not { } bytes)
        {
            return;
        }
        _suppressHide = true;
        try
        {
            var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "clipboard.png" };
            if (dialog.ShowDialog(this) == true)
            {
                TryRun(() => File.WriteAllBytes(dialog.FileName, bytes));
            }
        }
        finally
        {
            _suppressHide = false;
        }
        Activate();
    }

    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not PopupItemVm { PasteText: { } text })
        {
            return;
        }
        _suppressHide = true;
        bool ok;
        var editor = new EditWindow(text) { Owner = this };
        try
        {
            ok = editor.ShowDialog() == true;
        }
        finally
        {
            _suppressHide = false;
        }
        if (ok)
        {
            Hide();
            _app.PasteText(editor.ResultText);
        }
    }

    private void MenuTrimSpace_Click(object sender, RoutedEventArgs e) => TransformSelected(t => t.Trim());
    private void MenuSingleLine_Click(object sender, RoutedEventArgs e) =>
        TransformSelected(t => string.Join(" ", t.Split('\n').Select(l => l.Trim('\r', ' ', '\t')).Where(l => l.Length > 0)));
    private void MenuUpper_Click(object sender, RoutedEventArgs e) => TransformSelected(t => t.ToUpperInvariant());
    private void MenuLower_Click(object sender, RoutedEventArgs e) => TransformSelected(t => t.ToLowerInvariant());

    private void TransformSelected(Func<string, string> transform)
    {
        if (ItemsList.SelectedItem is PopupItemVm { PasteText: { } text })
        {
            Hide();
            _app.PasteText(transform(text));
        }
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // best effort
        }
    }
}
