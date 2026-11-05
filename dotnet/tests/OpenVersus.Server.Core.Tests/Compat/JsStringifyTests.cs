using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Tests.Compat;

/// <summary>Js.Stringify against what node's JSON.stringify writes for the same values.</summary>
public sealed class JsStringifyTests
{
    [Theory]
    // Written as itself: non-ASCII, emoji (a surrogate pair), <>&' and U+2028 (System.Text.Json escapes all of these).
    [InlineData("♡Hatsune~Alice♡", "\"♡Hatsune~Alice♡\"")]
    [InlineData("😀 <b>&'", "\"😀 <b>&'\"")]
    [InlineData("a\u2028b", "\"a\u2028b\"")]
    // Escaped: the quote, the backslash, the short control escapes, other controls as lowercase \u00xx.
    [InlineData("\"\\\b\f\n\r\t", "\"\\\"\\\\\\b\\f\\n\\r\\t\"")]
    [InlineData("\u0001\u001f\u007f", "\"\\u0001\\u001f\u007f\"")]
    public void StringsAsJsonStringifyWritesThem(string value, string expected) =>
        Assert.Equal(expected, Js.Stringify(JsonValue.Create(value)));

    [Fact]
    // Lone surrogates (JSON.stringify since ES2019), in lowercase hex. Not theory data: xUnit's serialization turns a
    // lone surrogate into U+FFFD before the test sees it.
    public void LoneSurrogatesAreEscaped()
    {
        Assert.Equal("\"x\\ud83d\"", Js.Stringify(JsonValue.Create("x" + (char)0xD83D)));
        Assert.Equal("\"\\ude00y\"", Js.Stringify(JsonValue.Create((char)0xDE00 + "y")));
        Assert.Equal("\"\\ude00\\ud83d\"", Js.Stringify(JsonValue.Create(new string([(char)0xDE00, (char)0xD83D]))));
    }

    [Fact]
    public void ObjectsKeepTheirOrderAndNullsAndNumbers()
    {
        var node = new JsonObject { ["total"] = 2, ["b"] = null, ["a"] = new JsonArray(1, null, true, "x"), ["é"] = new JsonObject() };
        Assert.Equal("""{"total":2,"b":null,"a":[1,null,true,"x"],"é":{}}""", Js.Stringify(node));
    }
}
