using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Arenas;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Matchmaking;

namespace OpenVersus.Server.Core.Tests.Arenas;

/// <summary>
/// Arenas:Enabled shows the mode select's Arena button and, while on, Arenas:MatchmakingBotTimeoutSeconds sets the game's
/// Arena matchmaking timer; each combination has its own Crc. Needs no stores.
/// </summary>
public sealed class ArenaHissTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheArenaButtonsToggleFollowsTheSetting(bool enabled)
    {
        var answer = HissService.Fill(HissService.Values(1, 2, []));
        ArenaHiss.ApplyTo(answer, enabled, 240);
        var data = answer["body"]!["Data"]!;
        var toggles = data["feature-toggles"]!["_hydra_compressed"]!;
        Assert.Equal(enabled, toggles["Arena"]!.GetValue<bool>());
        // The event queue that lists Arena among a party lobby's queues stays off either way.
        Assert.False(toggles["evtq_arena"]!.GetValue<bool>());
        Assert.False(data["event-queue-config"]!["_hydra_compressed"]!["evtq_arena"]!["data"]!["bAlwaysAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public void OnUnlessSet() => Assert.True(new ArenaSettings().Enabled);

    [Fact]
    // The mode's rule: about 4 minutes in matchmaking before bots (the game's own default; the TS hiss sent 500).
    public void TheBotTimeoutIs240SecondsUnlessSet() => Assert.Equal(240, new ArenaSettings().MatchmakingBotTimeoutSeconds);

    [Theory]
    [InlineData(true, 15, 15)]
    [InlineData(true, 240, 240)]
    // Off: the TS hiss's value, which nothing reads without the button.
    [InlineData(false, 15, 500)]
    public void TheBotTimeoutFloatFollowsTheSettingWhileOn(bool enabled, int seconds, int sent)
    {
        var answer = HissService.Fill(HissService.Values(1, 2, []));
        ArenaHiss.ApplyTo(answer, enabled, seconds);
        var floats = answer["body"]!["Data"]!["float-settings"]!["_hydra_compressed"]!;
        Assert.Equal(sent, floats["ArenaMatchmakingBotTimeout"]!.GetValue<int>());
        // The other floats as they were.
        Assert.Equal(0.5, floats["MissionBulkClaimTimeout"]!.GetValue<double>());
        Assert.Equal(0.25, floats["MultiplayMaxPing"]!.GetValue<double>());
    }

    [Theory]
    [InlineData(false, 240, 0)]
    [InlineData(true, 240, ArenaHiss.CrcOffset + 240 * ArenaHiss.CrcPerBotTimeoutSecond)]
    [InlineData(true, 15, ArenaHiss.CrcOffset + 15 * ArenaHiss.CrcPerBotTimeoutSecond)]
    // Off, the timeout is not in the hiss, so not in the Crc either.
    [InlineData(false, 15, 0)]
    public async Task TheCrcCarriesItsOffsetsWhileOn(bool enabled, int seconds, double offset)
    {
        var services = new ServiceCollection()
            .Configure<ArenaSettings>(o => (o.Enabled, o.MatchmakingBotTimeoutSeconds) = (enabled, seconds))
            // Testing Grounds closed, so its offset stays out of the sum.
            .Configure<TestingGroundsSettings>(o => o.Enabled = false)
            .BuildServiceProvider();
        // No Mongo: the default CRC, plus Hiss:ContentRevision's default (15).
        Assert.Equal(HissService.DefaultCrc + 15 + offset, await HissService.CurrentCrcAsync(services, CancellationToken.None));
    }

    // Every schedule of the Testing Grounds and FFA tiles the server makes (TestingGrounds.ScheduleAt: 2v2 open never
    // shows FFA's tile).
    private static readonly double[] s_scheduleOffsets = [.. (from a in new[] { false, true } from b in new[] { false, true } from c in new[] { false, true }
        where !(b && c) select new TestingGrounds.Schedule(a, b, c).CrcOffset)];

    [Fact]
    // Every combination of the on/off offsets and any two timeouts gives its own Crc, and the largest stays an int.
    public void NoTwoCombinationsShareACrc()
    {
        var crcs = new List<double>();
        foreach (int seconds in (int[])[11, 12, 15, 240, 241, 500, 1800])
        {
            foreach (double testingGrounds in s_scheduleOffsets)
            {
                crcs.Add(testingGrounds + ArenaHiss.CrcOffset + seconds * ArenaHiss.CrcPerBotTimeoutSecond);
            }
        }

        crcs.AddRange(s_scheduleOffsets);
        Assert.Equal(crcs.Count, crcs.Distinct().Count());
        Assert.True(HissService.DefaultCrc + 15 + crcs.Max() < int.MaxValue);
    }
}
