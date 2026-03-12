using System.Text.RegularExpressions;

namespace YtmUrlSharp;

/// <summary>
/// Masks IP addresses in URLs.
/// Replaces the last octet (/24 range assumption).
/// </summary>
public static partial class UrlMasker
{
    // Matches ip=1.2.3.4 or ip%3D1.2.3.4 in URL parameters
    [GeneratedRegex(@"(ip(?:=|%3D))(\d{1,3}\.\d{1,3}\.\d{1,3})\.\d{1,3}", RegexOptions.IgnoreCase)]
    private static partial Regex IpParamPattern();

    // Matches bare IPv4 in hostnames like rNNN---sn-xxx.googlevideo.com
    [GeneratedRegex(@"(\d{1,3}\.\d{1,3}\.\d{1,3})\.\d{1,3}(?=[\s&,;/\]]|$)")]
    private static partial Regex BareIpPattern();

    /// <summary>
    /// For logging — replaces last octet with "xxx".
    /// </summary>
    public static string MaskForLog(string url)
    {
        var result = IpParamPattern().Replace(url, "$1$2.xxx");
        result = BareIpPattern().Replace(result, "$1.xxx");
        return result;
    }

    /// <summary>
    /// For actual URL usage — replaces last octet with "1" (valid IP format).
    /// URL may still work if YouTube validates on /24.
    /// </summary>
    public static string MaskForUrl(string url)
    {
        var result = IpParamPattern().Replace(url, "$1$2.1");
        result = BareIpPattern().Replace(result, "$1.1");
        return result;
    }
}
