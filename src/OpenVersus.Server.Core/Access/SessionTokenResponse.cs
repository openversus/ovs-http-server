using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Access;

/// <summary>
/// The WB network account /sessions/auth/token answers with: where its realtime servers and avatar image are. The
/// defaults are the TS server's (the live service's last WB network servers).
/// </summary>
public sealed class WbNetworkSettings : IValidatableObject
{
    [Description("The WB network SDK's realtime cluster name (sdk.realtime.default-cluster).")]
    public string RealtimeCluster { get; set; } = "ec2-us-east-1-prod-network";

    [Description("The cluster's realtime servers, a JSON object: name -> { ws, wss, udp }.")]
    public string RealtimeServers { get; set; } = """
{"prod-network-realtime-2/2":{"ws":"ws://3.212.5.165:8102","wss":"wss://us-east-1-prod-network-realtime-2.wbagora.com:9102","udp":"0.0.0.0:0"},"prod-network-realtime-2/1":{"ws":"ws://3.212.5.165:8101","wss":"wss://us-east-1-prod-network-realtime-2.wbagora.com:9101","udp":"0.0.0.0:0"},"prod-network-realtime-5/1":{"ws":"ws://107.22.28.83:8101","wss":"wss://us-east-1-prod-network-realtime-5.wbagora.com:9101","udp":"0.0.0.0:0"},"prod-network-realtime-8/1":{"ws":"ws://18.212.34.2:8101","wss":"wss://us-east-1-prod-network-realtime-8.wbagora.com:9101","udp":"0.0.0.0:0"},"prod-network-realtime-6/1":{"ws":"ws://3.86.158.46:8101","wss":"wss://us-east-1-prod-network-realtime-6.wbagora.com:9101","udp":"0.0.0.0:0"},"prod-network-realtime-8/2":{"ws":"ws://18.212.34.2:8102","wss":"wss://us-east-1-prod-network-realtime-8.wbagora.com:9102","udp":"0.0.0.0:0"},"prod-network-realtime-1/2":{"ws":"ws://3.82.168.122:8102","wss":"wss://us-east-1-prod-network-realtime-1.wbagora.com:9102","udp":"0.0.0.0:0"},"prod-network-realtime-10/1":{"ws":"ws://100.24.54.107:8101","wss":"wss://us-east-1-prod-network-realtime-10.wbagora.com:9101","udp":"0.0.0.0:0"},"prod-network-realtime-10/2":{"ws":"ws://100.24.54.107:8102","wss":"wss://us-east-1-prod-network-realtime-10.wbagora.com:9102","udp":"0.0.0.0:0"},"prod-network-realtime-4/1":{"ws":"ws://44.204.13.72:8101","wss":"wss://us-east-1-prod-network-realtime-4.wbagora.com:9101","udp":"0.0.0.0:0"}}
""";

    [Description("The WB network account's avatar image URL.")]
    public string AvatarUrl { get; set; } = "https://prod-network-images.wbagora.com/network/account-wbgames-com/multiversus-arya.jpg";

    /// <summary>sdk.realtime as the response carries it.</summary>
    public JsonObject Realtime() => new()
    {
        ["enabled"] = true,
        ["default-cluster"] = RealtimeCluster,
        ["servers"] = new JsonObject { [RealtimeCluster] = JsonNode.Parse(RealtimeServers) },
    };

    // Refused when set: the value shown is always one the response can use.
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        bool isObject;
        try
        {
            isObject = JsonNode.Parse(RealtimeServers) is JsonObject;
        }
        catch (JsonException)
        {
            isObject = false;
        }

        if (!isObject)
        {
            yield return new ValidationResult("RealtimeServers must be a JSON object (name -> { ws, wss, udp })", [nameof(RealtimeServers)]);
        }
    }
}

/// <summary>
/// POST /sessions/auth/token (the TS server's handlers/sessions.ts): the WB network SDK exchanging the /access token
/// for its own. JSON in, JSON out, not Hydra. The answer is a fixed WB account (sessions-auth-token.json, generated
/// from the TS literal by tools/access/gen_templates.mjs) whose access_token is the request's code, echoed.
/// </summary>
public static class SessionTokenResponse
{
    // As Express writes it (JSON.stringify): compact, and nothing escaped that JSON does not require; the default
    // encoder would write the timestamps' + as +.
    private static readonly JsonSerializerOptions s_json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Lazy<JsonObject> s_template = new(() =>
    {
        using var stream = typeof(SessionTokenResponse).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Access.sessions-auth-token.json")
            ?? throw new InvalidOperationException("sessions-auth-token.json is not embedded");
        return JsonNode.Parse(stream)!.AsObject();
    });

    /// <summary>The response's JSON text for a request body (null when there was none).</summary>
    public static string Build(JsonNode? body, WbNetworkSettings network)
    {
        var response = s_template.Value.DeepClone().AsObject();
        response["sdk"]!["realtime"] = network.Realtime();
        response["account"]!["avatar"]!["image_url"] = network.AvatarUrl;
        if (body is JsonObject request && request.TryGetPropertyValue("code", out var code))
        {
            response["access_token"] = code?.DeepClone();
        }
        else
        {
            // req.body.code is undefined, and JSON.stringify leaves the key out.
            response.Remove("access_token");
        }

        return response.ToJsonString(s_json);
    }
}
