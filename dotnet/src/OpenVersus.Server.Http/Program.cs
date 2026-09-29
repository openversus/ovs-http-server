using FastEndpoints;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.FileStorage;
using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Inventory;
using OpenVersus.Server.Core.Layouts;
using OpenVersus.Server.Core.Leaderboards;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Profiles;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Hosting;
using OpenVersus.Server.Http.Stubs;

// The game's HTTP API (the TS server's index service). The public port is HTTP_PORT, as in the TS server's .env.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Http, args);
builder.AddSetting<StubSettings>("Stubs");
builder.AddAccess();
builder.AddFriends();
builder.AddProfiles();
builder.AddLayouts();
builder.AddFileStorage();
builder.AddRanks();
builder.AddLeaderboards();
builder.AddAccountResolver();
builder.AddInventory();
builder.AddMatchHistory();
builder.Services.AddFastEndpoints();

var app = builder.Build();

// Before routing: the Hydra SDK sends some GETs as PUT with the real method in a header.
app.UseHydraMethodOverride();
// The game's Hydra binary bodies become JSON for the endpoints, and their JSON answers go back as Hydra.
app.UseHydraBodies();
app.UseRouting();
// Names the endpoint that answered (ported or stub), for captures and the route map tests.
app.Use((context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<EndpointDefinition>() is { } endpoint)
    {
        context.Response.Headers[Stub.EndpointHeader] = endpoint.EndpointType.Name;
    }

    return next(context);
});
// Game routes answer only a valid session token, as the TS server's hydraTokenMiddleware.
app.UseHydraToken();
app.UseFastEndpoints(c =>
{
    // ASP.NET's authorization is not used: game endpoints require the session token through RequiresHydraToken
    // (see HydraToken), unless they carry NoHydraToken; the other kinds (OpenVersus client, website, AccelByte) do
    // their own checks, as in the TS server.
    c.Endpoints.Configurator = ep =>
    {
        ep.AllowAnonymous();
        // FastEndpoints does not carry class attributes into the endpoint's metadata, so the exemption is read here.
        var exemption = ep.EndpointType.GetCustomAttributes(typeof(NoHydraTokenAttribute), false).OfType<NoHydraTokenAttribute>().SingleOrDefault();
        bool game = ep.EndpointType.Namespace?.StartsWith("OpenVersus.Server.Http.Endpoints.Game", StringComparison.Ordinal) == true;
        if (game && (exemption is null || exemption.RouteValue is not null))
        {
            ep.Options(b => b.WithMetadata(exemption is null ? [RequiresHydraToken.Instance] : [RequiresHydraToken.Instance, exemption]));
        }
    };
});
app.UseOpenVersus();
// The TS server checks the token before it knows whether a path exists.
app.MapFallback(Stub.FallbackAsync).WithMetadata(RequiresHydraToken.Instance);

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
