using System.ComponentModel;
using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Inventory;

// Who owns End Game's restricted items (inventory-restricted.json, written from the TS data by
// tools/inventory/gen_restricted.ts). Every other item is everyone's (the inventory and the hiss's
// OwnedByDefaultInventoryItems list every enabled asset). These are nobody's until they are paid:
//
//   battlePass   the battle pass's rewards: owned once their tier is claimed (claim_all / claim_milestone_reward_track_tiers)
//   fighterPass  the Chromium skins: owned once the character's level reaches its Fighter Pass's last tier (paid at once)
//
// Both come back through what rewards paid (playeritems, RewardGrants), which the inventory adds. The OVS Dev badge
// (devBadge) is owned by Ownership:OvsDevAccountIds alone, which also get its OVSDev stat at login.

/// <summary>Ownership settings.</summary>
public sealed class OwnershipSettings
{
    [Description("Accounts that own the OVS Dev profile badge and get its OVSDev stat at login, as comma-separated account ids (TS: OVS_DEV_ACCOUNT_IDS).")]
    public string OvsDevAccountIds { get; set; } = "";
}

public static class Ownership
{
    private static readonly Lazy<(HashSet<string> Paid, string DevBadge)> s_restricted = new(() =>
    {
        using var stream = typeof(Ownership).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Inventory.inventory-restricted.json")
            ?? throw new InvalidOperationException("Inventory/inventory-restricted.json is not embedded");
        var data = JsonNode.Parse(stream)!.AsObject();
        var paid = new[] { "battlePass", "fighterPass" }
            .SelectMany(k => data[k]?.AsArray().Select(s => s?.GetValue<string>()) ?? [])
            .OfType<string>()
            .ToHashSet();
        return (paid, data["devBadge"]?.GetValue<string>() ?? "");
    });

    /// <summary>Whether <paramref name="slug"/> is owned only once paid (or, the badge, by the dev accounts).</summary>
    public static bool IsRestricted(string? slug) =>
        slug is not null && (s_restricted.Value.Paid.Contains(slug) || slug == s_restricted.Value.DevBadge);

    /// <summary>Whether <paramref name="slug"/> is the OVS Dev badge.</summary>
    public static bool IsDevBadge(string? slug) => slug is not null && slug == s_restricted.Value.DevBadge;

    /// <summary>Whether <paramref name="accountId"/> is one of Ownership:OvsDevAccountIds.</summary>
    public static bool IsDevAccount(OwnershipSettings settings, string accountId) =>
        accountId.Length > 0 && settings.OvsDevAccountIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(accountId);
}
