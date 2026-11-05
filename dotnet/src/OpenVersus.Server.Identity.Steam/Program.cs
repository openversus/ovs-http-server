using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Identity.Steam;

// The Steam identity service (docs/IDENTIFY.md "Asking Steam"): an anonymous Steam game server for the app
// (Access:SteamAppId) that asks Steam about each registered client's session ticket and holds the auth session while the
// game runs. Requests come through Redis from /api/identify; what Steam says goes back into Redis for the login, the
// presence readers and ovsctl (`ovsctl steam status`). No public port; outbound to Steam only; Steam:Enabled (on by default) turns it off.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Steam, args);
builder.Services.AddSingleton<ISteamAuthClient, SteamKitAuthClient>();
builder.AddSteamAuthSessions();

var app = builder.Build();
app.UseOpenVersus();
app.MapSteamIdentityControl();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
