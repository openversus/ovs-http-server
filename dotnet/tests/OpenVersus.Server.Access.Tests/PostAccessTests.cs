using System.Text.Json.Nodes;
using OpenVersus.Server.Access.Endpoints.Game.Access;

namespace OpenVersus.Server.Access.Tests;

/// <summary>What of the login body is read: auth.epic, a string of a token's size, nothing else.</summary>
public sealed class PostAccessTests
{
    [Fact]
    public void TheEpicCredentialIsReadFromAuthEpic()
    {
        Assert.Equal("eyJ.abc.def", PostAccess.EpicCredential(JsonNode.Parse("""{"auth":{"epic":"eyJ.abc.def","fail_on_missing":false},"metadata":{}}""")));
        Assert.Equal("", PostAccess.EpicCredential(JsonNode.Parse("""{"auth":{"steam":"0800","fail_on_missing":false}}""")));
        Assert.Equal("", PostAccess.EpicCredential(JsonNode.Parse("""{"auth":{"epic":12}}""")));
        Assert.Equal("", PostAccess.EpicCredential(JsonNode.Parse("""{"auth":"epic"}""")));
        Assert.Equal("", PostAccess.EpicCredential(JsonNode.Parse("""{"auth":{"epic":""}}""")));
        Assert.Equal("", PostAccess.EpicCredential(JsonNode.Parse("[]")));
        Assert.Equal("", PostAccess.EpicCredential(null));
        Assert.Equal("", PostAccess.EpicCredential(JsonNode.Parse("{\"auth\":{\"epic\":\"" + new string('a', 9000) + "\"}}")));
    }
}
