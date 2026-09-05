using System;
using System.Threading.Tasks;

namespace Yilan.Voice.Runtime.Transport
{
    // ---------------------------------------------------------------------------
    // Delegate types for IVoiceSocket events. All callbacks are delivered from the
    // main thread via DispatchMessageQueue (thread-safe queue), so handlers never run
    // on a background thread.
    // ---------------------------------------------------------------------------

    public delegate void VoiceSocketOpenedHandler();
    public delegate void VoiceSocketPayloadHandler(byte[] payload);
    public delegate void VoiceSocketErrorHandler(VoiceSocketError error);
    public delegate void VoiceSocketClosedHandler(VoiceCloseInfo info);

    /// <summary>Stable classification of a transport-level failure.</summary>
    public enum VoiceSocketErrorKind
    {
        Connect,     // connecting / handshake failed
        Send,        // sending a text or binary frame failed
        Timeout,     // operation exceeded its deadline
        InvalidState, // socket not in the expected state (e.g. send while closed)
        Unexpected   // anything not otherwise classified
    }

    /// <summary>Immutable transport error value; carries no payload content or URL query.</summary>
    public sealed class VoiceSocketError
    {
        public VoiceSocketErrorKind Kind { get; }
        public string Message { get; }

        public VoiceSocketError(VoiceSocketErrorKind kind, string message)
        {
            Kind = kind;
            Message = message ?? string.Empty;
        }

        public override string ToString() => $"VoiceSocketError[{Kind}] {Message}";
    }

    /// <summary>Stable classification of a WebSocket close code (RFC 6455).</summary>
    public enum VoiceCloseCategory
    {
        Normal,      // 1000
        Protocol,    // 1002, 1003, 1007, 1009, 1010
        Policy,      // 1008
        ServerError, // 1011
        Abnormal,    // 1006 (transport drop / no close frame)
        Application, // any app-defined code 1000-4999 not otherwise classified
        Unknown      // 0, 1004, 1005, 1015, or unparseable
    }

    /// <summary>Close event payload delivered on the main thread.</summary>
    public sealed class VoiceCloseInfo
    {
        public int Code { get; }
        public VoiceCloseCategory Category { get; }
        public string Reason { get; }

        public VoiceCloseInfo(int code, VoiceCloseCategory category, string reason)
        {
            Code = code;
            Category = category;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Stable mapping from a numeric WebSocket close code to a coarse category.
    /// Server-initiated codes and transport drops route here so consumers can react
    /// without switch/case-ing raw numbers.
    /// </summary>
    public static class VoiceCloseMapper
    {
        public static VoiceCloseCategory Classify(int code)
        {
            switch (code)
            {
                case 1000: return VoiceCloseCategory.Normal;
                case 1002:
                case 1003:
                case 1007:
                case 1009:
                case 1010: return VoiceCloseCategory.Protocol;
                case 1008: return VoiceCloseCategory.Policy;
                case 1011: return VoiceCloseCategory.ServerError;
                case 1006: return VoiceCloseCategory.Abnormal;
                case 1004:
                case 1005:
                case 1015: return VoiceCloseCategory.Unknown;
                default:
                    return (code >= 1000 && code <= 4999)
                        ? VoiceCloseCategory.Application
                        : VoiceCloseCategory.Unknown;
            }
        }
    }

    /// <summary>
    /// Transport abstraction over a WebSocket connection. Implementations must be safe to
    /// call from any thread and must deliver every event on the main thread (pump with
    /// <see cref="DispatchMessageQueue"/>, typically from Unity Update). Raw binary frames
    /// are surfaced verbatim on <see cref="Payload"/>; deciding whether a payload is TTS
    /// PCM16 is delegated to the protocol layer's pending state, never to content guessing.
    /// </summary>
    public interface IVoiceSocket : IDisposable
    {
        event VoiceSocketOpenedHandler Opened;
        event VoiceSocketPayloadHandler Payload;
        event VoiceSocketErrorHandler Error;
        event VoiceSocketClosedHandler Closed;

        /// <summary>True while the underlying socket is in the Open state.</summary>
        bool IsOpen { get; }

        /// <summary>
        /// Drain the thread-safe event queue into subscribers. Implementations must be
        /// main-thread idempotent. Returns immediately when nothing is queued.
        /// </summary>
        void DispatchMessageQueue();

        /// <summary>Open a connection to <paramref name="url"/>. <paramref name="timeoutMs"/> &lt;= 0 uses the default.</summary>
        Task ConnectAsync(Uri url, int timeoutMs = 0);

        /// <summary>Send a text frame (JSON envelope). <paramref name="timeoutMs"/> &lt;= 0 uses the default.</summary>
        Task SendTextAsync(string text, int timeoutMs = 0);

        /// <summary>Send a raw binary frame (e.g. PCM16 audio input). <paramref name="timeoutMs"/> &lt;= 0 uses the default.</summary>
        Task SendBinaryAsync(byte[] data, int timeoutMs = 0);

        /// <summary>Close the connection with the given RFC-6455 code. Must be idempotent.</summary>
        Task CloseAsync(int code, string reason, int timeoutMs = 0);
    }
}
