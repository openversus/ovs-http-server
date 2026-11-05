using System.Runtime.CompilerServices;

namespace OpenVersus.Server.Realtime.Tests;

/// <summary>
/// A test gateway must never reach a real Redis: it would take players there offline. The environment's REDIS is cleared
/// before any host is built.
/// </summary>
internal static class NoAmbientRedis
{
    [ModuleInitializer]
    internal static void Clear() => Environment.SetEnvironmentVariable("REDIS", null);
}
