namespace OpenVersus.Server.Core.Matches;

/// <summary>
/// The codes players join a lobby by: five characters that cannot be misread for one another, under one key namespace
/// (lobby_code:{CODE}, the lobby's id) for custom and Arena lobbies alike, so a code never names two lobbies.
/// </summary>
public static class LobbyCodes
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Key(string code) => $"lobby_code:{code.ToUpperInvariant()}";

    public static string Draw() => string.Concat(Enumerable.Range(0, 5).Select(_ => Alphabet[Random.Shared.Next(Alphabet.Length)]));
}
