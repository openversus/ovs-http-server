using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Bans;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>Ban files, one per identifier kind, the auto-ban record file, and CIDR matching as the TS server does it.</summary>
public sealed class BanServiceTests : IDisposable
{
    private const string Steam = "76561198000000066", Epic = "0123456789abcdef0123456789abcdef";
    private const string Hardware = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-bans-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, text);
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }

    private static BanSettings Nothing() => new()
    {
        IpFile = null, CidrFile = null, SteamIdFile = null, EpicIdFile = null, HardwareFile = null, InstallIdFile = null, AutoBansFile = null,
    };

    private static BanService Service(BanSettings settings) =>
        new(new ServiceCollection().BuildServiceProvider(), new TestOptions<BanSettings>(settings), TimeProvider.System, NullLogger<BanService>.Instance);

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
    public async Task ReadsTheTextFormat()
    {
        var settings = Nothing();
        settings.IpFile = File("bans.txt", "# comment\r\n198.51.100.1\r\n\r\n   \n198.51.100.2 \n  # an indented comment\n");
        var bans = Service(settings);
        Assert.True(await bans.IsBannedAsync("198.51.100.1"));
        // Entries are trimmed (the TS server did not trim them, so a trailing space made a ban miss).
        Assert.True(await bans.IsBannedAsync("198.51.100.2"));
        Assert.False(await bans.IsBannedAsync("# comment"));
        Assert.False(await bans.IsBannedAsync("# an indented comment"));
    }

    [Fact]
    public async Task ChecksEachIdentifierAgainstItsOwnListOnly()
    {
        var settings = Nothing();
        settings.CidrFile = File("cidr.txt", "192.0.2.0/24\n");
        settings.SteamIdFile = File("steamid_bans.txt", Steam + "\n");
        settings.EpicIdFile = File("epicid_bans.txt", Epic.ToUpperInvariant() + "\n");
        settings.HardwareFile = File("hashbans.txt", Hardware + "\n");
        var bans = Service(settings);
        Assert.Equal(new BanMatch(BanKind.Ip, "192.0.2.77", "cidr.txt 192.0.2.0/24"), await bans.FindAsync(new BanIdentifiers(Ip: "192.0.2.77")));
        Assert.Equal(new BanMatch(BanKind.Steam, Steam, "steamid_bans.txt"), await bans.FindAsync(new BanIdentifiers(Ip: "198.51.100.1", SteamId: Steam)));
        // Hex ids in any case.
        Assert.Equal(new BanMatch(BanKind.Epic, Epic, "epicid_bans.txt"), await bans.FindAsync(new BanIdentifiers(EpicId: Epic)));
        Assert.Equal(new BanMatch(BanKind.Hardware, Hardware, "hashbans.txt"), await bans.FindAsync(new BanIdentifiers(HardwareId: Hardware.ToUpperInvariant())));
        // Not across kinds: the TS server checked the IP against every file.
        Assert.Null(await bans.FindAsync(new BanIdentifiers(Ip: Steam, EpicId: Steam, InstallId: Hardware)));
        Assert.Null(await bans.FindAsync(new BanIdentifiers(Ip: "198.51.100.1", SteamId: "76561198000000067")));
    }

    [Fact]
    public async Task RereadsAFileThatChanged()
    {
        var settings = Nothing();
        settings.IpFile = File("bans.txt", "198.51.100.1\n");
        var bans = Service(settings);
        Assert.False(await bans.IsBannedAsync("198.51.100.3"));
        System.IO.File.WriteAllText(settings.IpFile, "198.51.100.3\n");
        System.IO.File.SetLastWriteTimeUtc(settings.IpFile, DateTime.UtcNow.AddMinutes(1));
        Assert.True(await bans.IsBannedAsync("198.51.100.3"));
    }

    private static BanRecord Record(string player, BanIdentifiers who, string name = "Old 'name' #1: x") =>
        new(Guid.NewGuid().ToString(), new DateTimeOffset(2026, 10, 7, 21, 14, 3, 512, TimeSpan.Zero), "banned name", "namechange", who with { PlayerId = player },
            NameAtBan: name, AttemptedName: "new\nname \"quoted\" ünïcode", PlayerCreatedAt: "2026-09-01T00:00:00Z", MatchedList: "banned_names", MatchedTerm: "x$y",
            RequestIp: who.Ip, UserAgent: "Mozilla/5.0 (X11; Linux x86_64)", Online: true, Disconnected: true);

    [Fact]
    public async Task TheAutoBanFileIsASourceAndAppendsRecordsThatReadBack()
    {
        string path = File("auto_bans.yaml", "# records written by the services\n");
        AutoBans.Append(path, Record("6a0000000000000000000001", new BanIdentifiers(Ip: "198.51.100.7", SteamId: Steam)));
        AutoBans.Append(path, Record("6a0000000000000000000002", new BanIdentifiers(Ip: "198.51.100.8", HardwareId: Hardware, InstallId: Epic)));
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(
            [
                new BanIdentifiers("198.51.100.7", Steam, "", "", "", "6a0000000000000000000001"),
                new BanIdentifiers("198.51.100.8", "", "", Hardware, Epic, "6a0000000000000000000002"),
            ],
            AutoBans.Parse(System.IO.File.ReadAllText(path)));
        var settings = Nothing();
        settings.AutoBansFile = path;
        var bans = Service(settings);
        Assert.Equal(new BanMatch(BanKind.Steam, Steam, "auto_bans.yaml"), await bans.FindAsync(new BanIdentifiers(SteamId: Steam)));
        Assert.Equal(new BanMatch(BanKind.Player, "6a0000000000000000000002", "auto_bans.yaml"), await bans.FindAsync(new BanIdentifiers(PlayerId: "6A0000000000000000000002")));
        Assert.Null(await bans.FindAsync(new BanIdentifiers(Ip: "198.51.100.9")));
    }

    [Fact]
    public void TheRecordKeepsItsTextExactly()
    {
        var record = Record("6a0000000000000000000001", new BanIdentifiers(Ip: "198.51.100.7"));
        var yaml = new YamlDotNet.RepresentationModel.YamlStream();
        yaml.Load(new StringReader(AutoBans.ToYaml(record)));
        var item = (YamlDotNet.RepresentationModel.YamlMappingNode)((YamlDotNet.RepresentationModel.YamlSequenceNode)yaml.Documents[0].RootNode)[0];
        var player = (YamlDotNet.RepresentationModel.YamlMappingNode)item["player"];
        Assert.Equal(record.NameAtBan, player["name_at_ban"].ToString());
        Assert.Equal(record.AttemptedName, player["attempted_name"].ToString());
        Assert.Equal("2026-10-07T21:14:03.512Z", item["at"].ToString());
        Assert.Equal("true", item["disconnected"].ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only comments\n")]
    public void AnEmptyAutoBanFileHasNoRecords(string yaml)
    {
        Assert.Empty(AutoBans.Parse(yaml));
    }

    [Fact]
    public void AppendsAfterALastLineWithoutANewline()
    {
        string path = File("auto_bans.yaml", "# no newline at the end");
        AutoBans.Append(path, Record("6a0000000000000000000001", new BanIdentifiers(Ip: "198.51.100.7")));
        Assert.Single(AutoBans.Parse(System.IO.File.ReadAllText(path)));
    }
}
