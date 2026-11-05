using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Seasons;

namespace OpenVersus.Server.Core.Tests.Seasons;

/// <summary>The login profile's SeasonalData for seasons the TS literal has none for (seasonal-data.json).</summary>
public sealed class SeasonalDataTests
{
    private static JsonObject Login(params string[] seasons)
    {
        var seasonal = new JsonObject();
        foreach (string season in seasons)
        {
            seasonal[season] = new JsonObject { ["NumLogins"] = 1 };
        }

        return new JsonObject { ["profile"] = new JsonObject { ["server_data"] = new JsonObject { ["SeasonalData"] = seasonal } } };
    }

    [Fact]
    public void SeasonSixIsAddedAfterTheLiteralsSeasons()
    {
        var login = Login("Season:SeasonFour", "Season:SeasonFive");
        Assert.True(SeasonalData.AddTo(login, "Season:SeasonSix"));
        var seasonal = login["profile"]!["server_data"]!["SeasonalData"]!.AsObject();
        Assert.Equal(["Season:SeasonFour", "Season:SeasonFive", "Season:SeasonSix"], seasonal.Select(kv => kv.Key));
        Assert.Equal(["LastLoginDay", "NumDaysLoggedIn", "NumLogins", "Ranked", "Arenas"], seasonal["Season:SeasonSix"]!.AsObject().Select(kv => kv.Key));
    }

    [Theory]
    // Season 5 current: the TS literal's own answer, unchanged.
    [InlineData("Season:SeasonFive")]
    // No data for it.
    [InlineData("Season:SeasonSeven")]
    public void OtherwiseTheLoginIsUnchanged(string current)
    {
        var login = Login("Season:SeasonFive");
        string before = login.ToJsonString();
        Assert.False(SeasonalData.AddTo(login, current));
        Assert.Equal(before, login.ToJsonString());
    }

    [Fact]
    public void AnEntryTheLoginHasIsKept()
    {
        var login = Login("Season:SeasonSix");
        Assert.False(SeasonalData.AddTo(login, "Season:SeasonSix"));
        Assert.Equal(1, login["profile"]!["server_data"]!["SeasonalData"]!["Season:SeasonSix"]!["NumLogins"]!.GetValue<int>());
    }

    [Fact]
    public void DatesAreHydraDatesAndNothingIsIsoText()
    {
        var entry = SeasonalData.For("Season:SeasonSix")!;
        Assert.Equal(1746748800, entry["LastLoginDay"]!["_hydra_unix_date"]!.GetValue<long>()); // 2025-05-09T00:00:00+00:00
        Assert.DoesNotMatch(@"\d{4}-\d\d-\d\dT\d\d:\d\d", entry.ToJsonString());
    }
}
