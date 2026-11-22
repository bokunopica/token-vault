using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TokenVault.Services;

/// <summary>Token Plan 额度（Credits）：总量 / 剩余 / 已用，以及订阅周期。</summary>
public class AlibabaQuota
{
    public decimal? Total { get; init; }
    public decimal? Remaining { get; init; }
    public decimal? Used { get; init; }
    public DateTime? PeriodStart { get; init; }
    public DateTime? PeriodEnd { get; init; }
    public string Source { get; init; } = "";

    public double? UsedPct => Total is > 0 && Used.HasValue
        ? Math.Round((double)(Used.Value / Total.Value * 100), 1)
        : null;

    /// <summary>缺一个数时用另外两个补齐（接口有时只返回其中两项）。</summary>
    public AlibabaQuota Normalized()
    {
        var total = Total;
        var remaining = Remaining;
        var used = Used;
        if (total == null && used != null && remaining != null) total = used + remaining;
        if (remaining == null && total != null && used != null) remaining = total - used;
        if (used == null && total != null && remaining != null) used = total - remaining;
        return new AlibabaQuota
        {
            Total = total,
            Remaining = remaining,
            Used = used,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            Source = Source,
        };
    }
}

/// <summary>
/// 阿里云 Token Plan 额度查询（官方 OpenAPI 2026-02-10）：必须用 AccessKey + ACS3-HMAC-SHA256 签名，
/// 官方文档明确"不支持 Authorization: Bearer"，所以 sk- / sk-sp- 这类 API Key 查不了额度。
///   GET /tokenplan/subscription/shared-packages → 权益实例（CREDITS 的 CycleTotalValue / CycleSurplusValue）
///   GET /tokenplan/subscription/stats           → 席位额度（SeatCredits / SeatRemainingCredits）
/// 签名自己算（官方 SDK 2.2.5 实测会发错 x-acs-content-sha256，导致 signature mismatch）。
/// </summary>
public class AlibabaAkService
{
    public const string Endpoint = "modelstudio.cn-beijing.aliyuncs.com";
    public const string ApiVersion = "2026-02-10";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>上一次请求算出的规范请求（排错用：服务端报 signature mismatch 时会回显它自己的，两者可直接比对）。</summary>
    public string? LastCanonicalRequest { get; private set; }

    /// <summary>把权益/席位里的数值汇总成额度（纯函数，便于自检）。</summary>
    public static AlibabaQuota Aggregate(
        IEnumerable<(string? equityType, double? total, double? surplus, long? cycleStart, long? cycleEnd)> equities,
        IEnumerable<(double? credits, double? remaining, long? start, long? end)> seats)
    {
        var credits = equities
            .Where(e => string.IsNullOrEmpty(e.equityType) || e.equityType.Equals("CREDITS", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.total != null || e.surplus != null)
            .ToList();

        if (credits.Count > 0)
        {
            return new AlibabaQuota
            {
                Total = (decimal)credits.Sum(e => e.total ?? 0),
                Remaining = (decimal)credits.Sum(e => e.surplus ?? 0),
                PeriodStart = FromMs(credits.Select(e => e.cycleStart).FirstOrDefault(v => v is > 0)),
                PeriodEnd = FromMs(credits.Select(e => e.cycleEnd).FirstOrDefault(v => v is > 0)),
                Source = "共享包（Credits）",
            }.Normalized();
        }

        var seatList = seats.Where(s => s.credits != null || s.remaining != null).ToList();
        if (seatList.Count > 0)
        {
            return new AlibabaQuota
            {
                Total = (decimal)seatList.Sum(s => s.credits ?? 0),
                Remaining = (decimal)seatList.Sum(s => s.remaining ?? 0),
                PeriodStart = FromMs(seatList.Select(s => s.start).FirstOrDefault(v => v is > 0)),
                PeriodEnd = FromMs(seatList.Select(s => s.end).FirstOrDefault(v => v is > 0)),
                Source = "席位额度",
            }.Normalized();
        }

        return new AlibabaQuota { Source = "接口未返回额度数据" };
    }

    private static DateTime? FromMs(long? ms)
        => ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value).LocalDateTime : null;

    public async Task<AlibabaQuota> QueryAsync(string accessKeyId, string accessKeySecret, CancellationToken ct = default)
    {
        var equities = new List<(string?, double?, double?, long?, long?)>();
        var seats = new List<(double?, double?, long?, long?)>();
        var errors = new List<string>();
        long? subStart = null, subEnd = null;

        try
        {
            using var doc = await SendAsync(accessKeyId, accessKeySecret,
                "ListSubscriptionSharedPackages", "/tokenplan/subscription/shared-packages",
                new[] { new KeyValuePair<string, string>("PageSize", "50") }, ct).ConfigureAwait(false);
            var data = GetData(doc.RootElement);
            if (data is { } d && d.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("EquityList", out var eqs) || eqs.ValueKind != JsonValueKind.Array) continue;
                    foreach (var eq in eqs.EnumerateArray())
                        equities.Add((GetString(eq, "EquityType"), GetDouble(eq, "CycleTotalValue"),
                            GetDouble(eq, "CycleSurplusValue"), GetLong(eq, "CycleStartTime"), GetLong(eq, "CycleEndTime")));
                }
        }
        catch (Exception ex) { errors.Add("共享包：" + ex.Message); }

        try
        {
            using var doc = await SendAsync(accessKeyId, accessKeySecret,
                "GetSubscriptionStats", "/tokenplan/subscription/stats",
                Array.Empty<KeyValuePair<string, string>>(), ct).ConfigureAwait(false);
            var data = GetData(doc.RootElement);
            if (data is { } d)
            {
                subStart = GetLong(d, "SubscriptionStartTime");
                subEnd = GetLong(d, "SubscriptionEndTime");
                if (d.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var it in items.EnumerateArray())
                        seats.Add((GetDouble(it, "SeatCredits"), GetDouble(it, "SeatRemainingCredits"),
                            GetLong(it, "SeatRefreshTime"), null));
            }
        }
        catch (Exception ex) { errors.Add("席位：" + ex.Message); }

        var quota = Aggregate(equities, seats);
        if (quota.Total != null || quota.Remaining != null)
        {
            if (quota.PeriodStart == null)
                quota = new AlibabaQuota
                {
                    Total = quota.Total,
                    Remaining = quota.Remaining,
                    Used = quota.Used,
                    PeriodStart = FromMs(subStart),
                    PeriodEnd = FromMs(subEnd),
                    Source = quota.Source,
                };
            return quota;
        }

        throw new InvalidOperationException(
            (errors.Count > 0 ? string.Join("；", errors)
                              : "接口没有返回额度数据（该 AccessKey 可能没有订阅 Token Plan，或缺少 RAM 权限）"));
    }

    // ── ACS3 签名 + HTTP ────────────────────────────────────────────────────

    private async Task<JsonDocument> SendAsync(string ak, string sk, string action, string path,
        IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        var q = string.Join("&", query
            .Select(kv => new KeyValuePair<string, string>(PercentEncode(kv.Key), PercentEncode(kv.Value)))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));

        var date = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString();
        var emptyHash = HexSha256(Array.Empty<byte>());

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = Endpoint,
            ["x-acs-action"] = action,
            ["x-acs-content-sha256"] = emptyHash,
            ["x-acs-date"] = date,
            ["x-acs-signature-nonce"] = nonce,
            ["x-acs-version"] = ApiVersion,
        };

        var canonicalRequest = BuildCanonical(method, path, q, headers, emptyHash);
        LastCanonicalRequest = canonicalRequest;
        var stringToSign = $"ACS3-HMAC-SHA256\n{date}\n{HexSha256(Encoding.UTF8.GetBytes(canonicalRequest))}";
        var signature = HexHmacSha256(stringToSign, sk.Trim());
        var signedHeaders = string.Join(";", headers.Keys);
        var authorization = $"ACS3-HMAC-SHA256 Credential={ak.Trim()},SignedHeaders={signedHeaders},Signature={signature}";

        var url = $"https://{Endpoint}{path}" + (q.Length > 0 ? "?" + q : "");
        using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        req.Headers.TryAddWithoutValidation("Authorization", authorization);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        Diag.Log($"ali ak req: GET {url}");
        foreach (var kv in req.Headers) Diag.Log($"ali ak hdr: {kv.Key}={string.Join(",", kv.Value)}");
        Diag.Log("ali ak auth: ACS3-HMAC-SHA256 Credential=" + ak.Trim()[..Math.Min(6, ak.Trim().Length)] + "…,Signature=…" + signature[^8..]);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(DescribeHttpError((int)resp.StatusCode, body, canonicalRequest, ak, sk));

        return JsonDocument.Parse(body);
    }

    private const string method = "GET";

    /// <summary>按 ACS3 规范拼规范请求（自检会拿阿里云报错里回显的那份逐字比对）。</summary>
    public static string BuildCanonical(string method, string path, string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers, string hashedPayload)
    {
        var sorted = headers.OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        foreach (var h in sorted) sb.Append(h.Key).Append(':').Append(h.Value.Trim()).Append('\n');
        var signedHeaders = string.Join(";", sorted.Select(h => h.Key));
        return $"{method}\n{path}\n{canonicalQuery}\n{sb}\n{signedHeaders}\n{hashedPayload}";
    }

    private static string DescribeHttpError(int status, string body, string canonicalRequest, string ak, string sk)
    {
        string? code = null, message = null, requestId = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            code = GetString(r, "Code");
            message = GetString(r, "Message");
            requestId = GetString(r, "RequestId");
        }
        catch (JsonException) { /* 非 JSON 错误体 */ }

        var text = $"HTTP {status}"
                   + (code != null ? $" {code}" : "")
                   + (message != null ? "：" + message : "")
                   + (requestId != null ? $"（RequestId {requestId}）" : "");

        if (message?.Contains("Specified access key is not found") == true)
            return "AccessKey 无效：AK/SK 填错了，或该 AK 已被禁用。";
        if (message?.Contains("Forbidden") == true || code is "Forbidden" or "NoPermission")
            return "该 AccessKey 没有调用权限：给对应 RAM 用户授予 AliyunTokenPlanReadOnlyAccess（+ AliyunBSSReadOnlyAccess）。";
        if (message?.Contains("does not match our calculation") == true)
            return text + "\n" + DiffServerCanonical(message, canonicalRequest)
                   + $"\n（本地 AK 长度 {ak.Trim().Length}，SK 长度 {sk.Trim().Length}；AK 通常 24 位以 LTAI 开头，SK 通常 30 位）";
        if (string.IsNullOrEmpty(body))
            return text;

        var snippet = body.Length > 400 ? body[..400] + "…" : body;
        return text + "\n" + snippet;
    }

    /// <summary>
    /// 把服务端回显的 canonical request 与本机签的逐行对比：给出第一处差异，
    /// 差异明细同时写进 app.log（截图/复制都不会再破坏内容）。
    /// </summary>
    private static string DiffServerCanonical(string serverMessage, string ours)
    {
        const string marker = "server CanonicalRequest is [";
        var start = serverMessage.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "本机规范请求：\n" + ours;
        start += marker.Length;
        var end = serverMessage.LastIndexOf(']');
        if (end <= start) return "本机规范请求：\n" + ours;
        var server = serverMessage[start..end].Replace("\r\n", "\n");

        Diag.Log("ali ak sign - server canonical: " + server.Replace("\n", "\\n"));
        Diag.Log("ali ak sign - local  canonical: " + ours.Replace("\n", "\\n"));

        var sLines = server.Split('\n');
        var oLines = ours.Split('\n');
        for (var i = 0; i < Math.Max(sLines.Length, oLines.Length); i++)
        {
            var s = i < sLines.Length ? sLines[i] : "(缺)";
            var o = i < oLines.Length ? oLines[i] : "(缺)";
            if (s == o) continue;
            var detail = $"服务端第 {i + 1} 行：{s}\n本机第 {i + 1} 行：{o}";
            Diag.Log("ali ak sign - first diff at line " + (i + 1) + ": server=[" + s + "] local=[" + o + "]");
            return "签名与服务端不一致，第一处差异：\n" + detail;
        }
        return "服务端与本机的规范请求**逐字一致**，说明请求构造没问题 —— 是 AccessKey Secret 本身不对（被截断/复制成了别的字段/已轮换）。";
    }

    private static JsonElement? GetData(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("Data", out var data)
           && data.ValueKind == JsonValueKind.Object
            ? data : null;

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static long? GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : null,
            JsonValueKind.String when long.TryParse(v.GetString(), out var l) => l,
            _ => null,
        };
    }

    private static string PercentEncode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static string HexSha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string HexHmacSha256(string data, string key)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
}
