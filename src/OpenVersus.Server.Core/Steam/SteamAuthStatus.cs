namespace OpenVersus.Server.Core.Steam;

/// <summary>The Steam identity service's state as its control API (/control/steam/status) and ovsctl show it: this replica's connection and the sessions it holds.</summary>
public sealed record SteamAuthStatus(bool Enabled, bool Connected, long? ConnectedSinceMs, string Instance, string? LastError, long Queued,
    IReadOnlyDictionary<string, int> Sessions, IReadOnlyDictionary<string, long> Verdicts, IReadOnlyList<SteamAuthSessionView> Held);

/// <summary>One held session: whose, its state and Steam's last word on it.</summary>
public sealed record SteamAuthSessionView(string SteamId, string PlayerId, string State, string Response, string OwnerSteamId, long OpenedAtMs, long? VerdictAtMs);
