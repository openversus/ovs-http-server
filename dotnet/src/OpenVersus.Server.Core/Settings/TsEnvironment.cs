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
    ];

    // Values the TS server reads with envalid's bool (true/t/1, false/f/0), which .NET's binding does not; anything else
    // is passed on and refused at startup, as there.
    private static readonly HashSet<string> s_booleans = ["CLIENT_VERSION_CHECK"];

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
                values[key] = s_booleans.Contains(tsName) ? EnvalidBool(value) : value;
            }
        }

        builder.AddInMemoryCollection(values);
    }
}
