using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace OpenVersus.Server.Core.Epic;

/// <summary>
/// The Epic account check (docs/IDENTIFY.md "An Epic id needs the game's ID token"): an Epic Games Store client proves
/// its Epic account with the ID token the game's EOS SDK holds, a JWT Epic signs for the game's client id. Read by the
/// web service (identify verifies the token) and the login (an unverified Epic id is a claim). The check is enforced
/// only with a client id; the game's own is the default (there is one for every build, in its packaged EOS settings),
/// and clearing it turns the check off: Epic ids are then taken as claimed, as before.
/// </summary>
public sealed class EpicSettings
{
    [Description("Verify Epic account ID tokens and take an Epic id only from a verified one. Off: Epic ids are taken as claimed, as before. Also off while ClientId is empty.")]
    public bool Enabled { get; set; } = true;

    [Description("The game's EOS client id, the audience an Epic ID token names (the packaged DefaultEngine.ini, EOSSettings ClientId); the game's own by default. Empty: no verification, Epic ids are taken as claimed.")]
    public string ClientId { get; set; } = GameClientId;

    /// <summary>The one EOS client id every build of the game carries; the audience of its Epic ID tokens.</summary>
    public const string GameClientId = "xyza78914ucg8qGrE2tBEtjF6iWWc9OQ";

    [Description("The issuers an Epic account ID token may name, separated by spaces: the game's SDK (1.15) issues v1 tokens, Epic's current discovery document says v2; both sign with the same keys.")]
    public string Issuer { get; set; } = "https://api.epicgames.dev/epic/oauth/v1 https://api.epicgames.dev/epic/oauth/v2";

    [Description("Where Epic publishes the keys that sign ID tokens (JWKS; the discovery document's jwks_uri).")]
    public string JwksUrl { get; set; } = "https://api.epicgames.dev/epic/oauth/v2/.well-known/jwks.json";

    [Description("How long the fetched keys serve before they are fetched again. A token naming a key not held triggers a fetch at once (at most once a minute).")]
    [Range(5, 10080)]
    public int JwksRefreshMinutes { get; set; } = 360;

    [Description("How far a token's expiry or not-before may be past, to absorb clock differences with Epic.")]
    [Range(0, 600)]
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>The accepted issuers, one per entry.</summary>
    public IReadOnlyList<string> Issuers => Issuer.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether Epic ids are checked at all: on, and a client id to check the audience against.</summary>
    public bool Enforced => Enabled && ClientId.Length > 0;
}
