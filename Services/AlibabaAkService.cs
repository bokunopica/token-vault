using System.Net.Http;
using AlibabaCloud.SDK.ModelStudio20260210;
using AlibabaCloud.SDK.ModelStudio20260210.Models;

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
/// 阿里云 Token Plan 额度查询（官方 OpenAPI，2026-02-10 版）：需要 AccessKey（AK/SK），
/// 不支持 sk- / sk-sp- 这类 API Key —— 官方文档明确要求 RAM 用户 AccessKey + ACS3 签名。
///   GET /tokenplan/subscription/shared-packages  → 权益实例（CREDITS 的 CycleTotalValue / CycleSurplusValue）
///   GET /tokenplan/subscription/stats            → 席位额度（SeatCredits / SeatRemainingCredits）
/// </summary>
public class AlibabaAkService
{
    public const string Endpoint = "modelstudio.cn-beijing.aliyuncs.com";

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
        => await Task.Run(() => Query(accessKeyId, accessKeySecret), ct).ConfigureAwait(false);

    private static AlibabaQuota Query(string ak, string sk)
    {
        var config = new AlibabaCloud.OpenApiClient.Models.Config
        {
            AccessKeyId = ak.Trim(),
            AccessKeySecret = sk.Trim(),
            Endpoint = Endpoint,
            Protocol = "https",
            RegionId = "cn-beijing",
            ConnectTimeout = 15000,
            ReadTimeout = 20000,
        };
        var client = new Client(config);

        var equities = new List<(string?, double?, double?, long?, long?)>();
        var seats = new List<(double?, double?, long?, long?)>();
        string? lastError = null;

        // 1) 共享包 / 权益实例：个人版与团队版都能拿到 Credits 总额与剩余
        try
        {
            var resp = client.ListSubscriptionSharedPackages(new ListSubscriptionSharedPackagesRequest { PageSize = 50 });
            var data = resp.Body?.Data;
            if (data?.Items != null)
                foreach (var item in data.Items)
                {
                    if (item.EquityList == null) continue;
                    foreach (var eq in item.EquityList)
                        equities.Add((eq.EquityType, eq.CycleTotalValue, eq.CycleSurplusValue, eq.CycleStartTime, eq.CycleEndTime));
                }
        }
        catch (Exception ex) { lastError = Describe(ex); }

        // 2) 席位统计：团队版
        long? subStart = null, subEnd = null;
        try
        {
            var resp = client.GetSubscriptionStats(new GetSubscriptionStatsRequest());
            var data = resp.Body?.Data;
            subStart = data?.SubscriptionStartTime;
            subEnd = data?.SubscriptionEndTime;
            if (data?.Items != null)
                foreach (var it in data.Items)
                    seats.Add((it.SeatCredits, it.SeatRemainingCredits, it.SeatRefreshTime, null));
        }
        catch (Exception ex) { lastError ??= Describe(ex); }

        var quota = Aggregate(equities, seats);
        if (quota.Total != null || quota.Remaining != null)
        {
            if (quota.PeriodStart == null) quota = new AlibabaQuota
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
            "阿里云接口没有返回额度数据" + (lastError != null ? "：" + lastError : "（请确认该 AccessKey 已订阅 Token Plan 且有 RAM 权限）"));
    }

    /// <summary>把 SDK 的异常整理成一句人话。</summary>
    private static string Describe(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        if (msg.Contains("Specified access key is not found"))
            return "AccessKey 无效（AK/SK 填错，或已被禁用）";
        if (msg.Contains("Forbidden") || msg.Contains("NoPermission"))
            return "该 AccessKey 没有调用权限（需给 RAM 用户授予百炼/ModelStudio 相关权限）";
        if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) || ex is HttpRequestException)
            return "网络不可达：" + msg;
        return msg;
    }
}
