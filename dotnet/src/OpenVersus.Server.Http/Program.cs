using FastEndpoints;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

// The game's HTTP API (the TS server's index service). The public port is HTTP_PORT, as in the TS server's .env.
var builder = OpenVersusHost.CreateBuilder(new ServiceDefinition("http", "HTTP_PORT", DefaultPublicPort: 8000, DefaultControlPort: 17801), args);
builder.AddSetting<StubSettings>("Stubs");
builder.Services.AddFastEndpoints();

var app = builder.Build();

// Before routing: the Hydra SDK sends some GETs as PUT with the real method in a header.
app.UseHydraMethodOverride();
app.UseRouting();
app.UseFastEndpoints(c =>
{
    // Every route is open until authentication is ported (the TS server's hydraTokenMiddleware).
    c.Endpoints.Configurator = ep => ep.AllowAnonymous();
});
app.UseOpenVersus();
app.MapFallback(Stub.FallbackAsync);

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
