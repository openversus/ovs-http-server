using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Edge;

// The edge (docs/REALTIME.md "The edge"): the game's websocket on EDGE_PORT, any path, behind the TLS-terminating proxy.
// Each game gets its own link to a gateway node (found in the instance registry); the node does everything a direct
// connection gets, and the edge only forwards. When the node goes, the edge moves the game to another one, and the game
// never sees it. No game logic, no state outside memory: an edge that dies takes its games' sockets with it.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Edge, args);
builder.AddEdge();

var app = builder.Build();
app.UseOpenVersus();
app.UseEdge();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
