using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>Arenas:Enabled shows the mode select's Arena button and nothing else, under its own Crc. Needs no stores.</summary>
public sealed class ArenaHissTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheArenaButtonsToggleFollowsTheSetting(bool enabled)
    {
        var answer = HissService.Fill(HissService.Values(1, 2, []));
        ArenaHiss.ApplyTo(answer, enabled);
        var data = answer["body"]!["Data"]!;
        var toggles = data["feature-toggles"]!["_hydra_compressed"]!;
        Assert.Equal(enabled, toggles["Arena"]!.GetValue<bool>());
        // The event queue that lists Arena among a party lobby's queues stays off either way.
        Assert.False(toggles["evtq_arena"]!.GetValue<bool>());
        Assert.False(data["event-queue-config"]!["_hydra_compressed"]!["evtq_arena"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public void OnUnlessSet() => Assert.True(new ArenaSettings().Enabled);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, ArenaHiss.CrcOffset)]
    public async Task TheCrcCarriesItsOffsetWhileOn(bool enabled, double offset)
    {
        var services = new ServiceCollection()
            .Configure<ArenaSettings>(o => o.Enabled = enabled)
            // Testing Grounds closed, so its offset stays out of the sum.
            .Configure<TestingGroundsSettings>(o => o.Enabled = false)
            .BuildServiceProvider();
        // No Mongo: the default CRC, plus Hiss:ContentRevision's default (15).
        Assert.Equal(HissService.DefaultCrc + 15 + offset, await HissService.CurrentCrcAsync(services, CancellationToken.None));
    }
}
