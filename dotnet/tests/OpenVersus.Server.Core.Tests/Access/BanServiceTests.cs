using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Bans;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>Ban files as the TS server reads them, and CIDR matching as it does it.</summary>
public sealed class BanServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-bans-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BanService Service(BanSettings settings) =>
        new(new ServiceCollection().BuildServiceProvider(), new StaticMonitor(settings), NullLogger<BanService>.Instance);

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
    public async Task ReadsTheTsFilesFormat()
    {
        string ips = Path.Combine(_dir, "bans.txt");
        File.WriteAllText(ips, "# comment\r\n198.51.100.1\r\n\r\n   \n198.51.100.2 \n  # indented comment is an entry\n");
        var bans = Service(new BanSettings { IpFile = ips, CidrFile = null, IdFile = null });
        Assert.True(await bans.IsBannedAsync("198.51.100.1"));
        // Entries are not trimmed, as there.
        Assert.False(await bans.IsBannedAsync("198.51.100.2"));
        Assert.True(await bans.IsBannedAsync("198.51.100.2 "));
        Assert.False(await bans.IsBannedAsync("# comment"));
    }

    [Fact]
    public async Task ChecksTheIpAgainstEveryList()
    {
        string cidr = Path.Combine(_dir, "cidr.txt"), ids = Path.Combine(_dir, "ids.txt");
        File.WriteAllText(cidr, "192.0.2.0/24\n");
        File.WriteAllText(ids, "76561198000000066\n");
        var bans = Service(new BanSettings { IpFile = Path.Combine(_dir, "missing.txt"), CidrFile = cidr, IdFile = ids });
        Assert.True(await bans.IsBannedAsync("192.0.2.77"));
        Assert.True(await bans.IsBannedAsync("76561198000000066"));
        Assert.False(await bans.IsBannedAsync("198.51.100.1"));
    }

    [Fact]
    public async Task RereadsAFileThatChanged()
    {
        string ips = Path.Combine(_dir, "bans.txt");
        File.WriteAllText(ips, "198.51.100.1\n");
        var bans = Service(new BanSettings { IpFile = ips, CidrFile = null, IdFile = null });
        Assert.False(await bans.IsBannedAsync("198.51.100.3"));
        File.WriteAllText(ips, "198.51.100.3\n");
        File.SetLastWriteTimeUtc(ips, DateTime.UtcNow.AddMinutes(1));
        Assert.True(await bans.IsBannedAsync("198.51.100.3"));
    }

    private sealed class StaticMonitor(BanSettings value) : IOptionsMonitor<BanSettings>
    {
        public BanSettings CurrentValue => value;

        public BanSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<BanSettings, string?> listener) => null;
    }
}
