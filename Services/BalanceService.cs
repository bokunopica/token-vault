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
