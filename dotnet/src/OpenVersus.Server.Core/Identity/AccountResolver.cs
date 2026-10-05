using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenVersus.Server.Core.Access;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Identity;

// Which player a request is from, ported from the TS server's resolveAccountFromRequest (services/identityService.ts,
// branch infinity-war): the session token's id, then the Steam, Epic and install ids, then the IP.
//
// Redis, read     connections:{id} (the session hash; a hit is one with a non-empty id field, and that field is the
//                 answer, not the key), identity:steam|epic|install:{id} (a player id)
// Redis, written  active_ip_accounts:{ip}: entries older than 90 s are removed before it is read (as the TS server does)

/// <summary>What the TS server reads from a request to find its player.</summary>
/// <param name="Token">The session token's fields, or null when it has no id (the TS server then ignores it).</param>
/// <param name="SteamHeader">x-steam-id; <paramref name="EpicHeader"/> x-epic-id; <paramref name="InstallHeader"/> x-install-id.</param>
/// <param name="RequestIp">The request's IPv4 address (TS GetReqIP), or null.</param>
public sealed record AccountLookup(AccountLookup.TokenFields? Token, string? SteamHeader, string? EpicHeader, string? InstallHeader, string? RequestIp)
{
    public sealed record TokenFields(string Id, string? SteamId, string? EpicId, string? InstallId, string? CurrentIp);
}

/// <summary>A resolved player: their session hash (connections:{id}) and which step found it.</summary>
public sealed record ResolvedAccount(string Id, HashEntry[] Connection, string Source);

public interface IAccountResolver
{
    /// <summary>The player the request is from, or null when nothing identifies one.</summary>
    Task<ResolvedAccount?> ResolveAsync(AccountLookup lookup);
}

internal sealed class AccountResolver(IServiceProvider services, TimeProvider time) : IAccountResolver
{
    private static readonly TimeSpan s_activeSession = TimeSpan.FromSeconds(90);

    public async Task<ResolvedAccount?> ResolveAsync(AccountLookup lookup)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return null;
        }

        var token = lookup.Token;
        if (!string.IsNullOrEmpty(token?.Id) && await ConnectionAsync(redis, token.Id, "jwt") is { } byToken)
        {
            return byToken;
        }

        // req.header("x-steam-id") || token?.steamId, normalized; then the index it names.
        foreach (var (kind, header, fromToken, source) in new[]
        {
            (IdentityKind.Steam, lookup.SteamHeader, token?.SteamId, "steam"),
            (IdentityKind.Epic, lookup.EpicHeader, token?.EpicId, "epic"),
            (IdentityKind.Install, lookup.InstallHeader, token?.InstallId, "install"),
        })
        {
            string id = IdentityRules.Normalize(kind, string.IsNullOrEmpty(header) ? fromToken : header);
            if (id.Length > 0 && await redis.StringGetAsync($"identity:{source}:{id}") is { HasValue: true } accountId
                && await ConnectionAsync(redis, accountId.ToString(), source) is { } byIndex)
            {
                return byIndex;
            }
        }

        // The IP, last: the token's current_ip (::ffff: removed), then the request's; the one player active there.
        string? tokenIp = string.IsNullOrEmpty(token?.CurrentIp) ? null
            : token.CurrentIp.StartsWith("::ffff:", StringComparison.Ordinal) ? token.CurrentIp[7..] : token.CurrentIp;
        foreach (string ip in new[] { tokenIp, lookup.RequestIp }.Where(ip => !string.IsNullOrEmpty(ip)).Distinct().Cast<string>())
        {
            var key = (RedisKey)$"active_ip_accounts:{ip}";
            await redis.SortedSetRemoveRangeByScoreAsync(key, 0, time.GetUtcNow().ToUnixTimeMilliseconds() - s_activeSession.TotalMilliseconds);
            var active = await redis.SortedSetRangeByRankAsync(key);
            if (active.Length == 1 && await ConnectionAsync(redis, active[0].ToString(), "ip") is { } byIp)
            {
                return byIp;
            }
        }

        return null;
    }

    private static async Task<ResolvedAccount?> ConnectionAsync(IDatabase redis, string accountId, string source)
    {
        var hash = await redis.HashGetAllAsync($"connections:{accountId}");
        string? id = hash.FirstOrDefault(e => e.Name == "id").Value;
        return string.IsNullOrEmpty(id) ? null : new ResolvedAccount(id, hash, source);
    }
}

public static class AccountResolverHosting
{
    public static WebApplicationBuilder AddAccountResolver(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IAccountResolver, AccountResolver>();
        return builder;
    }
}
