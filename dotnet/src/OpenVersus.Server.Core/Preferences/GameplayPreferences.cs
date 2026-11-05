using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Preferences;

// GameplayPreferences carries the player's own input settings: stick deadzones, the input buffer, whether an attack press
// picks up items. The value the game sends is the player's, and it must reach every match unchanged; a wrong one changes
// how the game feels. 0 is a real value (three prod players have it), not a missing one.
//
// The game sends its current value in every party-lobby request (create_party_lobby at each login, lock_lobby_loadout,
// set_ready_for_lobby, set_lobby_joinable, set_lobby_not_joinable) and rarely, if ever, calls update_player_preferences;
// the server keeps the value it last saw, and every match reads that one. The TS server (utils/gameplayPreferences.ts,
// services/gameplayPreferences.ts, fixed 2026-09-30) keeps it the same way: same rules, same stores.
//
// Mongo, written  playertesters {_id, GameplayPreferences: {$ne: v}} $set GameplayPreferences (an int32 when it fits)
// Redis, written  connections:{id} GameplayPreferences (text), only when the session exists and differs;
//                 connections:{ip} GameplayPreferences, only while that legacy copy is this player's (its id)

/// <summary>Reading a GameplayPreferences value (see the file's header).</summary>
public static class GameplayPreferences
{
    /// <summary>What the server assumes for a player whose value it has never seen (the new-account default).</summary>
    public const long Default = 964;

    /// <summary>
    /// The value as the whole number it is: a JSON number that is an integer, or text of one written plainly (digits, an
    /// optional minus). Anything else (missing, null, "", text "1e3" or "0x10", "abc", 1.5, true) is null: not a value to
    /// store. The same rules as the TS server's parseGameplayPreferences.
    /// </summary>
    public static long? Parse(JsonNode? value) => value switch
    {
        // A JSON number by its value, as JS reads it (1000.0 and 1e3 are 1000).
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) is var d && d == Math.Floor(d) && Math.Abs(d) <= 9007199254740991 ? (long)d : null,
        JsonValue v when v.TryGetValue(out string? s) => Parse(s),
        _ => null,
    };

    /// <summary>As <see cref="Parse(JsonNode?)"/>, for text (a stored session field).</summary>
    public static long? Parse(string? text) =>
        !string.IsNullOrEmpty(text) && (text[0] == '-' ? text.Length > 1 && text.Skip(1).All(char.IsAsciiDigit) : text.All(char.IsAsciiDigit))
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) && Safe(n) ? n : null;

    /// <summary>The value to play with: the stored one when it is a value (0 included), else <see cref="Default"/>.</summary>
    public static long Of(string? stored) => Parse(stored) ?? Default;

    // Number.isSafeInteger.
    private static bool Safe(long n) => n is >= -9007199254740991 and <= 9007199254740991;
}

public interface IGameplayPreferencesStore
{
    /// <summary>
    /// Stores <paramref name="raw"/> as the player's GameplayPreferences when it is a value; nothing is written otherwise
    /// (never the default over a real value). The stored value, or null.
    /// </summary>
    Task<long?> SaveAsync(string accountId, JsonNode? raw, string? ip, CancellationToken ct);
}

internal sealed class GameplayPreferencesStore(IServiceProvider services, ILogger<GameplayPreferencesStore> log) : IGameplayPreferencesStore
{
    public async Task<long?> SaveAsync(string accountId, JsonNode? raw, string? ip, CancellationToken ct)
    {
        if (GameplayPreferences.Parse(raw) is not { } value || !ObjectId.TryParse(accountId, out var id))
        {
            return null;
        }

        var mongo = services.GetService<IMongoDatabase>() ?? throw new InvalidOperationException("this service has no Mongo (MONGODB_URI)");
        var redis = services.GetService<IConnectionMultiplexer>()?.GetDatabase() ?? throw new InvalidOperationException("this service has no Redis (REDIS)");
        // As the TS driver stores a JS number: an int32 when it fits, else a double.
        BsonValue stored = value is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)value) : new BsonDouble(value);
        await mongo.GetCollection<BsonDocument>("playertesters").UpdateOneAsync(
            new BsonDocument { { "_id", id }, { "GameplayPreferences", new BsonDocument("$ne", stored) } },
            new BsonDocument("$set", new BsonDocument("GameplayPreferences", stored)),
            cancellationToken: ct);

        string text = value.ToString(CultureInfo.InvariantCulture);
        string key = $"connections:{accountId}";
        var session = await redis.HashGetAsync(key, "GameplayPreferences");
        if (session != text && await redis.KeyExistsAsync(key))
        {
            await redis.HashSetAsync(key, "GameplayPreferences", text);
        }

        // The legacy IP-keyed copy of the session, only while it is this player's (a household shares an IP).
        // Compared as text: RedisValue == string compares numbers when both look like one, and two ids such as
        // 0000000000000000000e0001 and ...0e0002 (both 0) or two long all-digit ids (the same double) would match.
        if (!string.IsNullOrEmpty(ip) && (string?)await redis.HashGetAsync($"connections:{ip}", "id") == accountId)
        {
            await redis.HashSetAsync($"connections:{ip}", "GameplayPreferences", text);
        }

        if (session != text)
        {
            log.LogInformation("GameplayPreferences for {Account}: {Old} -> {New}", accountId, session.HasValue ? session.ToString() : "none", text);
        }

        return value;
    }
}

public static class GameplayPreferencesHosting
{
    public static WebApplicationBuilder AddGameplayPreferences(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IGameplayPreferencesStore, GameplayPreferencesStore>();
        return builder;
    }
}
