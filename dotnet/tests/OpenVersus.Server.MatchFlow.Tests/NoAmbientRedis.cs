using System.Runtime.CompilerServices;

namespace OpenVersus.Server.MatchFlow.Tests;

/// <summary>
/// A test match flow reaches a real Redis only when a test gives it one (OVS_TEST_REDIS): it would record the results
/// published there. The environment's REDIS is cleared before any host is built.
/// </summary>
internal static class NoAmbientRedis
{
    [ModuleInitializer]
    internal static void Clear() => Environment.SetEnvironmentVariable("REDIS", null);
}
