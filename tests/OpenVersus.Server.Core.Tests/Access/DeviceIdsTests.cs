using System.Text.Json.Nodes;
using MongoDB.Bson;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Bans;
using YamlDotNet.RepresentationModel;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>The device ids of a registration (docs/IDENTIFY.md): what is kept of them, how a ban carries them, and the name history's shape.</summary>
public sealed class DeviceIdsTests
{
    private static readonly string A = new('a', 64), B = new('b', 64), C = new('c', 64);

    [Fact]
    public void OnlyKnownKindsWithAHashAreKeptLowercasedInKindOrder()
    {
        var ids = IdentityRules.NormalizeDeviceIds(new JsonObject
        {
            ["osMachineId"] = " " + A.ToUpperInvariant() + " ",
            ["machineGuid"] = B,
            ["somethingElse"] = C,
            ["ueMachineId"] = "short",
            ["boardSerial"] = 12,
        });

        Assert.Equal([("machineGuid", B), ("osMachineId", A)], ids.Select(i => (i.Key, i.Value)));
        Assert.Empty(IdentityRules.NormalizeDeviceIds(null));
        Assert.Empty(IdentityRules.NormalizeDeviceIds(JsonValue.Create("not an object")));
        Assert.Empty(IdentityRules.NormalizeDeviceIds(new JsonArray()));
    }

    [Fact]
    public void TheRecordsJsonRoundTripsAndAnythingUnreadableIsNothing()
    {
        var ids = IdentityRules.NormalizeDeviceIds(new JsonObject { ["systemUuid"] = C, ["machineGuid"] = A });
        string json = IdentityRules.DeviceIdsJson(ids).ToJsonString();

        Assert.Equal("""{"machineGuid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","systemUuid":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}""", json);
        Assert.Equal(ids, IdentityRules.ParseDeviceIds(json));
        Assert.Empty(IdentityRules.ParseDeviceIds(""));
        Assert.Empty(IdentityRules.ParseDeviceIds(null));
        Assert.Empty(IdentityRules.ParseDeviceIds("{not json"));
        Assert.Equal("{}", IdentityRules.DeviceIdsJson(new Dictionary<string, string>()).ToJsonString());
    }

    [Theory]
    [InlineData(" Proton ", "proton")]
    [InlineData("windows", "windows")]
    [InlineData("crossover", "crossover")]
    [InlineData("wine", "wine")]
    [InlineData("macos", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void OnlyTheRuntimesTheClientNamesAreKept(string? value, string kept) => Assert.Equal(kept, IdentityRules.NormalizeRuntime(value));

    [Fact]
    public void APlayersDeviceIdsAreItsCurrentOnesAndEveryPastOneOnce()
    {
        var player = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "ip", "198.51.100.9" },
            { DeviceIdsStore.CurrentField, new BsonDocument { { "machineGuid", A }, { "osMachineId", C } } },
            {
                DeviceIdsStore.HistoryField, new BsonArray
                {
                    new BsonDocument { { "kind", "machineGuid" }, { "id", B } }, new BsonDocument { { "kind", "machineGuid" }, { "id", A } }, new BsonDocument { { "kind", "ueMachineId" }, { "id", "" } },
                }
            },
        };

        Assert.Equal([B, A, C], DeviceIdsStore.IdsOf(player));
        Assert.Equal([("machineGuid", A), ("osMachineId", C)], DeviceIdsStore.CurrentOf(player).Select(c => (c.Key, c.Value)));
        var who = BanIdentifiers.OfPlayer(player);
        Assert.Equal([B, A, C], who.DeviceIds);
        Assert.Equal([(BanKind.Ip, "198.51.100.9"), (BanKind.Player, player["_id"].ToString()!), (BanKind.Device, B), (BanKind.Device, A), (BanKind.Device, C)], who.Known());
        Assert.Empty(DeviceIdsStore.IdsOf(new BsonDocument()));
    }

    [Fact]
    public void ABanRecordCarriesTheDeviceIdsAndTheSkippedOnesInMongoAndInTheTrail()
    {
        var who = new BanIdentifiers(Ip: "198.51.100.7", SteamId: "76561198000000001", PlayerId: "6a0000000000000000000001", DeviceIds: [A, B]);
        var record = new BanRecord(Guid.NewGuid().ToString(), new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.Zero), "banned by an administrator", "manual", who,
            DeviceIdsSkipped: [new SkippedDeviceId(C, 7)]);

        var bson = PersonBans.ToBson(record);
        Assert.Equal(who, PersonBans.IdentifiersOf(bson));
        Assert.Equal([C], bson["identifiers"]["device_ids_skipped"].AsBsonArray.Select(d => d["id"].AsString));
        Assert.Equal(7, bson["identifiers"]["device_ids_skipped"][0]["holders"].AsInt64);
        Assert.Equal(BanKind.Device, PersonBans.KindOf("device"));
        Assert.Equal("identifiers.device_ids", BanStore.RecordField(BanKind.Device));
        Assert.Equal("bans:device", BanService.Key(BanKind.Device));
        Assert.Equal(A.ToUpperInvariant().Length, BanService.Canonical(BanKind.Device, " " + A.ToUpperInvariant()).Length);
        Assert.Equal(A, BanService.Canonical(BanKind.Device, " " + A.ToUpperInvariant()));

        var yaml = new YamlStream();
        yaml.Load(new StringReader(AutoBans.ToYaml(record)));
        var item = (YamlMappingNode)((YamlSequenceNode)yaml.Documents[0].RootNode)[0];
        var identifiers = (YamlMappingNode)item["identifiers"];
        Assert.Equal([A, B], ((YamlSequenceNode)identifiers["device_ids"]).Select(n => n.ToString()));
        var skipped = (YamlMappingNode)((YamlSequenceNode)identifiers["device_ids_skipped"])[0];
        Assert.Equal((C, "7"), (skipped["id"].ToString(), skipped["holders"].ToString()));
    }

    [Fact]
    public void ARecordWithoutDeviceIdsReadsAsNone()
    {
        var bson = new BsonDocument { { "identifiers", new BsonDocument { { "ip", "198.51.100.7" } } }, { "player", new BsonDocument("id", "6a0000000000000000000001") } };
        var who = PersonBans.IdentifiersOf(bson);
        Assert.Empty(who.DeviceIds);
        Assert.Equal(new BanIdentifiers(Ip: "198.51.100.7", PlayerId: "6a0000000000000000000001"), who);
    }

    [Fact]
    public void TheNameHistoryReadsOldestFirstAndSkipsWhatIsNotAnEntry()
    {
        var at = new DateTime(2026, 11, 5, 1, 2, 3, DateTimeKind.Utc);
        var account = new BsonDocument
        {
            { NameHistory.Field, new BsonArray { new BsonDocument { { "from", "a" }, { "to", "b" }, { "at", at }, { "ip", "198.51.100.7" }, { "source", "web" } }, "junk", new BsonDocument { { "from", "b" }, { "to", "c" } } } },
        };

        Assert.Equal([new NameChange("a", "b", at, "198.51.100.7", "web"), new NameChange("b", "c", DateTime.MinValue, "", "")], NameHistory.Of(account));
        Assert.Empty(NameHistory.Of(new BsonDocument()));
    }
}
