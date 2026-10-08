using OpenVersus.Server.Core.Bans;
using YamlDotNet.RepresentationModel;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>The hand-edited ban files' format (as the import reads it), the auto-ban trail's, and CIDR matching as the TS server does it.</summary>
public sealed class BanServiceTests : IDisposable
{
    private const string Steam = "76561198000000066";
    private const string Hardware = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-bans-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.0/24", true)]
    [InlineData("203.0.114.9", "203.0.113.0/24", false)]
    [InlineData("203.0.113.9", "203.0.113.9", true)]
    [InlineData("203.0.113.9", "203.0.113.8", false)]
    [InlineData("10.1.2.3", "0.0.0.0/0", true)]
    [InlineData("10.1.2.3", "10.0.0.0/8", true)]
    [InlineData("2001:db8::1", "0.0.0.0/8", false)]
    [InlineData("203.0.113.9", "not a block", false)]
    [InlineData("203.0.113.9", "203.0.113.0/33", false)]
    public void MatchesCidrBlocks(string ip, string block, bool expected)
    {
        Assert.Equal(expected, BanService.InCidr(ip, block));
    }

    [Fact]
    public void ReadsABanFileWithTheCommentAboveEachEntry()
    {
        // The TS server's banIP wrote a comment line before each address.
        const string text = "# a header\r\n\r\n# 2026-01-01T00:00:00Z - Ban ID: x, Reason: y\r\n198.51.100.1\r\n198.51.100.2 \n\n   \n  # indented comment\n198.51.100.3\n";
        Assert.Equal(
            [("198.51.100.1", "2026-01-01T00:00:00Z - Ban ID: x, Reason: y"), ("198.51.100.2", ""), ("198.51.100.3", "indented comment")],
            BanStore.Parse(text, BanKind.Ip));
    }

    [Fact]
    public void KeepsEachKindsCanonicalForm()
    {
        Assert.Equal([(Hardware, "")], BanStore.Parse(Hardware.ToUpperInvariant() + "\n", BanKind.Hardware));
        Assert.Equal([(Steam, "")], BanStore.Parse($" {Steam}\n", BanKind.Steam));
        // A block (no kind) as written.
        Assert.Equal([("192.0.2.0/24", "")], BanStore.Parse("192.0.2.0/24\n", null));
    }

    private static BanRecord Record(string player, BanIdentifiers who) =>
        new(Guid.NewGuid().ToString(), new DateTimeOffset(2026, 10, 7, 21, 14, 3, 512, TimeSpan.Zero), "banned name", "namechange", who with { PlayerId = player },
            NameAtBan: "Old 'name' #1: x", AttemptedName: "new\nname \"quoted\" ünïcode", PlayerCreatedAt: "2026-09-01T00:00:00Z", MatchedList: "banned_names",
            MatchedTerm: "x$y", RequestIp: who.Ip, UserAgent: "Mozilla/5.0 (X11; Linux x86_64)", Online: true, Disconnected: true);

    private static YamlSequenceNode Trail(string path)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(path)));
        return (YamlSequenceNode)yaml.Documents[0].RootNode;
    }

    [Fact]
    public void TheTrailKeepsEachRecordsTextExactly()
    {
        string path = Path.Combine(_dir, "auto_bans.yaml");
        File.WriteAllText(path, "# the services' trail\n");
        var ban = Record("6a0000000000000000000001", new BanIdentifiers(Ip: "198.51.100.7", SteamId: Steam));
        AutoBans.Append(path, AutoBans.ToYaml(ban));
        AutoBans.Append(path, AutoBans.LiftToYaml(ban.At.AddDays(1), "a false positive", "manual", "6a0000000000000000000001", "Old 'name' #1: x",
            [ban.BanId], ["Steam " + Steam], ["Ip 198.51.100.7 (bans.txt: remove it there)"]));

        var items = Trail(path);
        Assert.Equal(2, items.Children.Count);
        var banned = (YamlMappingNode)items[0];
        var player = (YamlMappingNode)banned["player"];
        Assert.Equal(("ban", ban.BanId, "2026-10-07T21:14:03.512Z", "true"), (banned["action"].ToString(), banned["ban_id"].ToString(), banned["at"].ToString(), banned["disconnected"].ToString()));
        Assert.Equal((ban.NameAtBan, ban.AttemptedName), (player["name_at_ban"].ToString(), player["attempted_name"].ToString()));
        Assert.Equal(Steam, ((YamlMappingNode)banned["identifiers"])["steam_id"].ToString());
        var lifted = (YamlMappingNode)items[1];
        Assert.Equal(("lift", "a false positive"), (lifted["action"].ToString(), lifted["reason"].ToString()));
        Assert.Equal([ban.BanId], ((YamlSequenceNode)lifted["lifted_bans"]).Select(n => n.ToString()));
        Assert.Equal(["Ip 198.51.100.7 (bans.txt: remove it there)"], ((YamlSequenceNode)lifted["still_banned"]).Select(n => n.ToString()));
    }

    [Fact]
    public void AppendsAfterALastLineWithoutANewline()
    {
        string path = Path.Combine(_dir, "auto_bans.yaml");
        File.WriteAllText(path, "# no newline at the end");
        AutoBans.Append(path, AutoBans.ToYaml(Record("6a0000000000000000000001", new BanIdentifiers(Ip: "198.51.100.7"))));
        Assert.Single(Trail(path).Children);
    }

    [Theory]
    [InlineData("banned:6a0000000000000000000001", BanEventKind.Banned, "6a0000000000000000000001")]
    [InlineData("lifted:6a0000000000000000000001", BanEventKind.Lifted, "6a0000000000000000000001")]
    [InlineData("loaded", BanEventKind.Loaded, "")]
    // Anything else is a reread, never a ban or a lift.
    [InlineData("6a0000000000000000000001", BanEventKind.Loaded, "")]
    [InlineData("banned:", BanEventKind.Loaded, "")]
    [InlineData("", BanEventKind.Loaded, "")]
    public void ReadsTheChangeMessage(string message, BanEventKind kind, string player)
    {
        var change = BanEvent.Parse(message);
        Assert.Equal((kind, player), (change.Kind, change.PlayerId));
        if (kind != BanEventKind.Loaded || message == "loaded")
        {
            Assert.Equal(message, change.ToString());
        }
    }
}
