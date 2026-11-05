using System.Text.Json;
using System.Text.RegularExpressions;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Identity;

namespace OpenVersus.Server.Http.Shared.Hosting;

/// <summary>
/// What a request's player is found by (see <see cref="IAccountResolver"/>): its verified token and its IP. Never the
/// x-steam-id, x-epic-id or x-install-id headers the TS server's resolveAccountFromRequest also read: anyone can send them.
/// </summary>
public static partial class AccountLookups
{
    public static AccountLookup From(HttpContext context) => From(context, context.Session()?.Claims);

    /// <summary>As <see cref="From(HttpContext)"/>, with claims verified some other way (a route that checks its token itself).</summary>
    public static AccountLookup From(HttpContext context, System.Text.Json.Nodes.JsonObject? claims)
    {
        // safeDecodeToken: a token without an id is no token.
        var token = Str(claims, "id") is { Length: > 0 } id
            ? new AccountLookup.TokenFields(id, Str(claims, "steamId"), Str(claims, "epicId"), Str(claims, "installId"), Str(claims, "current_ip"))
            : null;
        return new AccountLookup(token, RequestIp(context, Str(claims, "current_ip")));
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

    private static string? Str(System.Text.Json.Nodes.JsonObject? claims, string name) =>
        claims?[name] is { } node && node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}
