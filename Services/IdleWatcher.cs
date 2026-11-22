using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TokenVault.Services;

/// <summary>
/// 空闲检测：用 Win32 GetLastInputInfo 取"全系统最后一次键鼠输入"距今多久，
/// 超过 IdleTimeout 触发 IdleTimedOut（同一次空闲只触发一次，用户恢复操作后重新计时）。
/// </summary>
public sealed class IdleWatcher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private readonly DispatcherTimer _timer;
    private readonly Func<TimeSpan> _idleProvider;
    private bool _fired;

    public IdleWatcher(TimeSpan timeout, TimeSpan? pollInterval = null, Func<TimeSpan>? idleProvider = null)
    {
        IdleTimeout = timeout;
        _idleProvider = idleProvider ?? GetIdleTime;
        _timer = new DispatcherTimer { Interval = pollInterval ?? TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => Poll();
    }

    public TimeSpan IdleTimeout { get; set; }

    public event Action? IdleTimedOut;

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        _fired = false;
    }

    public static TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    /// <summary>一次轮询判定：返回 true 表示"这次刚刚越过阈值"（同一次空闲只报一次）。</summary>
    public bool PollOnce()
    {
        if (IdleTimeout <= TimeSpan.Zero) return false;
        var idle = _idleProvider();
        if (idle >= IdleTimeout)
        {
            if (_fired) return false;
            _fired = true;
            return true;
        }
        _fired = false;
        return false;
    }

    private void Poll()
    {
        if (PollOnce()) IdleTimedOut?.Invoke();
    }

    public void Dispose() => _timer.Stop();
}
