using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Lobbies.Tests;

/// <summary>
/// The player's GameplayPreferences (deadzones, input buffer, pickup: every match uses the exact value) are recorded
/// from each party-lobby request this service answers, before its endpoint runs. How the recorder reads a body:
/// OpenVersus.Server.Http.Tests.
/// </summary>
public sealed class GameplayPreferencesTests(ServiceFactory<Program> factory) : IClassFixture<ServiceFactory<Program>>
{
    private sealed class RecordingStore : IGameplayPreferencesStore
    {
        public readonly List<(string Account, string? Raw)> Saved = [];

        public Task<long?> SaveAsync(string accountId, JsonNode? raw, string? ip, CancellationToken ct)
        {
            lock (Saved)
            {
                Saved.Add((accountId, raw?.ToJsonString()));
            }

            return Task.FromResult(GameplayPreferences.Parse(raw));
        }
    }

    public static TheoryData<string> Paths => [.. GameplayPreferencesRecorder.Paths];

    [Fact]
    public void EveryPathItRecordsFromIsThisServicesRoute()
    {
        Assert.All(GameplayPreferencesRecorder.Paths, p => Assert.Equal(KnownServices.Lobbies.Name, RouteTable.OwnerOfPath(p)));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task EachPartyLobbyRequestsValueIsRecordedHere(string path)
    {
        var store = new RecordingStore();
        // The gate would turn a request away first without Redis; the recorder is what is under test.
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Clients:VersionCheck", "false");
            b.ConfigureTestServices(s => s.AddSingleton<IGameplayPreferencesStore>(store));
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, ServiceFactory<Program>.Token());

        using var response = await client.PutAsync(path, new StringContent("""{"GameplayPreferences":448,"LobbyTemplate":"party_lobby"}""",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal([(ServiceFactory<Program>.AccountId, "448")], store.Saved);
        // An endpoint here answered it.
        Assert.True(response.Headers.Contains(Stub.EndpointHeader), $"{path}: {(int)response.StatusCode}");
    }
}
