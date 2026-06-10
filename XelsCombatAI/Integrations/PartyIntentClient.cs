using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XelsCombatAI.Integrations;

internal sealed record PartyIntentRescueAdvisory(
    bool Active,
    bool ClaimedByLocal,
    bool NetworkTest,
    string SosId,
    string TargetName,
    string Reason,
    DateTime ExpiresUtc,
    Vector3? TargetPosition)
{
    public static PartyIntentRescueAdvisory None { get; } = new(false, false, false, string.Empty, string.Empty, "none", DateTime.MinValue, null);
}

internal sealed record PartyIntentStatus(
    bool Enabled,
    string State,
    string ServerUrl,
    int PeerCount,
    string LastError,
    DateTime LastAttemptUtc,
    DateTime LastSuccessUtc,
    string Context,
    string RoomMode,
    bool AutoRescueEnabled,
    string AutoRescueStatus,
    int DirectPeerCount,
    string DirectPeerStatus,
    PartyIntentNetworkTestResult LastNetworkTestTrigger,
    PartyIntentNetworkTestResult LastNetworkTestReceived,
    PartyIntentRescueAdvisory Rescue);

internal sealed record PartyIntentNetworkTestResult(
    bool Active,
    bool Success,
    string Message,
    DateTime TimestampUtc)
{
    public static PartyIntentNetworkTestResult None { get; } = new(false, false, "none", DateTime.MinValue);
}

internal sealed record PartyIntentSocialDestackIntent(
    Vector2 ActorPosition,
    Vector2 Bias,
    float Strength,
    DateTime ExpiresUtc);

internal sealed class PartyIntentClient : IDisposable
{
    private const string ProtocolId = "xcai-party-intent";
    private const int ProtocolVersion = 1;
    private const string ServerUrl = "https://xcai.xel-serv.com";
    private const string AvailabilityPath = "/v1/availability";
    private const string WebSocketPath = "/v1/ws";
    private const string RoomSecret = "xcai-party-intent-v1";
    private const string FeatureSocialDestack = "social-destack";
    private const string FeatureRescueSos = "rescue-sos";
    private const string NetworkTestSosPrefix = "test-";
    private const string NetworkTestDestackSource = "network-test";
    private const uint RescueRange = 30;
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WebSocketReconnectDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RescueClaimTtl = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan RescueSosCooldown = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RescueSosTtl = TimeSpan.FromMilliseconds(2500);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Configuration config;
    private readonly DalamudServices services;
    private readonly HttpClient httpClient = new() { Timeout = HttpTimeout };
    private readonly string session = NewToken();
    private readonly string sender = NewToken();
    private readonly ConcurrentQueue<object> outgoingMessages = new();
    private readonly ConcurrentQueue<RescueSosWire> pendingRescueSos = new();
    private readonly PartyIntentDirectPeerTransport directTransport;
    private readonly object clientAvailableGate = new();
    private readonly object rescueGate = new();
    private DateTime nextAnnounceUtc = DateTime.MinValue;
    private DateTime lastAttemptUtc = DateTime.MinValue;
    private DateTime lastSuccessUtc = DateTime.MinValue;
    private DateTime nextWebSocketConnectUtc = DateTime.MinValue;
    private DateTime lastLocalRescueSosUtc = DateTime.MinValue;
    private DateTime activeLocalRescueSosExpiresUtc = DateTime.MinValue;
    private CancellationTokenSource? webSocketCts;
    private Task? webSocketTask;
    private Task? announceTask;
    private ClientAvailableWire? pendingClientAvailable;
    private long sequence;
    private int peerCount;
    private string state = "idle";
    private string lastError = "none";
    private string context = "not built";
    private string roomMode = "static";
    private string autoRescueStatus = "disabled";
    private IReadOnlyList<PartyIntentPeer> directTransportKnownPeers = [];
    private string? activeLocalRescueSosId;
    private string? lastAutoRescueAttemptSosId;
    private IncomingRescueSos? incomingRescue;
    private bool incomingRescueClaimedByLocal;
    private PartyIntentNetworkTestResult lastNetworkTestTrigger = PartyIntentNetworkTestResult.None;
    private PartyIntentNetworkTestResult lastNetworkTestReceived = PartyIntentNetworkTestResult.None;
    private bool disposed;

    public PartyIntentClient(Configuration config, DalamudServices services)
    {
        this.config = config;
        this.services = services;
        this.directTransport = new PartyIntentDirectPeerTransport(this.sender, this.EnqueueOutgoing);
    }

    public PartyIntentStatus Status => new(
        config.PartyIntentEnabled,
        this.state,
        ServerUrl,
        this.peerCount,
        this.lastError,
        this.lastAttemptUtc,
        this.lastSuccessUtc,
        this.context,
        this.roomMode,
        config.PartyIntentAutoRescueEnabled,
        this.autoRescueStatus,
        this.directTransport.ConnectedPeerCount,
        this.directTransport.Status,
        this.lastNetworkTestTrigger,
        MostRecent(this.lastNetworkTestReceived, this.directTransport.LastNetworkTestReceived),
        this.BuildRescueAdvisory(DateTime.UtcNow));

    public void Tick(DateTime nowUtc)
    {
        if (!config.PartyIntentEnabled)
        {
            this.StopWebSocket();
            this.state = "disabled";
            this.peerCount = 0;
            this.nextAnnounceUtc = DateTime.MinValue;
            this.directTransport.UpdatePeers([]);
            this.ClearPendingClientAvailable();
            return;
        }

        if (!this.TickClientAvailable(nowUtc))
        {
            this.StopWebSocket();
            this.peerCount = 0;
            this.directTransport.UpdatePeers([]);
            return;
        }

        this.ObserveWebSocket(nowUtc);
        if (this.webSocketTask is { IsCompleted: false })
        {
            return;
        }

        if (nowUtc < this.nextWebSocketConnectUtc)
        {
            this.state = "reconnecting";
            return;
        }

        this.StartWebSocketIfNeeded(nowUtc);
    }

    public void EvaluateRescueAssists(DateTime nowUtc, BossModReflectionSafety bossModSafety)
        => this.EvaluateRescueAssistsCore(nowUtc, bossModSafety, networkTestsOnly: false);

#if XCAI_NETWORK_TEST_CONTROLS
    public void EvaluateNetworkTestRescueAssists(DateTime nowUtc, BossModReflectionSafety bossModSafety)
        => this.EvaluateRescueAssistsCore(nowUtc, bossModSafety, networkTestsOnly: true);
#endif

    private void EvaluateRescueAssistsCore(DateTime nowUtc, BossModReflectionSafety bossModSafety, bool networkTestsOnly)
    {
        if (!config.PartyIntentEnabled)
        {
            this.autoRescueStatus = "disabled";
            return;
        }

        var pendingCount = this.pendingRescueSos.Count;
        for (var i = 0; i < pendingCount && this.pendingRescueSos.TryDequeue(out var sos); i++)
        {
            if (networkTestsOnly && !IsNetworkTestSos(sos.SosId))
            {
                this.pendingRescueSos.Enqueue(sos);
                continue;
            }

            this.TryClaimIncomingRescue(sos, nowUtc, bossModSafety);
        }

        lock (this.rescueGate)
        {
            if (this.incomingRescue != null && this.incomingRescue.ExpiresUtc <= nowUtc)
            {
                this.incomingRescue = null;
                this.incomingRescueClaimedByLocal = false;
            }
        }

        this.TryAutoRescueClaim(nowUtc, bossModSafety);
    }

    public void EvaluateLocalRescueSos(
        DateTime nowUtc,
        BossModMechanicPressure pressure,
        BossModReflectionSafety bossModSafety,
        EscapeGapCloserController escapeGapCloserController,
        bool suppressAutomatedMovement)
    {
        if (!config.PartyIntentEnabled)
        {
            return;
        }

        if (this.activeLocalRescueSosId != null && this.activeLocalRescueSosExpiresUtc <= nowUtc)
        {
            this.activeLocalRescueSosId = null;
        }

        if (this.activeLocalRescueSosId != null &&
            bossModSafety.TryIsPositionSafe(services.ObjectTable.LocalPlayer?.Position ?? default, out var nowSafe, out _) &&
            nowSafe)
        {
            this.EnqueueOutgoing(new RescueReleaseWire(
                "rescue.resolved",
                ProtocolVersion,
                this.sender,
                this.activeLocalRescueSosId));
            this.activeLocalRescueSosId = null;
            return;
        }

        if (suppressAutomatedMovement ||
            nowUtc - this.lastLocalRescueSosUtc < RescueSosCooldown ||
            pressure.PrimaryPressure is BossModMechanicPressureKind.None or BossModMechanicPressureKind.Raidwide or BossModMechanicPressureKind.Tankbuster)
        {
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null ||
            player.IsDead ||
            player.CurrentHp == 0 ||
            !services.ClientState.IsLoggedIn)
        {
            return;
        }

        if (!bossModSafety.TryIsPositionSafe(player.Position, out var currentSafe, out _) ||
            currentSafe)
        {
            return;
        }

        if (!bossModSafety.TryGetSafeMovementIntent(player.Position, out var safeDestination, out _))
        {
            return;
        }

        var dangerInMs = EstimateDangerInMs(pressure);
        if (dangerInMs <= 0)
        {
            return;
        }

        var walkingTimeMs = EstimateWalkingTimeMs(Geometry.Distance2D(player.Position, safeDestination));
        if (walkingTimeMs + 350 <= dangerInMs)
        {
            return;
        }

        if (escapeGapCloserController.LastSafeEscapeDestination.HasValue)
        {
            return;
        }

        if (!this.TryBuildLocalActorKey(player, out var actorKey))
        {
            return;
        }

        var sosId = NewToken();
        this.activeLocalRescueSosId = sosId;
        this.activeLocalRescueSosExpiresUtc = nowUtc.Add(RescueSosTtl);
        this.lastLocalRescueSosUtc = nowUtc;
        this.EnqueueOutgoing(new RescueSosWire(
            "rescue.sos",
            ProtocolVersion,
            this.sender,
            sosId,
            new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
            (int)RescueSosTtl.TotalMilliseconds,
            actorKey,
            dangerInMs,
            Confidence: 0.75d,
            $"unsafe; walking {walkingTimeMs}ms; {escapeGapCloserController.LastEscapeGapCloserSafety}"));
    }

    public void Reset()
    {
        this.nextAnnounceUtc = DateTime.MinValue;
        this.peerCount = 0;
        this.state = config.PartyIntentEnabled ? "idle" : "disabled";
        this.lastError = "none";
        this.context = "not built";
        this.roomMode = "static";
        this.autoRescueStatus = config.PartyIntentAutoRescueEnabled ? "idle" : "disabled";
        this.activeLocalRescueSosId = null;
        this.lastAutoRescueAttemptSosId = null;
        this.directTransport.UpdatePeers([]);
        lock (this.rescueGate)
        {
            this.incomingRescue = null;
            this.incomingRescueClaimedByLocal = false;
        }
    }

    public void Dispose()
    {
        this.disposed = true;
        this.directTransport.Dispose();
        this.StopWebSocket();
        this.httpClient.Dispose();
    }

    public void PublishSocialDestackIntent(Vector2 bias, float strength, DateTime nowUtc)
    {
        if (!config.PartyIntentEnabled ||
            services.ObjectTable.LocalPlayer is not { } player ||
            !this.TryBuildLocalActorKey(player, out var actorKey))
        {
            return;
        }

        this.directTransport.PublishSocialDestack(actorKey, bias, strength, nowUtc);
    }

    public IReadOnlyList<PartyIntentSocialDestackIntent> GetSocialDestackIntents(DateTime nowUtc)
    {
        if (!config.PartyIntentEnabled)
        {
            return [];
        }

        var intents = new List<PartyIntentSocialDestackIntent>();
        foreach (var intent in this.directTransport.GetSocialDestackIntents(nowUtc))
        {
            if (this.TryFindPartyActorByActorKey(intent.ActorKey, out var actor))
            {
                intents.Add(new(
                    new Vector2(actor.Position.X, actor.Position.Z),
                    intent.Bias,
                    intent.Strength,
                    intent.ExpiresUtc));
            }
        }

        return intents;
    }

#if XCAI_NETWORK_TEST_CONTROLS
    public PartyIntentNetworkTestResult TriggerNetworkTestSos(DateTime nowUtc)
    {
        if (!this.TryValidateNetworkTestTrigger(requireDirectPeer: false, out var player, out var reason))
        {
            return this.RecordNetworkTestTrigger(false, reason, nowUtc);
        }

        if (!this.TryBuildLocalActorKey(player, out var actorKey))
        {
            return this.RecordNetworkTestTrigger(false, "local actor key unavailable", nowUtc);
        }

        var sosId = NetworkTestSosPrefix + NewToken();
        if (!this.EnqueueOutgoing(new RescueSosWire(
                "rescue.sos",
                ProtocolVersion,
                this.sender,
                sosId,
                new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
                (int)RescueSosTtl.TotalMilliseconds,
                actorKey,
                (int)RescueSosTtl.TotalMilliseconds,
                Confidence: 1d,
                "network test SOS")))
        {
            return this.RecordNetworkTestTrigger(false, "SOS queue full", nowUtc);
        }

        this.activeLocalRescueSosId = sosId;
        this.activeLocalRescueSosExpiresUtc = nowUtc.Add(RescueSosTtl);
        return this.RecordNetworkTestTrigger(true, $"sent test SOS to {this.peerCount} peers", nowUtc);
    }

    public PartyIntentNetworkTestResult TriggerNetworkTestDestack(DateTime nowUtc)
    {
        if (!this.TryValidateNetworkTestTrigger(requireDirectPeer: true, out var player, out var reason))
        {
            return this.RecordNetworkTestTrigger(false, reason, nowUtc);
        }

        if (!this.TryBuildLocalActorKey(player, out var actorKey))
        {
            return this.RecordNetworkTestTrigger(false, "local actor key unavailable", nowUtc);
        }

        var bias = ResolveNetworkTestDestackBias(player.Rotation);
        this.directTransport.PublishSocialDestack(actorKey, bias, 1f, nowUtc, NetworkTestDestackSource);
        return this.RecordNetworkTestTrigger(true, $"sent test destack to {this.directTransport.ConnectedPeerCount} direct peers", nowUtc);
    }
#endif

    private void TickHttpAvailability(DateTime nowUtc)
    {
        if (this.announceTask is { IsCompleted: false })
        {
            return;
        }

        if (this.announceTask is { IsFaulted: true } faulted)
        {
            this.lastError = faulted.Exception?.GetBaseException().Message ?? "availability failed";
            this.state = "unavailable";
            this.peerCount = 0;
            this.announceTask = null;
        }
        else if (this.announceTask is { IsCompleted: true })
        {
            this.announceTask = null;
        }

        if (nowUtc < this.nextAnnounceUtc)
        {
            return;
        }

        this.nextAnnounceUtc = nowUtc.Add(AnnounceInterval);
        this.lastAttemptUtc = nowUtc;

        if (!this.TryBuildAvailability(nowUtc, out var uri, out var envelope, out var reason))
        {
            this.state = "not ready";
            this.lastError = reason;
            this.peerCount = 0;
            return;
        }

        this.state = "announcing";
        this.lastError = "none";
        this.announceTask = this.AnnounceAsync(uri, envelope);
    }

    private bool TickClientAvailable(DateTime nowUtc)
    {
        if (nowUtc < this.nextAnnounceUtc)
        {
            return true;
        }

        this.nextAnnounceUtc = nowUtc.Add(AnnounceInterval);
        this.lastAttemptUtc = nowUtc;
        if (!this.TryBuildClientAvailable(nowUtc, out var message, out var reason))
        {
            this.state = "not ready";
            this.lastError = reason;
            this.ClearPendingClientAvailable();
            return false;
        }

        lock (this.clientAvailableGate)
        {
            this.pendingClientAvailable = message;
        }

        return true;
    }

    private void ObserveWebSocket(DateTime nowUtc)
    {
        if (this.webSocketTask is not { IsCompleted: true } completed)
        {
            return;
        }

        if (completed.IsFaulted)
        {
            this.lastError = completed.Exception?.GetBaseException().Message ?? "websocket failed";
            this.state = "unavailable";
            this.nextWebSocketConnectUtc = nowUtc.Add(WebSocketReconnectDelay);
        }
        else
        {
            this.state = "unavailable";
            this.lastError = "websocket closed";
            this.nextWebSocketConnectUtc = nowUtc.Add(WebSocketReconnectDelay);
        }

        this.webSocketTask = null;
        this.webSocketCts?.Dispose();
        this.webSocketCts = null;
    }

    private void StartWebSocketIfNeeded(DateTime nowUtc)
    {
        if (this.webSocketTask is { IsCompleted: false } ||
            nowUtc < this.nextWebSocketConnectUtc ||
            this.disposed)
        {
            return;
        }

        if (!TryBuildWebSocketUri(out var uri))
        {
            this.lastError = "invalid websocket URL";
            this.state = "unavailable";
            this.nextWebSocketConnectUtc = nowUtc.Add(WebSocketReconnectDelay);
            return;
        }

        this.webSocketCts = new CancellationTokenSource();
        var token = this.webSocketCts.Token;
        this.state = "connecting";
        this.webSocketTask = Task.Run(() => this.RunWebSocketAsync(uri, token), token);
    }

    private void StopWebSocket()
    {
        this.webSocketCts?.Cancel();
        this.webSocketCts?.Dispose();
        this.webSocketCts = null;
        this.webSocketTask = null;
    }

    private async Task RunWebSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            this.state = "connecting";
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            this.state = "connected";
            this.lastError = "none";

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sendTask = this.SendLoopAsync(socket, linkedCts.Token);
            var receiveTask = this.ReceiveLoopAsync(socket, linkedCts.Token);
            var completedTask = await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
            linkedCts.Cancel();
            var siblingTask = ReferenceEquals(completedTask, sendTask) ? receiveTask : sendTask;
            await SwallowAsync(siblingTask).ConfigureAwait(false);
            await completedTask.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("websocket closed");
            }
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                this.lastError = ex.Message;
                this.state = "unavailable";
                this.nextWebSocketConnectUtc = DateTime.UtcNow.Add(WebSocketReconnectDelay);
                throw;
            }
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var nextPingUtc = DateTime.UtcNow.Add(PingInterval);

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (now >= nextPingUtc)
            {
                await SendJsonAsync(socket, new PingWire("ping", ProtocolVersion, this.sender, new DateTimeOffset(now).ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
                nextPingUtc = now.Add(PingInterval);
            }

            if (this.TakePendingClientAvailable() is { } available)
            {
                await SendJsonAsync(socket, available, cancellationToken).ConfigureAwait(false);
            }

            while (this.outgoingMessages.TryDequeue(out var message))
            {
                await SendJsonAsync(socket, message, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException(FormatWebSocketClose(result));
                }

                stream.Write(buffer, 0, result.Count);
                if (stream.Length > buffer.Length)
                {
                    throw new InvalidOperationException("websocket message too large");
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(stream.ToArray());
            this.HandleWebSocketMessage(json);
        }
    }

    private ClientAvailableWire? TakePendingClientAvailable()
    {
        lock (this.clientAvailableGate)
        {
            var message = this.pendingClientAvailable;
            this.pendingClientAvailable = null;
            return message;
        }
    }

    private void ClearPendingClientAvailable()
    {
        lock (this.clientAvailableGate)
        {
            this.pendingClientAvailable = null;
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string FormatWebSocketClose(WebSocketReceiveResult result)
    {
        if (result.CloseStatus == null)
        {
            return "websocket closed";
        }

        return string.IsNullOrWhiteSpace(result.CloseStatusDescription)
            ? $"websocket closed: {result.CloseStatus}"
            : $"websocket closed: {result.CloseStatus}: {result.CloseStatusDescription}";
    }

    private void HandleWebSocketMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "server.available":
                    var peers = ParsePeers(root);
                    this.directTransportKnownPeers = peers;
                    this.peerCount = peers.Count;
                    this.directTransport.UpdatePeers(peers);
                    this.lastSuccessUtc = DateTime.UtcNow;
                    this.state = "connected";
                    this.lastError = "none";
                    break;
                case "peer.available":
                    this.peerCount = Math.Max(this.peerCount, 1);
                    if (TryParsePeer(root, out var peer))
                    {
                        var knownPeers = new List<PartyIntentPeer>(this.directTransportKnownPeers)
                        {
                            peer
                        };
                        this.directTransportKnownPeers = knownPeers
                            .GroupBy(item => item.Sender, StringComparer.Ordinal)
                            .Select(group => group.Last())
                            .ToArray();
                        this.directTransport.UpdatePeers(this.directTransportKnownPeers);
                    }

                    break;
                case "peer.offer":
                case "peer.answer":
                case "peer.ice":
                    if (TryParsePeerSignal(root, out var signal))
                    {
                        this.directTransport.HandleSignal(signal);
                    }

                    break;
                case "rescue.sos":
                    if (root.Deserialize<RescueSosWire>(JsonOptions) is { } sos &&
                        !string.Equals(sos.Sender, this.sender, StringComparison.Ordinal))
                    {
#if !XCAI_NETWORK_TEST_CONTROLS
                        if (IsNetworkTestSos(sos.SosId))
                        {
                            return;
                        }
#else
                        if (IsNetworkTestSos(sos.SosId))
                        {
                            this.lastNetworkTestReceived = new(
                                true,
                                true,
                                "received test SOS",
                                DateTime.UtcNow);
                        }
#endif
                        this.pendingRescueSos.Enqueue(sos);
                    }

                    break;
                case "rescue.claimed":
                    this.HandleRescueClaimed(root);
                    break;
                case "rescue.release":
                case "rescue.resolved":
                    this.HandleRescueCleared(root);
                    break;
                case "server.error":
                    this.lastError = TryGetString(root, "reason") ?? TryGetString(root, "code") ?? "server error";
                    break;
            }
        }
        catch (JsonException ex)
        {
            this.lastError = ex.Message;
        }
    }

    private void HandleRescueClaimed(JsonElement root)
    {
        var sosId = TryGetString(root, "sosId");
        var claimant = TryGetString(root, "claimant");
        if (string.IsNullOrWhiteSpace(sosId))
        {
            return;
        }

        lock (this.rescueGate)
        {
            if (this.incomingRescue == null ||
                !string.Equals(this.incomingRescue.SosId, sosId, StringComparison.Ordinal))
            {
                return;
            }

            this.incomingRescueClaimedByLocal = string.Equals(claimant, this.sender, StringComparison.Ordinal);
            if (!this.incomingRescueClaimedByLocal)
            {
                this.incomingRescue = null;
                this.autoRescueStatus = "claimed by another healer";
            }
            else
            {
                this.autoRescueStatus = config.PartyIntentAutoRescueEnabled ? "local Rescue claim won" : "advisory ready";
            }
        }
    }

    private void HandleRescueCleared(JsonElement root)
    {
        var sosId = TryGetString(root, "sosId");
        if (string.IsNullOrWhiteSpace(sosId))
        {
            return;
        }

        lock (this.rescueGate)
        {
            if (this.incomingRescue != null &&
                string.Equals(this.incomingRescue.SosId, sosId, StringComparison.Ordinal))
            {
                this.incomingRescue = null;
                this.incomingRescueClaimedByLocal = false;
                this.autoRescueStatus = "cleared";
            }
        }
    }

    private async Task AnnounceAsync(Uri uri, AvailabilityEnvelope envelope)
    {
        if (this.disposed)
        {
            return;
        }

        try
        {
            using var response = await this.httpClient.PostAsJsonAsync(uri, envelope, JsonOptions).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                this.peerCount = 0;
                this.lastError = $"HTTP {(int)response.StatusCode}";
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOptions).ConfigureAwait(false);
            if (result?.Ok != true)
            {
                this.peerCount = 0;
                this.lastError = result?.Reason ?? "server rejected availability";
                return;
            }

            this.peerCount = Math.Max(this.peerCount, result.PeerCount);
            this.lastSuccessUtc = DateTime.UtcNow;
            this.lastError = "none";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            this.peerCount = 0;
            this.lastError = ex.Message;
        }
    }

    private bool TryBuildAvailability(DateTime nowUtc, out Uri uri, out AvailabilityEnvelope envelope, out string reason)
    {
        uri = null!;
        envelope = null!;
        reason = string.Empty;

        if (!TryBuildAvailabilityUri(out uri))
        {
            reason = "invalid server URL";
            return false;
        }

        if (!this.TryBuildAvailabilityBody(out var body, out reason))
        {
            return false;
        }

        envelope = new AvailabilityEnvelope(
            ProtocolId,
            ProtocolVersion,
            this.session,
            this.sender,
            ++this.sequence,
            new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
            5000,
            "availability",
            body);
        return true;
    }

    private bool TryBuildClientAvailable(DateTime nowUtc, out ClientAvailableWire message, out string reason)
    {
        message = null!;
        if (!this.TryBuildAvailabilityBody(out var body, out reason))
        {
            return false;
        }

        message = new ClientAvailableWire(
            "client.available",
            ProtocolVersion,
            this.session,
            this.sender,
            ++this.sequence,
            new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds(),
            5000,
            body.RoomKey,
            body.ContextHash,
            body.PartyToken,
            body.RosterHash,
            body.ActorKey,
            body.Features,
            body.SupportsDirect,
            body.SupportsRelay);
        return true;
    }

    private bool TryBuildAvailabilityBody(out AvailabilityBody body, out string reason)
    {
        body = null!;
        reason = string.Empty;

        var player = services.ObjectTable.LocalPlayer;
        if (player == null || !services.ClientState.IsLoggedIn)
        {
            reason = "player unavailable";
            return false;
        }

        var territory = services.ClientState.TerritoryType;
        var map = services.ClientState.MapId;
        var instance = services.ClientState.Instance;
        var contentFinder = services.DutyState.ContentFinderCondition.RowId;
        var partySize = services.PartyList.Length;
        var boundByDuty = services.Condition[ConditionFlag.BoundByDuty];
        var dutyStarted = services.DutyState.IsDutyStarted;

        var rosterTokens = BuildRosterTokens(RoomSecret, territory, contentFinder, player.EntityId);
        var rosterHash = Hmac(RoomSecret, "roster|" + string.Join("|", rosterTokens.Order(StringComparer.Ordinal)));
        var contextInput = string.Create(
            CultureInfo.InvariantCulture,
            $"context|t={territory}|m={map}|i={instance}|cfc={contentFinder}|ps={partySize}|d={dutyStarted}|b={boundByDuty}");
        if (!this.TryBuildLocalActorKey(player, out var actorKey))
        {
            reason = "local actor key unavailable";
            return false;
        }

        body = new AvailabilityBody(
            Hmac(RoomSecret, "room|" + contextInput),
            Hmac(RoomSecret, contextInput),
            null,
            rosterHash,
            actorKey,
            [FeatureSocialDestack, FeatureRescueSos],
            SupportsDirect: true,
            SupportsRelay: true);

        this.context = string.Create(
            CultureInfo.InvariantCulture,
            $"territory={territory}; map={map}; instance={instance}; cfc={contentFinder}; partySize={partySize}");
        this.roomMode = "static context";
        return true;
    }

    private void TryClaimIncomingRescue(RescueSosWire sos, DateTime nowUtc, BossModReflectionSafety bossModSafety)
    {
        if (checked(sos.SentMs + sos.TtlMs) < new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds())
        {
            return;
        }

        var networkTest = IsNetworkTestSos(sos.SosId);
        if (!this.CanOfferRescue(sos.ActorKey, bossModSafety, config.PartyIntentAutoRescueEnabled, networkTest, out var target, out var reason))
        {
            this.lastError = reason;
            this.autoRescueStatus = $"blocked: {reason}";
            return;
        }

        var incoming = new IncomingRescueSos(
            sos.SosId,
            sos.ActorKey,
            FormatActorName(target),
            sos.Reason,
            nowUtc.AddMilliseconds(Math.Min(sos.TtlMs, (int)RescueSosTtl.TotalMilliseconds)));

        lock (this.rescueGate)
        {
            this.incomingRescue = incoming;
            this.incomingRescueClaimedByLocal = false;
        }

        this.autoRescueStatus = config.PartyIntentAutoRescueEnabled ? "claiming SOS" : "advisory ready";

        this.EnqueueOutgoing(new RescueClaimWire(
            "rescue.claim",
            ProtocolVersion,
            this.sender,
            sos.SosId,
            (int)RescueClaimTtl.TotalMilliseconds));
    }

    private bool CanOfferRescue(
        string actorKey,
        BossModReflectionSafety bossModSafety,
        bool allowCastInterrupt,
        bool networkTest,
        out IBattleChara target,
        out string reason)
        => this.CanOfferRescueCore(actorKey, bossModSafety, allowCastInterrupt, networkTest, out target, out reason);

    private bool CanOfferRescueCore(
        string actorKey,
        BossModReflectionSafety bossModSafety,
        bool allowCastInterrupt,
        bool networkTest,
        out IBattleChara target,
        out string reason)
    {
        target = null!;
        var player = services.ObjectTable.LocalPlayer;
        if (player == null || JobRoles.GetRangeRole(player) != RangeRole.Healer)
        {
            reason = "local player is not a healer";
            return false;
        }

        unsafe
        {
            if (!ActionUse.CanUseAction(ActionUse.RescueActionId, checkCastingActive: !allowCastInterrupt))
            {
                reason = "Rescue unavailable";
                return false;
            }
        }

        if (!networkTest ||
            CombatEngagementDetector.IsEffectivelyInCombat(services))
        {
            if (!bossModSafety.TryIsPositionSafe(player.Position, out var healerSafe, out var safetyReason) || !healerSafe)
            {
                reason = $"healer unsafe: {safetyReason}";
                return false;
            }
        }

        if (!this.TryFindPartyActorByActorKey(actorKey, out target))
        {
            reason = "SOS actor not visible in party";
            return false;
        }

        if (target.IsDead || target.CurrentHp == 0)
        {
            reason = "SOS actor is dead";
            return false;
        }

        if (Geometry.Distance2D(player.Position, target.Position) > RescueRange)
        {
            reason = "SOS actor outside Rescue range";
            return false;
        }

        reason = "Rescue advisory ready";
        return true;
    }

    private void TryAutoRescueClaim(DateTime nowUtc, BossModReflectionSafety bossModSafety)
    {
        if (!config.PartyIntentAutoRescueEnabled)
        {
            this.autoRescueStatus = config.PartyIntentEnabled ? "disabled" : "party intent disabled";
            return;
        }

        if (services.Condition[ConditionFlag.Unconscious])
        {
            this.autoRescueStatus = "blocked: dead";
            return;
        }

        IncomingRescueSos? rescue;
        lock (this.rescueGate)
        {
            rescue = this.incomingRescue;
            if (rescue == null)
            {
                this.autoRescueStatus = "waiting for SOS";
                return;
            }

            if (!this.incomingRescueClaimedByLocal)
            {
                this.autoRescueStatus = "waiting for local claim";
                return;
            }

            if (rescue.ExpiresUtc <= nowUtc)
            {
                this.incomingRescue = null;
                this.incomingRescueClaimedByLocal = false;
                this.autoRescueStatus = "SOS expired";
                return;
            }
        }

        var networkTest = IsNetworkTestSos(rescue.SosId);
        if (string.Equals(this.lastAutoRescueAttemptSosId, rescue.SosId, StringComparison.Ordinal))
        {
            return;
        }

        if (services.Condition[ConditionFlag.Unconscious])
        {
            this.autoRescueStatus = "blocked: dead";
            return;
        }

        if (!CombatEngagementDetector.IsEffectivelyInCombat(services) &&
            !CanAutoRescueOutOfCombatForNetworkTest(networkTest))
        {
            this.autoRescueStatus = "blocked: not in combat";
            return;
        }

        if (!this.CanOfferRescue(rescue.ActorKey, bossModSafety, allowCastInterrupt: true, networkTest, out var target, out var reason))
        {
            this.autoRescueStatus = $"blocked: {reason}";
            this.EnqueueOutgoing(new RescueReleaseWire("rescue.release", ProtocolVersion, this.sender, rescue.SosId));
            this.ClearIncomingRescue(rescue.SosId);
            return;
        }

        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            this.autoRescueStatus = "blocked: player unavailable";
            this.EnqueueOutgoing(new RescueReleaseWire("rescue.release", ProtocolVersion, this.sender, rescue.SosId));
            this.ClearIncomingRescue(rescue.SosId);
            return;
        }

        unsafe
        {
            if (ActionUse.HasAnimationLock())
            {
                this.autoRescueStatus = "blocked: animation lock";
                this.EnqueueOutgoing(new RescueReleaseWire("rescue.release", ProtocolVersion, this.sender, rescue.SosId));
                this.ClearIncomingRescue(rescue.SosId);
                return;
            }
        }

        this.lastAutoRescueAttemptSosId = rescue.SosId;
        unsafe
        {
            var interruptedCast = player.IsCasting;
            if (interruptedCast)
            {
                ActionUse.CancelCast();
            }

            var used = ActionManager.Instance()->UseAction(ActionType.Action, ActionUse.RescueActionId, target.GameObjectId);
            this.autoRescueStatus = FormatAutoRescueUseStatus(used, interruptedCast, target);
            if (used)
            {
                return;
            }
        }

        this.EnqueueOutgoing(new RescueReleaseWire("rescue.release", ProtocolVersion, this.sender, rescue.SosId));
        this.ClearIncomingRescue(rescue.SosId);
    }

    private static string FormatAutoRescueUseStatus(bool used, bool interruptedCast, IBattleChara target)
    {
        if (used)
        {
            return interruptedCast
                ? $"interrupted cast; used Rescue on {FormatActorName(target)}"
                : $"used Rescue on {FormatActorName(target)}";
        }

        return interruptedCast ? "interrupted cast; Rescue use failed" : "Rescue use failed";
    }

    private void ClearIncomingRescue(string sosId)
    {
        lock (this.rescueGate)
        {
            if (this.incomingRescue != null &&
                string.Equals(this.incomingRescue.SosId, sosId, StringComparison.Ordinal))
            {
                this.incomingRescue = null;
                this.incomingRescueClaimedByLocal = false;
            }
        }
    }

    private PartyIntentRescueAdvisory BuildRescueAdvisory(DateTime nowUtc)
    {
        IncomingRescueSos rescue;
        bool claimedByLocal;
        lock (this.rescueGate)
        {
            if (this.incomingRescue == null || this.incomingRescue.ExpiresUtc <= nowUtc)
            {
                return PartyIntentRescueAdvisory.None;
            }

            rescue = this.incomingRescue;
            claimedByLocal = this.incomingRescueClaimedByLocal;
        }

        var targetPosition = this.TryFindPartyActorByActorKey(rescue.ActorKey, out var actor)
            ? actor.Position
            : (Vector3?)null;

        return new PartyIntentRescueAdvisory(
            true,
            claimedByLocal,
            IsNetworkTestSos(rescue.SosId),
            rescue.SosId,
            rescue.TargetName,
            rescue.Reason,
            rescue.ExpiresUtc,
            targetPosition);
    }

    private bool TryFindPartyActorByActorKey(string actorKey, out IBattleChara actor)
    {
        actor = null!;
        var player = services.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var territory = services.ClientState.TerritoryType;
        var contentFinder = services.DutyState.ContentFinderCondition.RowId;
        foreach (var candidate in PartyAllyProvider.EnumerateVisiblePartyAllies(services, player))
        {
            if (this.TryBuildActorKey(candidate, territory, contentFinder, out var candidateActorKey) &&
                string.Equals(candidateActorKey, actorKey, StringComparison.Ordinal))
            {
                actor = candidate;
                return true;
            }
        }

        return false;
    }

    private bool TryBuildLocalActorKey(IBattleChara player, out string actorKey)
    {
        return this.TryBuildActorKey(
            player,
            services.ClientState.TerritoryType,
            services.DutyState.ContentFinderCondition.RowId,
            out actorKey);
    }

#if XCAI_NETWORK_TEST_CONTROLS
    private bool TryValidateNetworkTestTrigger(bool requireDirectPeer, out IBattleChara player, out string reason)
    {
        player = null!;
        if (!config.PartyIntentEnabled)
        {
            reason = "Party intent discovery is disabled";
            return false;
        }

        if (this.state != "connected")
        {
            reason = $"Party intent is not connected ({this.state})";
            return false;
        }

        if (this.peerCount <= 0)
        {
            reason = "no Party Intent peers in this party context";
            return false;
        }

        if (requireDirectPeer && this.directTransport.ConnectedPeerCount <= 0)
        {
            reason = "no direct peer channel is open";
            return false;
        }

        if (services.PartyList.Length <= 0)
        {
            reason = "join a party before sending test traffic";
            return false;
        }

        player = services.ObjectTable.LocalPlayer!;
        if (player == null || !services.ClientState.IsLoggedIn || player.IsDead || player.CurrentHp == 0)
        {
            reason = "local player unavailable";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private PartyIntentNetworkTestResult RecordNetworkTestTrigger(bool success, string message, DateTime nowUtc)
    {
        this.lastNetworkTestTrigger = new(true, success, message, nowUtc);
        return this.lastNetworkTestTrigger;
    }
#endif

    private bool EnqueueOutgoing(object message)
    {
        if (this.outgoingMessages.Count > 32)
        {
            return false;
        }

        this.outgoingMessages.Enqueue(message);
        return true;
    }

    private bool TryBuildActorKey(IBattleChara actor, uint territory, uint contentFinder, out string actorKey)
    {
        actorKey = string.Empty;
        if (actor.GameObjectId == services.ObjectTable.LocalPlayer?.GameObjectId &&
            services.PlayerState.ContentId != 0)
        {
            actorKey = BuildContentActorKey(services.PlayerState.ContentId, territory, contentFinder);
            return true;
        }

        foreach (var member in services.PartyList)
        {
            if (member.ContentId == 0 ||
                !PartyMemberMatchesActor(member.EntityId, member.GameObject?.GameObjectId ?? 0, actor))
            {
                continue;
            }

            actorKey = BuildContentActorKey(member.ContentId, territory, contentFinder);
            return true;
        }

        if (actor.EntityId == 0)
        {
            return false;
        }

        actorKey = BuildEntityActorKey(actor.EntityId, territory, contentFinder);
        return true;
    }

    private static bool PartyMemberMatchesActor(uint memberEntityId, ulong memberGameObjectId, IBattleChara actor)
    {
        return (memberGameObjectId != 0 && memberGameObjectId == actor.GameObjectId) ||
               (memberEntityId != 0 && memberEntityId == actor.EntityId);
    }

    private IReadOnlyList<string> BuildRosterTokens(string secret, uint territory, uint contentFinder, uint localEntityId)
    {
        var tokens = new List<string>();
        foreach (var member in services.PartyList)
        {
            if (member.ContentId != 0)
            {
                tokens.Add(Hmac(secret, string.Create(CultureInfo.InvariantCulture, $"member|content|{member.ContentId}|{territory}|{contentFinder}")));
                continue;
            }

            if (member.EntityId != 0)
            {
                tokens.Add(Hmac(secret, string.Create(CultureInfo.InvariantCulture, $"member|entity|{member.EntityId}|{territory}|{contentFinder}")));
            }
        }

        if (tokens.Count == 0)
        {
            tokens.Add(Hmac(secret, string.Create(CultureInfo.InvariantCulture, $"member|local|{localEntityId}|{territory}|{contentFinder}")));
        }

        return tokens;
    }

    private static bool TryBuildAvailabilityUri(out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme switch
            {
                "ws" => "http",
                "wss" => "https",
                _ => baseUri.Scheme
            },
            Path = AvailabilityPath
        };

        uri = builder.Uri;
        return uri.Scheme is "http" or "https";
    }

    private static bool TryBuildWebSocketUri(out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == "http" ? "ws" : "wss",
            Path = WebSocketPath
        };

        uri = builder.Uri;
        return uri.Scheme is "ws" or "wss";
    }

    private static string BuildContentActorKey(ulong contentId, uint territory, uint contentFinder)
        => Hmac(RoomSecret, string.Create(CultureInfo.InvariantCulture, $"actor|content|{contentId}|{territory}|{contentFinder}"));

    private static string BuildEntityActorKey(uint entityId, uint territory, uint contentFinder)
        => Hmac(RoomSecret, string.Create(CultureInfo.InvariantCulture, $"actor|entity|{entityId}|{territory}|{contentFinder}"));

    private static string Hmac(string secret, string value)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var bytes = Encoding.UTF8.GetBytes(value);
        using var hmac = new HMACSHA256(key);
        return Convert.ToBase64String(hmac.ComputeHash(bytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool IsNetworkTestSos(string sosId)
        => sosId.StartsWith(NetworkTestSosPrefix, StringComparison.Ordinal);

    private static bool CanAutoRescueOutOfCombatForNetworkTest(bool networkTest)
    {
#if XCAI_NETWORK_TEST_CONTROLS
        return networkTest;
#else
        _ = networkTest;
        return false;
#endif
    }

    private static PartyIntentNetworkTestResult MostRecent(PartyIntentNetworkTestResult first, PartyIntentNetworkTestResult second)
    {
        if (!first.Active)
        {
            return second;
        }

        if (!second.Active)
        {
            return first;
        }

        return first.TimestampUtc >= second.TimestampUtc ? first : second;
    }

#if XCAI_NETWORK_TEST_CONTROLS
    private static Vector2 ResolveNetworkTestDestackBias(float rotation)
    {
        var forward = new Vector2(MathF.Sin(rotation), MathF.Cos(rotation));
        if (forward.LengthSquared() <= 0.0001f)
        {
            return Vector2.UnitX;
        }

        return Vector2.Normalize(forward);
    }
#endif

    private static int EstimateDangerInMs(BossModMechanicPressure pressure)
    {
        var seconds = pressure.PrimaryPressure switch
        {
            BossModMechanicPressureKind.Pyretic or BossModMechanicPressureKind.NoMovement or BossModMechanicPressureKind.Freezing => pressure.BMRSpecialModeIn,
            BossModMechanicPressureKind.Knockback => pressure.BMRKnockbackIn,
            BossModMechanicPressureKind.SharedDamage or BossModMechanicPressureKind.Damage => pressure.BMRDamageIn,
            BossModMechanicPressureKind.Downtime => pressure.BMRDowntimeIn,
            BossModMechanicPressureKind.Vulnerable => pressure.BMRVulnerableIn,
            _ => float.MaxValue
        };

        return float.IsFinite(seconds) && seconds > 0f && seconds < 5f
            ? (int)(seconds * 1000f)
            : 0;
    }

    private static int EstimateWalkingTimeMs(float distance)
    {
        const float yalmsPerSecond = 6f;
        return float.IsFinite(distance) && distance > 0f
            ? (int)((distance / yalmsPerSecond) * 1000f)
            : 0;
    }

    private static int CountPeers(JsonElement root)
    {
        return root.TryGetProperty("peers", out var peers) && peers.ValueKind == JsonValueKind.Array
            ? peers.GetArrayLength()
            : 0;
    }

    private static IReadOnlyList<PartyIntentPeer> ParsePeers(JsonElement root)
    {
        if (!root.TryGetProperty("peers", out var peersElement) ||
            peersElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var peers = new List<PartyIntentPeer>();
        foreach (var peerElement in peersElement.EnumerateArray())
        {
            if (TryParsePeerElement(peerElement, out var peer))
            {
                peers.Add(peer);
            }
        }

        return peers;
    }

    private static bool TryParsePeer(JsonElement root, out PartyIntentPeer peer)
    {
        peer = null!;
        return root.TryGetProperty("peer", out var peerElement) &&
               TryParsePeerElement(peerElement, out peer);
    }

    private static bool TryParsePeerElement(JsonElement element, out PartyIntentPeer peer)
    {
        peer = null!;
        var sender = TryGetString(element, "sender");
        var actorKey = TryGetString(element, "actorKey");
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(actorKey))
        {
            return false;
        }

        var features = new List<string>();
        if (element.TryGetProperty("features", out var featuresElement) &&
            featuresElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var feature in featuresElement.EnumerateArray())
            {
                if (feature.ValueKind == JsonValueKind.String &&
                    feature.GetString() is { Length: > 0 } value)
                {
                    features.Add(value);
                }
            }
        }

        var supportsDirect = element.TryGetProperty("supportsDirect", out var supportsDirectElement) &&
                             supportsDirectElement.ValueKind == JsonValueKind.True;
        peer = new(sender, actorKey, features, supportsDirect);
        return true;
    }

    private static bool TryParsePeerSignal(JsonElement root, out PartyIntentPeerSignal signal)
    {
        signal = null!;
        var type = TryGetString(root, "type");
        var sender = TryGetString(root, "sender");
        var target = TryGetString(root, "target");
        if (string.IsNullOrWhiteSpace(type) ||
            string.IsNullOrWhiteSpace(sender) ||
            string.IsNullOrWhiteSpace(target) ||
            !root.TryGetProperty("payload", out var payload))
        {
            return false;
        }

        signal = new(type, sender, target, payload.Clone());
        return true;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static string FormatActorName(IBattleChara actor)
    {
        var name = actor.Name.TextValue;
        return string.IsNullOrWhiteSpace(name) ? "party member" : name;
    }

    private sealed record IncomingRescueSos(
        string SosId,
        string ActorKey,
        string TargetName,
        string Reason,
        DateTime ExpiresUtc);

    private sealed record AvailabilityEnvelope(
        string Proto,
        int Version,
        string Session,
        string Sender,
        long Seq,
        long SentMs,
        int TtlMs,
        string Kind,
        AvailabilityBody Body);

    private sealed record AvailabilityBody(
        string RoomKey,
        string ContextHash,
        string? PartyToken,
        string RosterHash,
        string ActorKey,
        IReadOnlyList<string> Features,
        bool SupportsDirect,
        bool SupportsRelay);

    private sealed record AvailabilityResponse(bool Ok, int PeerCount, string? Reason);

    private sealed record ClientAvailableWire(
        string Type,
        int ProtocolVersion,
        string Session,
        string Sender,
        long Seq,
        long SentMs,
        int TtlMs,
        string RoomKey,
        string ContextHash,
        string? PartyToken,
        string RosterHash,
        string ActorKey,
        IReadOnlyList<string> Features,
        bool SupportsDirect,
        bool SupportsRelay);

    private sealed record PingWire(string Type, int ProtocolVersion, string Sender, long SentMs);

    private sealed record RescueSosWire(
        string Type,
        int ProtocolVersion,
        string? Sender,
        string SosId,
        long SentMs,
        int TtlMs,
        string ActorKey,
        int DangerInMs,
        double Confidence,
        string Reason);

    private sealed record RescueClaimWire(
        string Type,
        int ProtocolVersion,
        string Sender,
        string SosId,
        int ClaimTtlMs);

    private sealed record RescueReleaseWire(
        string Type,
        int ProtocolVersion,
        string Sender,
        string SosId);
}
