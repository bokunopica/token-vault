using System.IO;

namespace TokenVault.Services;

/// <summary>轻量诊断日志：%LOCALAPPDATA%\TokenVault\app.log（不记录任何密钥/密码内容）。</summary>
public static class Diag
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenVault", "app.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (Gate)
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响主功能 */ }
    }
}
