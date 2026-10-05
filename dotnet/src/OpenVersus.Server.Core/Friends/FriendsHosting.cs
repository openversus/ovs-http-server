using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenVersus.Server.Core.Friends;

public static class FriendsHosting
{
    /// <summary>The friends reads the game makes at login.</summary>
    public static WebApplicationBuilder AddFriends(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IFriendsService, FriendsService>();
        return builder;
    }
}
