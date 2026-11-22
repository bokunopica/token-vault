namespace TokenVault.Services;

/// <summary>把用户填的地址规范成可点击的网页链接（只允许 http/https）。</summary>
public static class UrlHelper
{
    public static bool TryNormalize(string? raw, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (s.Contains(' ')) return false;

        if (s.Contains("://"))
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var u)
                && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            {
                url = s;
                return true;
            }
            return false;   // postgres:// mysql:// 等不当作网页
        }

        // 无协议：主机名 / IP / IP:端口 / 带路径 → 补 http://
        var candidate = "http://" + s;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var u2) || string.IsNullOrEmpty(u2.Host)) return false;
        var host = u2.Host.Trim('[', ']');
        if (host.Contains('.') || host == "localhost"
            || System.Net.IPAddress.TryParse(host, out _)
            || s.Contains(':'))
        {
            url = candidate;
            return true;
        }
        return false;
    }
}
