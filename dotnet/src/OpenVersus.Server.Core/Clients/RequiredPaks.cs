using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Realtime;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Clients;

// The OVS paks players must run, and telling the ones that do not to restart (2026-10-14): every client the version gate
// lets in closes itself when told to (update_required), so its next launch's startup update installs the paks. A player
// who still runs other paks is kept out of anything that leads into a match (ClientUpdateGate; Tuggernuts, 2026-10-28:
// one told nothing while busy could queue on old paks) and told again there.
//
//   What a player runs: the client's X-OVS-Paks header ("name=sha256" of each OVS_* pak's .utoc, of its .pak when it has
//     none, ";"-separated), on every game request; the login keeps it on the connection (connections:{id} paks). Paks
//     only change at a restart, which is a new login.
//   What they must run (Clients:RequiredPaks): release (the default) = the latest Clients:ReleaseRepo release's OVS_*
//     paks at GitHub's own sha256 digests, the release /ovs/client-version offers the client's startup update; off = none;
//     anything else = a literal "name=sha256;..." list (local tests). The access service reads it every five minutes, under a
//     lock so one replica does, and keeps it in Redis for every service (required_paks): one GitHub request per five
//     minutes for the whole server (unauthenticated: 60 an hour per IP). Fetch failed: the stored list stays.
//   Telling them (PakUpdateNotices): when the list changes, every online player whose paks differ and who is not busy
//     (in a match, queued, in a custom lobby's pregame, or between the games of a ranked set); and the players of every
//     match at its end (MatchEnd), the break a busy player gets (a set's players at the set's end). Once per player per list. Never at login: a player whose update keeps failing would be
//     closed at every launch.
//
// Redis, read     required_paks; connections:{id} paks; online_players; match_config:{id}; player:{id} status;
//                 ssc_custom_lobby_player:{id}; player_ranked_set:{id}
// Redis, written  required_paks (JSON {version, paks}); required_paks:lock NX EX 60 s; pak_update_notice:{id}:{version}
//                 NX EX 1 h; dll_notifications:{id} (update_required, PlayerMessages)

/// <summary>The paks players must run: <paramref name="Version"/> names the list (the release tag), <paramref name="Paks"/> name -> sha256.</summary>
public sealed record RequiredPakList(string Version, IReadOnlyDictionary<string, string> Paks);

/// <summary>The current required paks, as the access service last stored them; null when none are required.</summary>
public interface IRequiredPaks
{
    Task<RequiredPakList?> CurrentAsync();
}

/// <summary>The X-OVS-Paks form: "name=sha256" pairs separated by semicolons.</summary>
public static class PakReport
{
    public const string Header = "X-OVS-Paks";

    /// <summary>
    /// The client's other paks (2026-10-16): every pak the game mounts that is neither the game's own nor OVS_*, by name,
    /// ";"-separated. A player running any is kept out of online play (ClientUpdateGate), never out of the Lab.
    /// </summary>
    public const string OtherHeader = "X-OVS-Other-Paks";

    /// <summary>
    /// The other paks' names as stored: each trimmed, cut to 100 characters, anything but letters, digits and . _ - + as _
    /// (a name is never dropped for its characters: that would let its pak through), distinct, sorted; at most 20, joined
    /// with ";".
    /// </summary>
    public static string OtherNames(string? text)
    {
        var names = (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => new string(n[..Math.Min(n.Length, 100)].Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+' ? c : '_').ToArray()))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(20);
        return string.Join(";", names);
    }

    /// <summary>The pairs of <paramref name="text"/>, by name (any case), hashes in lower case; malformed items skipped.</summary>
    public static Dictionary<string, string> Parse(string? text)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            if (eq > 0 && eq < item.Length - 1)
            {
                pairs[item[..eq].Trim()] = item[(eq + 1)..].Trim().ToLowerInvariant();
            }
        }

        return pairs;
    }

    /// <summary>Whether <paramref name="reported"/> is exactly <paramref name="required"/>: the same names, each at the same hash.</summary>
    public static bool Matches(IReadOnlyDictionary<string, string> required, IReadOnlyDictionary<string, string> reported) =>
        required.Count == reported.Count
        && required.All(r => reported.TryGetValue(r.Key, out var hash) && string.Equals(hash, r.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What a release requires: each OVS_* pak group (the files sharing a name before the extension) at its .utoc's digest,
    /// or its .pak's when it has no .utoc. The .ucas and .sig are covered by the .utoc (it holds every chunk's hash).
    /// </summary>
    public static Dictionary<string, string> FromRelease(IEnumerable<ClientUpdateFile> files)
    {
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in files.Where(f => f.Kind == "paks").GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase))
        {
            var pick = group.FirstOrDefault(f => f.Name.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                ?? group.FirstOrDefault(f => f.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
            if (pick != null)
            {
                required[group.Key] = pick.Sha256.ToLowerInvariant();
            }
        }

        return required;
    }

    /// <summary>The pairs in the header's form, by name.</summary>
    public static string Format(IReadOnlyDictionary<string, string> paks) =>
        string.Join(';', paks.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>The stored list (required_paks), read again at most once a minute per process.</summary>
internal sealed class RequiredPaks(IServiceProvider services, TimeProvider time) : IRequiredPaks
{
    public const string Key = "required_paks";
    private static readonly TimeSpan s_reread = TimeSpan.FromMinutes(1);
    private (RequiredPakList? List, DateTimeOffset ReadAt)? _cached;

    public async Task<RequiredPakList?> CurrentAsync()
    {
        if (_cached is { } c && time.GetUtcNow() - c.ReadAt < s_reread)
        {
            return c.List;
        }

        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return null;
        }

        var list = Read(await redis.StringGetAsync(Key));
        _cached = (list, time.GetUtcNow());
        return list;
    }

    /// <summary>A stored list: {"version", "paks": {name: sha256}}; null for none or anything else.</summary>
    internal static RequiredPakList? Read(RedisValue stored)
    {
        if (stored.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(stored.ToString()) is JsonObject json && json["paks"] is JsonObject paks && json["version"] is JsonValue version
                && version.TryGetValue(out string? name))
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (pak, hash) in paks)
                {
                    if (hash is JsonValue v && v.TryGetValue(out string? sha))
                    {
                        map[pak] = sha.ToLowerInvariant();
                    }
                }

                return map.Count > 0 ? new RequiredPakList(name, map) : null;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    internal static string Write(RequiredPakList list)
    {
        var paks = new JsonObject();
        foreach (var (name, hash) in list.Paks.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            paks[name] = hash;
        }

        return Js.Stringify(new JsonObject { ["version"] = list.Version, ["paks"] = paks });
    }
}

/// <summary>
/// The access service's job: every five minutes, the required list from Clients:RequiredPaks into Redis (required_paks), under
/// a lock so one replica does it; when the list changes, the players who must restart are told (PakUpdateNotices).
/// </summary>
internal sealed class RequiredPaksRefresher(IServiceProvider services, IOptionsMonitor<ClientSettings> settings, TimeProvider time,
    ILogger<RequiredPaksRefresher> log) : BackgroundService
{
    public const string LockKey = "required_paks:lock";
    private static readonly TimeSpan s_interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_lockTime = TimeSpan.FromSeconds(60);
    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception e) when (e is RedisException or InvalidOperationException)
            {
                log.LogWarning("Required paks: could not refresh ({Error})", e.Message);
            }

            try
            {
                await Task.Delay(s_interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One refresh; true when the stored list changed (and the players were told).</summary>
    internal async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return false;
        }

        var (wanted, known) = await WantedAsync(ct);
        if (!known)
        {
            return false;
        }

        if (!await redis.StringSetAsync(LockKey, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), s_lockTime, When.NotExists))
        {
            return false;
        }

        try
        {
            var stored = RequiredPaks.Read(await redis.StringGetAsync(RequiredPaks.Key));
            if (Same(stored, wanted))
            {
                return false;
            }

            if (wanted is null)
            {
                await redis.KeyDeleteAsync(RequiredPaks.Key);
                log.LogInformation("Required paks: none (was {Version})", stored?.Version);
                return true;
            }

            await redis.StringSetAsync(RequiredPaks.Key, RequiredPaks.Write(wanted));
            log.LogInformation("Required paks: {Version} ({Paks}), was {Previous}", wanted.Version, PakReport.Format(wanted.Paks), stored?.Version ?? "none");
            int told = await PakUpdateNotices.ListChangedAsync(redis, wanted, settings.CurrentValue.PaksUpdateMessage, time);
            log.LogInformation("Required paks {Version}: {Count} online player(s) told to restart to update", wanted.Version, told);
            return true;
        }
        finally
        {
            await redis.KeyDeleteAsync(LockKey);
        }
    }

    // The list the setting asks for, and whether it is known (a release that could not be read is not: the stored one stays).
    private async Task<(RequiredPakList? List, bool Known)> WantedAsync(CancellationToken ct)
    {
        string mode = (settings.CurrentValue.RequiredPaks ?? "").Trim();
        if (mode.Length == 0 || mode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return (null, true);
        }

        if (!mode.Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            var literal = PakReport.Parse(mode);
            return (literal.Count > 0 ? new RequiredPakList("literal:" + ShortHash(PakReport.Format(literal)), literal) : null, true);
        }

        if (services.GetService<IClientReleases>() is not { } releases)
        {
            return (null, false);
        }

        try
        {
            var release = await releases.LatestAsync(ct);
            var paks = PakReport.FromRelease(ClientReleaseManifest.Build(release["assets"] as JsonArray ?? [], null, releases.Repo));
            string tag = release["tag_name"] is JsonValue t && t.TryGetValue(out string? name) ? name : "";
            _failing = false;
            return (paks.Count > 0 ? new RequiredPakList(tag.Length > 0 ? tag : "release:" + ShortHash(PakReport.Format(paks)), paks) : null, true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or ClientReleaseException)
        {
            if (!_failing)
            {
                _failing = true;
                log.LogWarning("Required paks: the release could not be read ({Error}); the stored list stays", e.Message);
            }

            return (null, false);
        }
    }

    private static bool Same(RequiredPakList? a, RequiredPakList? b) =>
        a is null || b is null ? a is null && b is null : a.Version == b.Version && PakReport.Matches(a.Paks, b.Paks);

    private static string ShortHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}

/// <summary>Telling a player to close the game so its next launch installs the required paks (update_required).</summary>
public static class PakUpdateNotices
{
    public const string Type = "update_required";
    public const string Title = "Update required";
    private static readonly TimeSpan s_once = TimeSpan.FromHours(1);

    /// <summary>A new required list: every online player whose paks differ and who is not busy (<see cref="BusyAsync"/>). How many were told.</summary>
    public static async Task<int> ListChangedAsync(IDatabase redis, RequiredPakList list, string message, TimeProvider time)
    {
        var online = (await redis.SetMembersAsync("online_players")).Select(m => m.ToString()).Where(id => id.Length > 0).ToList();
        var idle = new List<string>();
        foreach (string id in online)
        {
            if (!await BusyAsync(redis, id))
            {
                idle.Add(id);
            }
        }

        return await TellOutdatedAsync(redis, list, idle, message, time);
    }

    /// <summary>A match's end (MatchEnd): its players whose paks differ from the current list. Nothing without a list.</summary>
    public static async Task<int> MatchEndedAsync(IServiceProvider services, IDatabase redis, IReadOnlyList<string> playerIds, TimeProvider time)
    {
        if (services.GetService<IRequiredPaks>() is not { } required || await required.CurrentAsync() is not { } list)
        {
            return 0;
        }

        string message = services.GetService<IOptionsMonitor<ClientSettings>>()?.CurrentValue.PaksUpdateMessage ?? new ClientSettings().PaksUpdateMessage;
        return await TellOutdatedAsync(redis, list, playerIds, message, time);
    }

    /// <summary>
    /// Each of <paramref name="playerIds"/> whose login reported paks that are not <paramref name="list"/>'s is sent
    /// update_required, once per list (pak_update_notice:{id}:{version}). A player with no report (a session from before
    /// clients reported every pak) is left alone.
    /// </summary>
    internal static async Task<int> TellOutdatedAsync(IDatabase redis, RequiredPakList list, IEnumerable<string> playerIds, string message, TimeProvider time)
    {
        int told = 0;
        foreach (string id in playerIds.Where(id => id.Length > 0).Distinct())
        {
            var reported = await redis.HashGetAsync($"connections:{id}", "paks");
            if (!Outdated(list, reported.IsNullOrEmpty ? null : reported.ToString()))
            {
                continue;
            }

            if (!await redis.StringSetAsync($"pak_update_notice:{id}:{list.Version}", "1", s_once, When.NotExists))
            {
                continue;
            }

            await SendAsync(redis, id, list, message, time);
            told++;
        }

        return told;
    }

    /// <summary>
    /// Whether a login that reported <paramref name="reported"/> runs other paks than <paramref name="list"/>'s. No list, or
    /// no report (a session from before clients reported every pak): no.
    /// </summary>
    public static bool Outdated(RequiredPakList? list, string? reported) =>
        list is not null && !string.IsNullOrEmpty(reported) && !PakReport.Matches(list.Paks, PakReport.Parse(reported));

    /// <summary>Sends <paramref name="playerId"/> update_required: the game closes, and its next launch installs <paramref name="list"/>.</summary>
    public static Task SendAsync(IDatabase redis, string playerId, RequiredPakList list, string message, TimeProvider time) =>
        PlayerMessages.NotifyClientAsync(redis, playerId, Type, Title, message, new JsonObject { ["version"] = list.Version },
            time.GetUtcNow().ToUnixTimeMilliseconds());

    /// <summary>
    /// Whether a player is not to be interrupted: in a match (its config is kept until the match ends, GameplayConfigs,
    /// deleted by MatchEnd; or the matchmaker has them in_match), queued, in a custom lobby (its pregame), or between the
    /// games of a ranked set (Tuggernuts, 2026-10-14). They are told at their match's (their set's) end instead.
    /// </summary>
    internal static async Task<bool> BusyAsync(IDatabase redis, string playerId) =>
        await redis.KeyExistsAsync(Matches.GameplayConfigs.Key(playerId))
        || (string?)await redis.HashGetAsync($"player:{playerId}", "status") is "in_match" or "queued"
        || await redis.KeyExistsAsync(CustomLobbies.CustomLobbyService.PlayerKey(playerId))
        || await redis.KeyExistsAsync($"player_ranked_set:{playerId}");
}

public static class RequiredPaksHosting
{
    /// <summary>The stored required list, for the services that tell players at a match's end.</summary>
    public static WebApplicationBuilder AddRequiredPaks(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IRequiredPaks, RequiredPaks>();
        return builder;
    }

    /// <summary>The access service's refresh of the required list (needs <see cref="ClientReleasesHosting.AddClientReleases"/>).</summary>
    public static WebApplicationBuilder AddRequiredPaksRefresher(this WebApplicationBuilder builder)
    {
        builder.AddRequiredPaks();
        builder.Services.AddHostedService<RequiredPaksRefresher>();
        return builder;
    }
}
