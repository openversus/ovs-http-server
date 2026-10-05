using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Http.Shared;

// The website, the OpenVersus client's API and the admin pages (routes owned by "web" in docs/routes.json). The public
// port is WEB_PORT; the router sends these routes here.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Web, args);
builder.AddGameHttp(typeof(Program).Assembly);

var app = builder.Build();
app.UseGameHttp();

app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
