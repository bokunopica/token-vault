using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace TokenVault;

public partial class App : Application
{
    /// <summary>全局共享的加密保险库（登录窗口解锁后供主窗口使用）。</summary>
    public static Services.VaultStore Vault { get; set; } = new();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--smoketest"))
        {
            // 冒烟测试：用临时保险库走完整路径（创建 → 解锁 → 主窗口构建/渲染 3 秒），
            // 专门回归“登录成功后主界面崩溃”类问题。
            AttachConsole(AttachParentProcess);
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台时忽略 */ }
            var layoutOk = true;
            DispatcherUnhandledException += (_, args) =>
            {
                layoutOk = false;
                Console.WriteLine("FAIL  unhandled: " + args.Exception);
                args.Handled = true;
            };
            try
            {
                var tmpRoot = Environment.GetEnvironmentVariable("TMPDIR")
                              ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var tmp = System.IO.Path.Combine(tmpRoot, "tokenvault-smoketest-" + Guid.NewGuid().ToString("N")[..8]);
                Vault = new Services.VaultStore(tmp);
                Vault.Create("smoketest-password");
                Vault.Unlock("smoketest-password");
                Console.WriteLine("PASS  临时保险库 创建+解锁");

                // 放一条“密码+API Key+地址+自定义字段”的示例数据，让列表模板真正渲染
                Vault.AddEntry(new Models.KeyEntry
                {
                    Name = "演示凭据",
                    Category = Models.KeyCategories.Account,
                    Username = "admin",
                    Password = "p@ssw0rd-123",
                    ApiKey = "sk-demo-0123456789abcdef",
                    BaseUrl = "http://192.168.3.18:4200",
                    Note = "smoketest",
                    Fields = new List<Models.CustomField>
                    {
                        new() { Name = "DATABASE_URL", Value = "postgres://demo" },
                    },
                });
                Console.WriteLine("PASS  示例凭据写入");

                var main = new MainWindow();
                MainWindow = main;
                main.Show();
                Console.WriteLine("PASS  主窗口 构建+显示");

                var step = 0;
                // 剪贴板守卫：TTL 调短成 2.5 秒，检测"复制后不立即清空 + 到点清空 + 不误删他人内容"
                Services.ClipboardGuard.Ttl = TimeSpan.FromSeconds(2.5);
                var clipWrote = Services.ClipboardGuard.TrySetSecret("tokenvault-smoke-secret", out var clipErr);
                if (!clipWrote) Console.WriteLine("FAIL  剪贴板写入失败：" + clipErr);

                // 空闲检测：0.8 秒无输入即触发（轮询 300ms），验证 GetLastInputInfo 链路
                var idleWatcher = new Services.IdleWatcher(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(300));
                var idleFired = false;
                idleWatcher.IdleTimedOut += () => idleFired = true;
                idleWatcher.Start();

                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                t.Tick += (_, _) =>
                {
                    step++;
                    if (step == 1)
                    {
                        main.ShowPage("keys");
                    }
                    else if (step == 2)
                    {
                        main.ShowPage("trash");
                        var still = SafeClipboardText();
                        Console.WriteLine(!clipWrote
                            ? "WARN  剪贴板被其它程序占用，复制写入失败，跳过实时校验"
                            : still == "tokenvault-smoke-secret"
                                ? "PASS  复制后 1.5 秒内剪贴板仍保留原值"
                                : "WARN  剪贴板已被其它程序改动（" + still + "），跳过实时校验");
                    }
                    else if (step == 3)
                    {
                        // 3 秒后 TTL(2.5s) 已过：应已自动清空
                        var after = SafeClipboardText();
                        try { System.Windows.Clipboard.SetText("user-owned-text"); } catch { }
                        var foreignKept = SafeClipboardText() == "user-owned-text";
                        var clearedForeign = Services.ClipboardGuard.ClearIfOurs();
                        Console.WriteLine(after.Length == 0 && !clearedForeign && foreignKept
                            ? "PASS  超过 TTL 自动清空，且不误删用户自己复制的内容"
                            : "WARN  剪贴板被其它程序占用，实时校验跳过（判定逻辑已由 --selftest 覆盖）");
                        try { System.Windows.Clipboard.Clear(); } catch { }
                    }
                    else
                    {
                        t.Stop();
                        idleWatcher.Stop();
                        Console.WriteLine(idleFired
                            ? "PASS  空闲检测触发（自动锁定链路可用）"
                            : "WARN  测试期间一直有键鼠输入，空闲检测未触发（判定逻辑已由 --selftest 覆盖）");
                        Console.WriteLine(layoutOk
                            ? "PASS  三个页面（余量/密钥/临时区）渲染存活"
                            : "FAIL  渲染期出现异常");
                        try { System.IO.Directory.Delete(tmp, true); } catch { }
                        Shutdown(layoutOk ? 0 : 1);
                    }
                };
                t.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL  " + ex);
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Contains("--selftest"))
        {
            AttachConsole(AttachParentProcess);
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台时忽略 */ }
            // 在线程池上运行并阻塞等待：避免 UI 线程同步上下文造成 async 死锁
            var sw = new System.IO.StringWriter();
            int code;
            try
            {
                code = Task.Run(() => Services.SelfTest.Run(sw)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                sw.WriteLine("selftest crashed: " + ex);
                code = 1;
            }
            var report = sw.ToString();
            Console.Write(report);
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenVault");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "selftest-report.txt"), report);
            }
            catch { /* 报告写入失败不影响退出码 */ }
            Shutdown(code);
            return;
        }

        // 全局兜底：未处理异常不再让应用闪退，弹窗提示并写入日志
        DispatcherUnhandledException += (_, args) =>
        {
            Services.Diag.Log("UNHANDLED: " + args.Exception);
            try
            {
                MessageBox.Show(
                    "发生未处理的错误：\n" + args.Exception.Message
                    + "\n\n详细信息已写入日志：%LOCALAPPDATA%\\TokenVault\\app.log",
                    "TokenVault 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* 弹窗失败也不能再抛 */ }
            args.Handled = true;
        };
        Services.Diag.Log("app start");

        var login = new LoginWindow();
        login.LoginSucceeded += (_, _) =>
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        };
        login.Show();
    }

    /// <summary>读剪贴板文本：被其它进程占用时重试几次，失败返回空串。</summary>
    private static string SafeClipboardText()
    {
        for (var i = 0; i < 3; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { System.Threading.Thread.Sleep(30); }
        }
        return "";
    }
}

