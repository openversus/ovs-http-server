using System.Text.RegularExpressions;

namespace OpenVersus.Server.Http.Hosting;

/// <summary>
/// The client's address behind the reverse proxy: the first of X-Real-IP, X-Forwarded-Host and the last entry of
/// X-Forwarded-For that is an IP address (IPv4, or IPv6 in any form, as Node's net.isIP takes it), else the
/// connection's own address. The same rule as the TS server's clientIpFromHeaders (src/utils/clientIp.ts, which the
/// HTTP routes and the websocket server both use), so every IP-keyed record (identity:{ip}, connections:{ip},
/// active_ip_accounts:{ip}, ...) is the same one from either server.
/// <para>
/// X-Forwarded-For's last entry is the address the proxy saw; the entries before it are whatever the client sent, so
/// they are never used. The headers are trusted as they arrive, so only the reverse proxy should be able to reach this
/// service.
/// </para>
/// </summary>
public static partial class ClientAddress
{
    // Node's net.isIPv4 / isIPv6 (lib/internal/net.js), anchored at the true end of the string as JavaScript's $ is.
    private const string V4Seg = "(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9][0-9]|[0-9])";
    private const string V4 = $@"(?:{V4Seg}\.){{3}}{V4Seg}";
    private const string V6Seg = "(?:[0-9a-fA-F]{1,4})";

    [GeneratedRegex($"^{V4}\\z")]
    private static partial Regex IPv4();

    [GeneratedRegex("^(?:" +
        $"(?:{V6Seg}:){{7}}(?:{V6Seg}|:)|" +
        $"(?:{V6Seg}:){{6}}(?:{V4}|:{V6Seg}|:)|" +
        $"(?:{V6Seg}:){{5}}(?::{V4}|(?::{V6Seg}){{1,2}}|:)|" +
        $"(?:{V6Seg}:){{4}}(?:(?::{V6Seg}){{0,1}}:{V4}|(?::{V6Seg}){{1,3}}|:)|" +
        $"(?:{V6Seg}:){{3}}(?:(?::{V6Seg}){{0,2}}:{V4}|(?::{V6Seg}){{1,4}}|:)|" +
        $"(?:{V6Seg}:){{2}}(?:(?::{V6Seg}){{0,3}}:{V4}|(?::{V6Seg}){{1,5}}|:)|" +
        $"(?:{V6Seg}:){{1}}(?:(?::{V6Seg}){{0,4}}:{V4}|(?::{V6Seg}){{1,6}}|:)|" +
        $"(?::(?:(?::{V6Seg}){{0,5}}:{V4}|(?::{V6Seg}){{1,7}}|:))" +
        ")(?:%[0-9a-zA-Z\\-.:]{1,})?\\z")]
    private static partial Regex IPv6();

    public static bool IsIPAddress(string value) => IPv4().IsMatch(value) || IPv6().IsMatch(value);

    /// <summary>
    /// The client IP. <paramref name="stripMapped"/> removes an IPv4-mapped prefix (::ffff:), as /access does; the TS
    /// server's other routes keep it.
    /// </summary>
    public static string Of(HttpContext context, bool stripMapped = false)
    {
        var headers = context.Request.Headers;
        string[] forwardedFor = headers["x-forwarded-for"].ToString().Split(',');
        string ip = new[] { headers["x-real-ip"].ToString().Trim(), headers["x-forwarded-host"].ToString().Trim(), forwardedFor[^1].Trim() }
            .FirstOrDefault(value => value.Length > 0 && IsIPAddress(value))
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "";
        return stripMapped && ip.StartsWith("::ffff:", StringComparison.Ordinal) ? ip["::ffff:".Length..] : ip;
    }
}
