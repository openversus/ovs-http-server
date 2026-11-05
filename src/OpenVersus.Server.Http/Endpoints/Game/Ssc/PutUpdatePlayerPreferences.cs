using System.Text.Json.Nodes;
using FastEndpoints;
using MongoDB.Driver;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_player_preferences {AutoPartyPreference, CrossplayPreference, GameplayPreferences, ...}: the
/// player's GameplayPreferences (their input settings), stored only when it is a value (<see cref="IGameplayPreferencesStore"/>;
/// a missing or unreadable one leaves the stored value as it is). Answers {body: {}}, whatever happened, as the TS server
/// does. The game sends the same value with every party-lobby request too (<see cref="GameplayPreferencesRecorder"/>).
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_player_preferences.
/// </summary>
public sealed class PutUpdatePlayerPreferences : JsonBodyEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/update_player_preferences");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var session = HttpContext.Session();
        var raw = (await ReadBodyAsync(ct) as JsonObject)?["GameplayPreferences"];
        try
        {
            string? ip = session?.Claims["current_ip"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
            if (await Resolve<IGameplayPreferencesStore>().SaveAsync(session?.AccountId ?? "", raw, ip, ct) is null)
            {
                Logger.LogWarning("update_player_preferences for {Account}: GameplayPreferences {Value} not stored", session?.AccountId, raw?.ToJsonString() ?? "missing");
            }
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or RedisException or TimeoutException)
        {
            Logger.LogError("Error updating GameplayPreferences for player {Account}: {Error}", session?.AccountId, e.Message);
        }

        await SendJsonAsync(new JsonObject { ["body"] = new JsonObject(), ["metadata"] = null, ["return_code"] = 0 }, ct);
    }
}
