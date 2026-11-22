using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace TokenVault.Services;

public class MimoPlanInfo
{
    public string? PlanCode { get; init; }
    public decimal? TotalCredits { get; init; }
    public decimal? UsedCredits { get; init; }
    public decimal? RemainingCredits { get; init; }
    public double? UsedPct { get; init; }
    public DateTime? PeriodStart { get; init; }
    public DateTime? PeriodEnd { get; init; }
    public bool AutoRenew { get; init; }
}

public class DeepseekBalance
{
    public string Currency { get; init; } = "CNY";
    public decimal? TotalBalance { get; init; }
    public decimal? GrantedBalance { get; init; }
    public decimal? ToppedUpBalance { get; init; }
    public bool IsAvailable { get; init; }
}

/// <summary>阿里云百炼 Token 套餐 / 资源包余量（字段按官方返回自适应解析）。</summary>
public class AlibabaPlanInfo
{
    public string? PlanCode { get; init; }
    public decimal? Total { get; init; }
    public decimal? Used { get; init; }
    public decimal? Remaining { get; init; }
    public double? UsedPct { get; init; }
    public string? Period { get; init; }
    public string RawSnippet { get; init; } = "";
}

/// <summary>今日/本周/本月消耗估算（快照增量口径）。</summary>
public class ConsumptionInfo
{
    public decimal Day { get; init; }
    public decimal Week { get; init; }
    public decimal Month { get; init; }
    public DateTime? Since { get; init; }
}

/// <summary>
/// 余量查询服务（均为官方接口）：
///   MiMo 套餐余量  GET {https://token-plan-cn.xiaomimimo.com/v1}/platform/token_plan （tp- key）
///   DeepSeek 余额  GET https://api.deepseek.com/user/balance （sk- key）
///   MiMo 日用量    GET {https://api.xiaomimimo.com/v1}/platform/usage/daily （sk- key，测试连通用）
/// 每次成功查询写入本地快照，用于日/周/月消耗估算。
/// </summary>
public class BalanceService
{
    public const string DefaultMimoPlanBase = "https://token-plan-cn.xiaomimimo.com/v1";
    public const string DefaultMimoSkBase = "https://api.xiaomimimo.com/v1";
    public const string DefaultDeepseekBase = "https://api.deepseek.com";
    public const string DefaultAlibabaBase = "https://dashscope.aliyuncs.com";
    /// <summary>百炼网关上的额度接口（实测：无效密钥返回 401 InvalidApiKey → 路由存在），默认就用它，不用手填地址。</summary>
    public const string DefaultAlibabaQuotaUrl = "https://dashscope.aliyuncs.com/api/v1/quotas";
    /// <summary>千问 Token Plan 专属网关（sk-sp- 前缀密钥配套使用）。</summary>
    public const string DefaultAlibabaTokenPlanBase = "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1";
    /// <summary>百炼 OpenAI 兼容的模型列表（实测：无效密钥返回 401「Incorrect API key provided」），用于连通性测试。</summary>
    public const string AlibabaModelsPath = "/api/v1/models";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const double SnapshotMinIntervalSeconds = 600;

    private readonly string _snapshotPath;

    public BalanceService(string? snapshotPath = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenVault");
        Directory.CreateDirectory(dir);
        _snapshotPath = snapshotPath ?? Path.Combine(dir, "snapshots.jsonl");
    }

    // ── 查询 ────────────────────────────────────────────────────────────────

    public async Task<MimoPlanInfo> GetMimoPlanAsync(string apiKey, string? baseUrl, CancellationToken ct = default)
    {
        var url = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultMimoPlanBase : baseUrl).TrimEnd('/') + "/platform/token_plan";
        using var doc = await GetJsonAsync(url, apiKey, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var quota = root.TryGetProperty("quota", out var q) ? q : default;
        var period = root.TryGetProperty("period", out var p) ? p : default;

        decimal? total = GetDecimal(quota, "total_credits");
        decimal? used = GetDecimal(quota, "used_credits");
        decimal? remaining = GetDecimal(quota, "remaining_credits");
        return new MimoPlanInfo
        {
            PlanCode = GetString(root, "plan_code"),
            TotalCredits = total,
            UsedCredits = used,
            RemainingCredits = remaining,
            UsedPct = total is > 0 && used.HasValue ? Math.Round((double)(used.Value / total.Value * 100), 1) : null,
            PeriodStart = GetDate(period, "start_at"),
            PeriodEnd = GetDate(period, "end_at"),
            AutoRenew = root.TryGetProperty("auto_renew", out var ar) && ar.ValueKind == JsonValueKind.True,
        };
    }

    public async Task<DeepseekBalance> GetDeepseekBalanceAsync(string apiKey, string? baseUrl = null, CancellationToken ct = default)
    {
        // 余额接口在域名根路径下：容错把用户误填的 “…/v1” 去掉
        var basePart = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultDeepseekBase : baseUrl).TrimEnd('/');
        if (basePart.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) basePart = basePart[..^3];
        var url = basePart + "/user/balance";
        using var doc = await GetJsonAsync(url, apiKey, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var info = default(JsonElement);
        if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array && infos.GetArrayLength() > 0)
            info = infos[0];

        return new DeepseekBalance
        {
            Currency = GetString(info, "currency") ?? "CNY",
            TotalBalance = GetDecimal(info, "total_balance"),
            GrantedBalance = GetDecimal(info, "granted_balance"),
            ToppedUpBalance = GetDecimal(info, "topped_up_balance"),
            IsAvailable = !root.TryGetProperty("is_available", out var av) || av.ValueKind != JsonValueKind.False,
        };
    }

    /// <summary>
    /// 阿里云百炼 Token 套餐 / 资源包余量。
    /// 官方把套餐余量放在控制台侧，没有稳定的公开 REST 端点（/api/v1/usage 等实测均 404），
    /// 所以这里使用凭据「地址」里填写的完整查询 URL，并按常见字段名自适应解析返回 JSON。
    /// </summary>
    public async Task<AlibabaPlanInfo> GetAlibabaPlanAsync(string apiKey, string? queryUrl, CancellationToken ct = default)
    {
        var raw = (queryUrl ?? "").Trim();
        string url;
        if (raw.Length == 0)
        {
            if (KeyStartsWithTokenPlan(apiKey))
                throw new InvalidOperationException(
                    "千问 Token Plan 专属密钥（sk-sp-）目前没有公开的余量接口，官方只在控制台展示。"
                    + "如果你有查询 URL，填到凭据「地址」即可；否则请在 platform.qianwenai.com 工作台查看剩余 Credits。");
            url = DefaultAlibabaQuotaUrl;   // 地址留空就用内置默认接口
        }
        else if (!UrlHelper.TryNormalize(raw, out url))
        {
            throw new InvalidOperationException("「地址」不是合法 URL：" + raw);
        }
        else if (IsBareHost(url))
        {
            url = url.TrimEnd('/') + "/api/v1/quotas";   // 只填了域名时自动补默认路径
        }
        using var doc = await GetJsonAsync(url, apiKey, ct).ConfigureAwait(false);
        return ParseAlibabaPlan(doc.RootElement);
    }

    private static bool IsBareHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.Trim('/').Length == 0;

    /// <summary>sk-sp- 前缀 = 千问 Token Plan 专属密钥。</summary>
    public static bool KeyStartsWithTokenPlan(string apiKey)
        => apiKey.StartsWith("sk-sp", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] TotalKeys = { "total_credits", "total_quota", "total_tokens", "total_amount", "total", "quota", "amount" };
    private static readonly string[] UsedKeys = { "used_credits", "used_quota", "used_tokens", "used_amount", "used", "consumed", "usage" };
    private static readonly string[] RemainingKeys = { "remaining_credits", "remaining_quota", "remaining_tokens", "remaining_amount", "remaining", "remain", "left", "available", "balance" };
    private static readonly string[] PlanKeys = { "plan_code", "plan_name", "plan", "package_name", "product_name", "instance_name", "name" };
    private static readonly string[] PeriodKeys = { "period_start", "period_end", "start_at", "end_at", "start_time", "end_time", "expire_time", "expired_time" };

    /// <summary>自适应解析：从任意结构的 JSON 里按常见字段名（深度优先不超过 6 层）提取总量/已用/剩余。</summary>
    public static AlibabaPlanInfo ParseAlibabaPlan(JsonElement root)
    {
        decimal? total = null, used = null, remaining = null;
        string? plan = null, periodStart = null, periodEnd = null;

        var queue = new Queue<JsonElement>();
        queue.Enqueue(root);
        for (var depth = 0; depth < 6 && queue.Count > 0; depth++)
        {
            var level = queue.Count;
            for (var i = 0; i < level; i++)
            {
                var el = queue.Dequeue();
                if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray()) queue.Enqueue(item);
                    continue;
                }
                if (el.ValueKind != JsonValueKind.Object) continue;

                foreach (var prop in el.EnumerateObject())
                {
                    var name = Normalize(prop.Name);
                    var v = prop.Value;
                    if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        queue.Enqueue(v);
                        continue;
                    }
                    if (TryDecimal(v, out var d))
                    {
                        if (total == null && TotalKeys.Contains(name)) total = d;
                        if (used == null && UsedKeys.Contains(name)) used = d;
                        if (remaining == null && RemainingKeys.Contains(name)) remaining = d;
                    }
                    else if (v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        if (plan == null && PlanKeys.Contains(name)) plan = s;
                        if (periodStart == null && PeriodKeys.Contains(name) && name.Contains("start")) periodStart = s;
                        if (periodEnd == null && PeriodKeys.Contains(name) && (name.Contains("end") || name.Contains("expire"))) periodEnd = s;
                    }
                }
            }
        }

        // 三个数缺一个时互相补齐
        if (total == null && used != null && remaining != null) total = used + remaining;
        if (remaining == null && total != null && used != null) remaining = total - used;
        if (used == null && total != null && remaining != null) used = total - remaining;

        var raw = root.GetRawText();
        return new AlibabaPlanInfo
        {
            PlanCode = plan,
            Total = total,
            Used = used,
            Remaining = remaining,
            UsedPct = total is > 0 && used.HasValue ? Math.Round((double)(used.Value / total.Value * 100), 1) : null,
            Period = periodStart != null || periodEnd != null ? $"{periodStart ?? "?"} ~ {periodEnd ?? "?"}" : null,
            RawSnippet = raw.Length > 600 ? raw[..600] + "…" : raw,
        };
    }

    private static string Normalize(string name)
        => name.Replace("-", "_").ToLowerInvariant();

    private static bool TryDecimal(JsonElement v, out decimal d)
    {
        d = 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDecimal(out d);
        if (v.ValueKind == JsonValueKind.String)
            return decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out d);
        return false;
    }

    /// <summary>OpenAI 兼容的 /models 列表：需要鉴权且零 token 消耗，用于判断密钥是否有效。</summary>
    public async Task<JsonDocument> GetModelsAsync(string apiKey, string baseWithPath, CancellationToken ct = default)
        => await GetJsonAsync(baseWithPath.TrimEnd('/') + "/models", apiKey, ct).ConfigureAwait(false);

    /// <summary>按类别测试密钥连通性，返回 (成功, 消息)。</summary>
    public async Task<(bool ok, string message)> TestKeyAsync(string category, string apiKey, string? baseUrl, CancellationToken ct = default)
    {
        try
        {
            switch (category)
            {
                case Models.KeyCategories.MimoPlan:
                {
                    var info = await GetMimoPlanAsync(apiKey, baseUrl, ct).ConfigureAwait(false);
                    return (true, $"连接成功：套餐 {info.PlanCode ?? "-"}，已用 {info.UsedCredits}/{info.TotalCredits}");
                }
                case Models.KeyCategories.DeepSeek:
                {
                    var bal = await GetDeepseekBalanceAsync(apiKey, baseUrl, ct).ConfigureAwait(false);
                    return (true, $"连接成功：余额 {bal.TotalBalance} {bal.Currency}");
                }
                case Models.KeyCategories.Alibaba:
                {
                    // sk-sp- 是千问 Token Plan 专属 Key，只在 Token Plan 网关可用；
                    // 其它（sk-）走百炼主网关。地址里填了完整 URL 时只取域名判断不出前缀，这里优先按 Key 前缀。
                    using var doc = KeyStartsWithTokenPlan(apiKey)
                        ? await GetJsonAsync(DefaultAlibabaTokenPlanBase + "/models", apiKey, ct).ConfigureAwait(false)
                        : await GetModelsAsync(apiKey, DefaultAlibabaBase + "/api/v1", ct).ConfigureAwait(false);
                    return (true, "连接成功：密钥有效（模型列表可访问）");
                }
                case Models.KeyCategories.MimoSk:
                {
                    // 用 OpenAI 兼容的 /models 探测：需鉴权（无效密钥返回 401），零 token 消耗。
                    // 旧版用的 /platform/usage/daily 对无效密钥也返回 200，无法判断密钥有效性。
                    var basePart = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultMimoSkBase : baseUrl).TrimEnd('/');
                    try
                    {
                        using var doc = await GetModelsAsync(apiKey, basePart, ct).ConfigureAwait(false);
                        return (true, "连接成功：密钥有效（/models 可访问）");
                    }
                    catch (HttpRequestException ex) when (ex.Message.Contains("HTTP 404"))
                    {
                        var url = basePart + $"/platform/usage/daily?start_date={DateTime.Now:yyyy-MM-dd}&end_date={DateTime.Now:yyyy-MM-dd}";
                        using var doc = await GetJsonAsync(url, apiKey, ct).ConfigureAwait(false);
                        return (true, "连接成功：日用量接口可访问（该端点不校验密钥，仅供参考）");
                    }
                }
                default:
                    return (false, "“其他”类别没有内置测试接口，请手动验证");
            }
        }
        catch (Exception ex)
        {
            return (false, "连接失败：" + ex.Message);
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "TokenVault/1.0");
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("请求超时（15 秒），请检查网络");
        }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var snippet = body.Length > 200 ? body[..200] + "…" : body;
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}：{snippet}");
            }
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                throw new InvalidDataException("接口返回的不是合法 JSON：" + (body.Length > 120 ? body[..120] + "…" : body));
            }
        }
    }

    private static string? GetString(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static decimal? GetDecimal(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), out var d) => d,
            _ => null,
        };
    }

    private static DateTime? GetDate(JsonElement e, string name)
    {
        var s = GetString(e, name);
        return s != null && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
    }

    // ── 快照与消耗估算 ──────────────────────────────────────────────────────

    private sealed class Snapshot
    {
        public double Ts { get; set; }
        public decimal? MimoUsed { get; set; }
        public decimal? MimoTotal { get; set; }
        public decimal? DsBalance { get; set; }
    }

    public void RecordSnapshot(MimoPlanInfo? mimo, DeepseekBalance? deepseek)
    {
        try
        {
            var rows = LoadSnapshots();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (rows.Count > 0 && now - rows[^1].Ts < SnapshotMinIntervalSeconds) return;
            rows.Add(new Snapshot
            {
                Ts = now,
                MimoUsed = mimo?.UsedCredits,
                MimoTotal = mimo?.TotalCredits,
                DsBalance = deepseek?.TotalBalance,
            });
            var line = JsonSerializer.Serialize(rows[^1], new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.AppendAllText(_snapshotPath, line + Environment.NewLine);
        }
        catch (IOException) { /* 快照失败不影响主功能 */ }
    }

    private List<Snapshot> LoadSnapshots()
    {
        var rows = new List<Snapshot>();
        if (!File.Exists(_snapshotPath)) return rows;
        foreach (var line in File.ReadLines(_snapshotPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<Snapshot>(line, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
                if (s != null) rows.Add(s);
            }
            catch (JsonException) { }
        }
        return rows;
    }

    /// <summary>usedCredits 增长即消耗（mimo）；balance 下降即消耗（deepseek）。</summary>
    public ConsumptionInfo GetConsumption(bool forMimo)
    {
        var rows = LoadSnapshots();
        var now = DateTime.Now;
        var day0 = now.Date;
        var week0 = day0.AddDays(-(((int)day0.DayOfWeek + 6) % 7));   // 周一为一周起点
        var month0 = new DateTime(now.Year, now.Month, 1);
        return new ConsumptionInfo
        {
            Day = Consumption(rows, forMimo, day0),
            Week = Consumption(rows, forMimo, week0),
            Month = Consumption(rows, forMimo, month0),
            Since = rows.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)rows[0].Ts).LocalDateTime : null,
        };
    }

    private static decimal Consumption(List<Snapshot> rows, bool forMimo, DateTime windowStart)
    {
        double w0 = new DateTimeOffset(windowStart).ToUnixTimeSeconds();
        double w1 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool growing = forMimo;
        decimal total = 0;
        (double ts, decimal val)? prev = null;
        foreach (var row in rows)
        {
            var val = forMimo ? row.MimoUsed : row.DsBalance;
            if (row.Ts == 0 || val == null) continue;
            if (prev is { } p)
            {
                double t0 = p.ts, t1 = row.Ts;
                if (t1 > t0)
                {
                    var delta = (val.Value - p.val) * (growing ? 1 : -1);
                    if (delta > 0)
                    {
                        double ov = Math.Max(0, Math.Min(t1, w1) - Math.Max(t0, w0));
                        total += delta * (decimal)(ov / (t1 - t0));
                    }
                }
            }
            prev = (row.Ts, val.Value);
        }
        return Math.Round(total, 2);
    }
}
