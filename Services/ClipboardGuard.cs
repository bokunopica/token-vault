using System.Windows;
using System.Windows.Threading;

namespace TokenVault.Services;

/// <summary>
/// 复制敏感值（密码 / API Key）到剪贴板，并在 30 秒后自动清空。
/// 清空的前提是剪贴板内容仍是当初复制的那一段——用户自己复制了别的东西就不动它。
/// </summary>
public static class ClipboardGuard
{
    /// <summary>敏感值在剪贴板上的存活时间（默认 30 秒）。</summary>
    public static TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    private static string? _secret;
    private static DispatcherTimer? _timer;

    /// <summary>复制并安排自动清空。返回 false 时 error 里带失败原因（通常是剪贴板被其它进程占用）。</summary>
    public static bool TrySetSecret(string value, out string error)
    {
        error = "";
        Exception? last = null;
        for (var i = 0; i < 6; i++)
        {
            try
            {
                Clipboard.SetText(value);
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                System.Threading.Thread.Sleep(60);   // 剪贴板常被其它进程短暂占用（CLIPBRD_E_CANT_OPEN）
            }
        }
        if (last != null)
        {
            error = last.Message;
            return false;
        }

        _secret = value;
        RestartTimer();
        return true;
    }

    /// <summary>若剪贴板里仍是上次复制的敏感值，则清空。</summary>
    public static bool ClearIfOurs()
    {
        if (_secret == null) return false;
        try
        {
            var current = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            if (!ShouldClear(current, _secret)) return false;
            Clipboard.Clear();
            return true;
        }
        catch (Exception) { return false; }
        finally { _secret = null; }
    }

    /// <summary>判定是否该清空：只清自己写入且内容未变的那一段，用户复制了别的东西就不动。</summary>
    public static bool ShouldClear(string? currentClipboard, string? ourSecret)
        => ourSecret != null && currentClipboard != null && string.Equals(currentClipboard, ourSecret, StringComparison.Ordinal);

    /// <summary>立即清空（锁库时调用，不论剪贴板是否还是我们写入的值都不强删，只在匹配时清）。</summary>
    public static void ClearNow()
    {
        _timer?.Stop();
        ClearIfOurs();
        _secret = null;
    }

    private static void RestartTimer()
    {
        if (_timer == null || _timer.Interval != Ttl)
        {
            _timer?.Stop();
            _timer = new DispatcherTimer { Interval = Ttl };
            _timer.Tick += (_, _) =>
            {
                _timer!.Stop();
                ClearIfOurs();
            };
        }
        _timer.Stop();
        _timer.Start();
    }
}
