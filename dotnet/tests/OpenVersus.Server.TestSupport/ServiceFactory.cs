using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.TestSupport;

/// <summary>
/// A service under test: Development (the container checks every registration when it is built), a JWT secret, and
/// never a live router or TS server (nothing listens on port 1).
/// </summary>
public class ServiceFactory<TProgram> : WebApplicationFactory<TProgram> where TProgram : class
{
    public const string Secret = "test-secret-0123456789abcdef0123456789abcdef";
    public const string IdentifySecret = "test-identify-0123456789abcdef0123456789abcdef";
    public const string AccountId = "0000000000000000000a0001";

    /// <summary>The service's registrations as its program made them, for <see cref="Registrations.UnboundOptions"/>.</summary>
    public IServiceCollection Registered
    {
        get
        {
            _ = Services;
            return _registered!;
        }
    }

    private IServiceCollection? _registered;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services => _registered = services);
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Access:JwtSecret", Secret);
        builder.UseSetting("Access:IdentifySecret", IdentifySecret);
        builder.UseSetting("Batch:EdgeUrl", "http://127.0.0.1:1");
        builder.UseSetting("Batch:TsUrl", "http://127.0.0.1:1");
    }

    public static string Token() =>
        AccessTokens.Sign(new JsonObject { ["id"] = AccountId, ["username"] = "Tester" }, Secret, TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

    /// <summary>A client whose every request carries a valid x-hydra-access-token.</summary>
    public HttpClient CreateGameClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, Token());
        return client;
    }
}
