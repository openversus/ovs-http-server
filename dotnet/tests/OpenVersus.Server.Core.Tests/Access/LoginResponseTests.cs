using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using OpenVersus.Server.Core.Access;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>
/// The /access response. With OVS_TEST_HYDRA_CORPUS set (dotnet/local/hydra-corpus, see tools/hydra/extract_corpus.py),
/// every captured /access response is rebuilt from what varies in it (token, ids, names, websocket, icon, stats) and
/// must come out as the same bytes.
/// </summary>
public sealed class LoginResponseTests
{
    private static LoginValues Values(string? assetPath = "/Game/Icon.Icon") =>
        new("tok", new RealtimeSettings().Configuration(), "a1", "p1", "u1", "a1", "Name", "hydra-name", "profile_icon_default", assetPath,
            new AccessSettings().IdentityAvatarUrl, new AccessSettings().SteamAvatarUrl);

    [Fact]
    public void FillsEveryMarker()
    {
        string json = LoginResponse.Build(Values(), StatTrackers.From(null, null)).ToJsonString(new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        Assert.DoesNotContain("{{", json);
        Assert.Contains("\"network_token\":\"tok\"", json);
    }

    [Fact]
    public void AnIconMissingFromTheCatalogIsNaNAsTheTsServerSendsIt()
    {
        var response = LoginResponse.Build(Values(assetPath: null), StatTrackers.From(null, null));
        Assert.True(double.IsNaN(response["account"]!["server_data"]!["ProfileIcon"]!["AssetPath"]!.GetValue<double>()));
    }

    [Fact]
    public void StatTrackersFollowJavaScriptArithmetic()
    {
        var stats = new BsonDocument("characters_2v2", new BsonDocument
        {
            { "character_A", new BsonDocument { { "wins", 1 }, { "losses", 2 }, { "totalDamageDealt", 2.5 }, { "highestDamageDealt", 1.25 } } },
        });
        var trackers = LoginResponse.Build(Values(), StatTrackers.From(new BsonDocument { { "wins_1v1", 3 }, { "wins_2v2", BsonNull.Value } }, stats))["profile"]!["server_data"]!["stat_trackers"]!;
        // Math.round rounds half up; .NET's Math.Round would make this 2.
        Assert.Equal(3, trackers["character_total_damage_dealt"]!["character_A"]!.GetValue<double>());
        Assert.Equal(3, trackers["character_matches"]!["character_A"]!.GetValue<double>());
        Assert.Equal(3, trackers["TotalWins"]!.GetValue<double>());
        Assert.Equal(1.25, trackers["season5"]!["HighestDamageDealt"]!.GetValue<double>());
    }

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -2)]
    [InlineData(0.49999999999999994, 0)]
    [InlineData(100.60000000000001, 101)]
    public void RoundsAsMathRound(double x, double expected)
    {
        Assert.Equal(expected, StatTrackers.JsRound(x));
    }

    [SkippableFact]
    public void RebuildsEveryCapturedResponseByteForByte()
    {
        string? corpus = Environment.GetEnvironmentVariable("OVS_TEST_HYDRA_CORPUS");
        Skip.If(string.IsNullOrEmpty(corpus), "set OVS_TEST_HYDRA_CORPUS to run");
        var files = Directory.GetFiles(Path.Combine(corpus!, "resp"), "*__access.bin");
        Assert.NotEmpty(files);
        var mismatches = new List<string>();
        foreach (string file in files)
        {
            byte[] captured = File.ReadAllBytes(file);
            var response = HydraCodec.Decode(captured)!.AsObject();
            byte[] rebuilt = HydraCodec.Encode(LoginResponse.Build(ValuesOf(response), StatsOf(response)));
            if (!captured.AsSpan().SequenceEqual(rebuilt))
            {
                mismatches.Add($"{Path.GetFileName(file)}: {captured.Length} captured, {rebuilt.Length} rebuilt, first difference at {captured.AsSpan().CommonPrefixLength(rebuilt)}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    // What varies per login is read from the capture; the addresses come from the settings' defaults (only the
    // websocket's host and port, which each server was configured with, from the capture), so the defaults are
    // checked against what the game was sent.
    private static LoginValues ValuesOf(JsonObject r)
    {
        var account = r["account"]!;
        var icon = account["server_data"]!["ProfileIcon"]!;
        var assetPath = icon["AssetPath"]!;
        var ws = new Uri((string)r["configuration"]!["realtime"]!["servers"]!["ec2-us-east-1-dokken"]!["ovs-realtime"]!["ws"]!);
        var realtime = ws.Scheme == "wss"
            ? new RealtimeSettings { Domain = ws.Host, Secure = 1, SecurePort = ws.Port }
            : new RealtimeSettings { Domain = ws.Host, Port = ws.Port };
        var access = new AccessSettings();
        return new LoginValues(
            (string)r["token"]!,
            realtime.Configuration(),
            (string)account["id"]!,
            (string)r["profile"]!["id"]!,
            (string)account["public_id"]!,
            (string)account["identity"]!["alternate"]!["wb_network"]![0]!["id"]!,
            (string)account["identity"]!["username"]!,
            (string)account["identity"]!["usernames"]![0]!["username"]!,
            (string)icon["Slug"]!,
            assetPath.GetValueKind() == JsonValueKind.String ? (string)assetPath! : null,
            access.IdentityAvatarUrl,
            access.SteamAvatarUrl);
    }

    [Fact]
    public void TheAddressesAreSettings()
    {
        var realtime = new RealtimeSettings { Domain = "ws.example", Port = 1234, Cluster = "c", ServerName = "s", Udp = "1.2.3.4:5" };
        var response = LoginResponse.Build(Values() with { Realtime = realtime.Configuration(), IdentityAvatarUrl = "https://a/1", SteamAvatarUrl = "https://a/2" }, StatTrackers.From(null, null));
        Assert.Equal("""{"enabled":true,"default-cluster":"c","servers":{"c":{"s":{"ws":"ws://ws.example:1234","udp":"1.2.3.4:5"}}}}""", response["configuration"]!["realtime"]!.ToJsonString());
        Assert.Equal("https://a/1", (string)response["account"]!["identity"]!["avatar"]!);
        Assert.Equal("https://a/2", (string)response["account"]!["identity"]!["alternate"]!["steam"]![0]!["avatar"]!);
    }

    // Stats that compute to the captured trackers: each character's numbers as one mode's entry, total wins as 1v1 wins.
    private static StatTrackers StatsOf(JsonObject r)
    {
        var t = r["profile"]!["server_data"]!["stat_trackers"]!;
        double N(JsonNode? n) => n is null ? 0 : n.Deserialize<double>();
        var characters = new BsonDocument();
        foreach (var (slug, matches) in t["character_matches"]!.AsObject())
        {
            double wins = N(t["character_wins"]![slug]);
            characters[slug] = new BsonDocument
            {
                { "wins", wins },
                { "losses", N(matches) - wins },
                { "ringouts", N(t["character_ringouts"]![slug]) },
                { "totalDamageDealt", N(t["character_total_damage_dealt"]![slug]) },
                { "highestDamageDealt", N(t["character_highest_damage_dealt"]![slug]) },
            };
        }

        return StatTrackers.From(new BsonDocument("wins_1v1", N(t["TotalWins"])), new BsonDocument("characters_1v1", characters));
    }
}
