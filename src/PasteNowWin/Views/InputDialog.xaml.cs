using System.Windows;

namespace PasteNowWin.Views;

public partial class InputDialog : Window
{
    public string Text => Box.Text.Trim();

    public InputDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        Box.Text = initial;
        Loaded += (_, _) =>
        {
            Box.Focus();
            Box.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
