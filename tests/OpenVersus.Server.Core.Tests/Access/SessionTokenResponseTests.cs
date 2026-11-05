using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>POST /sessions/auth/token against the response prod sent in a capture (its token replaced by "fixture-code").</summary>
public sealed class SessionTokenResponseTests
{
    private static string Build(JsonNode? body) => SessionTokenResponse.Build(body, new WbNetworkSettings());

    private static readonly string s_captured = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Access", "sessions-auth-token.captured.json"));

    [Fact]
    public void AnswersTheCapturedBytes()
    {
        Assert.Equal(s_captured, Build(JsonNode.Parse("""{"code":"fixture-code","grant_type":"authorization_code"}""")));
    }

    [Fact]
    public void WithoutACodeTheTokenIsLeftOut()
    {
        // JSON.stringify drops a key whose value is undefined.
        string expected = s_captured.Replace("\"access_token\":\"fixture-code\",", "");
        Assert.Equal(expected, Build(JsonNode.Parse("""{"grant_type":"authorization_code"}""")));
        Assert.Equal(expected, Build(null));
        Assert.Equal(expected, Build(JsonNode.Parse("[1]")));
    }

    [Fact]
    public void EchoesTheCodeAsItCame()
    {
        Assert.StartsWith("{\"access_token\":null,", Build(JsonNode.Parse("""{"code":null}""")));
        Assert.StartsWith("{\"access_token\":42,", Build(JsonNode.Parse("""{"code":42}""")));
    }

    [Fact]
    public void TheAddressesAreSettings()
    {
        var network = new WbNetworkSettings { RealtimeCluster = "c", RealtimeServers = """{"s":{"ws":"ws://h:1"}}""", AvatarUrl = "https://a/1" };
        var response = JsonNode.Parse(SessionTokenResponse.Build(null, network))!;
        Assert.Equal("""{"enabled":true,"default-cluster":"c","servers":{"c":{"s":{"ws":"ws://h:1"}}}}""", response["sdk"]!["realtime"]!.ToJsonString());
        Assert.Equal("https://a/1", (string)response["account"]!["avatar"]!["image_url"]!);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("\"a string\"")]
    public void ServersThatAreNotAnObjectAreRefused(string servers)
    {
        var settings = new WbNetworkSettings { RealtimeServers = servers };
        Assert.NotEmpty(settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)));
    }
}
