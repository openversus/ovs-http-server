using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenVersus.Server.Core.Hydra;

/// <summary>
/// The Hydra codec in one place: anything to Hydra bytes and back. Values follow the TS data's conventions (see
/// <see cref="HydraEncoder"/>): <c>{ "_hydra_unix_date": n }</c> is a DATE, and so on.
/// </summary>
public static class Hydra
{
    // Decoded Hydra can hold NaN doubles (the TS data's undefined fields), which plain JSON text cannot.
    private static readonly JsonSerializerOptions s_text = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static readonly JsonSerializerOptions s_indented = new(s_text) { WriteIndented = true };

    /// <summary>Encodes a value: a JSON node, or any object, serialized with <paramref name="options"/> first.</summary>
    public static byte[] Encode<T>(T value, bool webSocket = false, JsonSerializerOptions? options = null) =>
        HydraEncoder.Encode(value as JsonNode ?? JsonSerializer.SerializeToNode(value, options), webSocket);

    /// <summary>Encodes JSON text. (<see cref="Encode{T}"/> with a string would encode the string itself.)</summary>
    public static byte[] EncodeJson(string json, bool webSocket = false) =>
        HydraEncoder.Encode(JsonNode.Parse(json), webSocket);

    /// <summary>Decodes Hydra bytes (a websocket message's frame is read through) to a JSON node.</summary>
    public static JsonNode? Decode(ReadOnlySpan<byte> bytes) => HydraDecoder.Decode(bytes);

    /// <summary>
    /// Decodes Hydra bytes to JSON text. JSON has no NaN, so a NaN double comes out as the string <c>"NaN"</c>; use
    /// <see cref="Decode"/> where the exact value matters.
    /// </summary>
    public static string DecodeToJson(ReadOnlySpan<byte> bytes, bool indented = false) =>
        Decode(bytes)?.ToJsonString(indented ? s_indented : s_text) ?? "null";
}
