using System.Runtime.CompilerServices;

namespace OpenVersus.Server.Identity.Steam.Tests;

/// <summary>
/// A test Steam identity service must never reach a real Redis: it would take the live open requests there and ask
/// Steam about them. The environment's REDIS is cleared before any host is built.
/// </summary>
internal static class NoAmbientRedis
{
    [ModuleInitializer]
    internal static void Clear() => Environment.SetEnvironmentVariable("REDIS", null);
}
