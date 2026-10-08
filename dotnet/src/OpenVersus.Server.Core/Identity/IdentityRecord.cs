namespace OpenVersus.Server.Core.Identity;

/// <summary>What the identity record (identity:{ip}) and the account share with the identify service (OpenVersus.Server.Identity).</summary>
public static class IdentityRecord
{
    /// <summary>The account field holding the verified Steam ticket's decoded fields and hash, and the identity:{ip} field carrying them to the login.</summary>
    public const string TicketField = "steamTicket";
}
