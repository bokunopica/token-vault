using System.Collections.ObjectModel;
using System.Windows;
using TokenVault.Infrastructure;
using TokenVault.Models;
using TokenVault.Services;

namespace TokenVault.ViewModels;

/// <summary>凭据列表的一行（密码与 API Key 独立掩码/显示）。</summary>
public class KeyRow : ObservableObject
{
    private bool _pwdRevealed;
    private bool _keyRevealed;

    public KeyRow(KeyEntry entry) => Entry = entry;

    public KeyEntry Entry { get; }
    public string Name => Entry.Name;
    public string UsernameDisplay => Entry.Username;
    public bool HasUsername => !string.IsNullOrEmpty(Entry.Username);
    public string CategoryDisplay => KeyCategories.DisplayName(Entry.Category);
    public string UrlDisplay => Entry.BaseUrl;
    public bool HasClickableUrl => UrlHelper.TryNormalize(Entry.BaseUrl, out _);
    public string UpdatedAtDisplay => Entry.UpdatedAt.ToString("yyyy-MM-dd HH:mm");

    public bool HasPassword => !string.IsNullOrEmpty(Entry.Password);
    public bool HasApiKey => !string.IsNullOrEmpty(Entry.ApiKey);

    public bool IsPasswordRevealed
    {
        get => _pwdRevealed;
        set { if (SetProperty(ref _pwdRevealed, value)) OnPropertyChanged(nameof(PasswordDisplay)); }
    }

    public bool IsApiKeyRevealed
    {
        get => _keyRevealed;
        set { if (SetProperty(ref _keyRevealed, value)) OnPropertyChanged(nameof(ApiKeyDisplay)); }
    }

    public string PasswordDisplay => string.IsNullOrEmpty(Entry.Password)
        ? "—" : (_pwdRevealed ? Entry.Password : Mask(Entry.Password));

    public string ApiKeyDisplay => string.IsNullOrEmpty(Entry.ApiKey)
        ? "—" : (_keyRevealed ? Entry.ApiKey : Mask(Entry.ApiKey));

    public static string Mask(string s)
        => s.Length <= 8 ? new string('•', s.Length) : s[..3] + new string('•', Math.Min(16, s.Length - 7)) + s[^4..];

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(UsernameDisplay));
        OnPropertyChanged(nameof(HasUsername));
        OnPropertyChanged(nameof(CategoryDisplay));
        OnPropertyChanged(nameof(UrlDisplay));
        OnPropertyChanged(nameof(HasClickableUrl));
        OnPropertyChanged(nameof(UpdatedAtDisplay));
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(PasswordDisplay));
        OnPropertyChanged(nameof(ApiKeyDisplay));
    }
}

public class KeysViewModel : ObservableObject
{
    private readonly VaultStore _vault;
    private KeyRow? _selected;
    private string _statusText = "";

    public KeysViewModel(VaultStore vault)
    {
        _vault = vault;
        Reload();
    }

    public ObservableCollection<KeyRow> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;

    public KeyRow? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void Report(string message) => StatusText = message;

    public void Reload()
    {
        Rows.Clear();
        foreach (var e in _vault.Data.Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(new KeyRow(e));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Add()
    {
        var dlg = new Views.KeyEditWindow(null) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _vault.AddEntry(dlg.Result);
            Reload();
            StatusText = $"已添加「{dlg.Result.Name}」";
        }
    }

    /// <summary>修改 — 二次确认后，旧版本进临时区。</summary>
    public void Edit(KeyRow row)
    {
        var dlg = new Views.KeyEditWindow(row.Entry.Clone()) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        var confirm = new Views.ConfirmWindow(
            "确认修改",
            $"即将修改凭据「{row.Name}」。\n\n修改前的旧版本会放入临时区，7 天后自动彻底删除（期间可随时恢复）。",
            "确认修改") { Owner = Application.Current.MainWindow };
        if (confirm.ShowDialog() != true) return;

        _vault.UpdateEntry(dlg.Result);
        Reload();
    }

    /// <summary>删除 — 二次确认后进临时区，7 天后才彻底删除。</summary>
    public void Delete(KeyRow row)
    {
        var confirm = new Views.ConfirmWindow(
            "确认删除",
            $"即将删除凭据「{row.Name}」。\n\n删除后会放入临时区，7 天后才会彻底删除，期间可随时恢复。",
            "确认删除") { Owner = Application.Current.MainWindow };
        if (confirm.ShowDialog() != true) return;

        _vault.DeleteEntry(row.Entry.Id);
        Reload();
    }

    public void CopyPassword(KeyRow row) => CopyValue(row, row.Entry.Password, "密码", row.Name);

    public void CopyApiKey(KeyRow row) => CopyValue(row, row.Entry.ApiKey, "API Key", row.Name);

    private void CopyValue(KeyRow row, string value, string label, string name)
    {
        if (string.IsNullOrEmpty(value))
        {
            StatusText = $"「{name}」没有{label}可复制";
            return;
        }
        if (!ClipboardGuard.TrySetSecret(value, out var error))
        {
            StatusText = "复制失败：" + error;
            return;
        }
        StatusText = $"已复制「{name}」的{label}到剪贴板（{ClipboardGuard.Ttl.TotalSeconds:0} 秒后自动清空）";
    }
}
