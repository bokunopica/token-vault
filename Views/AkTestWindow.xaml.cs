using System.Windows;
using System.Windows.Media;
using TokenVault.Services;

namespace TokenVault.Views;

/// <summary>
/// 一次性校验 AK/SK：不进保险库、不覆盖任何数据，直接把粘贴的值拿去调 STS GetCallerIdentity。
/// 用来区分「值本身不对」和「保存到保险库时出错」。
/// </summary>
public partial class AkTestWindow : Window
{
    private readonly AlibabaAkService _svc = new();

    public string? ResultAk { get; private set; }
    public string? ResultSk { get; private set; }
    public bool SaveRequested { get; private set; }

    public AkTestWindow(string? ak, string? sk)
    {
        InitializeComponent();
        AkBox.Text = ak ?? "";
        SkBox.Password = sk ?? "";
        SkVisible.Text = sk ?? "";
        Loaded += (_, _) => AkBox.Focus();
    }

    private string SkValue => ShowSk.IsChecked == true ? SkVisible.Text : SkBox.Password;

    private void ShowSk_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowSk.IsChecked == true)
        {
            SkVisible.Text = SkBox.Password;
            SkBox.Visibility = Visibility.Collapsed;
            SkVisible.Visibility = Visibility.Visible;
            SkVisible.CaretIndex = SkVisible.Text.Length;
        }
        else
        {
            SkBox.Password = SkVisible.Text;
            SkBox.Visibility = Visibility.Visible;
            SkVisible.Visibility = Visibility.Collapsed;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var ak = AkBox.Text.Trim();
        var sk = SkValue.Trim();
        if (ak.Length == 0 || sk.Length == 0)
        {
            Show("请把 AccessKey ID 和 Secret 都填上。", false);
            return;
        }

        TestBtn.IsEnabled = false;
        SaveBtn.IsEnabled = false;
        Show($"测试中…（AK {ak.Length} 位，SK {sk.Length} 位）", true);
        try
        {
            var msg = await _svc.CheckCredentialsAsync(ak, sk);
            Show(msg, true);
            ResultAk = ak;
            ResultSk = sk;
            SaveBtn.IsEnabled = true;
            Diag.Log("ak test ok: " + msg);
        }
        catch (Exception ex)
        {
            Show("失败：" + ex.Message, false);
            Diag.Log("ak test failed: " + ex.Message);
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private void Show(string text, bool ok)
    {
        ResultText.Text = text;
        ResultText.Foreground = (Brush)FindResource(ok ? "OkBrush" : "DangerBrush");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ResultAk == null || ResultSk == null) return;
        SaveRequested = true;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
