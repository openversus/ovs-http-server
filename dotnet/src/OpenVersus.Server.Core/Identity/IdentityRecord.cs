namespace OpenVersus.Server.Core.Identity;

/// <summary>What the identity record (identity:{ip}) and the account share with the identify service (OpenVersus.Server.Identity).</summary>
public static class IdentityRecord
{
    /// <summary>The account field holding the verified Steam ticket's decoded fields and hash, and the identity:{ip} field carrying them to the login.</summary>
    public const string TicketField = "steamTicket";

    /// <summary>The account field saying when its Epic id was last proved by the game's Epic ID token (the Steam counterpart is the ticket record itself).</summary>
    public const string EpicProvedField = "epicVerifiedAt";
}
