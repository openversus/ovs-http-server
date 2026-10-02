using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Http.Shared.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>The http service with a JWT secret, and clients that carry a valid game session token.</summary>
public sealed class GameAppFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-0123456789abcdef0123456789abcdef";
    public const string AccountId = "0000000000000000000a0001";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
