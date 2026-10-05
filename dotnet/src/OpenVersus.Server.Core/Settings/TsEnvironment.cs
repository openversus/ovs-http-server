using Microsoft.Extensions.Configuration;

namespace OpenVersus.Server.Core.Settings;

/// <summary>
/// The TS server's environment variable names for settings that live in the catalog under other keys, so the
/// containers' .env files carry over unchanged. A TS name fills its key only when the key itself is not configured,
/// and below the cluster and instance layers, so a change through the control API still wins.
/// </summary>
public static class TsEnvironment
{
    /// <summary>TS name, catalog key.</summary>
    public static readonly IReadOnlyList<(string TsName, string Key)> Aliases =
    [
        ("JWT_SECRET", "Access:JwtSecret"),
        ("ACCESS_TOKEN_TTL", "Access:TokenTtl"),
        ("WB_DOMAIN", "Realtime:Domain"),
        ("GAME_DOMAIN", "Assets:Domain"),
        ("WEBSOCKET_PORT", "Realtime:Port"),
        ("USE_SECURE_WEBSOCKET", "Realtime:Secure"),
        ("SECURE_WEBSOCKET_PORT", "Realtime:SecurePort"),
        ("IP_BANS_FILE", "Bans:IpFile"),
        ("CIDR_BANS_FILE", "Bans:CidrFile"),
        ("HASHBANS_FILE", "Bans:IdFile"),
        ("MIN_CLIENT_VERSION", "Clients:MinimumVersion"),
        ("CLIENT_VERSION_CHECK", "Clients:VersionCheck"),
        ("GAME_VERSION", "Lobbies:GameVersion"),
        ("LOCAL_PUBLIC_IP", "Lobbies:LocalPublicIp"),
        ("DEFAULT_ELO", "Ranked:DefaultElo"),
        ("ROLLBACK_UDP_PORT_LOW", "Rollback:UdpPortLow"),
        ("ROLLBACK_UDP_PORT_HIGH", "Rollback:UdpPortHigh"),
        ("ON_DEMAND_ROLLBACK", "Rollback:OnDemand"),
        ("P2P_ROLLBACK", "Rollback:P2P"),
        ("ON_DEMAND_ROLLBACK_PORT_LOW", "Rollback:OnDemandPortLow"),
        ("ON_DEMAND_ROLLBACK_PORT_HIGH", "Rollback:OnDemandPortHigh"),
        ("WEBHOOK_HOST", "Rollback:WebhookHost"),
        ("WEBHOOK_PORT", "Rollback:WebhookPort"),
        ("WEBHOOK_DEPLOY_PATH", "Rollback:WebhookDeployPath"),
        ("WEBHOOK_HMAC_SECRET", "Rollback:WebhookHmacSecret"),
        ("OVS_SERVER", "Rollback:OvsServer"),
        ("UDP_SERVER_IP", "Rollback:UdpServerIp"),
        ("UDP_PORT", "Rollback:UdpPort"),
        ("MATCHUPDATEKEY", "Rollback:MatchUpdateKey"),
        ("P2P_NODE_SIGNING_KEY", "Rollback:NodeSigningKey"),
        ("P2P_NODE_SIGNING_KEY_FILE", "Rollback:NodeSigningKeyFile"),
        ("P2P_NODE_CONFIG_FILE", "Rollback:NodeConfigFile"),
        ("P2P_NODE_PUBLIC_KEY", "Rollback:NodePublicKey"),
        ("FFA_WEEKEND_ONLY", "Ffa:WeekendOnly"),
        ("OVS_DEV_ACCOUNT_IDS", "Ownership:OvsDevAccountIds"),
    ];

    // Values the TS server reads with envalid's bool (true/t/1, false/f/0), which .NET's binding does not; anything else
    // is passed on and refused at startup, as there.
    private static readonly HashSet<string> s_booleans = ["CLIENT_VERSION_CHECK", "FFA_WEEKEND_ONLY"];

    // Numbers the TS server compares with === 1 (envalid's num): 1 is on, any other number off; anything else is passed
    // on and refused at startup, as envalid refuses it.
    private static readonly HashSet<string> s_numericBooleans = ["ON_DEMAND_ROLLBACK", "P2P_ROLLBACK"];

    private static string NumericBool(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)
            ? (n == 1 ? "true" : "false")
            : value;

    private static string EnvalidBool(string value) => value switch
    {
        "true" or "t" or "1" => "true",
        "false" or "f" or "0" => "false",
        _ => value,
    };

    /// <summary>Adds the aliased values; call after the environment is in and before the override layers.</summary>
    public static void AddAliases(IConfigurationBuilder builder, IConfiguration current)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (tsName, key) in Aliases)
        {
            if (current[key] is null && current[tsName] is { } value)
            {
                values[key] = s_booleans.Contains(tsName) ? EnvalidBool(value) : s_numericBooleans.Contains(tsName) ? NumericBool(value) : value;
            }
        }

        builder.AddInMemoryCollection(values);
    }
}
