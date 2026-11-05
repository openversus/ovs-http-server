using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Realtime;

// The realtime gateway (docs/REALTIME.md): the game's websocket on WEBSOCKET_PORT, any path. Each node holds the sockets
// of the players connected to it: the handshake, the ping, presence, and delivery of what the other services send
// through ws:send. No game logic: what happens when a player connects or leaves is read off realtime:connections by
// the services it concerns. Any number of replicas.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Realtime, args);
builder.AddGateway();

var app = builder.Build();
app.UseOpenVersus();
app.UseGateway();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
