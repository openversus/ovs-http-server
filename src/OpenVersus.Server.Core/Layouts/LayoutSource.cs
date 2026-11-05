using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Static;

namespace OpenVersus.Server.Core.Layouts;

/// <summary>A store layout as sent: JSON text, or the Hydra encoding for a Hydra request (one of the two is set).</summary>
public sealed record LayoutAnswer(string? Json, byte[]? Hydra);

/// <summary>
/// Where the store layouts (GET /layout/dokken-layout-type/personalized/{variant}/{id}) come from. A layout mixes
/// catalog data with the player's own (what they own, what they bought, which products they are shown); which is which
/// is written down in docs/fields/store-layouts.json, and docs/FROZEN-ACCOUNT-DATA.md says what serves it today.
/// </summary>
public interface ILayoutSource
{
    /// <summary>The variants this source answers; any other is not ported (the TS server has no route for it).</summary>
    IReadOnlySet<string> Variants { get; }

    /// <summary>The layout <paramref name="variant"/> for the player <paramref name="accountId"/> (null: no session).</summary>
    ValueTask<LayoutAnswer> GetAsync(string variant, string? accountId, bool hydra, CancellationToken ct = default);
}

/// <summary>
/// The TS server's layouts as they are: one captured answer per variant, the same for every player, including the
/// captured account's ownership and purchases (Static/layout-{variant}.json, generated from handlers/layout.ts). Both
/// forms are made once per process.
/// </summary>
internal sealed class FrozenLayoutSource : ILayoutSource
{
    public IReadOnlySet<string> Variants { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "account-cosmetics-variant", "battlepass-variant", "currency-variant", "fighter-road-layout", "fighter-variant",
        "main-variant", "prestige-variant", "rift-variant", "skin-variant",
    };

    public ValueTask<LayoutAnswer> GetAsync(string variant, string? accountId, bool hydra, CancellationToken ct) =>
        !Variants.Contains(variant)
            ? throw new ArgumentOutOfRangeException(nameof(variant), variant, "not a layout this source has")
            : ValueTask.FromResult(hydra
                ? new LayoutAnswer(null, StaticResponses.Hydra($"layout-{variant}"))
                : new LayoutAnswer(StaticResponses.Json($"layout-{variant}"), null));
}

public static class LayoutHosting
{
    /// <summary>The store layouts: today the TS server's frozen answers (see <see cref="FrozenLayoutSource"/>).</summary>
    public static WebApplicationBuilder AddLayouts(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ILayoutSource, FrozenLayoutSource>();
        builder.Services.AddFrozenAccountData("GET /layout/dokken-layout-type/personalized/{variant}/{id}",
            "one captured account's store state (owned, bought, adjusted prices, the main store's picks)");
        return builder;
    }
}
