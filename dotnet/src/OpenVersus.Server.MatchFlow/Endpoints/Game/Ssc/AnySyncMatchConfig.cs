namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/sync_match_config: <see cref="NeverSeenSscEndpoint"/>.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnySyncMatchConfig : NeverSeenSscEndpoint
{
    protected override string Route => "sync_match_config";
}
