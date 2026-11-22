namespace TokenVault.Models;

/// <summary>自定义字段（如 DATABASE_URL、PGWEB_AUTH_USER 等任意键值）。</summary>
public class CustomField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>一条凭据记录：可以是 API Key，也可以是账号密码、数据库连接等任意登录信息。</summary>
public class KeyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>mimo-plan | mimo-sk | deepseek | account | api-key | database | other</summary>
    public string Category { get; set; } = "other";
    public string ApiKey { get; set; } = "";
    /// <summary>地址 / Base URL（可选；MiMo/DeepSeek 查询时留空用官方默认地址）。</summary>
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public List<CustomField> Fields { get; set; } = new();
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>该条目对外展示/复制的“主密钥”：有 API Key 用 API Key，否则用密码。</summary>
    public string PrimarySecret => !string.IsNullOrEmpty(ApiKey) ? ApiKey : Password;

    /// <summary>下拉列表显示名（不参与序列化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => Name + " · " + KeyCategories.DisplayName(Category);

    public KeyEntry Clone()
    {
        var copy = (KeyEntry)MemberwiseClone();
        copy.Fields = Fields.Select(f => new CustomField { Name = f.Name, Value = f.Value }).ToList();
        return copy;
    }
}

public static class KeyCategories
{
    public const string MimoPlan = "mimo-plan";   // tp-… Token Plan key
    public const string MimoSk = "mimo-sk";       // sk-… 普通 key
    public const string DeepSeek = "deepseek";    // sk-… DeepSeek key
    public const string Account = "account";      // 账号密码（网站/控制台/SSH 等）
    public const string ApiKey = "api-key";       // 通用 API 密钥
    public const string Database = "database";    // 数据库 / 连接串
    public const string Other = "other";

    public static string DisplayName(string category) => category switch
    {
        MimoPlan => "MiMo Token Plan (tp-)",
        MimoSk => "MiMo API (sk-)",
        DeepSeek => "DeepSeek (sk-)",
        Account => "账号密码",
        ApiKey => "API 密钥",
        Database => "数据库",
        _ => "其他",
    };
}

/// <summary>临时区条目：被删除的密钥，或修改前的旧版本。</summary>
public class TrashItem
{
    public const string KindDeleted = "deleted";
    public const string KindOldVersion = "old-version";

    /// <summary>临时区条目自身唯一 Id（同一密钥可能同时存在旧版本与已删除两种记录）。</summary>
    public string TrashId { get; set; } = Guid.NewGuid().ToString("N");
    public string EntryId { get; set; } = "";
    /// <summary>deleted | old-version</summary>
    public string Kind { get; set; } = KindDeleted;
    public KeyEntry Entry { get; set; } = new();
    public DateTime EnteredAt { get; set; } = DateTime.Now;
    /// <summary>进入临时区 7 天后彻底删除。</summary>
    public DateTime PurgeAt { get; set; } = DateTime.Now.AddDays(7);

    public string KindDisplay => Kind == KindOldVersion ? "修改前版本" : "已删除";
    public double DaysLeft => Math.Max(0, (PurgeAt - DateTime.Now).TotalDays);
    public bool IsExpired => DateTime.Now >= PurgeAt;
}

public class VaultSettings
{
    public string MimoKeyId { get; set; } = "";
    public string DeepseekKeyId { get; set; } = "";
    public bool AutoRefresh { get; set; } = true;
    public int AutoRefreshMinutes { get; set; } = 5;
    /// <summary>空闲多少分钟后自动锁定（0 = 不自动锁定）。</summary>
    public int IdleLockMinutes { get; set; } = 5;
}

/// <summary>解密后的保险库整体数据。</summary>
public class VaultData
{
    public List<KeyEntry> Entries { get; set; } = new();
    public List<TrashItem> Trash { get; set; } = new();
    public VaultSettings Settings { get; set; } = new();
}
