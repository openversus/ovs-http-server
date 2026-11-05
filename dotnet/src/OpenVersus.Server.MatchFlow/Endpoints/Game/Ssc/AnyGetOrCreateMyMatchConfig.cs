namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/get_or_create_my_match_config: <see cref="NeverSeenSscEndpoint"/>.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyGetOrCreateMyMatchConfig : NeverSeenSscEndpoint
{
    protected override string Route => "get_or_create_my_match_config";
}
