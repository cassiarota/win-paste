using System.Windows;
using FineClipboard.Services;

namespace FineClipboard.Views;

public partial class SetMasterPasswordWindow : Window
{
    private readonly PasswordVault _vault;

    public SetMasterPasswordWindow(PasswordVault vault)
    {
        InitializeComponent();
        _vault = vault;
        Icon = AppIconFactory.CreateImageSource();

        if (vault.IsConfigured)
        {
            Title = "修改主密码";
            OldPanel.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => (vault.IsConfigured ? OldBox : NewBox).Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string next = NewBox.Password;
        if (next.Length < 4)
        {
            Warn("主密码至少需要 4 位。");
            return;
        }
        if (next != ConfirmBox.Password)
        {
            Warn("两次输入的新主密码不一致。");
            return;
        }

        if (_vault.IsConfigured)
        {
            if (!_vault.ChangeMasterPassword(OldBox.Password, next))
            {
                Warn("当前主密码不正确。");
                return;
            }
        }
        else
        {
            _vault.SetMasterPassword(next);
        }

        DialogResult = true;
    }

    private static void Warn(string message) =>
        MessageBox.Show(message, "FineClipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
}
