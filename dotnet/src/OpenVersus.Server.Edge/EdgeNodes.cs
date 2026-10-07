using OpenVersus.Server.Core.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Edge;

/// <summary>A gateway node a game can be linked to: its instance id and its websocket address.</summary>
internal sealed record EdgeNode(string Instance, string Address);

/// <summary>
/// The gateway nodes, as the instance registry knows them: ready ws instances that say where they are reached
/// (<see cref="ServiceInstance.Address"/>). A node that died stays listed as ready for up to the registry's TTL (20 s), so a
/// game being moved names the nodes that already failed it.
/// </summary>
internal sealed class EdgeNodes(IServiceProvider services, TimeProvider time)
{
    /// <summary>A node for a game, chosen at random among the ready ones not in <paramref name="excluded"/>; null: none.</summary>
    public async Task<EdgeNode?> PickAsync(IReadOnlySet<string> excluded)
    {
        if (services.GetService<IConnectionMultiplexer>()?.GetDatabase() is not { } redis)
        {
            return null;
        }

        var view = await InstanceRegistry.ReadAsync(redis, time.GetUtcNow());
        var ready = view.Instances
            .Where(i => i.Service == KnownServices.Realtime.Name && i.State == "Ready" && !string.IsNullOrEmpty(i.Address) && !excluded.Contains(i.Instance))
            .ToList();
        return ready.Count == 0 ? null : ready[Random.Shared.Next(ready.Count)] is var node ? new EdgeNode(node.Instance, node.Address!) : null;
    }
}
