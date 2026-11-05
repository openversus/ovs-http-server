using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Hydra;

namespace OpenVersus.Server.Core.Tests.Hydra;

/// <summary>Passing encoded values on as they are (HydraRaw) and finding them in a message (ArrayItems), for /batch.</summary>
public sealed class HydraRawTests
{
    [Fact]
    public void ARawValueIsWrittenAsItIs()
    {
        byte[] inner = HydraEncoder.Encode(new JsonObject { ["a"] = 1, ["b"] = new JsonArray("x", null) });
        byte[] outer = HydraEncoder.Encode(new JsonObject { ["before"] = true, ["raw"] = HydraRaw.Node(inner), ["after"] = 2 });
        Assert.Equal(HydraEncoder.Encode(new JsonObject { ["before"] = true, ["raw"] = HydraDecoder.Decode(inner), ["after"] = 2 }), outer);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(300)] // Array16, and a map past the first width too
    public void ArrayItemsFindsEachItemsBytes(int count)
    {
        var items = Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["status_code"] = 200 + i, ["body"] = new string('x', i) }).ToArray();
        var message = new JsonObject { ["first"] = new JsonObject { ["responses"] = "not this one" }, ["responses"] = new JsonArray(items.Select(i => i.DeepClone()).ToArray()), ["last"] = 1 };
        byte[] bytes = HydraEncoder.Encode(message);
        var ranges = HydraDecoder.ArrayItems(bytes, "responses");
        Assert.NotNull(ranges);
        Assert.Equal(count, ranges.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(HydraEncoder.Encode(items[i]), bytes[ranges[i]]);
        }
    }

    // ArrayItems steps over values without decoding them: it must end each one exactly where decoding would, for every
    // type there is (the fixtures) and every real shape (the captured responses, when OVS_TEST_HYDRA_CORPUS is set).
    [SkippableFact]
    public void ArrayItemsStepsOverEveryValueExactlyAsDecodingWould()
    {
        var fixtures = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Hydra", "encoder-fixtures.json")))!;
        var values = fixtures.Where(f => f.Value?["hex"] is not null).Select(f => Convert.FromHexString((string)f.Value!["hex"]!)).ToList();
        string? corpus = Environment.GetEnvironmentVariable("OVS_TEST_HYDRA_CORPUS");
        if (!string.IsNullOrEmpty(corpus))
        {
            values.AddRange(Directory.GetFiles(Path.Combine(corpus, "resp"), "*.bin").Select(File.ReadAllBytes));
        }

        foreach (byte[] value in values)
        {
            HydraDecoder.Decode(value); // decodes whole, or it is not a value to step over
            byte[] message = HydraEncoder.Encode(new JsonObject { ["skipped"] = HydraRaw.Node(value), ["responses"] = new JsonArray(HydraRaw.Node(value), 7) });
            var ranges = HydraDecoder.ArrayItems(message, "responses");
            Assert.NotNull(ranges);
            Assert.Equal(value, message[ranges[0]]);
            Assert.Equal([HydraCode.UInt8, 7], message[ranges[1]]);
        }

        Skip.If(string.IsNullOrEmpty(corpus), $"{values.Count} fixtures stepped over; set OVS_TEST_HYDRA_CORPUS for the captured responses too");
    }

    [Fact]
    public void ArrayItemsIsNullWithoutAnArrayThere()
    {
        Assert.Null(HydraDecoder.ArrayItems(HydraEncoder.Encode(new JsonObject { ["responses"] = new JsonObject() }), "responses"));
        Assert.Null(HydraDecoder.ArrayItems(HydraEncoder.Encode(new JsonObject { ["other"] = new JsonArray() }), "responses"));
        Assert.Null(HydraDecoder.ArrayItems(HydraEncoder.Encode(new JsonArray(1, 2)), "responses"));
    }
}
