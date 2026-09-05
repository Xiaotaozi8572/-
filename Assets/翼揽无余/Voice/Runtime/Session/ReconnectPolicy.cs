// ReconnectPolicy.cs
// VR5-T12: bounded automatic-reconnect policy for voice-ws-v1.
//
// A voice session is a VOICE_SESSION-level resource: after a transient transport drop the SAME
// session binding is re-established by re-opening the socket and letting the server reply with a
// fresh voice.state(session.started, protocol_version=voice-ws-v1) (HandleVoiceState then drives
// Connecting -> Ready). This class decides WHICH failures warrant that retry and at what pace.
//
// Only clearly transient, network-shaped failures are retried:
//   - Abnormal close (1006: dropped TCP / no close frame)   -> retry
//   - ServerError (1011: transient server-side error)       -> retry
//   - Connect / Timeout transport operation errors          -> retry
// Everything else is non-recoverable and MUST NOT be retried, because re-opening the socket cannot
// fix it and retrying would only mask the real condition:
//   - Normal (1000 clean close)
//   - Protocol (1002/1003/1007/1009/1010: a client-side protocol violation)
//   - Policy (1008: includes permission-like rejections)
//   - Application server-defined voice codes (4400 invalid / 4401 binding mismatch / 4408 timeout)
//   - Unknown close categories.
//
// The retry budget is fixed: at most MaxAttempts automatic re-open attempts with exponential
// backoff 1 s / 2 s / 4 s (deterministic, no jitter, fully unit-testable). A reconnect never
// changes the wire protocol (still voice-ws-v1) and never sends turn.cancel.
using Yilan.Voice.Runtime.Transport;

namespace Yilan.Voice.Runtime.Session
{
    /// <summary>Decision returned by <see cref="ReconnectPolicy"/> for one failure signal.</summary>
    public enum ReconnectVerdict
    {
        Retry,
        DoNotRetry
    }

    /// <summary>
    /// Pure decision/logic for bounded automatic reconnection. No state, no timers, no Unity API —
    /// the controller owns the scheduling and calls <see cref="DelayMsForAttempt"/> for each attempt.
    /// </summary>
    public static class ReconnectPolicy
    {
        /// <summary>Maximum number of automatic reconnection attempts after one recoverable drop.</summary>
        public const int MaxAttempts = 3;

        /// <summary>Backoff before attempt N (1-based), in milliseconds: 1 s, 2 s, 4 s.</summary>
        public static readonly int[] BackoffMs = { 1000, 2000, 4000 };

        /// <summary>Delay in ms to wait before the given (1-based) attempt. Clamped to the table.</summary>
        public static int DelayMsForAttempt(int attempt)
        {
            if (attempt <= 1) return BackoffMs[0];
            if (attempt >= BackoffMs.Length) return BackoffMs[BackoffMs.Length - 1];
            return BackoffMs[attempt - 1];
        }

        /// <summary>Whether a server close of the given category warrants an automatic reconnect.</summary>
        public static ReconnectVerdict ShouldReconnect(VoiceCloseCategory category)
        {
            switch (category)
            {
                case VoiceCloseCategory.Abnormal:    // 1006 transport drop
                case VoiceCloseCategory.ServerError: // 1011 transient server error
                    return ReconnectVerdict.Retry;
                default:
                    return ReconnectVerdict.DoNotRetry;
            }
        }

        /// <summary>Whether a transport operation error of the given kind warrants a reconnect.</summary>
        public static ReconnectVerdict ShouldReconnect(VoiceSocketErrorKind kind)
        {
            switch (kind)
            {
                case VoiceSocketErrorKind.Connect:
                case VoiceSocketErrorKind.Timeout:
                    return ReconnectVerdict.Retry;
                default:
                    return ReconnectVerdict.DoNotRetry;
            }
        }
    }
}
