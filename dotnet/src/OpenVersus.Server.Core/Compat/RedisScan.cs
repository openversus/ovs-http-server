using StackExchange.Redis;

namespace OpenVersus.Server.Core.Compat;

/// <summary>SCAN over every primary (KEYS would block Redis): the keys of the database that match a pattern.</summary>
public static class RedisScan
{
    public static async IAsyncEnumerable<RedisKey> KeysAsync(IConnectionMultiplexer multiplexer, int database, string pattern, int pageSize = 1000)
    {
        foreach (var server in multiplexer.GetServers().Where(s => s.IsConnected && !s.IsReplica))
        {
            await foreach (var key in server.KeysAsync(database, pattern, pageSize: pageSize))
            {
                yield return key;
            }
        }
    }
}
