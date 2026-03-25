using TeamsEchoBot.Helpers;
using System.Net;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using Microsoft.Skype.Bots.Media;
using TeamsEchoBot.Media;
using TeamsEchoBot.Models;

namespace TeamsEchoBot.Services;

/// <summary>
/// Core singleton service that initializes the Graph Communications client,
/// processes Teams webhook callbacks, and joins meetings on demand.
///
/// KEY PATTERN (verified against official Microsoft samples):
///   The ILocalMediaSession MUST be created BEFORE calling Calls().AddAsync().
///   Subscribing to AudioSocket events before AddAsync ensures no frames are missed
///   during the early establishment phase.
/// </summary>
public class BotService(
    BotConfiguration botConfig,
    SpeechConfiguration speechConfig,
    ILogger<BotService> logger) : IHostedService
{
    private readonly BotConfiguration _botConfig = botConfig;
    private readonly SpeechConfiguration _speechConfig = speechConfig;
    private readonly ILogger<BotService> _logger = logger;

    private ICommunicationsClient? _client;

    private readonly Dictionary<string, CallHandler> _callHandlers = [];
    private readonly object _handlersLock = new();

    // Called by ASP.NET Core on startup — replaces manual Initialize() call

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing BotService...");

        var mediaPlatformSettings = BuildMediaPlatformSettings();
        _client = BuildCommunicationsClient(mediaPlatformSettings);

        _client.Calls().OnUpdated += OnCallCollectionUpdated;

        _logger.LogInformation("BotService initialized successfully.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("BotService shutting down...");

        // Step 1: Delete all active calls via the Graph API first.
        // This sends a hangup signal to Teams so the call ends cleanly
        // on their side before we tear down the local media platform.
        // Skipping this step leaves the media engine waiting for a
        // call termination event that never arrives — causing the freeze.
        if (_client != null)
        {
            List<ICall> activeCalls;
            try
            {
                activeCalls = _client.Calls().Select(c => c).ToList();
            }
            catch { activeCalls = new List<ICall>(); }

            foreach (var call in activeCalls)
            {
                try
                {
                    _logger.LogInformation("Hanging up call {CallId}...", call.Id);
                    await call.DeleteAsync().ConfigureAwait(false);
                    _logger.LogInformation("Call {CallId} hung up.", call.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not hang up call {CallId} — continuing shutdown.", call.Id);
                }
            }
        }

        // Step 2: Dispose all local call handlers
        List<CallHandler> handlers;
        lock (_handlersLock)
        {
            handlers = _callHandlers.Values.ToList();
            _callHandlers.Clear();
        }

        foreach (var handler in handlers)
        {
            try { handler.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing call handler during shutdown.");
            }
        }

        // Step 3: Dispose the communications client and media platform.
        // Wrap in a Task.Run with a timeout — these are blocking native calls
        // and can hang if the media engine is in a bad state. We give them
        // 8 seconds and then force-continue regardless.
        var shutdownTask = Task.Run(() =>
        {
            try
            {
                _client?.Dispose();
                _logger.LogInformation("CommunicationsClient disposed.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing CommunicationsClient.");
            }

            try
            {
                MediaPlatform.Shutdown();
                _logger.LogInformation("MediaPlatform shut down.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error shutting down MediaPlatform.");
            }
        });

        if (await Task.WhenAny(shutdownTask, Task.Delay(8000, cancellationToken)) != shutdownTask)
            _logger.LogWarning("MediaPlatform shutdown timed out after 8 seconds — forcing exit.");

        _logger.LogInformation("BotService shutdown complete.");
    }

    /// <summary>
    /// Processes an inbound Teams callback HTTP POST to /api/calling.
    /// The Graph SDK parses the notification type and routes it internally.
    /// </summary>
    /// <summary>
    /// Converts ASP.NET Core HttpRequest → HttpRequestMessage (required by the SDK),
    /// calls the SDK, then writes the HttpResponseMessage back to HttpResponse.
    /// The SDK extension ProcessNotificationAsync takes HttpRequestMessage, not HttpRequest.
    /// </summary>
    public async Task ProcessNotificationAsync(HttpRequest request, HttpResponse response)
    {
        EnsureInitialized();

        // Convert ASP.NET Core HttpRequest to System.Net.Http.HttpRequestMessage
        var httpRequestMessage = await request.ToHttpRequestMessageAsync().ConfigureAwait(false);

        // SDK extension: CommunicationsClientExtensions.ProcessNotificationAsync(HttpRequestMessage)
        // Returns HttpResponseMessage which we write back to the ASP.NET Core HttpResponse
        var httpResponseMessage = await _client!
            .ProcessNotificationAsync(httpRequestMessage)
            .ConfigureAwait(false);

        await httpResponseMessage.CopyToHttpResponseAsync(response).ConfigureAwait(false);
    }

    /// <summary>
    /// Joins a Teams meeting from a meeting join URL.
    ///
    /// Correct order per official SDK documentation and samples:
    ///   1. Parse URL into ChatInfo + OrganizerMeetingInfo
    ///   2. Create ILocalMediaSession via client.CreateMediaSession()
    ///   3. Build CallHandler and subscribe AudioSocket BEFORE AddAsync
    ///   4. Build JoinMeetingParameters with the pre-created mediaSession
    ///   5. Call client.Calls().AddAsync() to initiate the join
    ///   6. Attach the resulting ICall to the handler for state tracking
    /// </summary>
    public async Task<string> JoinCallAsync(string joinUrl)
    {
        EnsureInitialized();

        _logger.LogInformation("Joining Teams meeting: {JoinUrl}", joinUrl);

        var (chatInfo, meetingInfo, tenantId) = ParseTeamsJoinUrl(joinUrl);
        _logger.LogInformation("Meeting tenant ID: {TenantId}", tenantId);
        var scenarioId = Guid.NewGuid();

        // Create media session first.
        // AudioSocketSettings from Microsoft.Skype.Bots.Media.
        // Sendrecv = bot can both receive audio from participants AND send TTS audio back.
        var audioSettings = new AudioSocketSettings
        {
            StreamDirections = StreamDirection.Sendrecv,
            SupportedAudioFormat = AudioFormat.Pcm16K,
        };

        // client.CreateMediaSession() is an extension method from
        // Microsoft.Graph.Communications.Calls.Media (MediaCallExtensions).
        // Returns ILocalMediaSession which exposes the AudioSocket property.
        // Pass (IEnumerable<VideoSocketSettings>?)null to resolve overload ambiguity
        // between CreateMediaSession(AudioSocketSettings, VideoSocketSettings, ...) and
        // CreateMediaSession(AudioSocketSettings, IEnumerable<VideoSocketSettings>, ...)
        var mediaSession = _client!.CreateMediaSession(
            audioSocketSettings: audioSettings,
            videoSocketSettings: (IEnumerable<VideoSocketSettings>?)null,
            vbssSocketSettings:  null);

        _logger.LogInformation("MediaSession created. ID: {SessionId}", mediaSession.MediaSessionId);

        // Create the CallHandler and wire up the AudioSocket BEFORE AddAsync.
        // This guarantees AudioMediaReceived events are not missed during early negotiation.
        var handler = new CallHandler(mediaSession, _speechConfig, _logger);

        // JoinMeetingParameters takes: chatInfo, meetingInfo, mediaSession.
        // Do NOT pass Modality[] here — that overload is for service-hosted media (no AHM).
        var joinParams = new JoinMeetingParameters(chatInfo, meetingInfo, mediaSession)
        {
            TenantId = tenantId
        };

        // AddAsync sends the join request to Teams and returns the stateful ICall.
        var call = await _client.Calls().AddAsync(joinParams, scenarioId).ConfigureAwait(false);

        _logger.LogInformation("Join request sent. Call ID: {CallId}", call.Id);

        // Now attach the ICall to the handler for state-change tracking
        handler.AttachCall(call);

        lock (_handlersLock)
        {
            _callHandlers[call.Id] = handler;
        }

        return call.Id;
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private void OnCallCollectionUpdated(ICallCollection sender, CollectionEventArgs<ICall> args)
    {
        foreach (var call in args.RemovedResources)
        {
            lock (_handlersLock)
            {
                if (_callHandlers.TryGetValue(call.Id, out var handler))
                {
                    handler.Dispose();
                    _callHandlers.Remove(call.Id);
                    _logger.LogInformation("Call {CallId} ended — handler disposed.", call.Id);
                }
            }
        }
    }

    private ICommunicationsClient BuildCommunicationsClient(MediaPlatformSettings mediaPlatformSettings)
    {
        // GraphLogger is from Microsoft.Graph.Communications.Common.Telemetry.
        // It is the logger type expected by CommunicationsClientBuilder.
        var graphLogger = new GraphLogger(_botConfig.BotName);

        var builder = new CommunicationsClientBuilder(
            _botConfig.BotName,
            _botConfig.AadAppId,
            graphLogger);

        builder.SetAuthenticationProvider(new AuthenticationProvider(_botConfig, _logger));

        // This URL is what Teams will POST callback events to.
        // It MUST exactly match the webhook URL in Azure Bot → Teams channel → Calling.
        builder.SetNotificationUrl(new Uri(_botConfig.CallbackUri));

        builder.SetMediaPlatformSettings(mediaPlatformSettings);

        // Use Graph v1.0 endpoint (not beta)
        builder.SetServiceBaseUrl(new Uri("https://graph.microsoft.com/v1.0/"));

        var client = builder.Build();
        _logger.LogInformation("CommunicationsClient built. CallbackUri: {Uri}", _botConfig.CallbackUri);
        return client;
    }

    private MediaPlatformSettings BuildMediaPlatformSettings()
    {
        // Resolve the public IP from the DNS name at startup.
        // On the VM this resolves to the static public IP directly.
        var publicIp = Dns.GetHostAddresses(_botConfig.ServiceDnsName)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException(
                $"Cannot resolve '{_botConfig.ServiceDnsName}' to an IPv4 address. " +
                "Verify your DNS label is set in Azure Portal → Public IP → Configuration.");

        _logger.LogInformation("Resolved DNS {Dns} → {Ip}", _botConfig.ServiceDnsName, publicIp);

        var settings = new MediaPlatformSettings
        {
            MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
            {
                // Thumbprint of the TLS cert in Cert:\LocalMachine\My on the VM.
                // Used for DTLS negotiation during media session setup.
                CertificateThumbprint = _botConfig.CertThumbprint,

                // On a VM with direct public IP (ILPIP) there is no NAT,
                // so internal port == public port.
                InstanceInternalPort   = _botConfig.MediaPort,
                InstancePublicPort     = _botConfig.MediaPort,
                InstancePublicIPAddress = publicIp,

                // Must exactly match the CN/SAN on your TLS certificate
                ServiceFqdn = _botConfig.ServiceDnsName,
            },
            ApplicationId = _botConfig.AadAppId,
        };

        // MediaPlatform.Initialize() is a one-time static initialization from
        // Microsoft.Skype.Bots.Media. Must be called before any calls are made.
        // MediaPlatform.Initialize(settings);
        _logger.LogInformation("MediaPlatform initialized. FQDN: {Fqdn}, Port: {Port}",
            _botConfig.ServiceDnsName, _botConfig.MediaPort);

        return settings;
    }

    /// <summary>
    /// Parses a Teams meeting join URL into the two objects required by JoinMeetingParameters.
    ///
    /// URL format: https://teams.microsoft.com/l/meetup-join/{threadId}/{messageId}?context={json}
    /// Context JSON contains Tid (tenantId) and Oid (organizerId).
    ///
    /// ChatInfo and OrganizerMeetingInfo are from Microsoft.Graph.Models.
    /// </summary>
    private static (ChatInfo chatInfo, OrganizerMeetingInfo meetingInfo, string tenantId) ParseTeamsJoinUrl(string joinUrl)
    {
        var uri      = new Uri(joinUrl);
        var segments = uri.AbsolutePath.TrimStart('/').Split('/');

        // Segments: [ "l", "meetup-join", threadId, messageId ]
        if (segments.Length < 4 || segments[1] != "meetup-join")
            throw new ArgumentException(
                $"Invalid Teams join URL format. Expected: " +
                $"https://teams.microsoft.com/l/meetup-join/{{threadId}}/{{messageId}}?context=... " +
                $"Got: {joinUrl}");

        var threadId  = Uri.UnescapeDataString(segments[2]);
        var messageId = Uri.UnescapeDataString(segments[3]);

        var query   = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var context = Uri.UnescapeDataString(query["context"] ?? "{}");
        var ctx     = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(context)
                      ?? new Dictionary<string, string>();

        ctx.TryGetValue("Tid", out var tenantId);
        ctx.TryGetValue("Oid", out var organizerId);

        var chatInfo = new ChatInfo
        {
            ThreadId  = threadId,
            MessageId = messageId,
        };

        var meetingInfo = new OrganizerMeetingInfo
        {
            Organizer = new IdentitySet
            {
                User = new Identity
                {
                    Id = organizerId,
                    AdditionalData = new Dictionary<string, object>
                    {
                        { "tenantId", tenantId ?? string.Empty }
                    }
                }
            }
        };
        return (chatInfo, meetingInfo, tenantId ?? string.Empty);
    }

    private void EnsureInitialized()
    {
        if (_client is null)
            throw new InvalidOperationException(
                "BotService not initialized. Ensure Initialize() is called in Program.cs startup.");
    }
}
