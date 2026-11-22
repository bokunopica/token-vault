using System.Windows;

namespace TokenVault.Views;

public partial class ChangePasswordWindow : Window
{
    public ChangePasswordWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PwCurrent.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (PwNew.Password.Length < 6)
        {
            ShowError("新主密码至少 6 位。");
            return;
        }
        if (PwNew.Password != PwConfirm.Password)
        {
            ShowError("两次输入的新密码不一致。");
            return;
        }
        try
        {
            App.Vault.ChangePassword(PwCurrent.Password, PwNew.Password);
            MessageBox.Show(this, "主密码已修改。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            ShowError("当前主密码错误。");
        }
        catch (Exception ex)
        {
            ShowError("修改失败：" + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
