# syntax=docker/dockerfile:1
# The C# services' images: one image per service (http, access, social, lobbies, web, ws, edge, matchmaking, matchflow, steam), each with only its own executable and
# ovsctl, built from one shared build of the solution. From this directory:
#   docker build --target matchmaking --build-arg SOURCE_REVISION=$(git rev-parse HEAD) -t ovs-matchmaking .
# build-images.sh builds them all. The proxy has no image: it exists only for the migration bench.
ARG DOTNET=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET} AS build
# The commit, shown by ovsctl status as the version's suffix: the build context has no .git to read it from.
ARG SOURCE_REVISION=unknown
WORKDIR /src
COPY .editorconfig ./
COPY Directory.Build.props ./
COPY docs/routes.json docs/
# Restore from the project files alone first, so a code change does not download the packages again.
COPY src/OpenVersus.Server.Core/OpenVersus.Server.Core.csproj src/OpenVersus.Server.Core/
COPY src/OpenVersus.Server.Http/OpenVersus.Server.Http.csproj src/OpenVersus.Server.Http/
COPY src/OpenVersus.Server.Http.Shared/OpenVersus.Server.Http.Shared.csproj src/OpenVersus.Server.Http.Shared/
COPY src/OpenVersus.Server.Identity/OpenVersus.Server.Identity.csproj src/OpenVersus.Server.Identity/
COPY src/OpenVersus.Server.Identity.Steam/OpenVersus.Server.Identity.Steam.csproj src/OpenVersus.Server.Identity.Steam/
COPY src/OpenVersus.Server.Web/OpenVersus.Server.Web.csproj src/OpenVersus.Server.Web/
COPY src/OpenVersus.Server.Access/OpenVersus.Server.Access.csproj src/OpenVersus.Server.Access/
COPY src/OpenVersus.Server.Social/OpenVersus.Server.Social.csproj src/OpenVersus.Server.Social/
COPY src/OpenVersus.Server.Lobbies/OpenVersus.Server.Lobbies.csproj src/OpenVersus.Server.Lobbies/
COPY src/OpenVersus.Server.Realtime/OpenVersus.Server.Realtime.csproj src/OpenVersus.Server.Realtime/
COPY src/OpenVersus.Server.Edge/OpenVersus.Server.Edge.csproj src/OpenVersus.Server.Edge/
COPY src/OpenVersus.Server.Matchmaking/OpenVersus.Server.Matchmaking.csproj src/OpenVersus.Server.Matchmaking/
COPY src/OpenVersus.Server.MatchFlow/OpenVersus.Server.MatchFlow.csproj src/OpenVersus.Server.MatchFlow/
COPY src/OpenVersus.Server.Cli/OpenVersus.Server.Cli.csproj src/OpenVersus.Server.Cli/
RUN for project in Http Access Social Lobbies Web Realtime Edge Matchmaking MatchFlow Identity.Steam Cli; do \
      dotnet restore src/OpenVersus.Server.$project/OpenVersus.Server.$project.csproj || exit 1; \
    done
COPY src/ src/
RUN for project in Http Access Social Lobbies Web Realtime Edge Matchmaking MatchFlow Identity.Steam Cli; do \
      dotnet publish src/OpenVersus.Server.$project/OpenVersus.Server.$project.csproj -c Release --no-restore \
        -p:SourceRevisionId=$SOURCE_REVISION -o /out/$project || exit 1; \
    done

# What every service image shares: the ASP.NET runtime, ovsctl, and a health check through it. ovsctl reaches the
# service over its control socket (/tmp/openversus/<service>.sock), the only way in for a service with no public port;
# OVS_SERVICE (set by each image) tells it which service it is in. `ovsctl health --probe` is the service's readiness:
# exit 0 ready, 1 not ready (a store it needs is missing or down), 2 unreachable. Docker only marks a container
# unhealthy; an orchestrator that replaces unhealthy containers (ECS does) would restart every service that needs Redis
# while Redis is down, which fixes nothing. There, the container check should be liveness instead (open question).
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET} AS runtime
COPY --from=build /out/Cli /opt/ovsctl
RUN ln -s /opt/ovsctl/ovsctl /usr/local/bin/ovsctl
# The services choose their own listeners (HTTP_PORT and the like); the base image's default of 8080 would only mislead.
ENV ASPNETCORE_HTTP_PORTS=
WORKDIR /app
USER $APP_UID
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 CMD ["ovsctl", "health", "--probe"]

# The game's HTTP API (HTTP_PORT, 8000 by default).
FROM runtime AS http
ENV OVS_SERVICE=http
COPY --from=build /out/Http .
EXPOSE 8000
ENTRYPOINT ["dotnet", "OpenVersus.Server.Http.dll"]

# The game's login and sessions (ACCESS_PORT, 8001 by default).
FROM runtime AS access
ENV OVS_SERVICE=access
COPY --from=build /out/Access .
EXPOSE 8001
ENTRYPOINT ["dotnet", "OpenVersus.Server.Access.dll"]

# Friends, relationships and blocks (SOCIAL_PORT, 8002 by default).
FROM runtime AS social
ENV OVS_SERVICE=social
COPY --from=build /out/Social .
EXPOSE 8002
ENTRYPOINT ["dotnet", "OpenVersus.Server.Social.dll"]

# Parties, lobbies, queueing and the game's /matches routes (LOBBIES_PORT, 8003 by default).
FROM runtime AS lobbies
ENV OVS_SERVICE=lobbies
COPY --from=build /out/Lobbies .
EXPOSE 8003
ENTRYPOINT ["dotnet", "OpenVersus.Server.Lobbies.dll"]

# The website, the OpenVersus client's API and the admin pages (WEB_PORT, 8004 by default).
FROM runtime AS web
ENV OVS_SERVICE=web
COPY --from=build /out/Web .
EXPOSE 8004
ENTRYPOINT ["dotnet", "OpenVersus.Server.Web.dll"]

# The realtime gateway: the game's websocket (WEBSOCKET_PORT, 3000 by default).
FROM runtime AS ws
ENV OVS_SERVICE=ws
COPY --from=build /out/Realtime .
EXPOSE 3000
ENTRYPOINT ["dotnet", "OpenVersus.Server.Realtime.dll"]

# The edge in front of the realtime gateway: the game's websocket (EDGE_PORT, 3001 by default), each game linked to a
# gateway node found in the instance registry. A stopping edge keeps its games until they leave (Edge:DrainTimeoutMs):
# give its container a stop timeout to match.
FROM runtime AS edge
ENV OVS_SERVICE=edge
COPY --from=build /out/Edge .
EXPOSE 3001
ENTRYPOINT ["dotnet", "OpenVersus.Server.Edge.dll"]

# The matchmaker: no public port.
FROM runtime AS matchmaking
ENV OVS_SERVICE=matchmaking
COPY --from=build /out/Matchmaking .
ENTRYPOINT ["dotnet", "OpenVersus.Server.Matchmaking.dll"]

# The match flow: check-ins, concedes, match config, the rollback server's callbacks, and match results (MATCHFLOW_PORT,
# 8005 by default).
FROM runtime AS matchflow
ENV OVS_SERVICE=matchflow
COPY --from=build /out/MatchFlow .
EXPOSE 8005
ENTRYPOINT ["dotnet", "OpenVersus.Server.MatchFlow.dll"]

# The Steam identity service: asks Steam about each registered client's ticket and holds the auth session while the game
# runs (docs/IDENTIFY.md "Asking Steam"). No public port; outbound to Steam (TCP 27015-27050 and 443).
FROM runtime AS steam
ENV OVS_SERVICE=steam
COPY --from=build /out/Identity.Steam .
ENTRYPOINT ["dotnet", "OpenVersus.Server.Identity.Steam.dll"]
