using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;

namespace OpenVersus.Server.Core.Static;

/// <summary>
/// Answers that are the same for everyone, as JSON text: the embedded Static/*.json files, generated from the TS
/// server's literals by tools/access/gen_templates.mjs (never edited by hand). Written as Express writes JSON
/// (JSON.stringify, byte for byte); for a Hydra request the Hydra layer encodes it.
/// </summary>
public static class StaticResponses
{
    private static readonly ConcurrentDictionary<string, string> s_cache = new();

    /// <summary>The JSON text of Static/<paramref name="name"/>.json.</summary>
    public static string Json(string name) => s_cache.GetOrAdd(name, static n =>
    {
        using var stream = typeof(StaticResponses).Assembly.GetManifestResourceStream($"OpenVersus.Server.Core.Static.{n}.json")
            ?? throw new InvalidOperationException($"Static/{n}.json is not embedded");
        return Js.Stringify(JsonNode.Parse(stream));
    });

    private static readonly ConcurrentDictionary<string, byte[]> s_hydra = new();

    /// <summary>
    /// The Hydra encoding of Static/<paramref name="name"/>.json, made once: the same bytes the Hydra layer would make
    /// from <see cref="Json"/> on every request (the largest layout is 1.6 MB).
    /// </summary>
    public static byte[] Hydra(string name) => s_hydra.GetOrAdd(name, static n => Core.Hydra.Hydra.EncodeJson(Json(n)));
}
