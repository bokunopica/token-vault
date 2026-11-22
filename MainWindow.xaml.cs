using System.Windows;
using System.Windows.Controls;
using TokenVault.Services;
using TokenVault.ViewModels;

namespace TokenVault;

public partial class MainWindow : Window
{
    private readonly VaultStore _vault = App.Vault;
    private readonly BalanceViewModel _balanceVm;
    private readonly KeysViewModel _keysVm;
    private readonly TrashViewModel _trashVm;
    private readonly Views.BalanceView _balanceView;
    private readonly Views.KeysView _keysView;
    private readonly Views.TrashView _trashView;
    private IdleWatcher? _idle;
    private bool _suppressIdleUi;

    public MainWindow()
    {
        InitializeComponent();
        _balanceVm = new BalanceViewModel(_vault);
        _keysVm = new KeysViewModel(_vault);
        _trashVm = new TrashViewModel(_vault);
        _balanceView = new Views.BalanceView(_balanceVm);
        _keysView = new Views.KeysView(_keysVm);
        _trashView = new Views.TrashView(_trashVm);
        _vault.Changed += OnVaultChanged;
        ShowPage("balance");

        // 空闲自动锁定：下拉框与保险库里的设置双向同步，检测到空闲即锁定
        _suppressIdleUi = true;
        var minutes = Math.Max(0, _vault.Data.Settings.IdleLockMinutes);
        IdleLockBox.SelectedItem = IdleLockBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == minutes.ToString()) ?? IdleLockBox.Items[1];
        _suppressIdleUi = false;

        _idle = new IdleWatcher(TimeSpan.FromMinutes(minutes));
        _idle.IdleTimedOut += OnIdleTimedOut;
        UpdateLockState();
        Loaded += (_, _) => _idle?.Start();
        Closed += (_, _) => _idle?.Stop();
    }

    private void OnVaultChanged()
        => Dispatcher.Invoke(() =>
        {
            _balanceVm.ReloadSettings();
            _keysVm.Reload();
            _trashVm.Reload();
        });

    private void Nav_Click(object sender, RoutedEventArgs e)
        => ShowPage((string)((Button)sender).Tag!);

    internal void ShowPage(string page)
    {
        NavBalance.Style = (Style)FindResource(page == "balance" ? "NavBtnActive" : "NavBtn");
        NavKeys.Style = (Style)FindResource(page == "keys" ? "NavBtnActive" : "NavBtn");
        NavTrash.Style = (Style)FindResource(page == "trash" ? "NavBtnActive" : "NavBtn");

        switch (page)
        {
            case "keys":
                PageTitle.Text = "密钥管理";
                PageSubtitle.Text = "查看 / 添加 / 修改 / 删除密钥；修改与删除均需二次确认，并进入临时区保留 7 天";
                _keysVm.Reload();
                PageHost.Content = _keysView;
                break;
            case "trash":
                PageTitle.Text = "临时区";
                PageSubtitle.Text = "被删除的密钥与修改前的旧版本在此保留 7 天，可随时恢复或提前彻底删除";
                _trashVm.Reload();
                PageHost.Content = _trashView;
                break;
            default:
                PageTitle.Text = "余量查询";
                PageSubtitle.Text = "MiMo Token Plan 使用程度与 DeepSeek 余额，均为官方接口实时查询";
                PageHost.Content = _balanceView;
                break;
        }
    }

    private void BtnLock_Click(object sender, RoutedEventArgs e) => LockAndShowLogin("手动锁定");

    private void UpdateLockState()
    {
        var m = _vault.Data.Settings.IdleLockMinutes;
        LockState.Text = m > 0
            ? $"保险库已解锁 · 空闲 {m} 分钟后自动锁定"
            : "保险库已解锁 · 数据仅存本机";
    }

    private void IdleLockBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressIdleUi || _idle == null || IdleLockBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out var minutes)) return;

        _vault.Data.Settings.IdleLockMinutes = minutes;
        try { _vault.Save(); } catch (Exception ex) { Diag.Log("save idle-lock setting failed: " + ex.Message); }
        _idle.IdleTimeout = TimeSpan.FromMinutes(minutes);
        UpdateLockState();
    }

    /// <summary>空闲到点：只有没有打开其它对话框时才锁定，避免打断正在进行的编辑。</summary>
    private void OnIdleTimedOut()
    {
        if (!_vault.IsUnlocked) return;
        var otherWindowOpen = Application.Current.Windows.Cast<Window>().Any(w => w != this && w.IsVisible);
        if (otherWindowOpen) return;
        LockAndShowLogin("空闲自动锁定");
    }

    private void LockAndShowLogin(string reason)
    {
        Diag.Log("lock: " + reason);
        ClipboardGuard.ClearNow();
        _idle?.Stop();
        _vault.Changed -= OnVaultChanged;
        _vault.Lock();
        var login = new LoginWindow();
        login.LoginSucceeded += (_, _) =>
        {
            var main = new MainWindow();
            Application.Current.MainWindow = main;
            main.Show();
        };
        login.Show();
        Close();
    }

    private void BtnChangePwd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.ChangePasswordWindow { Owner = this };
        dlg.ShowDialog();
    }
}