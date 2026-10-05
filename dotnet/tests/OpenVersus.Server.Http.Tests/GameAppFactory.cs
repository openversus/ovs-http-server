using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>The http service with a JWT secret, and clients that carry a valid game session token.</summary>
public sealed class GameAppFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-0123456789abcdef0123456789abcdef";
    public const string AccountId = "0000000000000000000a0001";

    /// <summary>The service's registrations as its program made them, for Registrations.UnboundOptions.</summary>
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
        builder.UseSetting("Access:JwtSecret", Secret);
        // Never a live router: a batch item another service owns would go there. Nothing listens on port 1.
        builder.UseSetting("Batch:EdgeUrl", "http://127.0.0.1:1");
        builder.UseSetting("Batch:TsUrl", "http://127.0.0.1:1");
    }

    public static string Token(string secret = Secret, TimeSpan? lifetime = null) =>
        AccessTokens.Sign(new JsonObject { ["id"] = AccountId, ["username"] = "Tester" }, secret, lifetime ?? TimeSpan.FromHours(1), DateTimeOffset.UtcNow);

    /// <summary>A client whose every request carries a valid x-hydra-access-token.</summary>
    public HttpClient CreateGameClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(HydraToken.Header, Token());
        return client;
    }
}
