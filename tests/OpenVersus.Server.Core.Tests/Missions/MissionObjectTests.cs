using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Missions;

namespace OpenVersus.Server.Core.Tests.Missions;

/// <summary>get_or_create_mission_object: the TS server's fixed object, emptied unless missions are on.</summary>
public sealed class MissionObjectTests
{
    private const string Id = "0000000000000000000a0001";

    private static JsonObject Containers(JsonObject answer) => answer["body"]!["server_data"]!["MissionControllerContainers"]!.AsObject();

    [Fact]
    public void OffAnswersNoContainersAndKeepsTheRest()
    {
        var answer = MissionObject.Answer(Id, enabled: false);
        Assert.Empty(Containers(answer));
        // The shape every captured answer since the switch has: the object's fields in the TS order, ClaimLocks kept.
        Assert.Equal(
            """{"body":{"updated_at":{"_hydra_unix_date":1742223633},"owner_id":"0000000000000000000a0001","unique_key":"missions","object_type_slug":"player-missions","server_data":{"MissionControllerContainers":{},"ClaimLocks":{}},"created_at":{"_hydra_unix_date":1717084198},"aggregates":{},"calculations":{},"id":"6658a026eaec8bdf5a91f886","owner":{},"expire_time":null,"owner_model":"account"},"metadata":null,"return_code":0}""",
            answer.ToJsonString());
    }

    [Fact]
    public void OnAnswersTheFixedSet()
    {
        var answer = MissionObject.Answer(Id, enabled: true);
        Assert.Equal(Id, answer["body"]!["owner_id"]!.GetValue<string>());
        Assert.Equal(9, Containers(answer).Count);
        Assert.Equal(2, Containers(answer)["miscon_ftue"]!["MissionControllers"]!["misctl_ftue"]!["Missions"]![0]!["mis_ftue_play_rift_matches"]!["MissionObjectives"]![0]!["Progress"]!.GetValue<int>());
    }

    [Fact]
    public void EachAnswerIsACopy()
    {
        Containers(MissionObject.Answer("someone", enabled: true)).Clear();
        Assert.Equal(9, Containers(MissionObject.Answer(Id, enabled: true)).Count);
        Assert.Equal(Id, MissionObject.Answer(Id, enabled: false)["body"]!["owner_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoPlayerIdMeansNoOwnerId(string? id) =>
        Assert.False(MissionObject.Answer(id, enabled: false)["body"]!.AsObject().ContainsKey("owner_id"));
}
