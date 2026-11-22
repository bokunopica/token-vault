using System.Collections.ObjectModel;
using System.Windows.Threading;
using TokenVault.Infrastructure;
using TokenVault.Models;
using TokenVault.Services;

namespace TokenVault.ViewModels;

public class BalanceViewModel : ObservableObject
{
    private readonly VaultStore _vault;
    private readonly BalanceService _balance = new();
    private readonly DispatcherTimer _timer;

    private bool _isBusy;
    private string _lastUpdated = "尚未查询";

    // ── MiMo ───────────────────────────────────────────────────────────────
    private string _mimoStatus = "选择密钥后点击“查询”";
    private bool _mimoHasData;
    private string _mimoPlanCode = "";
    private string _mimoPeriod = "";
    private string _mimoUsed = "–";
    private string _mimoTotal = "–";
    private string _mimoRemaining = "–";
    private string _mimoPct = "";
    private double _mimoProgress;
    private string _mimoAutoRenew = "";
    private string _mimoCons = "消耗估算：暂无快照数据";
    private KeyEntry? _selectedMimoKey;

    // ── DeepSeek ───────────────────────────────────────────────────────────
    private string _dsStatus = "选择密钥后点击“查询”";
    private bool _dsHasData;
    private string _dsTotal = "–";
    private string _dsGranted = "–";
    private string _dsTopped = "–";
    private string _dsAvailable = "";
    private string _dsCons = "消耗估算：暂无快照数据";
    private KeyEntry? _selectedDsKey;

    public BalanceViewModel(VaultStore vault)
    {
        _vault = vault;
        RefreshKeyLists();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, vault.Data.Settings.AutoRefreshMinutes)) };
        _timer.Tick += async (_, _) => { if (!IsBusy && AutoRefresh) await RefreshAllAsync(); };
        if (vault.Data.Settings.AutoRefresh) _timer.Start();
    }

    public void ReloadSettings()
    {
        RefreshKeyLists();
        OnPropertyChanged(nameof(AutoRefresh));
        _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, _vault.Data.Settings.AutoRefreshMinutes));
    }

    private void RefreshKeyLists()
    {
        // 下拉列出所有“有可用凭据”的条目：优先同类别的排前面，
        // 这样即使添加时类别选错、或 key 填在“密码”字段里，也能直接选中查询。
        MimoKeys.Clear();
        foreach (var e in _vault.Data.Entries
                     .Where(e => !string.IsNullOrEmpty(e.PrimarySecret))
                     .OrderByDescending(e => e.Category == KeyCategories.MimoPlan)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            MimoKeys.Add(e);

        DsKeys.Clear();
        foreach (var e in _vault.Data.Entries
                     .Where(e => !string.IsNullOrEmpty(e.PrimarySecret))
                     .OrderByDescending(e => e.Category == KeyCategories.DeepSeek)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            DsKeys.Add(e);

        SelectedMimoKey = MimoKeys.FirstOrDefault(e => e.Id == _vault.Data.Settings.MimoKeyId) ?? MimoKeys.FirstOrDefault();
        SelectedDsKey = DsKeys.FirstOrDefault(e => e.Id == _vault.Data.Settings.DeepseekKeyId) ?? DsKeys.FirstOrDefault();
    }

    public ObservableCollection<KeyEntry> MimoKeys { get; } = new();
    public ObservableCollection<KeyEntry> DsKeys { get; } = new();

    public KeyEntry? SelectedMimoKey
    {
        get => _selectedMimoKey;
        set
        {
            if (SetProperty(ref _selectedMimoKey, value) && value != null)
            {
                _vault.Data.Settings.MimoKeyId = value.Id;
                _vault.Save();
            }
        }
    }

    public KeyEntry? SelectedDsKey
    {
        get => _selectedDsKey;
        set
        {
            if (SetProperty(ref _selectedDsKey, value) && value != null)
            {
                _vault.Data.Settings.DeepseekKeyId = value.Id;
                _vault.Save();
            }
        }
    }

    public bool AutoRefresh
    {
        get => _vault.Data.Settings.AutoRefresh;
        set
        {
            _vault.Data.Settings.AutoRefresh = value;
            _vault.Save();
            if (value) _timer.Start(); else _timer.Stop();
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string LastUpdated
    {
        get => _lastUpdated;
        private set => SetProperty(ref _lastUpdated, value);
    }

    // MiMo 绑定
    public string MimoStatus { get => _mimoStatus; private set => SetProperty(ref _mimoStatus, value); }
    public bool MimoHasData { get => _mimoHasData; private set => SetProperty(ref _mimoHasData, value); }
    public string MimoPlanCode { get => _mimoPlanCode; private set => SetProperty(ref _mimoPlanCode, value); }
    public string MimoPeriod { get => _mimoPeriod; private set => SetProperty(ref _mimoPeriod, value); }
    public string MimoUsed { get => _mimoUsed; private set => SetProperty(ref _mimoUsed, value); }
    public string MimoTotal { get => _mimoTotal; private set => SetProperty(ref _mimoTotal, value); }
    public string MimoRemaining { get => _mimoRemaining; private set => SetProperty(ref _mimoRemaining, value); }
    public string MimoPct { get => _mimoPct; private set => SetProperty(ref _mimoPct, value); }
    public double MimoProgress { get => _mimoProgress; set => SetProperty(ref _mimoProgress, value); }
    public string MimoAutoRenew { get => _mimoAutoRenew; private set => SetProperty(ref _mimoAutoRenew, value); }
    public string MimoCons { get => _mimoCons; private set => SetProperty(ref _mimoCons, value); }

    // DeepSeek 绑定
    public string DsStatus { get => _dsStatus; private set => SetProperty(ref _dsStatus, value); }
    public bool DsHasData { get => _dsHasData; private set => SetProperty(ref _dsHasData, value); }
    public string DsTotal { get => _dsTotal; private set => SetProperty(ref _dsTotal, value); }
    public string DsGranted { get => _dsGranted; private set => SetProperty(ref _dsGranted, value); }
    public string DsTopped { get => _dsTopped; private set => SetProperty(ref _dsTopped, value); }
    public string DsAvailable { get => _dsAvailable; private set => SetProperty(ref _dsAvailable, value); }
    public string DsCons { get => _dsCons; private set => SetProperty(ref _dsCons, value); }

    public RelayCommand RefreshAllCommand => new(async () => await RefreshAllAsync());
    public RelayCommand QueryMimoCommand => new(async () => await QueryMimoAsync());
    public RelayCommand QueryDsCommand => new(async () => await QueryDsAsync());

    public async Task RefreshAllAsync()
    {
        await QueryMimoAsync();
        await QueryDsAsync();
        LastUpdated = "最近查询：" + DateTime.Now.ToString("HH:mm:ss");
    }

    public async Task QueryMimoAsync()
    {
        var key = SelectedMimoKey;
        if (key == null)
        {
            MimoStatus = "没有可用凭据（请先在“密钥管理”中添加，并填写密码或 API Key）";
            MimoHasData = false;
            return;
        }
        IsBusy = true;
        try
        {
            var plan = await _balance.GetMimoPlanAsync(key.PrimarySecret, key.BaseUrl);
            _balance.RecordSnapshot(plan, null);
            MimoPlanCode = plan.PlanCode ?? "-";
            MimoPeriod = plan.PeriodStart is { } s && plan.PeriodEnd is { } e
                ? $"周期 {s:yyyy-MM-dd} ~ {e:yyyy-MM-dd}" : "";
            MimoUsed = plan.UsedCredits?.ToString("N0") ?? "–";
            MimoTotal = plan.TotalCredits?.ToString("N0") ?? "–";
            MimoRemaining = plan.RemainingCredits?.ToString("N0") ?? "–";
            MimoPct = plan.UsedPct is { } pct ? $"已用 {pct}%" : "";
            MimoProgress = plan.UsedPct ?? 0;
            MimoAutoRenew = plan.AutoRenew ? "自动续订：开" : "自动续订：关";
            MimoCons = ConsText(_balance.GetConsumption(forMimo: true));
            MimoHasData = true;
            MimoStatus = "查询成功 " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            MimoHasData = false;
            MimoStatus = "查询失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task QueryDsAsync()
    {
        var key = SelectedDsKey;
        if (key == null)
        {
            DsStatus = "没有可用凭据（请先在“密钥管理”中添加，并填写密码或 API Key）";
            DsHasData = false;
            return;
        }
        IsBusy = true;
        try
        {
            var bal = await _balance.GetDeepseekBalanceAsync(key.PrimarySecret, key.BaseUrl);
            _balance.RecordSnapshot(null, bal);
            DsTotal = bal.TotalBalance?.ToString("N2") ?? "–";
            DsGranted = bal.GrantedBalance?.ToString("N2") ?? "–";
            DsTopped = bal.ToppedUpBalance?.ToString("N2") ?? "–";
            DsAvailable = bal.IsAvailable ? "余额可用" : "余额不足/不可用";
            DsCons = ConsText(_balance.GetConsumption(forMimo: false));
            DsHasData = true;
            DsStatus = "查询成功 " + DateTime.Now.ToString("HH:mm:ss") + $"（{bal.Currency}）";
        }
        catch (Exception ex)
        {
            DsHasData = false;
            DsStatus = "查询失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string ConsText(ConsumptionInfo c)
    {
        if (c.Since == null) return "消耗估算：暂无快照数据（自首次查询起自动累计）";
        return $"消耗估算　今日 {c.Day:N0}　本周 {c.Week:N0}　本月 {c.Month:N0}　（自 {c.Since:MM-dd} 起按快照增量估算）";
    }
}
