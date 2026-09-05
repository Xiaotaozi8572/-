using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Protocol;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Transport;

namespace Yilan.Voice.Session
{
    /// <summary>
    /// EditMode tests for the voice session state machine (VR2-T07). The controller is driven
    /// against <see cref="FakeVoiceSocket"/> (a deterministic, main-thread-pumped transport
    /// double). Gold samples for the wire frames are replayed as text payloads.
    ///
    /// Coverage: legal full chain Idle->...->Playing->Ready; illegal transition rejection;
    /// duplicate start/end/cancel idempotency; stale-round / other-session / late-message
    /// rejection; non-v1 started -> Faulted; audio/tts sequence out-of-order rejection.
    ///
    /// P0-3 (phase 4): playback_id is SERVER-issued (voice-ws-v1 tts.frame) and learned by the
    /// controller from the first tts.frame of a round; tests therefore pass explicit server-format
    /// playback ids ("playback_server1") instead of the retired local "playback-N" fabrication.
    /// </summary>
    [TestFixture]
    public class VoiceSessionControllerTests
    {
        private static readonly VoiceClientConfig Cfg = new VoiceClientConfig
        {
            WsHost = "voice.test",
            WsPort = 443,
            UseWss = true,
            WsPath = "/ws/voice/session",
            ConnectTimeoutMs = 1000,
            SendTimeoutMs = 1000
        };

        // ---- Payload builders (project typed DTOs to wire JSON via JsonUtility) ----

        private static string StartedJson(string sessionId, string turnId)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "listening",
                status = "session.started",
                protocol_version = VoiceProtocolParser.WireVersion
            };
            return JsonUtility.ToJson(m);
        }

        private static string StartedJsonNonV1(string sessionId, string turnId)
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "listening",
                status = "session.started",
                protocol_version = "voice-ws-v0"
            };
            return JsonUtility.ToJson(m);
        }

        private static string AnswerJson(string sessionId, string turnId)
        {
            var m = new AnswerDisplayMessage
            {
                type = "answer.display",
                session_id = sessionId,
                turn_id = turnId,
                answer_id = "ans-1",
                evidence_package_id = "ev-1",
                answer = new PublicAnswer { content = "你好", source = "rag" }
            };
            return JsonUtility.ToJson(m);
        }

        private static string TtsJson(string sessionId, string turnId, string playbackId, int seq, bool isFinal)
        {
            var m = new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = sessionId,
                turn_id = turnId,
                playback_id = playbackId,
                sequence = seq,
                codec = "pcm16",
                duration_ms = 100,
                is_final = isFinal
            };
            return JsonUtility.ToJson(m);
        }

        private static string AsrJson(string sessionId, string turnId, string type, string transcript)
        {
            var m = new AsrTranscriptMessage
            {
                type = type,
                session_id = sessionId,
                turn_id = turnId,
                transcript = transcript,
                confidence = 0.82f,
                provider = "whisper"
            };
            return JsonUtility.ToJson(m);
        }

        private static string BargeInJson(string sessionId, string turnId, string status, string intent)
        {
            var m = new BargeInAcceptedMessage
            {
                type = "barge_in.accepted",
                session_id = sessionId,
                turn_id = turnId,
                status = status,
                intent = intent
            };
            return JsonUtility.ToJson(m);
        }

        private static string ErrorJson(string sessionId, string turnId, string code)
        {
            // VoiceErrorMessage declares only code/field beyond the envelope, so the
            // wire JSON is built by hand to mirror the server payload (ids included).
            return $"{{\"type\":\"voice.error\",\"session_id\":\"{sessionId}\"," +
                   $"\"turn_id\":\"{turnId}\",\"code\":\"{code}\",\"field\":\"tts\"}}";
        }

        // ---- Harness helpers ----

        private static (VoiceSessionController controller, FakeVoiceSocket socket) NewController()
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var controller = new VoiceSessionController(Cfg, socket);
            return (controller, socket);
        }

        /// <summary>Connect and pump so the socket opens, session.start is sent, then replay
        /// the server session.started(v1) to reach Ready. Returns the controller.</summary>
        private static VoiceSessionController ToReady(out FakeVoiceSocket socket)
        {
            var (c, s) = NewController();
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            Assert.AreEqual(VoiceClientState.Connecting, c.State);
            s.DispatchMessageQueue(); // delivers Opened -> session.start sent
            Assert.AreEqual(1, s.SentTexts.Count); // session.start dispatched
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            socket = s;
            return c;
        }

        // =============================== Legal full chain ===============================

        [Test]
        public void FullChain_Legal_IdleToPlayingBackToReady()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);

            // Monotonic audio sequence 0,1,2 all accepted in order.
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, 0.35f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(1, 0.12f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(2, 0.05f));

            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // TTS monotonic 0,1(final) with a SERVER-format playback id (uuid style).
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, false)));
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 1, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            s.Dispose();
        }

        [Test]
        public void VoiceError_TtsAfterAnswer_SettlesReady_NotFaulted()
        {
            // VR10: VOICE_TTS_ERROR after answer.display is the server's display-only
            // degradation (connection stays open, text on screen) — settle to Ready.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            int degraded = 0;
            c.TtsDegraded += () => degraded++;

            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_TTS_ERROR")));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Ready, c.State, "TTS degradation must settle to Ready");
            Assert.AreNotEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual(1, degraded, "TtsDegraded must fire exactly once");

            s.Dispose();
        }

        [Test]
        public void VoiceError_TtsBeforeAnswer_StillFaulted()
        {
            // Regression guard: TTS failure BEFORE any answer means the user got
            // nothing — still a terminal fault.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            int degraded = 0;
            c.TtsDegraded += () => degraded++;

            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_TTS_ERROR")));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual(0, degraded);

            s.Dispose();
        }

        [Test]
        public void VoiceError_OtherCode_StillFaulted()
        {
            // Regression guard: every non-degradation code stays terminal.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(ErrorJson(c.SessionId, c.TurnId, "VOICE_PROTOCOL_INVALID")));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Faulted, c.State);

            s.Dispose();
        }

        [Test]
        public void Closed_4408_ActiveState_SettlesIdle_NotFaulted()
        {
            // VR10: 4408 VOICE_SESSION_TIMEOUT is the server reaping an idle/expired
            // session, not a protocol violation — settle to Idle (via the legal
            // Cancelling two-hop) with the SessionExpired hint instead of faulting.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            int expired = 0;
            c.SessionExpired += () => expired++;

            s.SimulateServerClose(4408, "voice session timeout");
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Idle, c.State, "4408 must settle back to Idle");
            Assert.AreNotEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual(1, expired, "SessionExpired must fire exactly once");

            s.Dispose();
        }

        [Test]
        public void Closed_4400_StillFaulted()
        {
            // Regression guard: protocol violations (4400/4401 Application codes) stay
            // terminal — a re-open cannot fix them.
            var c = ToReady(out var s);

            int expired = 0;
            c.SessionExpired += () => expired++;

            s.SimulateServerClose(4400, "voice error close");
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.AreEqual(0, expired);

            s.Dispose();
        }

        [Test]
        public void Reset_FromFaulted_GoesIdle_ThenCanReconnect()
        {
            // VR10: the Faulted exit — Reset() clears per-session state, then a fresh
            // ConnectAsync reaches Ready on a NEW session id (the dead one is gone).
            var c = ToReady(out var s);
            var deadSession = c.SessionId;

            s.SimulateServerClose(4400, "voice error close");
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, c.State);

            Assert.AreEqual(VoiceCommandResult.Ok, c.Reset());
            Assert.AreEqual(VoiceClientState.Idle, c.State);

            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreNotEqual(deadSession, c.SessionId, "Reset must discard the dead session id");

            s.Dispose();
        }

        [Test]
        public void SendAudioFrame_WithPayload_SendsHeaderThenBinaryAtomically()
        {
            // P0-1 regression: the wire protocol pairs every audio.frame JSON header
            // with exactly one binary PCM16 body; a header-only send leaves the
            // server's pending-header state armed and invalidates the next message.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            Assert.AreEqual(0, c.NextAudioSequence);

            var pcm = new byte[640];
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xff);

            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, pcm, 0.5f));

            Assert.AreEqual(2, s.SentTexts.Count, "session.start + one audio.frame header");
            StringAssert.Contains("\"type\":\"audio.frame\"", s.SentTexts[1]);
            StringAssert.Contains("\"sequence\":0", s.SentTexts[1]);
            Assert.AreEqual(1, s.SentBinaries.Count, "exactly one binary body must follow the header");
            Assert.AreEqual(640, s.SentBinaries[0].Length);
            CollectionAssert.AreEqual(pcm, s.SentBinaries[0]);
            Assert.AreEqual(1, c.NextAudioSequence);

            // An empty payload is not a protocol frame and must be refused outright.
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.SendAudioFrameAsync(1, new byte[0], 0f));
            Assert.AreEqual(2, s.SentTexts.Count, "the rejected frame must not reach the wire");
            Assert.AreEqual(1, s.SentBinaries.Count);

            s.Dispose();
        }

        [Test]
        public void FullChain_TwoRounds_SecondRoundRestartsAudioSequence()
        {
            // Regression for the multi-turn gap: after a completed round returns to
            // Ready, a SECOND round must send a fresh session.start and restart the
            // audio sequence at 0 (not reject frame 0 as SequenceMismatch).
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            // ---- Round 1 ----
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, 0.30f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(1, 0.10f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, false)));
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 1, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            // ---- Round 2 ----
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            // Audio sequence must restart at 0 for the new turn.
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, 0.25f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(1, 0.05f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            // A NEW server playback id for the new round must be accepted (learned fresh).
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server2", 0, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            s.Dispose();
        }

        // ====================== P1-1 regression: VAD early endpoint while capturing ======================

        [Test]
        public void EarlyAnswer_DuringCapturing_IsAccepted_PlaysTtsAndAllowsNextRound()
        {
            // P1-1 regression: the server VAD endpoint consumed the utterance and
            // published answer.display while the push-to-talk user still HELD the
            // button (client Capturing). The answer must be ACCEPTED — the old code
            // rejected it as StaleTurn, losing the round and every tts.frame after.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);

            string gotAnswer = null;
            c.AnswerReceived += a => gotAnswer = a;

            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State, "early answer must move Capturing -> Playing");
            Assert.AreEqual("你好", gotAnswer, "the answer text must be projected");

            // Trailing mic frames are refused while Playing (client-side drop; the
            // server drops them too via its trailing-frame guard).
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.SendAudioFrameAsync(0, 0.5f));

            // TTS completes the round normally.
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            // The user finally releases the button: no crash, no fault, and a new
            // round can start on the same connection (P0-6 multi-turn).
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordEndAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);

            s.Dispose();
        }

        [Test]
        public void EarlyClarification_DuringCapturing_IsProjected_StateUnchanged()
        {
            // Same P1-1 family: a low-confidence early endpoint can raise
            // clarification.required while the client is still Capturing. The
            // event must reach the UI instead of being dropped as a stale turn;
            // clarification never mutates the state machine.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);

            string reason = null;
            c.ClarificationRequired += r => reason = r;

            var m = new ClarificationRequiredMessage
            {
                type = "clarification.required",
                session_id = c.SessionId,
                turn_id = c.TurnId,
                reason = "confidence_low"
            };
            s.SimulatePayload(Encoding.UTF8.GetBytes(JsonUtility.ToJson(m)));
            s.DispatchMessageQueue();

            Assert.AreEqual("confidence_low", reason, "the clarification reason must be projected");
            Assert.AreEqual(VoiceClientState.Capturing, c.State, "clarification never mutates state");

            s.Dispose();
        }

        // ====================== P1-5 regression: pause -> resume reconnect ======================

        [Test]
        public void PauseThenReconnect_ReusesSessionId_AndAdvancesTurnIds()
        {
            // P1-5 regression: a lifecycle pause closes the socket and settles the
            // controller to Idle with nobody driving a reconnect. When the caller
            // DOES reconnect (VoiceClientRuntime.ResumeAsync -> ConnectAsync), the
            // SAME session id must be reused (the server keeps the conversation
            // context) while turn ids keep ADVANCING: the server rejects any turn_id
            // already registered in finished_turn_ids, so resetting the counter to 0
            // would resend "turn-0001" and the session.start would be refused.
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            string sessionBefore = c.SessionId;

            // One finished round consumed turn-0001 (realistic pause point).
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            // Lifecycle pause: local teardown (socket close 1000) -> Idle.
            Assert.AreEqual(VoiceCommandResult.Ok, c.PauseAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Idle, c.State);

            // Resume reconnect from Idle.
            Assert.AreEqual(VoiceCommandResult.Ok, c.ConnectAsync().GetAwaiter().GetResult());
            s.DispatchMessageQueue(); // Opened -> fresh session.start

            Assert.AreEqual(sessionBefore, c.SessionId, "the session id must survive the pause");
            StringAssert.Contains("turn-0002", s.SentTexts[s.SentTexts.Count - 1],
                "the reconnect session.start must carry an ADVANCED turn id (the server forbids reuse)");

            // The server accepts the fresh binding and the client is Ready again.
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            s.Dispose();
        }

        // ====================== P0-3 regression: server-issued playback id ======================

        [Test]
        public void ServerGeneratedPlaybackId_IsLearnedFromFirstTtsFrame()
        {
            // P0-3 regression: the client must accept a SERVER-format playback id
            // (e.g. "playback_ab12cd34ef56" from new_id("playback")) instead of
            // fabricating a local "playback-N" that never matches the server's.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            Assert.IsNull(c.PlaybackId, "no playback id is fabricated at answer time anymore");

            // First server frame carries the server-issued id -> learned and bound.
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_ab12cd34ef56", 0, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual("playback_ab12cd34ef56", c.PlaybackId,
                "the server-issued playback id must be learned from the first tts.frame");
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // The final frame with the SAME id completes the round.
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_ab12cd34ef56", 1, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            s.Dispose();
        }

        [Test]
        public void MidRoundPlaybackIdSwitch_IsRejectedAsLate()
        {
            // After learning a playback id, a frame with a DIFFERENT id mid-round is a
            // binding mismatch for the live turn -> late classification (anti bleed).
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, false)));
            s.DispatchMessageQueue();

            s.SimulatePayload(Encoding.UTF8.GetBytes(
                TtsJson(c.SessionId, c.TurnId, "playback_intruder", 1, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.LateAnswer, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Playing, c.State, "a foreign playback id must not advance the state");
            s.Dispose();
        }

        // ====================== P1-2 regression: asr / barge_in downstream types ======================

        [Test]
        public void AsrTranscripts_PartialThenFinal_FireEvent_WithoutStateChange()
        {
            // P1-2 regression: asr.partial / asr.final must reach subscribers (UI subtitle)
            // and must NOT disturb the AwaitingAnswer state (informational by protocol).
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);

            var received = new List<AsrTranscriptMessage>();
            c.TranscriptReceived += received.Add;

            s.SimulatePayload(Encoding.UTF8.GetBytes(AsrJson(c.SessionId, c.TurnId, "asr.partial", "飞机的发动机")));
            s.SimulatePayload(Encoding.UTF8.GetBytes(AsrJson(c.SessionId, c.TurnId, "asr.final", "飞机的发动机是做什么的")));
            s.DispatchMessageQueue();

            Assert.AreEqual(2, received.Count, "both asr events must be surfaced to subscribers");
            Assert.AreEqual("asr.partial", received[0].type);
            Assert.AreEqual("asr.final", received[1].type);
            Assert.AreEqual("飞机的发动机是做什么的", received[1].transcript);
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State, "asr events are informational only");
            Assert.AreEqual(VoiceCommandResult.Ok, c.LastRejection);
            s.Dispose();
        }

        [Test]
        public void AsrTranscript_AfterRoundClosed_IsRejectedAsLate()
        {
            // A straggler asr.final for an already-closed round must not bleed into the next
            // round's subtitle: classified late like every other downstream straggler.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            var fired = 0;
            c.TranscriptReceived += _ => fired++;
            s.SimulatePayload(Encoding.UTF8.GetBytes(AsrJson(c.SessionId, c.TurnId, "asr.final", "迟到的转写")));
            s.DispatchMessageQueue();

            Assert.AreEqual(0, fired, "a straggler transcript for the closed round must not fire");
            Assert.AreEqual(VoiceCommandResult.LateAnswer, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            s.Dispose();
        }

        [Test]
        public void BargeInAccepted_WhileCancelling_SettlesToReady_AndFiresEvent()
        {
            // VR9: the barge-in acknowledgement settles the controller out of Cancelling back
            // to Ready (the "barge-in done" edge), so the user can record the next round
            // without restarting PlayMode. The event still fires for UI projection.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            c.CancelAckDelayAsync = _ => new TaskCompletionSource<object>().Task;
            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(VoiceClientState.Cancelling, c.State);

            BargeInAcceptedMessage ack = null;
            c.BargeInAccepted += m => ack = m;
            s.SimulatePayload(Encoding.UTF8.GetBytes(BargeInJson(c.SessionId, c.TurnId, "stopped", "stop")));
            s.DispatchMessageQueue();

            Assert.IsNotNull(ack, "barge_in.accepted must be surfaced to subscribers");
            Assert.AreEqual("stopped", ack.status);
            Assert.AreEqual("stop", ack.intent);
            Assert.AreEqual(VoiceClientState.Ready, c.State, "the ack settles the cancel back to Ready");
            s.Dispose();
        }

        // ====================== P1-8 regression: unique per-device session id ======================

        [Test]
        public void SessionId_IsUniquePerController_AndWireLegal()
        {
            // P1-8 regression: two devices (controllers) must never share the hardcoded
            // "vr-session-001" — they would collide in the server's active-binding registry.
            var (c1, s1) = NewController();
            var (c2, s2) = NewController();
            Assert.DoesNotThrow(() => { c1.ConnectAsync().GetAwaiter().GetResult(); });
            Assert.DoesNotThrow(() => { c2.ConnectAsync().GetAwaiter().GetResult(); });
            s1.DispatchMessageQueue();
            s2.DispatchMessageQueue();

            Assert.AreNotEqual(c1.SessionId, c2.SessionId, "each controller must own a unique session id");
            Assert.AreNotEqual("vr-session-001", c1.SessionId, "the hardcoded demo id must be gone");
            StringAssert.IsMatch("^vr-session-[0-9a-f]{12}$", c1.SessionId);
            StringAssert.IsMatch("^vr-session-[0-9a-f]{12}$", c2.SessionId);
            // The session.start on the wire must carry exactly the generated id.
            Assert.IsTrue(s1.SentTexts[0].Contains(c1.SessionId), "session.start must carry the generated id");
            Assert.IsTrue(s2.SentTexts[0].Contains(c2.SessionId), "session.start must carry the generated id");

            c1.Dispose();
            c2.Dispose();
            s1.Dispose();
            s2.Dispose();
        }

        // =============================== State-table legality ===============================

        [Test]
        public void StateRules_LegalChain_IsOk()
        {
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Idle, VoiceClientState.HealthChecking));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.HealthChecking, VoiceClientState.Connecting));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Connecting, VoiceClientState.Ready));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Ready, VoiceClientState.Capturing));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Capturing, VoiceClientState.AwaitingAnswer));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.AwaitingAnswer, VoiceClientState.Playing));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Playing, VoiceClientState.Ready));
        }

        [Test]
        public void StateRules_IllegalJumps_AreRejected()
        {
            // Skipping states is illegal.
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Idle, VoiceClientState.Connecting));
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Ready, VoiceClientState.Playing));
            // Wrong direction on the forward chain.
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Capturing, VoiceClientState.Ready));
            Assert.AreEqual(VoiceStateTransitionCode.Illegal, VoiceStateRules.TryTransition(VoiceClientState.Playing, VoiceClientState.Capturing));
        }

        [Test]
        public void StateRules_Faulted_IsTerminalExceptReset()
        {
            Assert.AreEqual(VoiceStateTransitionCode.Terminal, VoiceStateRules.TryTransition(VoiceClientState.Faulted, VoiceClientState.Ready));
            Assert.AreEqual(VoiceStateTransitionCode.Ok, VoiceStateRules.TryTransition(VoiceClientState.Faulted, VoiceClientState.Idle));
        }

        // =============================== Duplicate idempotency ===============================

        [Test]
        public void DuplicateStart_IsIdempotent()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Capturing, c.State);
            s.Dispose();
        }

        [Test]
        public void DuplicateEnd_IsIdempotent()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);
            s.Dispose();
        }

        [Test]
        public void Cancel_Twice_IsIdempotent()
        {
            // VR9: only the Playing wire path keeps a cancel IN FLIGHT (Capturing/AwaitingAnswer
            // cancels settle synchronously to Ready), so drive to Playing to exercise the
            // idempotency guard. The default 5 s watchdog (real Task.Delay) cannot fire inside
            // this synchronous test, so the state stays Cancelling between the two calls.
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(VoiceCommandResult.Idempotent, c.CancelAsync());
            Assert.AreEqual(VoiceClientState.Cancelling, c.State);
            s.Dispose();
        }

        [Test]
        public void NoThrow_DuplicateAndInvalidCommands()
        {
            var c = ToReady(out var s);
            // These must never throw, only return stable codes.
            Assert.DoesNotThrow(() => c.RecordEndAsync());
            Assert.DoesNotThrow(() => c.SendAudioFrameAsync(0));
            Assert.DoesNotThrow(() => c.CancelAsync());
            s.Dispose();
        }

        // =============================== Command misuse (illegal transitions via controller) ===============================

        [Test]
        public void RecordEnd_BeforeCapturing_ReturnsInvalidState()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordEndAsync());
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            s.Dispose();
        }

        [Test]
        public void RecordStart_WhileIdle_ReturnsInvalidState()
        {
            var (c, s) = NewController();
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.RecordStartAsync());
            Assert.AreEqual(VoiceClientState.Idle, c.State);
            s.Dispose();
        }

        // =============================== Sequence out-of-order ===============================

        [Test]
        public void AudioFrame_OutOfOrder_Rejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0));
            // Skip to 5 -> out of order.
            Assert.AreEqual(VoiceCommandResult.SequenceMismatch, c.SendAudioFrameAsync(5));
            Assert.AreEqual(VoiceCommandResult.SequenceMismatch, c.LastRejection);
            s.Dispose();
        }

        [Test]
        public void TtsFrame_OutOfOrder_RejectedAndFaulted()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, "playback_server1", 2, false))); // expecting 0
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.SequenceMismatch, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            s.Dispose();
        }

        // =============================== Stale / other-session / late rejection ===============================

        [Test]
        public void OtherSession_StartedReplay_IsRejected()
        {
            var c = ToReady(out var s);
            // A started for a different session while already Ready is stale/other.
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson("vr-session-OTHER", c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State); // unchanged
            s.Dispose();
        }

        [Test]
        public void OtherSession_Answer_IsRejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson("vr-session-OTHER", c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.OtherSession, c.LastRejection);
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State); // not advanced to Playing
            s.Dispose();
        }

        [Test]
        public void StaleTurn_Answer_IsRejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, "turn-9999")));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.StaleTurn, c.LastRejection);
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);
            s.Dispose();
        }

        [Test]
        public void LateAnswer_IsRejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            // A second answer for the same already-answered turn is late.
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.LateAnswer, c.LastRejection);
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            s.Dispose();
        }

        [Test]
        public void LateTts_AfterPlaybackDone_IsRejected()
        {
            var c = ToReady(out var s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            const string pb = "playback_server1"; // server-issued id for this round
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, pb, 0, true)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            // Late tts after playback completed (uses the prior playback id).
            s.SimulatePayload(Encoding.UTF8.GetBytes(TtsJson(c.SessionId, c.TurnId, pb, 1, false)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceCommandResult.LateTts, c.LastRejection);
            s.Dispose();
        }

        // =============================== Version gating ===============================

        [Test]
        public void NonV1_Started_TransitionsFaulted()
        {
            var (c, s) = NewController();
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            s.DispatchMessageQueue(); // open + session.start
            // Server replies started with a non-v1 protocol_version.
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJsonNonV1("vr-session-001", c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            s.Dispose();
        }

        [Test]
        public void Reset_FromFaulted_ReturnsToIdle()
        {
            var c = NonV1Faulted();
            Assert.AreEqual(VoiceCommandResult.Ok, c.Reset());
            Assert.AreEqual(VoiceClientState.Idle, c.State);
        }

        [Test]
        public void Reset_FromNonFaulted_ReturnsInvalidState()
        {
            var c = ToReady(out var s);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.Reset());
            s.Dispose();
        }

        [Test]
        public void Faulted_Commands_ReturnFaulted()
        {
            var c = NonV1Faulted();
            Assert.AreEqual(VoiceCommandResult.Faulted, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Faulted, c.RecordEndAsync());
            Assert.AreEqual(VoiceCommandResult.Faulted, c.CancelAsync());
        }

        private static VoiceSessionController NonV1Faulted()
        {
            var (c, s) = NewController();
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJsonNonV1("vr-session-001", c.TurnId)));
            s.DispatchMessageQueue();
            s.Dispose();
            return c;
        }
    }
}
