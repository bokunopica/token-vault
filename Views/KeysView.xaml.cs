using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TokenVault.Services;
using TokenVault.ViewModels;

namespace TokenVault.Views;

public partial class KeysView : UserControl
{
    private readonly KeysViewModel _vm;

    public KeysView(KeysViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => _vm.Add();

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is KeyRow row) _vm.Edit(row);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is KeyRow row) _vm.Delete(row);
    }

    private void RevealPassword_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is KeyRow row) row.IsPasswordRevealed = !row.IsPasswordRevealed;
    }

    private void RevealApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is KeyRow row) row.IsApiKeyRevealed = !row.IsApiKeyRevealed;
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is KeyRow row) _vm.CopyPassword(row);
    }

    private void CopyApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is KeyRow row) _vm.CopyApiKey(row);
    }

    private void Url_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.DataContext is KeyRow row
            && UrlHelper.TryNormalize(row.Entry.BaseUrl, out var url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                _vm.Report($"已在浏览器打开：{url}");
            }
            catch (Exception ex)
            {
                _vm.Report("打开链接失败：" + ex.Message);
            }
        }
    }
}
