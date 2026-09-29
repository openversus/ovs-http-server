using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The stub status is a setting: changing it while the server runs changes what every stub (and the fallback)
/// answers, and a value the setting cannot take is refused. And the control API is not on the public listener.
/// Each test gets its own server, so a change cannot leak into another test.
/// </summary>
public sealed class SettingsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    private RuntimeSettings Settings => _factory.Services.GetRequiredService<RuntimeSettings>();

    [Fact]
    public async Task ChangingTheStubStatusAppliesAtOnce()
    {
        var client = _factory.CreateClient();
        Assert.Equal(501, (int)(await client.GetAsync("/friends/me")).StatusCode);

        Assert.Null(await Settings.SetAsync("Stubs:StatusCode", "503", SettingScope.Instance));
        Assert.Equal(503, (int)(await client.GetAsync("/friends/me")).StatusCode);
        Assert.Equal(503, (int)(await client.GetAsync("/not/a/route")).StatusCode);

        Assert.True(await Settings.RemoveAsync("Stubs:StatusCode", SettingScope.Instance));
        Assert.Equal(501, (int)(await client.GetAsync("/friends/me")).StatusCode);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("42")]
    [InlineData("600")]
    public async Task AStatusTheSettingCannotTakeIsRefused(string value)
    {
        Assert.NotNull(await Settings.SetAsync("Stubs:StatusCode", value, SettingScope.Instance));
        Assert.Equal(501, (int)(await _factory.CreateClient().GetAsync("/friends/me")).StatusCode);
    }

    [Fact]
    public async Task WithoutRedisAClusterChangeIsRefused()
    {
        Assert.NotNull(await Settings.SetAsync("Stubs:StatusCode", "503", SettingScope.Cluster));
    }

    [Fact]
    public async Task AnUnknownSettingIsRefused()
    {
        Assert.NotNull(await Settings.SetAsync("Stubs:StatusCod", "503", SettingScope.Instance));
    }

    [Fact]
    public async Task TheControlApiIsNotOnThePublicListener()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/control/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/control/settings/Stubs:StatusCode", new StringContent("200"))).StatusCode);
        Assert.Equal(501, (int)(await client.GetAsync("/friends/me")).StatusCode);
    }
}
