using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>The rollback port and the on-demand deploy request, as the TS rollbackService.ts makes them.</summary>
public sealed class MatchLauncherTests
{
    [Fact]
    // [Low, High) as the TS randomInt: High is never picked, and Low == High has no port (the TS server throws there).
    public void TheFixedPortIsInTheHalfOpenRange()
    {
        var settings = new RollbackSettings { UdpPortLow = 57000, UdpPortHigh = 57002 };
        var ports = Enumerable.Range(0, 200).Select(_ => MatchLauncher.RollbackPort(settings)).ToHashSet();

        Assert.Equal([57000, 57001], ports.Order());
        Assert.Null(MatchLauncher.RollbackPort(new RollbackSettings { UdpPortLow = 57000, UdpPortHigh = 57000 }));
        Assert.Null(MatchLauncher.RollbackPort(new RollbackSettings { UdpPortLow = 57000, UdpPortHigh = 57002, OnDemand = true }));
    }

    [Theory]
    [InlineData(60001, 60001)]
    // The high end is included, as the TS server's "> high" check leaves it.
    [InlineData(64000, 64000)]
    [InlineData(64001, 60000)]
    // The TS server would use these as they are (an empty Redis's first INCR is 1): wrapped here.
    [InlineData(1, 60000)]
    [InlineData(59999, 60000)]
    public void TheOnDemandPortWrapsToTheLowEnd(long incremented, int port) =>
        Assert.Equal(port, MatchLauncher.WrapOnDemandPort(incremented, new RollbackSettings()));

    [Fact]
    // Body and X-Deploy-Key as the TS DeployInfo.Deploy sends them for deploy-rollback-defaults.json, computed with
    // Node's JSON.stringify and crypto.createHmac("sha1", ...) for these values.
    public void TheDeployRequestIsTheTsServersByteForByte()
    {
        var settings = new RollbackSettings { OvsServer = "http://ovs.example:8000", WebhookHmacSecret = "test-secret", WebhookHost = "deployer", WebhookPort = 9001 };

        var (url, body, signature) = MatchLauncher.DeployRequest(settings, 60001);

        Assert.Equal("http://deployer:9001/hooks/deploy-rollback-server", url.ToString());
        Assert.Equal("""{"entrypoint":"\"dotnet\", \"OVS.Rollback.Server.dll\", \"60001\"","image":"ovs-rollback-server-csharp:latest","ovs_server":"http://ovs.example:8000","port":60001,"servicename":"ovs-rollback-server-csharp","created_at":0}""", body);
        Assert.Equal("a25da41d25021a8c2e68b4e3e08f800f77df07d3", signature);
    }
}
