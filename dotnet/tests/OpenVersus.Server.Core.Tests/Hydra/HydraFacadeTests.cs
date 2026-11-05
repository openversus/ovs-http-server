using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hydra;
using HydraCodec = OpenVersus.Server.Core.Hydra.Hydra;

namespace OpenVersus.Server.Core.Tests.Hydra;

public sealed class HydraFacadeTests
{
    private const string Json = """{"a":1,"when":{"_hydra_unix_date":5},"list":[true,null,-2,1.5]}""";

    [Fact]
    public void JsonTextObjectsAndNodesEncodeAlike()
    {
        byte[] fromText = HydraCodec.EncodeJson(Json);
        Assert.Equal(fromText, HydraCodec.Encode(JsonNode.Parse(Json)));
        Assert.Equal(fromText, HydraCodec.Encode(new { a = 1, when = new Dictionary<string, int> { ["_hydra_unix_date"] = 5 }, list = new object?[] { true, null, -2, 1.5 } }));
    }

    [Fact]
    public void DecodingGivesBackTheJson()
    {
        Assert.Equal(Json, HydraCodec.DecodeToJson(HydraCodec.EncodeJson(Json)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Json), HydraCodec.Decode(HydraCodec.EncodeJson(Json))));
    }

    [Fact]
    public void AStringIsEncodedAsAString()
    {
        Assert.Equal("\"{}\"", HydraCodec.DecodeToJson(HydraCodec.Encode("{}")));
        Assert.Equal("{}", HydraCodec.DecodeToJson(HydraCodec.EncodeJson("{}")));
    }

    [Fact]
    public void NaNIsTextInJsonAndADoubleInANode()
    {
        byte[] nan = Convert.FromHexString("217ff8000000000000");
        Assert.Equal("\"NaN\"", HydraCodec.DecodeToJson(nan));
        Assert.True(double.IsNaN(HydraCodec.Decode(nan)!.GetValue<double>()));
        Assert.Equal(nan, HydraCodec.Encode(HydraCodec.Decode(nan)));
    }
}
