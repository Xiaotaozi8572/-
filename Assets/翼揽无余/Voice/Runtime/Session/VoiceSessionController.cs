using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Protocol;
using Yilan.Voice.Runtime.Transport;

namespace Yilan.Voice.Runtime.Session
{
    /// <summary>Stable classification of a controller command / inbound-message outcome.</summary>
    public enum VoiceCommandResult
    {
        Ok,               // command accepted / message applied
        Idempotent,       // duplicate start / end / cancel — no-op success
        InvalidState,     // command not legal in the current state
        Faulted,          // controller is Faulted (terminal)
        TransportFailed,  // socket connect/send failed
        SequenceMismatch, // audio.frame / tts.frame sequence out of order
        StaleTurn,        // message belongs to an old / already-closed turn
        OtherSession,     // message belongs to a different session
        LateAnswer,       // answer.display arrived after an answer was already applied
        LateTts           // tts.frame arrived after playback completed
    }

    /// <summary>
    /// Session state machine controller. This is the UNIQUE entry point that mutates
    /// <see cref="VoiceClientState"/>: UI / capture / playback layers call the public
    /// commands and never write <see cref="State"/> directly.
    ///
    /// Responsibilities:
    ///   - Legal-transition enforcement via <see cref="VoiceStateRules"/>.
    ///   - Event binding (session / turn / audio-stream / playback) with stale-round,
    ///     other-session, and late-message rejection.
    ///   - Sequence out-of-order validation for audio.frame and tts.frame at this layer
    ///     (续接 VR1-T05：解析层已做 binding id 校验，本层补 sequence 乱序校验).
    ///   - Only a real <c>voice.state(status=session.started, protocol_version=voice-ws-v1)</c>
    ///     enters Ready; a non-v1 / mismatched started transitions Faulted.
    ///   - Duplicate start / end / cancel are idempotent and never throw.
    /// </summary>
    public sealed class VoiceSessionController : IDisposable
    {
        private const int SampleRateHz = 16000;   // mirrors Fixtures/voice_ws_v1/audio_frame.json
        private const int FrameDurationMs = 20;   // mirrors Fixtures/voice_ws_v1/audio_frame.json
        private const int Mono = 1;               // mirrors Fixtures/voice_ws_v1/audio_frame.json

        private readonly VoiceClientConfig _config;
        private IVoiceSocket _socket;
        private ITtsPlayback _playback;
        private TtsBinaryPairer _ttsPairer;
        private Task _autoReconnectTask;
        private Task _cancelWatchdogTask;
        private VoiceClientState _state = VoiceClientState.Idle;

        private string _sessionId;
        private string _turnId;
        private string _audioStreamId;
        private string _playbackId;
        private VoiceBinding _binding;

        private int _turnCounter;
        private int _expectedAudioSeq = -1;
        private int _expectedTtsSeq = -1;
        private bool _sessionStartSent;
        private bool _turnActive;
        private bool _answered;
        private bool _playbackCompleted;
        private bool _disposed;

        /// <summary>Injectable health preflight; null skips the probe. Production wires
        /// this to <see cref="VoiceHealthClient"/>; tests substitute a stub.</summary>
        internal Func<VoiceClientConfig, Task<bool>> HealthProbe;

        /// <summary>Fired after any committed state change.</summary>
        public event Action<VoiceClientState> StateChanged;
        /// <summary>Fired when an answer.display (final text) is applied.</summary>
        public event Action<string> AnswerReceived;
        /// <summary>Fired when a tts.frame is accepted (body pairing is a transport concern).</summary>
        public event Action<TtsFrameMessage> TtsFrameAccepted;
        /// <summary>Fired when an asr.partial / asr.final transcript for the bound turn is
        /// received (P1-2). Informational: no state transition.</summary>
        public event Action<AsrTranscriptMessage> TranscriptReceived;
        /// <summary>Fired when a barge_in.accepted acknowledgement for the bound turn is
        /// received (P1-2). Informational: local cancel already ran before it arrives.</summary>
        public event Action<BargeInAcceptedMessage> BargeInAccepted;
        /// <summary>Fired when a clarification.required for the live AwaitingAnswer turn is
        /// received (P1-6 / VRR3). Carries the machine-readable reason.</summary>
        public event Action<string> ClarificationRequired;

        /// <summary>VR10: TTS failed but the text answer is on screen (display-only degradation).</summary>
        public event Action TtsDegraded;

        /// <summary>VR10: the server reaped the session with close 4408 (VOICE_SESSION_TIMEOUT
        /// — idle or processing-budget expiry) and the controller settled back to Idle.
        /// UI hint copy: 会话超时，点击重新提问.</summary>
        public event Action SessionExpired;

        /// <summary>Fired when an inbound message is rejected (stale/other/late/sequence).</summary>
        public event Action<VoiceCommandResult> MessageRejected;

        public VoiceClientState State => _state;
        public string SessionId => _sessionId;
        public string TurnId => _turnId;
        public string AudioStreamId => _audioStreamId;
        public string PlaybackId => _playbackId;
        /// <summary>Last rejection classification (for diagnostics/tests).</summary>
        public VoiceCommandResult LastRejection { get; private set; } = VoiceCommandResult.Ok;

        /// <summary>Optional TTS playback queue this controller drives for barge-in ordering and TTS
        /// MP3 body delivery. Ownership stays with the caller (never disposed here).</summary>
        public ITtsPlayback Playback
        {
            get => _playback;
            set { _playback = value; }
        }

        /// <summary>Injectable per-attempt reconnect delay (production = real Task.Delay). Tests
        /// substitute a completed task for determinism.</summary>
        public Func<int, Task> ReconnectDelayAsync { get; set; } = ms => Task.Delay(ms);

        /// <summary>Injectable cancel-ack watchdog delay (production = real Task.Delay). Tests
        /// substitute a completed task (immediate timeout) or a never-completing
        /// TaskCompletionSource (no timeout) for determinism.</summary>
        public Func<int, Task> CancelAckDelayAsync { get; set; } = ms => Task.Delay(ms);

        /// <summary>The currently-running automatic reconnect cycle, or null when none is active.</summary>
        public Task AutoReconnectTask => _autoReconnectTask;

        /// <summary>True while an automatic reconnection cycle is scheduled or in progress.</summary>
        public bool IsAutoReconnecting => _autoReconnectTask != null && !_autoReconnectTask.IsCompleted;

        public VoiceSessionController(VoiceClientConfig config, IVoiceSocket socket = null, ITtsPlayback playback = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _ttsPairer = new TtsBinaryPairer(null); // id checks live in the parser against _binding
            _playback = playback;
            if (socket != null)
                Bind(socket);
        }

        public void Bind(IVoiceSocket socket)
        {
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            if (_socket != null && !ReferenceEquals(_socket, socket))
                throw new InvalidOperationException("controller already bound to a socket");
            _socket = socket;
            _socket.Opened += OnOpened;
            _socket.Payload += OnPayload;
            _socket.Error += OnError;
            _socket.Closed += OnClosed;
        }

        // =============================== Commands ===============================

        /// <summary>Idle -> HealthChecking -> Connecting; opens the WebSocket. Idempotent when active.</summary>
        public async Task<VoiceCommandResult> ConnectAsync()
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (_state != VoiceClientState.Idle)
                return VoiceStateRules.IsActive(_state) ? VoiceCommandResult.Idempotent : VoiceCommandResult.InvalidState;

            if (Transition(VoiceClientState.HealthChecking) != VoiceStateTransitionCode.Ok)
            { TransitionFault(); return VoiceCommandResult.InvalidState; }

            if (HealthProbe != null)
            {
                bool ok;
                try { ok = await HealthProbe(_config); }
                catch { ok = false; }
                if (!ok) { TransitionFault(); return VoiceCommandResult.TransportFailed; }
            }

            if (Transition(VoiceClientState.Connecting) != VoiceStateTransitionCode.Ok)
            { TransitionFault(); return VoiceCommandResult.TransportFailed; }

            // P1-8: a per-controller UNIQUE session id instead of the hardcoded
            // "vr-session-001": several devices against one server would otherwise
            // collide in the server's active-binding registry (one live connection
            // per session_id). Generated once per controller lifetime (null after
            // Reset) so bounded auto-reconnects reuse it and the server-side
            // conversation context survives transport drops.
            // P1-5: the turn counter is only reset for a FRESH session. Re-connecting
            // the SAME session id (pause -> resume) must keep counting up: the server
            // retains the session's finished_turn_ids and rejects any reused turn id
            // (VOICE_STATE error on session.start), so turn-N has to stay monotonic
            // across reconnects — exactly like the bounded auto-reconnect path.
            if (_sessionId == null)
            {
                _sessionId = NewSessionId();
                _turnCounter = 0;
            }
            _sessionStartSent = false;
            _turnActive = false;

            try
            {
                await _socket.ConnectAsync(_config.BuildWsUri(), _config.ConnectTimeoutMs).ConfigureAwait(false);
            }
            catch
            {
                TransitionFault();
                return VoiceCommandResult.TransportFailed;
            }
            return VoiceCommandResult.Ok;
        }

        /// <summary>Ready -> Capturing, starts the current turn. Idempotent when already capturing.</summary>
        public VoiceCommandResult RecordStartAsync()
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (_state == VoiceClientState.Capturing) return VoiceCommandResult.Idempotent;
            if (_state != VoiceClientState.Ready) return VoiceCommandResult.InvalidState;

            if (!_turnActive || !_sessionStartSent)
            {
                _turnCounter++;
                _turnId = "turn-" + _turnCounter.ToString("D4");
                _audioStreamId = "stream-audio-" + _turnCounter;
                // Keep the binding in sync with the client's actual turn: the server
                // echoes the session.start ids, so inbound messages (answer.display,
                // tts.frame) are validated against the turn WE use, not the stale one
                // from the initial (turn-less) session.start.
                // Downstream (server->client) voice-ws-v1 messages never carry
                // audio_stream_id (gold fixtures: answer.display/tts.frame/voice.state/
                // clarification.required have no audio_stream_id key). Keeping the binding's
                // AudioStreamId null makes VoiceProtocolParser.CheckIds skip that slot for
                // those types instead of mis-reporting a mismatch (null != "stream-...").
                _binding = new VoiceBinding
                {
                    SessionId = _sessionId,
                    TurnId = _turnId,
                    AudioStreamId = null,
                    PlaybackId = null
                };
                SendSessionStart();
                _turnActive = true;
            }
            _answered = false;
            _playbackCompleted = false;
            _expectedAudioSeq = _expectedAudioSeq < 0 ? 0 : _expectedAudioSeq;

            if (Transition(VoiceClientState.Capturing) != VoiceStateTransitionCode.Ok)
            { TransitionFault(); return VoiceCommandResult.InvalidState; }
            return VoiceCommandResult.Ok;
        }

        /// <summary>
        /// Sequence number the next outbound audio.frame must carry (the server enforces
        /// strict monotonic continuity from 0). Read-only view for callers that drive the
        /// capture pump (e.g. VoiceClientRuntime.PumpAsync).
        /// </summary>
        public int NextAudioSequence => _expectedAudioSeq;

        /// <summary>
        /// Send one audio.frame header for the current turn; validates monotonic sequence
        /// (out-of-order is rejected with <see cref="VoiceCommandResult.SequenceMismatch"/>).
        /// Only legal while Capturing.
        /// Header-only variant retained for tests; the wire protocol requires the binary
        /// PCM16 body to follow the header — production callers use
        /// <see cref="SendAudioFrameAsync(int, byte[], float)"/>.
        /// </summary>
        public VoiceCommandResult SendAudioFrameAsync(int sequence, float energy = 0f)
        {
            return SendAudioFrameCore(sequence, null, energy);
        }

        /// <summary>
        /// Send one audio.frame for the current turn as an atomic pair: the JSON header
        /// immediately followed by one binary PCM16 frame. voice-ws-v1 requires exactly
        /// this pairing — a header without its binary body leaves the server in a pending
        /// state that invalidates the next control message.
        /// </summary>
        public VoiceCommandResult SendAudioFrameAsync(int sequence, byte[] pcm16Payload, float energy = 0f)
        {
            return SendAudioFrameCore(sequence, pcm16Payload, energy);
        }

        private VoiceCommandResult SendAudioFrameCore(int sequence, byte[] pcm16Payload, float energy)
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (_state != VoiceClientState.Capturing) return VoiceCommandResult.InvalidState;
            if (pcm16Payload != null && pcm16Payload.Length == 0)
            {
                return VoiceCommandResult.InvalidState;
            }
            if (sequence != _expectedAudioSeq)
            {
                Reject(VoiceCommandResult.SequenceMismatch);
                return VoiceCommandResult.SequenceMismatch;
            }
            _expectedAudioSeq++;

            var f = new AudioFrameMessage
            {
                type = "audio.frame",
                session_id = _sessionId,
                turn_id = _turnId,
                audio_stream_id = _audioStreamId,
                sequence = sequence,
                sample_rate = SampleRateHz,
                channels = Mono,
                timestamp_ms = sequence * FrameDurationMs,
                energy = energy
            };
            TrySendText(JsonUtility.ToJson(f));
            if (pcm16Payload != null)
            {
                TrySendBinary(pcm16Payload);
            }
            return VoiceCommandResult.Ok;
        }

        /// <summary>Capturing -> AwaitingAnswer; sends audio.end. Idempotent when already awaiting.</summary>
        public VoiceCommandResult RecordEndAsync()
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (_state == VoiceClientState.AwaitingAnswer) return VoiceCommandResult.Idempotent;
            if (_state != VoiceClientState.Capturing) return VoiceCommandResult.InvalidState;

            if (Transition(VoiceClientState.AwaitingAnswer) != VoiceStateTransitionCode.Ok)
            { TransitionFault(); return VoiceCommandResult.InvalidState; }

            var end = new AudioEndMessage
            {
                type = "audio.end",
                session_id = _sessionId,
                turn_id = _turnId,
                audio_stream_id = _audioStreamId
            };
            TrySendText(JsonUtility.ToJson(end));
            return VoiceCommandResult.Ok;
        }

        /// <summary>
        /// Cancel / barge-in from any active state -> Cancelling. FIXED ORDER (VR5-T12):
        /// (1) stop local resources — playback.Cancel() silences the queue, clears it and bumps the
        /// generation so any late frame / completion of the old generation is dropped by the playback
        /// layer; (2) wire behavior per state (VR9):
        ///   - Playing (server SPEAKING): send the voice-ws-v1 <c>session.cancel</c> and settle to
        ///     Ready on barge_in.accepted, with a local watchdog (CancelAckTimeoutMs) settling
        ///     locally if no ack arrives (network stall / server stall).
        ///   - anything else (server LISTENING / THINKING / connecting): cancel LOCALLY only. The
        ///     server's barge-in requires SPEAKING, so a session.cancel here is answered with
        ///     voice.error + close 4400 and would kill the connection. The abandoned turn is
        ///     retired server-side when the next session.start arrives (P0-6 retire path).
        /// This controller never sends <c>turn.cancel</c>. Idempotent while already Cancelling.
        /// </summary>
        public VoiceCommandResult CancelAsync()
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (_state == VoiceClientState.Cancelling) return VoiceCommandResult.Idempotent;

            bool wasPlaying = _state == VoiceClientState.Playing;

            var code = Transition(VoiceClientState.Cancelling);
            if (code == VoiceStateTransitionCode.Illegal || code == VoiceStateTransitionCode.SameState)
                return VoiceCommandResult.InvalidState;

            // (1) local stop / silence + queue clear + generation++ (owned by the playback layer).
            _playback?.Cancel();
            _ttsPairer = new TtsBinaryPairer(null); // drop any stale pending header/body pairing

            if (!wasPlaying)
            {
                // Server is LISTENING / THINKING / pre-start: session.cancel would be rejected
                // with voice.error + close 4400 (barge-in requires server-side SPEAKING).
                SettleCancelToReady();
                return VoiceCommandResult.Ok;
            }

            // (2) voice-ws-v1 session.cancel — the ONLY cancel this client sends.
            var c = new SessionCancelMessage
            {
                type = "session.cancel",
                session_id = _sessionId,
                turn_id = _turnId,
                audio_stream_id = _audioStreamId,
                feedback = "barge-in"
            };
            TrySendText(JsonUtility.ToJson(c));
            StartCancelWatchdog();
            return VoiceCommandResult.Ok;
        }

        /// <summary>
        /// Settle a cancel in flight: close the CURRENT TURN locally and return to a quiescent
        /// state. VR9: the server keeps the connection alive after a barge-in (it waits for the
        /// next session.start), so Ready — not Idle — is the correct settled state: the next
        /// RecordStartAsync sends a fresh session.start with a new turn id and the server
        /// retires the interrupted turn (websocket_server._retire_turn, the P0-6 multi-turn
        /// path). Late messages for the old turn still parse against the old-turn binding and
        /// are rejected downstream (StaleTurn / LateTts), never applied.
        /// </summary>
        private void SettleCancelToReady()
        {
            if (_state != VoiceClientState.Cancelling) return;
            _answered = false;
            _playbackCompleted = false;
            _expectedTtsSeq = -1;
            _playbackId = null;
            _binding = _sessionId == null ? null : new VoiceBinding
            {
                SessionId = _sessionId,
                TurnId = _turnId,
                AudioStreamId = null,
                PlaybackId = null
            };
            // A session that never started (cancel during HealthChecking/Connecting before the
            // first session.started) settles to Idle, not Ready.
            if (_sessionId == null)
                Transition(VoiceClientState.Idle);
            else
                Transition(VoiceClientState.Ready);
            _turnActive = false;
            _sessionStartSent = false;
            _expectedAudioSeq = -1;
        }

        private void StartCancelWatchdog()
        {
            if (_disposed) return;
            if (_cancelWatchdogTask != null && !_cancelWatchdogTask.IsCompleted) return;
            _cancelWatchdogTask = CancelWatchdogAsync();
        }

        /// <summary>
        /// VR9 fallback: if no barge_in.accepted arrives within CancelAckTimeoutMs (network
        /// stall, server stall), settle the cancel locally instead of stranding the controller
        /// in Cancelling until the server's 120s idle timeout faults the session.
        /// </summary>
        private async Task CancelWatchdogAsync()
        {
            try { await CancelAckDelayAsync(_config.CancelAckTimeoutMs); }
            catch { return; }
            if (_disposed || _state != VoiceClientState.Cancelling) return;
            Debug.LogWarning("[Yilan.Voice] cancel 看门狗触发："
                + _config.CancelAckTimeoutMs + "ms 内未收到 barge_in.accepted，本地收口取消");
            SettleCancelToReady();
        }

        /// <summary>Explicit reconnect: active -> Reconnecting -> Connecting, then re-open the socket.</summary>
        public async Task<VoiceCommandResult> ReconnectAsync()
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (_state == VoiceClientState.Reconnecting) return VoiceCommandResult.Idempotent;
            if (!VoiceStateRules.IsActive(_state)) return VoiceCommandResult.InvalidState;

            if (Transition(VoiceClientState.Reconnecting) != VoiceStateTransitionCode.Ok)
                return VoiceCommandResult.InvalidState;
            if (Transition(VoiceClientState.Connecting) != VoiceStateTransitionCode.Ok)
            { TransitionFault(); return VoiceCommandResult.TransportFailed; }

            _sessionStartSent = false;
            _turnActive = false;
            try
            {
                await _socket.ConnectAsync(_config.BuildWsUri(), _config.ConnectTimeoutMs).ConfigureAwait(false);
            }
            catch
            {
                TransitionFault();
                return VoiceCommandResult.TransportFailed;
            }
            return VoiceCommandResult.Ok;
        }

        /// <summary>Faulted -> Idle reset. Only legal from Faulted.</summary>
        public VoiceCommandResult Reset()
        {
            if (_state != VoiceClientState.Faulted) return VoiceCommandResult.InvalidState;
            Transition(VoiceClientState.Idle);
            _sessionId = null;
            _turnId = null;
            _audioStreamId = null;
            _playbackId = null;
            _binding = null;
            _turnCounter = 0;
            _expectedAudioSeq = -1;
            _expectedTtsSeq = -1;
            _sessionStartSent = false;
            _turnActive = false;
            _answered = false;
            _playbackCompleted = false;
            _ttsPairer = new TtsBinaryPairer(null);
            return VoiceCommandResult.Ok;
        }

        // =============================== Lifecycle: pause / resume ===============================

        /// <summary>
        /// Lifecycle pause (app backgrounded / user toggle): stop local playback (silence + queue
        /// clear + generation++), close the socket, and settle the session to Idle. This is a LOCAL
        /// teardown — unlike <see cref="CancelAsync"/> no session.cancel is sent; the server observes
        /// the closed socket and releases the session binding on the next connect.
        /// </summary>
        public async Task<VoiceCommandResult> PauseAsync()
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (!VoiceStateRules.IsActive(_state)) return VoiceCommandResult.InvalidState;

            _playback?.Cancel();                    // stop playback / silence / clear queue
            _ttsPairer = new TtsBinaryPairer(null); // drop any pending header/body pair
            if (_socket != null)
            {
                try { await _socket.CloseAsync(1000, "pause"); } catch { }
            }
            // Route active -> Cancelling -> Idle (the two legal edges to a quiescent state).
            Transition(VoiceClientState.Cancelling);
            Transition(VoiceClientState.Idle);
            _sessionStartSent = false;
            _turnActive = false;
            _answered = false;
            _playbackCompleted = false;
            return VoiceCommandResult.Ok;
        }

        /// <summary>
        /// Resume after <see cref="PauseAsync"/>. The controller has no access to the permission or
        /// capture layers (harness whitelist), so resuming NEVER auto-requests RECORD_AUDIO and NEVER
        /// auto-opens the microphone; it only reports the controller is back in a connectable Idle
        /// state — the caller decides whether to ConnectAsync again.
        /// </summary>
        public VoiceCommandResult Resume()
        {
            if (_state == VoiceClientState.Faulted) return VoiceCommandResult.Faulted;
            if (_state != VoiceClientState.Idle) return VoiceCommandResult.InvalidState;
            return VoiceCommandResult.Ok; // Idle; caller may now explicitly ConnectAsync
        }

        // =============================== Bounded auto-reconnect ===============================

        /// <summary>Begin a bounded automatic reconnect cycle after a recoverable transport drop.</summary>
        private void StartAutoReconnect()
        {
            if (_disposed) return;
            if (_autoReconnectTask != null && !_autoReconnectTask.IsCompleted) return; // one cycle at a time

            if (Transition(VoiceClientState.Reconnecting) != VoiceStateTransitionCode.Ok)
            {
                TransitionFault();
                return;
            }
            // Re-establish a FRESH session binding: OnOpened will send a new session.start with a
            // new turn id, and the server's voice.state(session.started) must be accepted with a
            // clean (null) binding — exactly like the initial connect. Clear round/playback state so
            // no stale turn, answer or pending TTS body leaks into the recovered session, and
            // silence any audio left queued from the interrupted round.
            _playback?.Cancel();
            _sessionStartSent = false;
            _turnActive = false;
            _answered = false;
            _playbackCompleted = false;
            _binding = null;
            _expectedAudioSeq = -1;
            _expectedTtsSeq = -1;
            _ttsPairer = new TtsBinaryPairer(null);

            _autoReconnectTask = AutoReconnectAsync();
        }

        /// <summary>
        /// Bounded automatic reconnect: up to <see cref="ReconnectPolicy.MaxAttempts"/> re-open
        /// attempts with backoff 1/2/4 s (from <see cref="ReconnectPolicy"/>). Only network-shaped
        /// failures reach this loop (the decision was already made in <see cref="OnClosed"/>); the
        /// reconnect itself never changes the wire protocol and never sends turn.cancel. Every step
        /// is exception-contained; exhaustion ends in Faulted. A successful re-open waits for the
        /// server's fresh voice.state(session.started, voice-ws-v1) to drive Connecting -> Ready.
        /// </summary>
        private async Task AutoReconnectAsync()
        {
            for (int attempt = 1; attempt <= ReconnectPolicy.MaxAttempts; attempt++)
            {
                if (_disposed || _state != VoiceClientState.Reconnecting)
                    return; // a pause / explicit cancel / user fault interrupted the cycle

                try { await ReconnectDelayAsync(ReconnectPolicy.DelayMsForAttempt(attempt)); }
                catch { TransitionFault(); return; }

                if (_disposed || _state != VoiceClientState.Reconnecting)
                    return;

                if (Transition(VoiceClientState.Connecting) != VoiceStateTransitionCode.Ok)
                {
                    TransitionFault();
                    return;
                }
                _sessionStartSent = false;
                _turnActive = false;

                bool opened;
                try
                {
                    await _socket.ConnectAsync(_config.BuildWsUri(), _config.ConnectTimeoutMs).ConfigureAwait(false);
                    opened = true;
                }
                catch
                {
                    opened = false;
                    Reject(VoiceCommandResult.TransportFailed);
                }

                if (opened)
                {
                    // OnOpened dispatches session.start; the server's v1 started then drives
                    // Connecting -> Ready. The cycle stops here; a later drop starts a new one.
                    return;
                }
                // Connect failed: stay Reconnecting and back off to the next attempt.
                if (_state == VoiceClientState.Connecting)
                    Transition(VoiceClientState.Reconnecting);
            }
            // Budget exhausted without a successful re-open.
            TransitionFault();
        }

        // =============================== Events ===============================

        private void OnOpened()
        {
            if (_disposed) return;
            // While connecting (initial or after reconnect), establish the session binding.
            if ((_state == VoiceClientState.Connecting || _state == VoiceClientState.Reconnecting) && !_sessionStartSent)
            {
                _turnCounter++;
                _turnId = "turn-" + _turnCounter.ToString("D4");
                _audioStreamId = "stream-audio-" + _turnCounter;
                // P1-5 regression: a fresh connect invalidates any binding left over from
                // the previous connection (PauseAsync -> ConnectAsync, or a clean server
                // close followed by an explicit reconnect): the new session.start carries an
                // ADVANCED turn id, so the stale binding would reject the server's
                // session.started (turn_id mismatch) and strand the client in Connecting.
                // Drop it — session.started re-establishes the binding (HandleVoiceState),
                // exactly like the initial connect and StartAutoReconnect's reset.
                _binding = null;
                _playbackId = null;
                SendSessionStart();
                _turnActive = true;
            }
        }

        private void OnError(VoiceSocketError error)
        {
            if (_disposed) return;
            // A recoverable transport error kind (Connect / Timeout) triggers the same bounded
            // automatic reconnect as a recoverable close; every other error is terminal.
            if (VoiceStateRules.IsActive(_state) &&
                ReconnectPolicy.ShouldReconnect(error.Kind) == ReconnectVerdict.Retry)
            {
                StartAutoReconnect();
                return;
            }
            TransitionFault();
        }

        private void OnClosed(VoiceCloseInfo info)
        {
            if (_disposed) return;
            if (info.Category == VoiceCloseCategory.Normal)
            {
                // Clean server close -> settle to Idle if we were mid-session.
                // The state table has no active -> Idle edge, so route through the
                // two legal edges: active -> Cancelling -> Idle.
                if (VoiceStateRules.IsActive(_state))
                {
                    _playback?.Cancel(); // silent local stop on a server-requested clean close
                    Transition(VoiceClientState.Cancelling);
                    Transition(VoiceClientState.Idle);
                }
                return;
            }
            // VR10: 4408 VOICE_SESSION_TIMEOUT is the server reaping an idle/expired session,
            // not a protocol violation — a fresh ConnectAsync opens a new session cleanly, so
            // settle to Idle (the same legal two-hop the Normal close uses) with a UI hint
            // instead of faulting. Other Application codes (4400/4401) stay terminal below.
            if (VoiceStateRules.IsActive(_state) && info.Code == 4408)
            {
                _playback?.Cancel(); // silent local stop; no TTS will arrive on a dead socket
                Transition(VoiceClientState.Cancelling);
                Transition(VoiceClientState.Idle);
                SessionExpired?.Invoke();
                return;
            }
            // A recoverable transport drop triggers a BOUNDED automatic reconnect (backoff 1/2/4 s,
            // at most ReconnectPolicy.MaxAttempts attempts). Protocol / policy / server-defined
            // application close codes are never retried — a re-open cannot fix them.
            if (VoiceStateRules.IsActive(_state) &&
                ReconnectPolicy.ShouldReconnect(info.Category) == ReconnectVerdict.Retry)
            {
                StartAutoReconnect();
                return;
            }
            // Any other abnormal close across an established session is a transport fault.
            if (VoiceStateRules.IsActive(_state))
                TransitionFault();
        }

        private void OnPayload(byte[] payload)
        {
            if (_disposed) return;
            string json;
            try { json = Encoding.UTF8.GetString(payload); }
            catch { TransitionFault(); return; }

            var parse = VoiceProtocolParser.Parse(json, _binding);
            if (parse.IsOk)
            {
                HandleMessage(parse.Message);
                return;
            }

            // A wire payload that is not a parseable voice-ws-v1 text envelope is most likely the
            // MP3 binary body of a tts.frame. Pair it with the pending header (fed by HandleTts at
            // the accept point) and deliver header + body to the playback queue. Both halves are
            // validated/gated by the parser and state machine, so id checks are not duplicated here.
            // Deciding by the pairer's pending state (not by Playing/playbackCompleted) also covers
            // the FINAL frame: its body arrives after HandleTts(is_final) has already transitioned
            // the controller to Ready, but the accepted final header is still pending in the pairer.
            if (_playback != null && _ttsPairer.HasPending)
            {
                var pair = _ttsPairer.OnBinary(payload);
                if (pair.Code == TtsPairCode.HeaderConsumed && pair.Header != null)
                {
                    _playback.EnqueueTts(pair.Header, payload);
                    return;
                }
            }

            switch (parse.Code)
            {
                case VoiceParseCode.VersionMismatch:
                    // A started status without voice-ws-v1 -> protocol fault.
                    Reject(VoiceCommandResult.OtherSession);
                    TransitionFault();
                    return;
                case VoiceParseCode.BindingMismatch:
                    HandleBindingMismatch(json);
                    return;
                default:
                    // Malformed / unknown stray frame: ignore, no state change.
                    return;
            }
        }

        private void HandleBindingMismatch(string json)
        {
            // Distinguish an other-session message from a same-session message by re-reading
            // the ids. A message with the SAME session AND the CURRENT turn is a late/duplicate
            // message for the live turn: the parse can only have failed on a slot that this
            // downstream type does not carry (e.g. playback_id is absent from answer.display /
            // voice.state), so classify it as late, never as stale.
            var probe = JsonUtility.FromJson<IdProbe>(json);
            if (probe != null && _sessionId != null && probe.session_id != _sessionId)
            {
                Reject(VoiceCommandResult.OtherSession);
                return;
            }

            bool sameLiveTurn = probe != null && _turnId != null && probe.turn_id == _turnId;
            if (sameLiveTurn)
            {
                Reject(LateClassification(json));
                return;
            }
            Reject(VoiceCommandResult.StaleTurn);
        }

        /// <summary>Classify a same-session, same-current-turn binding mismatch as late.</summary>
        private VoiceCommandResult LateClassification(string json)
        {
            var env = JsonUtility.FromJson<VoiceEnvelope>(json);
            if (env == null) return VoiceCommandResult.LateAnswer;
            switch (env.type)
            {
                case "tts.frame":
                    return _playbackCompleted ? VoiceCommandResult.LateTts : VoiceCommandResult.LateAnswer;
                default:
                    // answer.display / voice.state / clarification for the live turn, already applied
                    return VoiceCommandResult.LateAnswer;
            }
        }

        [Serializable]
        private class IdProbe
        {
            public string session_id;
            public string turn_id;
        }

        private void HandleMessage(VoiceEnvelope msg)
        {
            switch (msg.type)
            {
                case "voice.state": HandleVoiceState((VoiceStateMessage)msg); break;
                case "answer.display": HandleAnswer((AnswerDisplayMessage)msg); break;
                case "tts.frame": HandleTts((TtsFrameMessage)msg); break;
                case "clarification.required": HandleClarification((ClarificationRequiredMessage)msg); break;
                case "asr.partial":
                case "asr.final": HandleAsrTranscript((AsrTranscriptMessage)msg); break;
                case "barge_in.accepted": HandleBargeInAccepted((BargeInAcceptedMessage)msg); break;
                case "voice.error": HandleVoiceError((VoiceErrorMessage)msg); break;
                // client->server types (session.start/audio.frame/audio.end/session.cancel): ignore upstream
            }
        }

        /// <summary>
        /// VR10: voice.error is classified by code. VOICE_TTS_ERROR after answer.display is the
        /// server's documented display-only degradation (websocket_server._publish_result /
        /// _watch_playback; contract: test_tts_failure_degradation) — the connection stays open
        /// and the text answer is already on screen. Settle to Ready instead of faulting so the
        /// user can immediately ask the next question. Every other code is terminal as before.
        /// </summary>
        private void HandleVoiceError(VoiceErrorMessage m)
        {
            bool ttsDegradation = m.code == "VOICE_TTS_ERROR" && _answered;
            if (ttsDegradation)
            {
                _playback?.Cancel(); // no TTS will arrive; silence any queued frames
                _playbackCompleted = true;
                if (_state == VoiceClientState.Playing) Transition(VoiceClientState.Ready);
                TtsDegraded?.Invoke();
                return;
            }
            Reject(VoiceCommandResult.InvalidState);
            TransitionFault();
        }

        private void HandleVoiceState(VoiceStateMessage m)
        {
            if (m.status != "session.started") return; // informational states: no mandatory transition

            // Ready is ONLY entered from Connecting upon a real v1 started.
            if (_state != VoiceClientState.Connecting)
            {
                Reject(VoiceCommandResult.StaleTurn);
                return;
            }

            _sessionId = m.session_id;
            _turnId = m.turn_id;
            _audioStreamId = _audioStreamId ?? "stream-audio-1";
            // Downstream messages carry no audio_stream_id, so the binding slot stays null
            // (see RecordStartAsync note); VoiceProtocolParser.CheckIds then skips it.
            _binding = new VoiceBinding
            {
                SessionId = m.session_id,
                TurnId = m.turn_id,
                AudioStreamId = null,
                PlaybackId = null
            };
            _turnActive = true;
            _expectedAudioSeq = 0;
            Transition(VoiceClientState.Ready);
        }

        private void HandleAnswer(AnswerDisplayMessage m)
        {
            if (_answered) { Reject(VoiceCommandResult.LateAnswer); return; }
            // P1-1: the server VAD endpoint may consume the utterance and publish
            // answer.display while the push-to-talk user is STILL holding the button
            // (client Capturing). The answer is authoritative for the live turn —
            // accept it from Capturing as well as AwaitingAnswer. After the move to
            // Playing, trailing mic frames are refused client-side (not Capturing
            // anymore) and dropped server-side by the trailing-frame guard; the
            // later audio.end / RecordEndAsync is a harmless no-op on the server.
            if (_state != VoiceClientState.AwaitingAnswer && _state != VoiceClientState.Capturing)
            {
                Reject(VoiceCommandResult.StaleTurn);
                return;
            }
            _answered = true;
            _playbackCompleted = false;
            // The server owns playback_id generation (voice-ws-v1: each tts.frame carries a
            // server-issued playback id). The client must NOT fabricate a local "playback-N":
            // that id never matches the server's, so every tts.frame is rejected as a binding
            // mismatch and the client deadlocks in Playing. The id is LEARNED from the first
            // tts.frame of this round in HandleTts.
            _playbackId = null;
            _expectedTtsSeq = 0;
            _binding = new VoiceBinding
            {
                SessionId = _sessionId,
                TurnId = _turnId,
                AudioStreamId = null,
                PlaybackId = null
            };
            Transition(VoiceClientState.Playing);
            AnswerReceived?.Invoke(m.answer?.content ?? string.Empty);
        }

        private void HandleTts(TtsFrameMessage m)
        {
            if (_playbackCompleted) { Reject(VoiceCommandResult.LateTts); return; }
            if (!_answered || _state != VoiceClientState.Playing) { Reject(VoiceCommandResult.StaleTurn); return; }

            // Learn the server-issued playback id from the first tts.frame of this round:
            // answer.display carries no playback_id, so until now the binding's playback
            // slot was null (CheckIds skips a null slot). After learning, subsequent
            // frames are validated against the same playback id — a different id
            // mid-round is a binding mismatch and classified late (anti cross-round bleed).
            if (_playbackId == null)
            {
                _playbackId = m.playback_id;
                _binding = new VoiceBinding
                {
                    SessionId = _sessionId,
                    TurnId = _turnId,
                    AudioStreamId = null,
                    PlaybackId = _playbackId
                };
            }

            if (m.sequence != _expectedTtsSeq)
            {
                Reject(VoiceCommandResult.SequenceMismatch);
                TransitionFault();
                return;
            }
            _expectedTtsSeq++;
            // Register the header for the immediately-following MP3 binary body, so OnPayload can
            // pair them and hand (header, body) to the playback queue. Deliberately kept across
            // is_final (the body for the final frame arrives right after the header).
            _ttsPairer.OnTextHeader(m);
            TtsFrameAccepted?.Invoke(m);

            if (m.is_final)
            {
                _playbackCompleted = true;
                _answered = false;
                _expectedTtsSeq = -1;
                _playbackId = null;
                _binding = new VoiceBinding
                {
                    SessionId = _sessionId,
                    TurnId = _turnId,
                    AudioStreamId = null,
                    PlaybackId = null
                };
                Transition(VoiceClientState.Ready);
                _turnActive = false;
                // Reset turn/session state so a SECOND round starts a fresh
                // session.start (new turn id) and restarts audio sequencing at 0.
                _sessionStartSent = false;
                _expectedAudioSeq = -1;
            }
        }

        private void HandleClarification(ClarificationRequiredMessage m)
        {
            // VRR3 / P1-6: surface the server's clarification request to the UI projection.
            // Only a live turn clarifies; anything else is stale. Accepted both from
            // AwaitingAnswer and — P1-1, same early-endpoint family — from Capturing:
            // a low-confidence VAD endpoint can raise clarification.required while the
            // user is still holding the record button. The controller deliberately
            // STAYS in its current state (design decision: the recovery policy —
            // re-record on a new turn vs cancel/reconnect — belongs to the caller).
            if (_state != VoiceClientState.AwaitingAnswer && _state != VoiceClientState.Capturing)
            {
                Reject(VoiceCommandResult.StaleTurn);
                return;
            }
            ClarificationRequired?.Invoke(m.reason ?? string.Empty);
        }

        private void HandleAsrTranscript(AsrTranscriptMessage m)
        {
            // Straggler guard: a transcript for an already-closed round (is_final tts already
            // reset the turn) must not bleed into the next round's subtitle projection.
            if (!_turnActive) { Reject(VoiceCommandResult.LateAnswer); return; }
            // Streaming/final transcript for the bound turn. Informational by design: the turn
            // stays in its current state (typically AwaitingAnswer, or Capturing under the
            // server-side early VAD endpoint) until answer.display or clarification.required
            // decides it. The parser already enforced the binding.
            TranscriptReceived?.Invoke(m);
        }

        private void HandleBargeInAccepted(BargeInAcceptedMessage m)
        {
            // Server-side acknowledgement of our session.cancel. The local cancel path already
            // stopped capture/playback before this arrives; VR9: the ack settles the controller
            // out of Cancelling back to Ready (the state-table edge "Cancelling -> Ready
            // (barge-in done)" finally executed) so the user can start the next round without a
            // reconnect. A late ack after the watchdog already settled is a no-op (state check).
            if (_state == VoiceClientState.Cancelling)
                SettleCancelToReady();
            BargeInAccepted?.Invoke(m);
        }

        private void SendSessionStart()
        {
            if (_sessionStartSent) return;
            var start = new SessionStartMessage
            {
                type = "session.start",
                session_id = _sessionId,
                turn_id = _turnId,
                audio_stream_id = _audioStreamId
            };
            _sessionStartSent = true;
            _expectedAudioSeq = 0;
            TrySendText(JsonUtility.ToJson(start));
        }

        /// <summary>Random, wire-legal session id (server _ID_PATTERN: alnum first char,
        /// then [A-Za-z0-9._:-]{0,127}). Two controllers on different devices never collide.</summary>
        private static string NewSessionId()
        {
            return "vr-session-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private void TrySendText(string json)
        {
            if (_socket == null || !_socket.IsOpen)
            {
                // §12.3 fix: a skipped send must be VISIBLE — a dropped audio.end
                // here strands the turn in AwaitingAnswer with no explanation.
                // Warning only, no TransitionFault: the socket's close event
                // (already queued) owns the reconnect/fault decision; faulting in
                // this window would race and kill a recovery that would succeed.
                Debug.LogWarning("[Yilan.Voice] TrySendText SILENT-SKIP not open — 消息已丢弃，等待断连/重连事件收口");
                return;
            }
            try
            {
                TrackSendAsync("text", _socket.SendTextAsync(json, _config.SendTimeoutMs));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Yilan.Voice] TrySendText THREW " + ex.GetType().Name);
                TransitionFault();
            }
        }

        private async Task TrackSendAsync(string kind, Task t)
        {
            try
            {
                await t;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Yilan.Voice] {kind} send EX {ex.GetType().Name} — 发送失败，该消息未到达服务端");
            }
        }

        private void TrySendBinary(byte[] data)
        {
            if (_socket == null || !_socket.IsOpen)
            {
                // §12.3 fix: same rationale as TrySendText — visible, not fatal.
                Debug.LogWarning("[Yilan.Voice] TrySendBinary SILENT-SKIP not open — 音频帧已丢弃，等待断连/重连事件收口");
                return;
            }
            try
            {
                TrackSendAsync("binary", _socket.SendBinaryAsync(data, _config.SendTimeoutMs));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Yilan.Voice] TrySendBinary THREW " + ex.GetType().Name);
                TransitionFault();
            }
        }

        private VoiceStateTransitionCode Transition(VoiceClientState to)
        {
            var code = VoiceStateRules.TryTransition(_state, to);
            if (code == VoiceStateTransitionCode.Ok)
            {
                _state = to;
                StateChanged?.Invoke(to);
            }
            return code;
        }

        private void TransitionFault()
        {
            if (_state == VoiceClientState.Faulted) return;
            _state = VoiceClientState.Faulted;
            StateChanged?.Invoke(VoiceClientState.Faulted);
        }

        private void Reject(VoiceCommandResult code)
        {
            LastRejection = code;
            // §12.3 fix: rejections were invisible — an answer.display dropped for
            // a binding mismatch left the UI silently stuck in AwaitingAnswer while
            // the server believed it had answered. Surface every rejection with its
            // classification and live turn so Console and server logs correlate.
            Debug.LogWarning($"[Yilan.Voice] 消息被拒收: {code} (state={_state}, turn={_turnId}, session={_sessionId})");
            MessageRejected?.Invoke(code);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_socket != null)
            {
                _socket.Opened -= OnOpened;
                _socket.Payload -= OnPayload;
                _socket.Error -= OnError;
                _socket.Closed -= OnClosed;
            }
        }
    }
}
