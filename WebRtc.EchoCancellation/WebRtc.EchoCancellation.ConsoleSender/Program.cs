using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using System.Net;
using WebRtc.EchoCancellation.ConsoleSender.Audio;
using WebSocketSharp.Server;

class Program
{
    private const int WEBSOCKET_PORT = 8081;
    private static Microsoft.Extensions.Logging.ILogger logger = NullLogger.Instance;

    static void Main(string[] args)
    {
        Console.WriteLine("WebRTC Audio Server Example Program");
        for (int i = -1; i < NAudio.Wave.WaveIn.DeviceCount; i++)
        {
            var caps = NAudio.Wave.WaveIn.GetCapabilities(i);
            Console.WriteLine($"{i}: {caps.ProductName}");
        }

        logger = AddConsoleLogger();

        // Start web socket.
        Console.WriteLine("Starting web socket server...");
        var webSocketServer = new WebSocketServer(IPAddress.Any, WEBSOCKET_PORT);
        webSocketServer.AddWebSocketService<WebRTCWebSocketPeer>("/", (peer) => peer.CreatePeerConnection = CreatePeerConnection);
        webSocketServer.Start();

        Console.WriteLine($"Waiting for browser web socket connection to {webSocketServer.Address}:{webSocketServer.Port}...");

        Console.WriteLine("Press ctrl-c to exit.");
        ManualResetEvent exitMre = new ManualResetEvent(false);
        Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            exitMre.Set();
        };

        // Wait for a signal saying the call failed, was cancelled with ctrl-c or completed.
        exitMre.WaitOne();
    }

    private static Task<RTCPeerConnection> CreatePeerConnection()
    {
        RTCConfiguration config = new RTCConfiguration
        {
            // iceServers = new List<RTCIceServer> { new RTCIceServer { urls = STUN_URL } }
        };
        var pc = new RTCPeerConnection(config);

        MicAudioSource audioSource = new MicAudioSource(new AudioEncoder(), audioInDeviceIndex: 0);
        //audioSource.RestrictFormats(x => x.FormatName == "OPUS");
        audioSource.OnAudioSourceEncodedSample += (uint durationRtpUnits, byte[] sample) =>
        {
            pc.SendAudio(durationRtpUnits, sample);
        };

        MediaStreamTrack audioTrack = new MediaStreamTrack(audioSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendOnly);

        pc.addTrack(audioTrack);

        pc.OnAudioFormatsNegotiated += (audioFormats) => logger.LogDebug($"Audio formats {audioFormats.First().FormatName}");

        pc.onconnectionstatechange += (state) =>
        {
            logger.LogDebug($"Peer connection state change to {state}.");

            if (state == RTCPeerConnectionState.connected)
            {
                audioSource.StartAudio();
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                pc.Close("ice disconnection");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                audioSource.CloseAudio();
            }
        };

        // Diagnostics
        //pc.OnReceiveReport += (re, media, rr) => logger.LogDebug($"RTCP Receive for {media} from {re}\n{rr.GetDebugSummary()}");
        //pc.OnSendReport += (media, sr) => logger.LogDebug($"RTCP Send for {media}\n{sr.GetDebugSummary()}");
        //pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) => logger.LogDebug($"STUN {msg.Header.MessageType} received from {ep}.");
        //pc.oniceconnectionstatechange += (state) => logger.LogDebug($"ICE connection state change to {state}.");
        pc.onsignalingstatechange += () =>
        {
            logger.LogDebug($"Signaling state change to {pc.signalingState}.");
            if (pc.signalingState == RTCSignalingState.have_local_offer)
            {
                //logger.LogDebug("Offer SDP:");
                //logger.LogDebug(pc.localDescription.sdp.ToString());
            }
            else if (pc.signalingState == RTCSignalingState.have_remote_offer || pc.signalingState == RTCSignalingState.stable)
            {
                //logger.LogDebug("Answer SDP:");
                //logger.LogDebug(pc.remoteDescription.sdp.ToString());
            }
        };

        return Task.FromResult(pc);
    }

    /// <summary>
    /// Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
    /// </summary>
    private static Microsoft.Extensions.Logging.ILogger AddConsoleLogger()
    {
        var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
            .WriteTo.Console()
            .CreateLogger();
        var factory = new SerilogLoggerFactory(serilogLogger);
        SIPSorcery.LogFactory.Set(factory);
        return factory.CreateLogger<Program>();
    }
}
