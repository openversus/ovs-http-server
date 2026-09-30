using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>The responses the http service warns about at startup (docs/FROZEN-ACCOUNT-DATA.md lists the same ones).</summary>
public sealed class FrozenAccountDataTests(GameAppFactory factory) : IClassFixture<GameAppFactory>
{
    [Fact]
    public void EveryResponseServingCapturedAccountDataIsRegistered()
    {
        var responses = factory.Services.GetServices<FrozenAccountData>().Select(f => f.Response).Order().ToList();
        Assert.Equal(
        [
            "GET /layout/dokken-layout-type/personalized/{variant}/{id}",
            "GET /ssc/invoke/load_rifts",
            "POST /access",
            "POST /sessions/auth/token",
            "PUT /ssc/invoke/create_rift_lobby",
            "PUT /ssc/invoke/start_rift_node",
        ], responses);
    }
}
