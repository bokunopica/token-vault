using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TokenVault.Models;

namespace TokenVault.Services;

/// <summary>
/// 加密保险库：负责主密码校验、条目 CRUD、临时区（7 天缓冲）与持久化。
/// 数据文件：%LOCALAPPDATA%\TokenVault\vault.json（AES-256-GCM + PBKDF2-SHA256）。
/// </summary>
public class VaultStore
{
    public const double TrashRetentionDays = 7;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private byte[]? _key;          // 解锁后驻留内存
    private VaultFile? _file;      // 复用原 salt/iterations 重新加密

    public VaultData Data { get; private set; } = new();

    public event Action? Changed;

    public VaultStore(string? path = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenVault");
        Directory.CreateDirectory(dir);
        _path = path ?? Path.Combine(dir, "vault.json");
    }

    public string FilePath => _path;
    public bool Exists => File.Exists(_path);
    public bool IsUnlocked => _key != null;

    // ── 生命周期 ────────────────────────────────────────────────────────────

    /// <summary>首次使用：设置主密码并创建空保险库。</summary>
    public void Create(string password)
    {
        Data = new VaultData();
        var payload = JsonSerializer.Serialize(Data, JsonOpts);
        _file = CryptoService.Encrypt(password, payload);
        _key = CryptoService.DeriveKey(password, Convert.FromBase64String(_file.Kdf.Salt), _file.Kdf.Iterations);
        Save();
    }

    /// <summary>解锁。口令错误/文件损坏抛出 CryptographicException。</summary>
    public void Unlock(string password)
    {
        var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(_path), JsonOpts)
                   ?? throw new InvalidDataException("保险库文件为空");
        var payload = CryptoService.Decrypt(file, password);   // GCM 校验失败 = 口令错误
        Data = JsonSerializer.Deserialize<VaultData>(payload, JsonOpts) ?? new VaultData();
        Data.Trash ??= new List<TrashItem>();
        Data.Entries ??= new List<KeyEntry>();
        Data.Settings ??= new VaultSettings();
        _file = file;
        _key = CryptoService.DeriveKey(password, Convert.FromBase64String(file.Kdf.Salt), file.Kdf.Iterations);
        PurgeExpired();
    }

    public void Lock()
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _file = null;
        Data = new VaultData();
        Changed?.Invoke();
    }

    public void Save()
    {
        if (_key == null || _file == null) throw new InvalidOperationException("保险库未解锁");
        var payload = JsonSerializer.Serialize(Data, JsonOpts);
        var plain = System.Text.Encoding.UTF8.GetBytes(payload);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new System.Security.Cryptography.AesGcm(_key, 16))
            aes.Encrypt(nonce, plain, cipher, tag);
        _file.Cipher.Nonce = Convert.ToBase64String(nonce);
        _file.Cipher.Tag = Convert.ToBase64String(tag);
        _file.Payload = Convert.ToBase64String(cipher);
        _file.SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        File.WriteAllText(_path, JsonSerializer.Serialize(_file, JsonOpts));
    }

    /// <summary>修改主密码。</summary>
    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (_key == null) throw new InvalidOperationException("保险库未解锁");
        // 先验证当前口令
        CryptoService.Decrypt(_file!, currentPassword);
        var payload = JsonSerializer.Serialize(Data, JsonOpts);
        _file = CryptoService.Encrypt(newPassword, payload);
        _key = CryptoService.DeriveKey(newPassword, Convert.FromBase64String(_file.Kdf.Salt), _file.Kdf.Iterations);
        Save();
    }

    // ── 条目 CRUD ───────────────────────────────────────────────────────────

    public void AddEntry(KeyEntry entry)
    {
        Data.Entries.Add(entry);
        Save();
        Changed?.Invoke();
    }

    /// <summary>修改条目：旧版本进入临时区（7 天后彻底删除）。</summary>
    public void UpdateEntry(KeyEntry updated)
    {
        var idx = Data.Entries.FindIndex(e => e.Id == updated.Id);
        if (idx < 0) throw new InvalidOperationException("条目不存在");
        var old = Data.Entries[idx];
        PushToTrash(old, TrashItem.KindOldVersion);
        updated.UpdatedAt = DateTime.Now;
        if (updated.CreatedAt == default) updated.CreatedAt = old.CreatedAt;
        Data.Entries[idx] = updated;
        Save();
        Changed?.Invoke();
    }

    /// <summary>删除条目 → 临时区（7 天后彻底删除）。</summary>
    public void DeleteEntry(string id)
    {
        var entry = Data.Entries.FirstOrDefault(e => e.Id == id)
                    ?? throw new InvalidOperationException("条目不存在");
        Data.Entries.Remove(entry);
        PushToTrash(entry, TrashItem.KindDeleted);
        if (Data.Settings.MimoKeyId == id) Data.Settings.MimoKeyId = "";
        if (Data.Settings.DeepseekKeyId == id) Data.Settings.DeepseekKeyId = "";
        Save();
        Changed?.Invoke();
    }

    /// <summary>从临时区恢复。若同 Id 的活动条目存在（旧版本恢复），当前版本进入临时区。</summary>
    public void Restore(string trashId)
    {
        var item = Data.Trash.FirstOrDefault(t => t.TrashId == trashId)
                   ?? throw new InvalidOperationException("临时区条目不存在");
        Data.Trash.Remove(item);
        var restored = item.Entry.Clone();
        var idx = Data.Entries.FindIndex(e => e.Id == restored.Id);
        if (idx >= 0)
        {
            PushToTrash(Data.Entries[idx], TrashItem.KindOldVersion);
            restored.UpdatedAt = DateTime.Now;
            Data.Entries[idx] = restored;
        }
        else
        {
            Data.Entries.Add(restored);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>立即彻底删除（仅临时区可用，需二次确认）。</summary>
    public void PurgeNow(string trashId)
    {
        var removed = Data.Trash.RemoveAll(t => t.TrashId == trashId);
        if (removed == 0) throw new InvalidOperationException("临时区条目不存在");
        Save();
        Changed?.Invoke();
    }

    /// <summary>清除超过 7 天的临时区条目（启动时与每次变更时调用）。</summary>
    public int PurgeExpired()
    {
        var removed = Data.Trash.RemoveAll(t => t.IsExpired);
        if (removed > 0)
        {
            Save();
            Changed?.Invoke();
        }
        return removed;
    }

    private void PushToTrash(KeyEntry entry, string kind)
    {
        // 同一密钥的“已删除”记录只保留最新一份；“修改前版本”保留全部历史
        if (kind == TrashItem.KindDeleted)
            Data.Trash.RemoveAll(t => t.EntryId == entry.Id && t.Kind == kind);
        Data.Trash.Add(new TrashItem
        {
            EntryId = entry.Id,
            Kind = kind,
            Entry = entry.Clone(),
            EnteredAt = DateTime.Now,
            PurgeAt = DateTime.Now.AddDays(TrashRetentionDays),
        });
    }
}
