using System;
using System.Threading.Tasks;
using System.Windows;
using FineClipboard.Services;

namespace FineClipboard.Views;

public partial class SyncWindow : Window
{
    private readonly SyncEngine _sync;

    public SyncWindow(SyncEngine sync)
    {
        InitializeComponent();
        Icon = AppIconFactory.CreateImageSource();
        _sync = sync;
        ServerBox.Text = _sync.BaseUrl;
        EmailBox.Text = _sync.Email ?? string.Empty;
        EnableBox.IsChecked = _sync.Enabled;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        string login = _sync.LoggedIn ? $"已登录:{_sync.Email}" : "未登录";
        string phrase = _sync.HasPassphrase ? "已设置同步口令" : "未设置同步口令";
        string enabled = _sync.Enabled ? "自动同步:开" : "自动同步:关";
        StatusText.Text = $"{login} · {phrase} · {enabled}";
    }

    private async void Login_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            _sync.SetBaseUrl(ServerBox.Text);
            await _sync.LoginAsync(EmailBox.Text, PassBox.Password);
            return "登录成功";
        });

    private async void Register_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            _sync.SetBaseUrl(ServerBox.Text);
            await _sync.RegisterAsync(EmailBox.Text, PassBox.Password);
            return "注册并登录成功";
        });

    private async void Redeem_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(async () =>
        {
            bool vip = await _sync.RedeemAsync(KeyBox.Text);
            return vip ? "激活成功,已获得 VIP" : "激活码无效";
        });

    private void SetPhrase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(PhraseBox.Password))
            {
                StatusText.Text = "请输入同步口令";
                return;
            }
            _sync.SetPassphrase(PhraseBox.Password);
            StatusText.Text = "同步口令已设置";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        _sync.Enable(EnableBox.IsChecked == true);
        RefreshStatus();
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => _sync.SyncNowAsync());

    private async Task RunAsync(Func<Task<string>> action)
    {
        StatusText.Text = "处理中…";
        try
        {
            string msg = await action();
            StatusText.Text = msg;
        }
        catch (SyncException ex)
        {
            StatusText.Text = "失败:" + ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = "出错:" + ex.Message;
        }
        RefreshStatus();
    }
}
