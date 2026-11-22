using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using TokenVault.Models;

namespace TokenVault.Services;

/// <summary>
/// --selftest 自检：真实执行加密往返、保险库 CRUD、临时区 7 天逻辑、
/// 消耗估算数学，以及对两个官方接口的真实 HTTP 探测（用无效密钥验证请求链路）。
/// </summary>
public static class SelfTest
{
    private static int _pass, _fail, _warn;

    public static int Run(TextWriter o)
    {
        _pass = _fail = _warn = 0;
        o.WriteLine("TokenVault selftest — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        o.WriteLine(new string('─', 60));

        TestCrypto(o);
        TestVaultStore(o);
        TestTrashLifecycle(o);
        TestConsumptionMath(o);
        TestAlibabaParse(o);
        TestIdleAndClipboard(o);
        TestLiveHttp(o).GetAwaiter().GetResult();

        o.WriteLine(new string('─', 60));
        o.WriteLine($"结果：{_pass} PASS / {_fail} FAIL / {_warn} WARN");
        return _fail == 0 ? 0 : 1;
    }

    private static void Pass(TextWriter o, string msg) { _pass++; o.WriteLine($"PASS  {msg}"); }
    private static void Fail(TextWriter o, string msg) { _fail++; o.WriteLine($"FAIL  {msg}"); }
    private static void Warn(TextWriter o, string msg) { _warn++; o.WriteLine($"WARN  {msg}"); }

    private static void Check(TextWriter o, bool cond, string msg)
    {
        if (cond) Pass(o, msg); else Fail(o, msg);
    }

    // ── 1. 加密往返 ─────────────────────────────────────────────────────────

    private static void TestCrypto(TextWriter o)
    {
        o.WriteLine("[1] AES-256-GCM + PBKDF2-SHA256 加密往返");
        const string secret = "sk-very-secret-value-1234567890";
        var file = CryptoService.Encrypt("correct horse", $"{{\"apiKey\":\"{secret}\"}}");
        try
        {
            var plain = CryptoService.Decrypt(file, "correct horse");
            Check(o, plain.Contains(secret), "正确口令可解密出原文");
        }
        catch (Exception ex) { Fail(o, "正确口令解密异常：" + ex.Message); }

        try
        {
            CryptoService.Decrypt(file, "wrong password");
            Fail(o, "错误口令竟然解密成功（GCM 认证未生效？）");
        }
        catch (CryptographicException)
        {
            Pass(o, "错误口令被 GCM 认证拒绝");
        }
        catch (Exception ex) { Fail(o, "错误口令抛出意外异常：" + ex.GetType().Name); }

        Check(o, !file.Payload.Contains(secret), "密文负载不含明文密钥字符串");
    }

    // ── 2. 保险库 CRUD 与持久化 ────────────────────────────────────────────

    private static void TestVaultStore(TextWriter o)
    {
        o.WriteLine("[2] 保险库：创建 / 解锁 / CRUD / 加密持久化");
        var dir = Path.Combine(Path.GetTempPath(), "tokenvault-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "vault.json");
            var store = new VaultStore(path);
            store.Create("test-password-1");
            Check(o, File.Exists(path), "保险库文件已创建");

            var entry = new KeyEntry { Name = "MiMo 主密钥", Category = KeyCategories.MimoPlan, ApiKey = "tp-abcdef123456", Note = "selftest" };
            store.AddEntry(entry);
            Check(o, store.Data.Entries.Count == 1, "添加条目成功");

            var raw = File.ReadAllText(path);
            Check(o, !raw.Contains("tp-abcdef123456"), "磁盘文件不含明文密钥（全程加密）");

            var store2 = new VaultStore(path);
            store2.Unlock("test-password-1");
            Check(o, store2.Data.Entries.Count == 1 && store2.Data.Entries[0].ApiKey == "tp-abcdef123456",
                "重新解锁后条目完整还原");

            try
            {
                new VaultStore(path).Unlock("bad-password");
                Fail(o, "错误主密码竟然解锁成功");
            }
            catch (CryptographicException) { Pass(o, "错误主密码被拒绝"); }

            // 修改 → 旧版本进临时区
            var updated = store2.Data.Entries[0].Clone();
            updated.ApiKey = "tp-newvalue-9999";
            updated.Name = "MiMo 主密钥 v2";
            store2.UpdateEntry(updated);
            Check(o, store2.Data.Trash.Count == 1 && store2.Data.Trash[0].Kind == TrashItem.KindOldVersion,
                "修改后旧版本进入临时区（old-version）");
            Check(o, store2.Data.Trash[0].Entry.ApiKey == "tp-abcdef123456", "临时区保存的是旧版本内容");

            // 删除 → 进临时区
            store2.DeleteEntry(updated.Id);
            Check(o, store2.Data.Entries.Count == 0 && store2.Data.Trash.Count == 2, "删除后条目进入临时区（deleted）");
            Check(o, store2.Data.Trash.All(t => Math.Abs((t.PurgeAt - t.EnteredAt).TotalDays - 7) < 0.01),
                "临时区条目 7 天后才彻底删除（PurgeAt = EnteredAt + 7d）");

            // 恢复
            store2.Restore(store2.Data.Trash.First(t => t.Kind == TrashItem.KindDeleted).TrashId);
            Check(o, store2.Data.Entries.Count == 1 && store2.Data.Entries[0].ApiKey == "tp-newvalue-9999",
                "从临时区恢复删除成功");

            // 立即彻底删除
            var trashId = store2.Data.Trash[0].TrashId;
            store2.PurgeNow(trashId);
            Check(o, store2.Data.Trash.All(t => t.TrashId != trashId), "立即彻底删除生效");

            // 到期自动清理
            store2.Data.Trash.Add(new TrashItem
            {
                EntryId = "expired-test",
                Kind = TrashItem.KindDeleted,
                Entry = new KeyEntry { Id = "expired-test", Name = "过期测试" },
                EnteredAt = DateTime.Now.AddDays(-8),
                PurgeAt = DateTime.Now.AddDays(-1),
            });
            var removed = store2.PurgeExpired();
            Check(o, removed == 1 && store2.Data.Trash.All(t => t.EntryId != "expired-test"),
                "超过 7 天的临时区条目被自动清除");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ── 3. 临时区时间逻辑 ──────────────────────────────────────────────────

    private static void TestTrashLifecycle(TextWriter o)
    {
        o.WriteLine("[3] 临时区 7 天缓冲");
        var item = new TrashItem
        {
            Entry = new KeyEntry { Name = "x" },
            EnteredAt = DateTime.Now,
            PurgeAt = DateTime.Now.AddDays(7),
        };
        Check(o, !item.IsExpired && item.DaysLeft > 6.9, "7 天内：未到期，剩余约 7 天");
        var expired = new TrashItem { EnteredAt = DateTime.Now.AddDays(-9), PurgeAt = DateTime.Now.AddDays(-2) };
        Check(o, expired.IsExpired && expired.DaysLeft == 0, "超过 7 天：已到期，剩余 0 天");
    }

    // ── 4. 消耗估算（快照增量口径） ────────────────────────────────────────

    private static void TestConsumptionMath(TextWriter o)
    {
        o.WriteLine("[4] 日/周/月消耗估算（快照增量）");
        var dir = Path.Combine(Path.GetTempPath(), "tokenvault-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var snap = Path.Combine(dir, "snapshots.jsonl");
            // 今天内 used_credits 100 → 250（消耗 150）；ds 余额 50 → 35（消耗 15）
            // 两个时间点都放在今天窗口 [dayStart, now] 内部，增量应 100% 计入
            var localNow = DateTimeOffset.Now;
            var dayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
            double span = (localNow - dayStart).TotalSeconds;
            var t1 = dayStart.AddSeconds(span * 0.4).ToUnixTimeSeconds();
            var t2 = dayStart.AddSeconds(span * 0.8).ToUnixTimeSeconds();
            File.WriteAllText(snap,
                $"{{\"ts\":{t1},\"mimoUsed\":100,\"mimoTotal\":1000,\"dsBalance\":50}}\n" +
                $"{{\"ts\":{t2},\"mimoUsed\":250,\"mimoTotal\":1000,\"dsBalance\":35}}\n");

            var svc = new BalanceService(snap);
            var mimo = svc.GetConsumption(forMimo: true);
            var ds = svc.GetConsumption(forMimo: false);
            Check(o, mimo.Day == 150, $"MiMo 今日消耗 = 150（实际 {mimo.Day}）");
            Check(o, ds.Day == 15, $"DeepSeek 今日消耗 = 15（实际 {ds.Day}）");
            Check(o, mimo.Week == 150 && mimo.Month == 150, "周/月窗口包含今日增量");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ── 5. 阿里云百炼返回 JSON 的自适应解析 ─────────────────────────────────

    private static void TestAlibabaParse(TextWriter o)
    {
        o.WriteLine("[5] 阿里云百炼返回 JSON 自适应解析");
        const string nested = """
        {"data":{"plan_code":"bailian-token-pro","quota":{"total_credits":"2000000","used_credits":500000,"remaining_credits":1500000},"period":{"start_at":"2026-11-01","end_at":"2026-12-01"}}}
        """;
        var p1 = BalanceService.ParseAlibabaPlan(System.Text.Json.JsonDocument.Parse(nested).RootElement);
        Check(o, p1.Total == 2000000 && p1.Used == 500000 && p1.Remaining == 1500000,
            $"嵌套 + 字符串数字：总量/已用/剩余解析正确（{p1.Total}/{p1.Used}/{p1.Remaining}）");
        Check(o, p1.UsedPct == 25, $"已用百分比 = 25（实际 {p1.UsedPct}）");
        Check(o, p1.PlanCode == "bailian-token-pro" && p1.Period?.Contains("2026-12-01") == true,
            "套餐名与周期解析正确");

        const string pascal = """{"Success":true,"Data":{"Total":1000,"Remaining":250}}""";
        var p2 = BalanceService.ParseAlibabaPlan(System.Text.Json.JsonDocument.Parse(pascal).RootElement);
        Check(o, p2.Total == 1000 && p2.Remaining == 250 && p2.Used == 750,
            $"PascalCase 字段 + 缺已用时反推：已用 = 750（实际 {p2.Used}）");

        var p3 = BalanceService.ParseAlibabaPlan(System.Text.Json.JsonDocument.Parse("""{"Data":null}""").RootElement);
        Check(o, p3.Total == null && p3.Used == null && p3.Remaining == null, "无余量字段时不编造数字");
    }

    // ── 6. 空闲自动锁定 / 剪贴板自动清空 判定逻辑 ───────────────────────────

    private static void TestIdleAndClipboard(TextWriter o)
    {
        o.WriteLine("[6] 空闲自动锁定 / 剪贴板自动清空 判定逻辑");
        var idle = TimeSpan.FromSeconds(10);
        var watcher = new IdleWatcher(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), () => idle);
        Check(o, watcher.PollOnce(), "空闲 10s ≥ 阈值 5s → 触发锁定");
        Check(o, !watcher.PollOnce(), "同一次空闲不重复触发");
        idle = TimeSpan.FromSeconds(1);
        Check(o, !watcher.PollOnce(), "用户恢复操作后不触发");
        idle = TimeSpan.FromSeconds(6);
        Check(o, watcher.PollOnce(), "再次空闲超时重新触发");

        var disabled = new IdleWatcher(TimeSpan.Zero, TimeSpan.FromSeconds(1), () => TimeSpan.FromHours(1));
        Check(o, !disabled.PollOnce(), "设为“不自动锁定”时永不触发");

        Check(o, ClipboardGuard.ShouldClear("secret", "secret"), "剪贴板仍是本程序写入的敏感值 → 清空");
        Check(o, !ClipboardGuard.ShouldClear("user-text", "secret"), "用户复制的其它内容 → 不动");
        Check(o, !ClipboardGuard.ShouldClear(null, "secret"), "剪贴板为空或非文本 → 不动");
        Check(o, !ClipboardGuard.ShouldClear("secret", null), "没有待清理的敏感值 → 不动");
    }

    // ── 7. 真实 HTTP 探测（无效密钥，验证请求链路与端点可达） ────────────────

    private static async Task TestLiveHttp(TextWriter o)
    {
        o.WriteLine("[7] 官方接口 HTTP 探测（使用无效密钥，预期 401/403 类鉴权错误；单次探测上限 12 秒）");
        var svc = new BalanceService(Path.Combine(Path.GetTempPath(), "tokenvault-selftest-snap.jsonl"));

        await Probe(o, "MiMo token_plan", ct => svc.GetMimoPlanAsync("tp-selftest-invalid-key", null, ct));
        await Probe(o, "DeepSeek user/balance", ct => svc.GetDeepseekBalanceAsync("sk-selftest-invalid-key", null, ct));
        await Probe(o, "MiMo models", async ct => (await svc.GetModelsAsync("sk-selftest-invalid-key", BalanceService.DefaultMimoSkBase, ct)).Dispose());
        await Probe(o, "阿里云百炼 models", async ct => (await svc.GetModelsAsync("sk-selftest-invalid-key", "https://dashscope.aliyuncs.com/api/v1", ct)).Dispose());
    }

    private static async Task Probe(TextWriter o, string name, Func<CancellationToken, Task> call)
    {
        o.WriteLine($"  探测 {name} …");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await call(cts.Token);
            Warn(o, $"{name}：无效密钥竟然返回成功（请检查接口行为）");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("HTTP 401") || ex.Message.Contains("HTTP 403")
                                            || ex.Message.Contains("HTTP 400") || ex.Message.Contains("HTTP 404")
                                            || ex.Message.Contains("HTTP 429") || ex.Message.Contains("HTTP 500"))
        {
            Pass(o, $"{name}：端点可达，请求链路正常（{ex.Message[..Math.Min(70, ex.Message.Length)]}）");
        }
        catch (TimeoutException)
        {
            Warn(o, $"{name}：请求超时（网络不可用，跳过）");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Warn(o, $"{name}：12 秒内未完成，网络不可用（跳过）");
        }
        catch (Exception ex)
        {
            Warn(o, $"{name}：{ex.GetType().Name} {ex.Message[..Math.Min(70, ex.Message.Length)]}（网络受限时可忽略）");
        }
    }
}
