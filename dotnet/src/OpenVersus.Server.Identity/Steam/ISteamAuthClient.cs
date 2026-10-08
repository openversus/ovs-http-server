namespace OpenVersus.Server.Identity.Steam;

/// <summary>Steam's answer about one held ticket (ClientTicketAuthComplete): whose, who owns the license (Family Sharing: the lender), and the response by name.</summary>
public sealed record SteamVerdict(ulong SteamId, ulong OwnerSteamId, uint Crc, string Response, bool Ok)
{
    /// <summary>The ticket is bound to another auth session: a retry of a ticket this connection opened, or someone else's session.</summary>
    public const string AlreadyUsed = "AuthTicketInvalidAlreadyUsed";

    /// <summary>The game closed (the client canceled its ticket).</summary>
    public const string Canceled = "AuthTicketCanceled";
}

/// <summary>
/// The connection to Steam as the session state machine (<see cref="SteamAuthSessions"/>) sees it: an anonymous game
/// server that holds a list of clients' tickets and hears Steam's verdict on each. The list is the whole set of held
/// tickets (Steam's ClientAuthList is complete, never a delta): opening or ending one re-sends the list. Implemented
/// over SteamKit2 in the Steam identity service; the tests use a fake.
/// </summary>
public interface ISteamAuthClient
{
    bool IsConnected { get; }

    /// <summary>Connects and logs on as an anonymous game server; returns once logged on, throws when that failed.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Logs off and disconnects (every held session ends on Steam's side). No <see cref="Disconnected"/> is raised for it.</summary>
    Task DisconnectAsync();

    /// <summary>Holds <paramref name="authPart"/> (the ticket's first 52 bytes) for <paramref name="steamId"/> and asks Steam about it; the CRC Steam names the ticket by.</summary>
    uint Open(ulong steamId, ReadOnlyMemory<byte> authPart);

    /// <summary>Lets go of the ticket held for <paramref name="steamId"/>, if any.</summary>
    void End(ulong steamId);

    event Action<SteamVerdict>? Verdict;

    /// <summary>The connection dropped (with why): every held session is gone with it.</summary>
    event Action<string>? Disconnected;
}
