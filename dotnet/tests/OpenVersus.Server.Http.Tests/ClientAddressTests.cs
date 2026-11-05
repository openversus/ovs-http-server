using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenVersus.Server.Core.Hosting;

namespace OpenVersus.Server.Http.Tests;

/// <summary>
/// The client address behind the reverse proxy, by the TS server's rule (src/utils/clientIp.ts and its
/// tests/clientIp.test.ts, which these mirror), and what counts as an address by Node's net.isIP (ip-fixtures.json,
/// written by tools/access/gen_ip_fixtures.mjs).
/// </summary>
public sealed class ClientAddressTests
{
    private const string Socket = "10.0.0.1";

    private static string Of(params (string Name, string Value)[] headers) => Of(Socket, false, headers);

    private static string Of(string remote, bool strip, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        foreach (var (name, value) in headers)
        {
            context.Request.Headers.Append(name, value);
        }

        return ClientAddress.Of(context, strip);
    }

    [Fact]
    public void RealIpFromTheProxyIsTheClient()
    {
        Assert.Equal("198.51.100.7", Of(("X-Real-IP", "198.51.100.7")));
        Assert.Equal("198.51.100.7", Of(("X-Real-IP", "198.51.100.7"), ("X-Forwarded-For", "203.0.113.1")));
    }

    [Fact]
    public void ForwardedForGivesItsLastEntryNeverOneTheClientWrote()
    {
        Assert.Equal("198.51.100.7", Of(("X-Forwarded-For", "198.51.100.7")));
        Assert.Equal("198.51.100.7", Of(("X-Forwarded-For", "203.0.113.66, 198.51.100.7")));
        Assert.Equal("198.51.100.7", Of(("X-Forwarded-For", "203.0.113.66"), ("X-Forwarded-For", "198.51.100.7")));
    }

    [Fact]
    public void IPv6ClientsInAnyForm()
    {
        Assert.Equal("2001:db8::1", Of(("X-Real-IP", "2001:db8::1")));
        Assert.Equal("2001:db8::1", Of(("X-Forwarded-For", "2001:db8::1")));
    }

    [Fact]
    public void AHeaderThatIsNotAnAddressDoesNotHideTheNextOne()
    {
        Assert.Equal("198.51.100.7", Of(("X-Forwarded-Host", "prod.openversus.org"), ("X-Forwarded-For", "198.51.100.7")));
        Assert.Equal("198.51.100.7", Of(("X-Real-IP", "unknown"), ("X-Forwarded-For", "198.51.100.7")));
        Assert.Equal("198.51.100.7", Of(("X-Forwarded-Host", "198.51.100.7")));
    }

    [Fact]
    public void WithoutAnAddressInTheHeadersTheConnectionsOwn()
    {
        Assert.Equal(Socket, Of());
        Assert.Equal(Socket, Of(("X-Real-IP", "not-an-ip")));
        Assert.Equal(Socket, Of(("X-Forwarded-For", "198.51.100.7, garbage")));
    }

    [Fact]
    public void TheMappedPrefixGoesOnlyWhenAsked()
    {
        Assert.Equal("::ffff:10.0.0.1", Of("::ffff:10.0.0.1", false));
        Assert.Equal("10.0.0.1", Of("::ffff:10.0.0.1", true));
    }

    [Fact]
    public void AnAddressIsWhatNodesIsIPTakes()
    {
        var cases = JsonSerializer.Deserialize<JsonElement[][]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ip-fixtures.json")))!;
        Assert.True(cases.Length > 20000, $"only {cases.Length} cases");
        var wrong = cases.Where(c => ClientAddress.IsIPAddress(c[0].GetString()!) != c[1].GetBoolean())
            .Select(c => $"{JsonSerializer.Serialize(c[0].GetString())}: node {c[1].GetBoolean()}").Take(20).ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
