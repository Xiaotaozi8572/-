using System;

namespace Yilan.Voice.Runtime.Session
{
    /// <summary>
    /// Voice session lifecycle states. The <see cref="VoiceSessionController"/> is the ONLY
    /// entry point that may mutate this value (UI / capture / playback call controller
    /// commands, never write the state directly).
    /// </summary>
    public enum VoiceClientState
    {
        Idle,            // constructed, not started; Faulted resets back here
        HealthChecking,  // running the controlled /health preflight probe
        Connecting,      // opening the WebSocket; waiting for session.started
        Ready,           // session established (server sent voice.state session.started v1)
        Capturing,       // recording audio input for the current turn
        AwaitingAnswer,  // audio.end sent, waiting for answer.display
        Playing,         // streaming TTS playback for the current answer
        Cancelling,      // an explicit cancel / barge-in is in flight
        Reconnecting,    // transport dropped; reconnecting
        Faulted          // terminal protocol/transport failure; only Reset() may leave it
    }

    /// <summary>Diagnostic classification of a state-transition attempt.</summary>
    public enum VoiceStateTransitionCode
    {
        Ok,        // edge is present in the legal transition table
        Illegal,   // edge is not a legal transition
        Terminal,  // attempted to leave Faulted without an explicit Reset
        SameState  // from == to (no self-loop)
    }

    /// <summary>
    /// Static legal-transition rules for <see cref="VoiceClientState"/>.
    ///
    /// Legal edges:
    ///   Idle -> HealthChecking -> Connecting -> Ready -> Capturing -> AwaitingAnswer
    ///        -> Playing -> Ready
    ///   Capturing -> Playing        (P1-1: the server-side VAD endpoint can consume the
    ///                                 utterance and publish answer.display while the
    ///                                 push-to-talk user is still holding the button)
    ///   Reconnecting -> Connecting -> ...
    ///   Cancelling -> Idle (session ended) | Cancelling -> Ready (barge-in done)
    ///   Faulted -> Idle (explicit reset)
    ///   any ACTIVE state -> Cancelling / Reconnecting / Faulted   (escape)
    ///   Idle -> Faulted                                            (config/precondition fault)
    ///
    /// Nothing here is a protocol threshold; only topological validity.
    /// </summary>
    public static class VoiceStateRules
    {
        private static readonly bool[,] Table = BuildTable();

        private static bool[,] BuildTable()
        {
            int n = (int)VoiceClientState.Faulted + 1;
            var t = new bool[n, n];
            void Edge(VoiceClientState a, VoiceClientState b) => t[(int)a, (int)b] = true;

            // Main forward chain.
            Edge(VoiceClientState.Idle, VoiceClientState.HealthChecking);
            Edge(VoiceClientState.HealthChecking, VoiceClientState.Connecting);
            Edge(VoiceClientState.Connecting, VoiceClientState.Ready);
            Edge(VoiceClientState.Ready, VoiceClientState.Capturing);
            Edge(VoiceClientState.Capturing, VoiceClientState.AwaitingAnswer);
            Edge(VoiceClientState.AwaitingAnswer, VoiceClientState.Playing);
            Edge(VoiceClientState.Playing, VoiceClientState.Ready);

            // P1-1: early answer. The server VAD endpoint may fire while the local
            // user is still holding the record button (client Capturing); the
            // answer.display that follows is authoritative for the live turn, so
            // the client follows it directly into Playing instead of losing the
            // round. Trailing mic frames are then refused client-side (not
            // Capturing anymore) and dropped server-side by the trailing-frame
            // guard; a later audio.end is a harmless no-op on the server.
            Edge(VoiceClientState.Capturing, VoiceClientState.Playing);

            // Reconnect recovery.
            Edge(VoiceClientState.Reconnecting, VoiceClientState.Connecting);
            Edge(VoiceClientState.Cancelling, VoiceClientState.Idle);
            Edge(VoiceClientState.Cancelling, VoiceClientState.Ready);

            // Faulted is terminal except an explicit reset.
            Edge(VoiceClientState.Faulted, VoiceClientState.Idle);

            // Escape from any active state -> Cancelling / Reconnecting / Faulted.
            var active = new[]
            {
                VoiceClientState.HealthChecking, VoiceClientState.Connecting,
                VoiceClientState.Ready, VoiceClientState.Capturing,
                VoiceClientState.AwaitingAnswer, VoiceClientState.Playing,
                VoiceClientState.Reconnecting, VoiceClientState.Cancelling
            };
            foreach (var s in active)
            {
                Edge(s, VoiceClientState.Cancelling);
                Edge(s, VoiceClientState.Reconnecting);
                Edge(s, VoiceClientState.Faulted);
            }

            // Configuration / precondition fault at cold start.
            Edge(VoiceClientState.Idle, VoiceClientState.Faulted);
            return t;
        }

        /// <summary>True when the state represents a live/active session (everything except Idle/Faulted).</summary>
        public static bool IsActive(VoiceClientState s)
            => s >= VoiceClientState.HealthChecking && s <= VoiceClientState.Reconnecting;

        public static VoiceStateTransitionCode TryTransition(VoiceClientState from, VoiceClientState to)
        {
            if (from == to)
                return VoiceStateTransitionCode.SameState;
            if (from == VoiceClientState.Faulted && to != VoiceClientState.Idle)
                return VoiceStateTransitionCode.Terminal;
            return Table[(int)from, (int)to]
                ? VoiceStateTransitionCode.Ok
                : VoiceStateTransitionCode.Illegal;
        }
    }
}
