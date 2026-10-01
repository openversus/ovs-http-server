using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Http.Hosting;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The party-lobby requests' GameplayPreferences reach the store before the endpoint, which still reads the whole body;
/// nothing else does, and a failing store never turns a request away.
/// </summary>
public sealed class GameplayPreferencesRecorderTests : IAsyncLifetime
{
    private sealed class RecordingStore : IGameplayPreferencesStore
    {
        public readonly List<(string Account, string? Raw, string? Ip)> Saved = [];
        public bool Fail;

        public Task<long?> SaveAsync(string accountId, JsonNode? raw, string? ip, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("store down");
            }

            Saved.Add((accountId, raw?.ToJsonString(), ip));
            return Task.FromResult(GameplayPreferences.Parse(raw));
        }
    }

    private readonly RecordingStore _store = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGameplayPreferencesStore>(_store);
        _app = builder.Build();
        _app.UseHydraBodies();
        _app.UseRouting();
        // The session the token check leaves (HydraToken).
        _app.Use((context, next) =>
        {
            context.Features.Set(new HydraSession("token", new JsonObject { ["id"] = "0000000000000000000c0001", ["current_ip"] = "198.51.100.40" }));
            return next(context);
        });
        _app.UseGameplayPreferencesRecorder();
        _app.MapPut("/ssc/invoke/{name}", (JsonNode? body) => Results.Json(body));
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static ByteArrayContent Hydra(string json)
    {
        var content = new ByteArrayContent(HydraCodec.EncodeJson(json));
        content.Headers.ContentType = new MediaTypeHeaderValue(HydraBodies.ContentType);
        return content;
    }

    private const string Body = """{"AutoPartyPreference":false,"CrossplayPreference":1,"GameplayPreferences":448,"LobbyTemplate":"party_lobby"}""";

    [Theory]
    [InlineData("/ssc/invoke/create_party_lobby")]
    [InlineData("/ssc/invoke/lock_lobby_loadout")]
    [InlineData("/ssc/invoke/set_ready_for_lobby")]
    [InlineData("/ssc/invoke/set_lobby_joinable")]
    [InlineData("/ssc/invoke/set_lobby_not_joinable")]
    [InlineData("/ssc/invoke/create_rift_lobby")]
    [InlineData("/SSC/invoke/Lock_Lobby_Loadout")]
    public async Task APartyLobbyRequestsValueIsStoredAndTheEndpointStillReadsTheBody(string path)
    {
        var response = await _client.PutAsync(path, Json(Body));
        Assert.Equal([("0000000000000000000c0001", "448", "198.51.100.40")], _store.Saved);
        Assert.Equal(Body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AHydraBodyToo()
    {
        var response = await _client.PutAsync("/ssc/invoke/lock_lobby_loadout", Hydra(Body));
        Assert.Equal("448", Assert.Single(_store.Saved).Raw);
        Assert.Equal(HydraCodec.EncodeJson(Body), await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    // Not a party-lobby request.
    [InlineData("/ssc/invoke/lock_lobby_loadout_x", Body)]
    [InlineData("/ssc/invoke/equip_banner", Body)]
    // No value at all: nothing is stored (an explicit null goes to the store, which stores nothing).
    [InlineData("/ssc/invoke/lock_lobby_loadout", """{"LobbyTemplate":"party_lobby"}""")]
    [InlineData("/ssc/invoke/lock_lobby_loadout", "not json")]
    public async Task OtherwiseNothingReachesTheStore(string path, string body)
    {
        var response = await _client.PutAsync(path, Json(body));
        Assert.Empty(_store.Saved);
        Assert.True(response.IsSuccessStatusCode || body == "not json");
    }

    [Fact]
    public async Task AFailingStoreNeverTurnsTheRequestAway()
    {
        _store.Fail = true;
        var response = await _client.PutAsync("/ssc/invoke/create_party_lobby", Json(Body));
        Assert.Equal(Body, await response.Content.ReadAsStringAsync());
    }
}
