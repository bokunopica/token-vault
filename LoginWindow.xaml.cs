using System.IO;
using System.Windows;
using System.Windows.Input;
using TokenVault.Services;

namespace TokenVault;

public partial class LoginWindow : Window
{
    private readonly VaultStore _vault = App.Vault;
    private bool _firstRun;

    public event EventHandler? LoginSucceeded;

    public LoginWindow()
    {
        InitializeComponent();
        ApplyMode(firstRun: !_vault.Exists, afterReset: false);
        Loaded += (_, _) => PwdBox.Focus();
    }

    /// <summary>当前输入框中的密码（“显示密码”模式下取 TextBox）。</summary>
    private string Pwd => ShowPwd.IsChecked == true ? PwdBoxVisible.Text : PwdBox.Password;
    private string Pwd2 => ShowPwd.IsChecked == true ? PwdConfirmVisible.Text : PwdConfirm.Password;

    private void ApplyMode(bool firstRun, bool afterReset)
    {
        _firstRun = firstRun;
        if (firstRun)
        {
            ModeTitle.Text = afterReset ? "保险库已重置 · 请设置新的主密码" : "首次使用 · 设置主密码";
            ModeHint.Text = afterReset
                ? "旧保险库文件已备份到同目录（vault.json.bak-*），可随时找回。请设置新的主密码（至少 6 位）。"
                : "主密码用于加密本机保险库（AES-256-GCM），所有密钥数据仅保存在本机。请设置至少 6 位的密码。";
            ConfirmPanel.Visibility = Visibility.Visible;
            SubmitBtn.Content = "创建保险库并进入";
            ForgotBtn.Visibility = Visibility.Collapsed;
            Footnote.Text = "忘记主密码将无法解密已保存的密钥，请妥善保管。";
        }
        else
        {
            ModeTitle.Text = "输入主密码以解锁";
            ModeHint.Text = "保险库使用 AES-256-GCM 加密存储在本机。";
            ConfirmPanel.Visibility = Visibility.Collapsed;
            SubmitBtn.Content = "解锁";
            ForgotBtn.Visibility = Visibility.Visible;
            Footnote.Text = "忘记主密码无法找回，请妥善保管。";
        }
        PwdBox.Clear();
        PwdBoxVisible.Clear();
        PwdConfirm.Clear();
        PwdConfirmVisible.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        ShowPwd.IsChecked = false;
    }

    private void PwdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Submit_Click(sender, e);
    }

    private void ShowPwd_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowPwd.IsChecked == true)
        {
            PwdBoxVisible.Text = PwdBox.Password;
            PwdConfirmVisible.Text = PwdConfirm.Password;
            PwdBox.Visibility = Visibility.Collapsed;
            PwdBoxVisible.Visibility = Visibility.Visible;
            PwdConfirm.Visibility = Visibility.Collapsed;
            PwdConfirmVisible.Visibility = Visibility.Visible;
            PwdBoxVisible.Focus();
            PwdBoxVisible.CaretIndex = PwdBoxVisible.Text.Length;
        }
        else
        {
            PwdBox.Password = PwdBoxVisible.Text;
            PwdConfirm.Password = PwdConfirmVisible.Text;
            PwdBox.Visibility = Visibility.Visible;
            PwdBoxVisible.Visibility = Visibility.Collapsed;
            PwdConfirm.Visibility = Visibility.Visible;
            PwdConfirmVisible.Visibility = Visibility.Collapsed;
            PwdBox.Focus();
        }
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var pwd = Pwd;
        try
        {
            if (_firstRun)
            {
                if (pwd.Length < 6)
                {
                    ShowError("主密码至少 6 位。");
                    return;
                }
                if (pwd != Pwd2)
                {
                    ShowError("两次输入的密码不一致。");
                    return;
                }
                _vault.Create(pwd);
                Diag.Log("vault created: " + _vault.FilePath);
            }
            else
            {
                if (pwd.Length == 0)
                {
                    ShowError("请输入主密码。");
                    return;
                }
                Diag.Log("unlock attempt");
                _vault.Unlock(pwd);
                Diag.Log("unlock OK");
            }
            LoginSucceeded?.Invoke(this, EventArgs.Empty);
            Close();
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            Diag.Log(_firstRun ? "create failed: crypto" : "unlock failed: GCM auth (wrong password)");
            string saved = "";
            try
            {
                if (_vault.Exists)
                    saved = "保险库最后保存于 " + File.GetLastWriteTime(_vault.FilePath).ToString("yyyy-MM-dd HH:mm:ss") + "。";
            }
            catch { }
            ShowError("主密码错误（或保险库文件已损坏）。" + (saved.Length > 0 ? "\n" + saved : "")
                      + "\n可勾选“显示密码”核对输入，或点下方“忘记主密码？重置保险库”。");
            PwdBox.Clear();
            PwdBoxVisible.Clear();
            PwdBox.Focus();
        }
        catch (Exception ex)
        {
            Diag.Log("unlock/create failed: " + ex);
            ShowError("无法打开保险库：" + ex.Message);
        }
    }

    private void Forgot_Click(object sender, RoutedEventArgs e)
    {
        var confirm = new Views.ConfirmWindow(
            "重置保险库",
            "将把现有保险库文件备份到同目录（vault.json.bak-时间戳），然后重新设置主密码。\n\n"
            + "现有保险库内的密钥需要在新的保险库中重新录入（备份文件不会被删除）。",
            "重置保险库",
            typedConfirmation: "重置") { Owner = this };
        if (confirm.ShowDialog() != true) return;

        try
        {
            _vault.Lock();
            var bak = _vault.FilePath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(_vault.FilePath, bak, overwrite: true);
            Diag.Log("vault reset by user; backup=" + bak);
            ApplyMode(firstRun: true, afterReset: true);
        }
        catch (Exception ex)
        {
            ShowError("重置失败：" + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
