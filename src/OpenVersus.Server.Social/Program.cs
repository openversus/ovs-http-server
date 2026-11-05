using OpenVersus.Server.Core.Friends;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared;

// Friends, relationships and blocks (routes owned by "social" in docs/routes.json), the game's and the OpenVersus
// client's. The public port is SOCIAL_PORT; the router sends these routes here.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Social, args);
builder.AddGameHttp(typeof(Program).Assembly);
builder.AddFriends();

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
