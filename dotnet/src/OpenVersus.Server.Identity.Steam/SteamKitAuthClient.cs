using System.IO.Hashing;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using SteamKit2;
using SteamKit2.Internal;

namespace OpenVersus.Server.Identity.Steam;

/// <summary>
/// <see cref="ISteamAuthClient"/> over SteamKit2: an anonymous game server logon for the app, and the game server side of
/// ISteamUser::BeginAuthSession done by hand (CMsgClientAuthList), as the experiment that proved it did: Steam gives a
/// verdict (ClientTicketAuthComplete) for an entry with estate 1 whose ticket is the session part of the client's ticket,
/// and the list sent is always every held ticket, as the Steam client sends it (an empty list ends every session). A
/// ticket binds to one auth session: the same ticket in a second session is AuthTicketInvalidAlreadyUsed.
/// </summary>
internal sealed class SteamKitAuthClient : ISteamAuthClient, IDisposable
{
    private static readonly TimeSpan s_logonTimeout = TimeSpan.FromSeconds(30);

    private readonly uint _appId;
    private readonly ILogger<SteamKitAuthClient> _log;
    private readonly SteamClient _client = new();
    private readonly CallbackManager _manager;
    private readonly SteamGameServer _gameServer;
    private readonly Dictionary<ulong, (byte[] Ticket, uint Crc)> _held = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _pumpStop = new();
    private Task? _pump;
    private uint _sequence;
    private TaskCompletionSource<bool>? _logon;
    private volatile bool _connected;
    private bool _disconnecting;

    public SteamKitAuthClient(IOptionsMonitor<AccessSettings> access, ILogger<SteamKitAuthClient> log)
    {
        _appId = access.CurrentValue.SteamAppId;
        _log = log;
        _manager = new CallbackManager(_client);
        _gameServer = _client.GetHandler<SteamGameServer>() ?? throw new InvalidOperationException("SteamKit2 has no game server handler");
        _client.AddHandler(new TicketAuthHandler());
        _manager.Subscribe<SteamClient.ConnectedCallback>(_ => _gameServer.LogOnAnonymous(_appId));
        _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _manager.Subscribe<TicketAuthComplete>(OnVerdict);
    }

    public bool IsConnected => _connected;

    public event Action<SteamVerdict>? Verdict;

    public event Action<string>? Disconnected;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_appId == 0)
        {
            throw new InvalidOperationException("Access:SteamAppId is 0: a game server logs on for one app");
        }

        _pump ??= Task.Factory.StartNew(Pump, _pumpStop.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var logon = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _logon = logon;
            _disconnecting = false;
            _held.Clear();
            _sequence = 0;
        }

        _client.Connect();
        using var timeout = new CancellationTokenSource(s_logonTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await logon.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                _disconnecting = true;
            }

            _client.Disconnect();
            throw new TimeoutException($"Steam did not answer the game server logon within {s_logonTimeout.TotalSeconds} s");
        }
    }

    public Task DisconnectAsync()
    {
        lock (_lock)
        {
            _disconnecting = true;
            _held.Clear();
        }

        _connected = false;
        _client.Disconnect();
        return Task.CompletedTask;
    }

    public uint Open(ulong steamId, ReadOnlyMemory<byte> authPart)
    {
        byte[] ticket = authPart.ToArray();
        uint crc = Crc32.HashToUInt32(ticket);
        lock (_lock)
        {
            _held[steamId] = (ticket, crc);
            SendList();
        }

        return crc;
    }

    public void End(ulong steamId)
    {
        lock (_lock)
        {
            if (_held.Remove(steamId))
            {
                SendList();
            }
        }
    }

    // Under the lock: the complete list of held tickets, as the Steam client sends it on every change. Steam answers every
    // entry again each time (OK again for a held one, its own refusal for a bad one, each by its crc): the state machine
    // reads a repeated OK as no change.
    private void SendList()
    {
        if (!_connected)
        {
            return;
        }

        var list = new ClientMsgProtobuf<CMsgClientAuthList>(EMsg.ClientAuthList);
        list.Body.tokens_left = 0;
        list.Body.message_sequence = ++_sequence;
        list.Body.app_ids.Add(_appId);
        foreach (var (steamId, (ticket, crc)) in _held)
        {
            list.Body.tickets.Add(new CMsgAuthTicket
            {
                estate = 1,
                eresult = (uint)EResult.OK,
                ticket_type = 1,
                steamid = steamId,
                gameid = _appId,
                h_steam_pipe = 1,
                ticket_crc = crc,
                ticket = ticket,
            });
        }

        _client.Send(list);
    }

    private void Pump()
    {
        while (!_pumpStop.IsCancellationRequested)
        {
            try
            {
                _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            }
            catch (Exception e)
            {
                _log.LogError(e, "A Steam callback failed");
            }
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback c)
    {
        if (c.Result == EResult.OK)
        {
            _connected = true;
            _log.LogInformation("Logged on to Steam as anonymous game server {SteamId} for app {App}; Steam sees us from {Ip}", c.ClientSteamID, _appId, c.PublicIP);
            _logon?.TrySetResult(true);
            return;
        }

        _logon?.TrySetException(new InvalidOperationException($"Steam refused the game server logon: {c.Result} / {c.ExtendedResult}"));
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback c)
    {
        bool was = _connected;
        _connected = false;
        if (was)
        {
            Disconnected?.Invoke($"logged off by Steam: {c.Result}");
        }
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback c)
    {
        bool was = _connected, wanted;
        _connected = false;
        lock (_lock)
        {
            wanted = _disconnecting;
            _held.Clear();
        }

        _logon?.TrySetException(new IOException("disconnected from Steam before the logon completed"));
        if (was && !wanted)
        {
            Disconnected?.Invoke(c.UserInitiated ? "disconnected" : "connection to Steam lost");
        }
    }

    private void OnVerdict(TicketAuthComplete c)
    {
        var body = c.Body;
        var response = (EAuthSessionResponse)body.eauth_session_response;
        Verdict?.Invoke(new SteamVerdict(body.steam_id, body.owner_steam_id, body.ticket_crc, response.ToString(), response == EAuthSessionResponse.OK));
    }

    public void Dispose()
    {
        _pumpStop.Cancel();
        lock (_lock)
        {
            _disconnecting = true;
        }

        _client.Disconnect();
    }

    /// <summary>The raw ClientTicketAuthComplete, for its owner_steam_id (SteamKit's TicketAuthCallback leaves it out).</summary>
    private sealed class TicketAuthComplete(CMsgClientTicketAuthComplete body) : CallbackMsg
    {
        public CMsgClientTicketAuthComplete Body { get; } = body;
    }

    private sealed class TicketAuthHandler : ClientMsgHandler
    {
        public override void HandleMsg(IPacketMsg packetMsg)
        {
            if (packetMsg.MsgType == EMsg.ClientTicketAuthComplete)
            {
                Client.PostCallback(new TicketAuthComplete(new ClientMsgProtobuf<CMsgClientTicketAuthComplete>(packetMsg).Body));
            }
        }
    }
}
