using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Identity;
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
        var lobbies = new RiftLobbyService(new ServiceCollection().BuildServiceProvider(), null!, null!, new StaticMonitor(new LobbySettings()),
            TimeProvider.System, NullLogger<RiftLobbyService>.Instance);
        var request = new JsonObject { ["Loadout"] = new JsonObject { ["Character"] = character, ["Skin"] = "skin" } };

        var answer = await lobbies.LockLoadoutAsync(new JsonObject { ["id"] = "0000000000000000000a0009" }, request, CancellationToken.None);

        Assert.Equal(locked, (bool)answer["body"]!["bAreAllLoadoutsLocked"]!);
        Assert.Equal(character, (string)answer["body"]!["Loadout"]!["Character"]!);
    }

    // A player who must update is refused as a disabled character is, and toasted.
    [Fact]
    public async Task AnOutdatedClientsLockIsRefusedAndToasted()
    {
        var gate = new Gate();
        gate.Outdated.Add("0000000000000000000a0009");
        var services = new ServiceCollection().AddSingleton<IClientUpdateGate>(gate).BuildServiceProvider();
        var lobbies = new RiftLobbyService(services, null!, null!, new StaticMonitor(new LobbySettings()), TimeProvider.System, NullLogger<RiftLobbyService>.Instance);
        var request = new JsonObject { ["Loadout"] = new JsonObject { ["Character"] = "character_wonder_woman", ["Skin"] = "skin" } };

        var refused = await lobbies.LockLoadoutAsync(new JsonObject { ["id"] = "0000000000000000000a0009" }, request, CancellationToken.None);
        Assert.False((bool)refused["body"]!["bAreAllLoadoutsLocked"]!);
        Assert.Equal(0, (int)refused["return_code"]!);
        Assert.Equal(["0000000000000000000a0009"], gate.Modals);

        var allowed = await lobbies.LockLoadoutAsync(new JsonObject { ["id"] = "0000000000000000000a0010" }, request, CancellationToken.None);
        Assert.True((bool)allowed["body"]!["bAreAllLoadoutsLocked"]!);
    }

    private sealed class Gate : IClientUpdateGate
    {
        public HashSet<string> Outdated { get; } = [];
        public List<string> Modals { get; } = [];

        public Task<IReadOnlyList<ClientUpdateState>> RequiringUpdateAsync(IEnumerable<string> playerIds) =>
            Task.FromResult<IReadOnlyList<ClientUpdateState>>(playerIds.Where(Outdated.Contains).Select(p => new ClientUpdateState(p, "", false, true)).ToList());

        public Task<IReadOnlyList<bool>> RequestModalsAsync(IEnumerable<string> playerIds)
        {
            Modals.AddRange(playerIds);
            return Task.FromResult<IReadOnlyList<bool>>([]);
        }

        public Task<ClientUpdateState> ForRequestAsync(AccountLookup lookup, JsonObject? claims) => throw new NotSupportedException();
        public Task<double> ModalNonceAsync(string playerId) => throw new NotSupportedException();
        public JsonObject FailureBody() => throw new NotSupportedException();
    }

    private sealed class StaticMonitor(LobbySettings value) : IOptionsMonitor<LobbySettings>
    {
        public LobbySettings CurrentValue => value;

        public LobbySettings Get(string? name) => value;

        public IDisposable? OnChange(Action<LobbySettings, string?> listener) => null;
    }
}
