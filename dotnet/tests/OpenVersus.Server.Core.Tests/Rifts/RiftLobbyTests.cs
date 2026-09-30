using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Rifts;

namespace OpenVersus.Server.Core.Tests.Rifts;

public sealed class RiftLobbyTests
{
    [Fact]
    public void AKnownRiftsRuntimeDataIsItsLoadRiftsEntryWithNoPowerups()
    {
        var data = RiftLobbyService.RuntimeData("mvs_joker_rift");

        Assert.NotEmpty(data["RuntimeChapterData"]!.AsObject());
        Assert.NotEmpty(data["RuntimeNodeData"]!.AsObject());
        Assert.Empty(data["Powerups"]!.AsArray());
    }

    [Fact]
    public void AnUnknownRiftGetsEmptyRuntimeData()
    {
        var data = RiftLobbyService.RuntimeData("no_such_rift");

        Assert.Empty(data["RuntimeChapterData"]!.AsObject());
        Assert.Empty(data["RuntimeNodeData"]!.AsObject());
        Assert.Empty(data["Powerups"]!.AsArray());
    }

    [Fact]
    public void EachCallGetsItsOwnCopy()
    {
        RiftLobbyService.RuntimeData("mvs_joker_rift")["RuntimeChapterData"]!.AsObject().Clear();

        Assert.NotEmpty(RiftLobbyService.RuntimeData("mvs_joker_rift")["RuntimeChapterData"]!.AsObject());
    }

    [Theory]
    [InlineData("character_wonder_woman", true)]
    [InlineData("character_c022", false)]
    [InlineData("character_supershaggy", false)]
    [InlineData("", false)]
    // The TS server never answers a lock of a disabled character (the game then hangs); this answers and refuses it.
    public async Task ADisabledCharacterIsRefusedNotLeftUnanswered(string character, bool locked)
    {
        var lobbies = new RiftLobbyService(new ServiceCollection().BuildServiceProvider(), null!, new StaticMonitor(new LobbySettings()),
            TimeProvider.System, NullLogger<RiftLobbyService>.Instance);
        var request = new JsonObject { ["Loadout"] = new JsonObject { ["Character"] = character, ["Skin"] = "skin" } };

        var answer = await lobbies.LockLoadoutAsync(new JsonObject { ["id"] = "0000000000000000000a0009" }, request, CancellationToken.None);

        Assert.Equal(locked, (bool)answer["body"]!["bAreAllLoadoutsLocked"]!);
        Assert.Equal(character, (string)answer["body"]!["Loadout"]!["Character"]!);
    }

    private sealed class StaticMonitor(LobbySettings value) : IOptionsMonitor<LobbySettings>
    {
        public LobbySettings CurrentValue => value;

        public LobbySettings Get(string? name) => value;

        public IDisposable? OnChange(Action<LobbySettings, string?> listener) => null;
    }
}
