using System.Text.Json;
using System.Text.RegularExpressions;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Identity;

namespace OpenVersus.Server.Http.Shared.Hosting;

/// <summary>What the TS server's resolveAccountFromRequest reads from a request (see <see cref="IAccountResolver"/>).</summary>
public static partial class AccountLookups
{
    public static AccountLookup From(HttpContext context)
    {
        var claims = context.Session()?.Claims;
        // safeDecodeToken: a token without an id is no token.
        var token = Str(claims, "id") is { Length: > 0 } id
            ? new AccountLookup.TokenFields(id, Str(claims, "steamId"), Str(claims, "epicId"), Str(claims, "installId"), Str(claims, "current_ip"))
            : null;
        var headers = context.Request.Headers;
        return new AccountLookup(token, Header(headers, "x-steam-id"), Header(headers, "x-epic-id"), Header(headers, "x-install-id"), RequestIp(context, Str(claims, "current_ip")));
    }

    /// <summary>
    /// TS GetReqIP: the IPv4 address in the request's real IP (::ffff: allowed), else in the token's current_ip, else null.
    /// </summary>
    private static string? RequestIp(HttpContext context, string? currentIp)
    {
        foreach (string? candidate in new[] { ClientAddress.Of(context), currentIp })
        {
            if (candidate is not null && Ipv4().Match(candidate) is { Success: true } match)
            {
                return match.Groups["ip"].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^(::ffff:)?(?<ip>(\d{1,3}\.){3}\d{1,3})$")]
    private static partial Regex Ipv4();

    // Node joins a repeated header with ", ".
    private static string? Header(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0 ? string.Join(", ", values.ToArray()) : null;

    private static string? Str(System.Text.Json.Nodes.JsonObject? claims, string name) =>
        claims?[name] is { } node && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}
