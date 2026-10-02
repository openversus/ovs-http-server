using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Proxy;

namespace OpenVersus.Server.Proxy.Tests;

/// <summary>
/// The proxy between two stand-in backends that answer with their own name and the headers they got: ported routes
/// (with the Hydra SDK's method override) reach C#, everything else the TS server, and both see the client's Host and
/// X-Real-IP.
/// </summary>
public sealed class ProxyTests : IAsyncLifetime
{
    private WebApplication? _csharp, _ts, _access;
    private WebApplicationFactory<Program>? _proxy;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        (_csharp, string csharpUrl) = await BackendAsync("csharp");
        (_ts, string tsUrl) = await BackendAsync("ts");
        (_access, string accessUrl) = await BackendAsync("access");
        _proxy = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Proxy:CSharpUrl", csharpUrl)
            .UseSetting("Proxy:TsUrl", tsUrl)
            .UseSetting("Proxy:PortedRoutes", "POST /access, GET /profiles/bulk, PUT /profiles/{id}/inventory")
            // POST /access is the access service's (docs/routes.json); the profiles routes are the http service's.
            .UseSetting("Proxy:Services", $"access={accessUrl}")
            .UseSetting("Control:Socket", "off"));
        _client = _proxy.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _proxy?.Dispose();
        foreach (var app in new[] { _csharp, _ts, _access })
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    private static async Task<(WebApplication, string)> BackendAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(context => context.Response.WriteAsync($"{name}|{context.Request.Headers.Host}|{context.Request.Headers["X-Real-IP"]}|{context.Request.Headers["X-Forwarded-Host"]}"));
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    private async Task<string[]> SendAsync(string method, string path, string? hydraMethod = null, string? realIp = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = "game.example";
        if (hydraMethod is not null)
        {
            request.Headers.Add(ProxyRoutes.MethodOverrideHeader, hydraMethod);
        }

        if (realIp is not null)
        {
            request.Headers.Add("X-Real-IP", realIp);
        }

        using var response = await _client.SendAsync(request);
        return (await response.Content.ReadAsStringAsync()).Split('|');
    }

    [Theory]
    [InlineData("POST", "/access", null, "access")]
    [InlineData("DELETE", "/access", null, "ts")]
    [InlineData("GET", "/profiles/bulk", null, "csharp")]
    // The Hydra SDK's GET as PUT: the header names the real method.
    [InlineData("PUT", "/profiles/bulk", "GET", "csharp")]
    [InlineData("PUT", "/profiles/bulk", "get", "csharp")]
    [InlineData("PUT", "/profiles/bulk", null, "ts")]
    // A real PUT is ported; the same path's GET (sent as PUT) is not.
    [InlineData("PUT", "/profiles/abc/inventory", null, "csharp")]
    [InlineData("PUT", "/profiles/abc/inventory", "GET", "ts")]
    [InlineData("GET", "/anything/else", null, "ts")]
    public async Task RoutesByWhatIsPorted(string method, string path, string? hydraMethod, string backend)
    {
        Assert.Equal(backend, (await SendAsync(method, path, hydraMethod))[0]);
    }

    [Theory]
    [InlineData("nosuchservice=http://127.0.0.1:1")]
    [InlineData("access")]
    [InlineData("access=not a url")]
    public void AServicesEntryThatIsNotAKnownServiceAndAUrlIsRefused(string services)
    {
        Assert.Throws<FormatException>(() => ProxyRoutes.ParseServices(services));
    }

    [Fact]
    public async Task BackendsSeeTheClientsHostAndAddress()
    {
        var reply = await SendAsync("POST", "/access", realIp: "198.51.100.7");
        Assert.Equal("game.example", reply[1]);
        Assert.Equal("198.51.100.7", reply[2]);
        // The TS server would take a forwarded host name for the client's address.
        Assert.Equal("", reply[3]);
    }

    [Theory]
    [InlineData("POST /access, GET /profiles/{id}", 2)]
    [InlineData("  ", 0)]
    [InlineData("", 0)]
    public void ParsesThePortedRoutes(string value, int count)
    {
        Assert.Equal(count, ProxyRoutes.Parse(value).Count);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH /x")]
    [InlineData("GET x")]
    [InlineData("GET /a /b")]
    public void RefusesWhatIsNotMethodAndPath(string value)
    {
        Assert.Throws<FormatException>(() => ProxyRoutes.Parse(value));
    }
}
