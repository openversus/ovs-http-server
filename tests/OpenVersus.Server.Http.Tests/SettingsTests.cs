using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// A setting changed while the server runs applies at once (Hiss:ContentRevision, read into every catch-all answer's
/// Crc; this service has no stub left to watch), and a value the setting cannot take is refused. And the control API
/// is not on the public listener. Each test gets its own server, so a change cannot leak into another test.
/// </summary>
public sealed class SettingsTests : IDisposable
{
    // A route the TS server never handled: its catch-all answer carries the Crc, the default CRC plus Hiss:ContentRevision.
    private const string CatchAllPath = "/ssc/invoke/get_preferred_currency";

    private readonly GameAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private RuntimeSettings Settings => _factory.Services.GetRequiredService<RuntimeSettings>();

    private async Task<double> CrcAsync(HttpClient client)
    {
        var answer = System.Text.Json.Nodes.JsonNode.Parse(await (await client.GetAsync(CatchAllPath)).Content.ReadAsStringAsync())!;
        return answer["body"]!["Crc"]!.GetValue<double>();
    }

    [Fact]
    public async Task ChangingTheContentRevisionAppliesAtOnce()
    {
        var client = _factory.CreateGameClient();
        Assert.Equal(1267552956 + 15, await CrcAsync(client));

        Assert.Null(await Settings.SetAsync("Hiss:ContentRevision", "16", SettingScope.Instance));
        Assert.Equal(1267552956 + 16, await CrcAsync(client));
        // The fallback answers the same way.
        Assert.Equal(200, (int)(await client.GetAsync("/not/a/route")).StatusCode);

        Assert.True(await Settings.RemoveAsync("Hiss:ContentRevision", SettingScope.Instance));
        Assert.Equal(1267552956 + 15, await CrcAsync(client));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public async Task AValueTheSettingCannotTakeIsRefused(string value)
    {
        Assert.NotNull(await Settings.SetAsync("Hiss:ContentRevision", value, SettingScope.Instance));
        Assert.Equal(1267552956 + 15, await CrcAsync(_factory.CreateGameClient()));
    }

    [Fact]
    public async Task WithoutRedisAClusterChangeIsRefused()
    {
        Assert.NotNull(await Settings.SetAsync("Hiss:ContentRevision", "16", SettingScope.Cluster));
    }

    [Fact]
    public async Task AnUnknownSettingIsRefused()
    {
        Assert.NotNull(await Settings.SetAsync("Hiss:ContentRevisio", "16", SettingScope.Instance));
    }

    [Fact]
    public async Task TheControlApiIsNotOnThePublicListener()
    {
        var client = _factory.CreateGameClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/control/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/control/settings/Hiss:ContentRevision", new StringContent("16"))).StatusCode);
        // A control path the service does not have is no game route either (the game's fallback asked for a token).
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/control/ops/no-such")).StatusCode);
        Assert.Equal(1267552956 + 15, await CrcAsync(client));
    }
}
