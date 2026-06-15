using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PasteNowWin.Models;
using PasteNowWin.Services;

namespace PasteNowWin.Views;

public partial class ListsWindow : Window
{
    private readonly HistoryStore _store;

    public ListsWindow(HistoryStore store)
    {
        InitializeComponent();
        _store = store;
        Reload(0);
    }

    private void Reload(long selectId)
    {
        List.ItemsSource = _store.GetLists();
        if (selectId != 0 && List.ItemsSource is List<ClipList> items)
        {
            List.SelectedItem = items.FirstOrDefault(l => l.Id == selectId);
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("新建列表", "列表名称:") { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Text.Length > 0)
        {
            Reload(_store.AddList(dialog.Text));
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ClipList list)
        {
            return;
        }
        var dialog = new InputDialog("重命名列表", "列表名称:", list.Name) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Text.Length > 0)
        {
            _store.RenameList(list.Id, dialog.Text);
            Reload(list.Id);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ClipList list)
        {
            return;
        }
        if (MessageBox.Show($"删除列表「{list.Name}」?(其中的条目会移出列表,但不会被删除)",
                "PasteNowWin", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            _store.DeleteList(list.Id);
            Reload(0);
        }
    }
}
