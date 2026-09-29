using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace OpenVersus.Server.Core.Access;

/// <summary>What the /access response says about this login.</summary>
/// <param name="Realtime">configuration.realtime (see <see cref="RealtimeSettings.Configuration"/>).</param>
/// <param name="ProfileIconAssetPath">Null when the icon is not in the catalog: the TS server sends undefined, a NaN.</param>
public sealed record LoginValues(
    string Token,
    JsonObject Realtime,
    string AccountId,
    string ProfileId,
    string PublicId,
    string WbNetworkId,
    string Username,
    string HydraUsername,
    string ProfileIcon,
    string? ProfileIconAssetPath,
    string IdentityAvatarUrl,
    string SteamAvatarUrl);

/// <summary>
/// The /access response: login-response.json (generated from the TS server's literal by
/// tools/access/gen_templates.mjs) with its markers filled, and the stat trackers computed from the player's
/// ratings and match stats as the TS server does. Most of it is a fixed account from the live service's last days;
/// moving that out into data is planned once this route runs.
/// </summary>
public static class LoginResponse
{
    private static readonly Lazy<JsonObject> s_template = new(() =>
    {
        using var stream = typeof(LoginResponse).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Access.login-response.json")
            ?? throw new InvalidOperationException("login-response.json is not embedded");
        return JsonNode.Parse(stream)!.AsObject();
    });

    public static JsonObject Build(LoginValues values, StatTrackers stats)
    {
        var response = s_template.Value.DeepClone().AsObject();
        Fill(response, values);
        stats.WriteTo(response["profile"]!["server_data"]!["stat_trackers"]!.AsObject());
        return response;
    }

    private static void Fill(JsonNode node, LoginValues values)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    if (obj[key] is { } child && Marker(child) is { } marker)
                    {
                        obj[key] = Value(marker, values);
                    }
                    else if (obj[key] is { } other)
                    {
                        Fill(other, values);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child && Marker(child) is { } marker)
                    {
                        array[i] = Value(marker, values);
                    }
                    else if (array[i] is { } other)
                    {
                        Fill(other, values);
                    }
                }

                break;
        }
    }

    private static string? Marker(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { } s && s.StartsWith("{{") && s.EndsWith("}}") ? s[2..^2] : null;

    private static JsonNode? Value(string marker, LoginValues v) => marker switch
    {
        "token" => v.Token,
        "realtime" => v.Realtime.DeepClone(),
        "identity.avatar" => v.IdentityAvatarUrl,
        "steam.avatar" => v.SteamAvatarUrl,
        "account.id" => v.AccountId,
        "account.profile_id" => v.ProfileId,
        "account.public_id" => v.PublicId,
        "account.wb_network_id" => v.WbNetworkId,
        "account.username" => v.Username,
        "account.hydraUsername" => v.HydraUsername,
        "player.profile_icon" => v.ProfileIcon,
        "profile_icon.assetPath" => v.ProfileIconAssetPath is { } path ? JsonValue.Create(path) : JsonValue.Create(double.NaN),
        _ => throw new InvalidOperationException($"login-response.json has a marker nothing fills: {{{{{marker}}}}}"),
    };
}

/// <summary>
/// The stat trackers the TS server computes for /access from eloratings (wins) and playerstats (per character, both
/// modes merged). Numbers are JavaScript's: doubles throughout, damage totals rounded half up at every step.
/// </summary>
public sealed class StatTrackers
{
    private readonly Dictionary<string, double> _wins = [], _matches = [], _ringouts = [], _damage = [], _highest = [];
    private double _totalWins, _totalRingouts, _highestDamage;

    public static StatTrackers From(BsonDocument? rating, BsonDocument? playerStats)
    {
        var stats = new StatTrackers
        {
            _totalWins = Num(rating, "wins_1v1") + Num(rating, "wins_2v2"),
        };

        foreach (string mode in new[] { "characters_1v1", "characters_2v2" })
        {
            if (playerStats?.GetValue(mode, BsonNull.Value) is not BsonDocument characters)
            {
                continue;
            }

            foreach (var element in characters)
            {
                var data = element.Value as BsonDocument;
                string slug = element.Name;
                stats._wins[slug] = stats._wins.GetValueOrDefault(slug) + Num(data, "wins");
                stats._matches[slug] = stats._matches.GetValueOrDefault(slug) + Num(data, "wins") + Num(data, "losses");
                stats._ringouts[slug] = stats._ringouts.GetValueOrDefault(slug) + Num(data, "ringouts");
                stats._damage[slug] = JsRound(stats._damage.GetValueOrDefault(slug) + Num(data, "totalDamageDealt"));
                double highest = Num(data, "highestDamageDealt");
                stats._highest[slug] = Math.Max(stats._highest.GetValueOrDefault(slug), highest);
                stats._totalRingouts += Num(data, "ringouts");
                if (highest > stats._highestDamage)
                {
                    stats._highestDamage = highest;
                }
            }
        }

        return stats;
    }

    /// <summary>Sets the computed fields in the template's stat_trackers, keeping its key order.</summary>
    public void WriteTo(JsonObject trackers)
    {
        trackers["HighestDamageDealt"] = _highestDamage;
        trackers["TotalRingouts"] = _totalRingouts;
        trackers["TotalWins"] = _totalWins;
        trackers["character_highest_damage_dealt"] = Map(_highest);
        trackers["character_ringouts"] = Map(_ringouts);
        trackers["character_total_damage_dealt"] = Map(_damage);
        trackers["character_wins"] = Map(_wins);
        trackers["character_matches"] = Map(_matches);

        var season5 = trackers["season5"]!.AsObject();
        season5["ranked"]!["1v1"]!["Wins"] = _totalWins;
        season5["character_matches"] = Map(_matches);
        season5["character_wins"] = Map(_wins);
        season5["character_ringouts"] = Map(_ringouts);
        season5["character_total_damage_dealt"] = Map(_damage);
        season5["character_highest_damage_dealt"] = Map(_highest);
        season5["TotalWins"] = _totalWins;
        season5["TotalRingouts"] = _totalRingouts;
        season5["HighestDamageDealt"] = _highestDamage;
    }

    private static JsonObject Map(Dictionary<string, double> values)
    {
        var map = new JsonObject();
        foreach (var (slug, value) in values)
        {
            map[slug] = value;
        }

        return map;
    }

    // `x || 0`: a missing, null, zero or non-numeric value counts as 0.
    private static double Num(BsonDocument? doc, string field) =>
        doc is not null && doc.TryGetValue(field, out var v) && v.IsNumeric && v.ToDouble() is var d && !double.IsNaN(d) ? d : 0;

    // Math.round: half up, towards +infinity (Math.Round's default is half to even).
    internal static double JsRound(double x)
    {
        double floor = Math.Floor(x);
        return x - floor >= 0.5 ? floor + 1 : floor;
    }
}
