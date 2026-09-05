// VoiceUiProjectionTests.cs
// VR4-T10 PlayMode tests. These verify that the voice ViewModel projects every UI state and
// that answer.display is fixed once from the gold carrier `answer.content` (never from a
// fabricated short/main_answer), inheriting the VR2-T07 turn/late/stale validation of the
// REAL VoiceSessionController.
//
// The PlayMode test assembly (Yilan.Voice.PlayModeTests) references only Yilan.Voice.Runtime,
// so FakeVoiceSocket (in the EditMode test assembly) is NOT reusable here. Per the task
// instruction, we embed a minimal deterministic fake socket in this file instead of modifying
// any asmdef.
//
// NOTE: the bundled ext.nunit 3.5 has no ThrowsAsync / DoesNotThrowAsync, so all task
// assertions are synchronous: Assert.DoesNotThrow(() => task.GetAwaiter().GetResult()).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Protocol;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;

namespace Yilan.Voice.Runtime.UI
{
    /// <summary>
    /// Minimal IVoiceSocket double for PlayMode projection tests. Mirrors the delivery
    /// contract of the real adapter: queued events are only delivered to subscribers when
    /// <see cref="DispatchMessageQueue"/> is called (the "main thread" pump).
    /// </summary>
    internal sealed class ProjectionFakeSocket : IVoiceSocket
    {
        private readonly ConcurrentQueue<Action> _pending = new ConcurrentQueue<Action>();
        private bool _disposed;

        public bool IsOpen { get; private set; }

        public event VoiceSocketOpenedHandler Opened;
        public event VoiceSocketPayloadHandler Payload;
        public event VoiceSocketErrorHandler Error;
        public event VoiceSocketClosedHandler Closed;

        public Task ConnectAsync(Uri url, int timeoutMs = 0)
        {
            if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(ProjectionFakeSocket)));
            IsOpen = true;
            Enqueue(() => Opened?.Invoke());
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, int timeoutMs = 0) => Task.CompletedTask;
        public Task SendBinaryAsync(byte[] data, int timeoutMs = 0) => Task.CompletedTask;

        public Task CloseAsync(int code, string reason, int timeoutMs = 0)
        {
            if (_disposed) return Task.CompletedTask;
            IsOpen = false;
            return Task.CompletedTask;
        }

        public void SimulatePayload(string json) => Enqueue(() => Payload?.Invoke(Encoding.UTF8.GetBytes(json)));

        public void SimulateServerClose(int code, string reason)
            => Enqueue(() => Closed?.Invoke(new VoiceCloseInfo(code, VoiceCloseMapper.Classify(code), reason)));

        public void SimulateError(VoiceSocketError e) => Enqueue(() => Error?.Invoke(e));

        public void DispatchMessageQueue()
        {
            while (_pending.TryDequeue(out var a)) a();
        }

        private void Enqueue(Action a)
        {
            if (_disposed) return;
            _pending.Enqueue(a);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            IsOpen = false;
            Opened = null;
            Payload = null;
            Error = null;
            Closed = null;
            while (_pending.TryDequeue(out _)) { }
        }
    }

    [TestFixture]
    public class VoiceUiProjectionTests
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

        // ---- wire builders (projected through JsonUtility, gold carrier answer.content) ----

        private static string StartedJson(string sessionId, string turnId)
            => JsonUtility.ToJson(new VoiceStateMessage
            {
                type = "voice.state",
                session_id = sessionId,
                turn_id = turnId,
                state = "listening",
                status = "session.started",
                protocol_version = VoiceProtocolParser.WireVersion
            });

        private static string AnswerJson(string sessionId, string turnId, string content, string answerId = "ans-1")
            => JsonUtility.ToJson(new AnswerDisplayMessage
            {
                type = "answer.display",
                session_id = sessionId,
                turn_id = turnId,
                answer_id = answerId,
                evidence_package_id = "ev-1",
                answer = new PublicAnswer { content = content, source = "handbook" }
            });

        private static string TtsJson(string sessionId, string turnId, string playbackId, int seq, bool isFinal)
            => JsonUtility.ToJson(new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = sessionId,
                turn_id = turnId,
                playback_id = playbackId,
                sequence = seq,
                codec = "pcm16",
                duration_ms = 100,
                is_final = isFinal
            });

        private static string VoiceErrorJson(string code, string field)
            => JsonUtility.ToJson(new VoiceErrorMessage { type = "voice.error", code = code, field = field });

        private static string AsrJson(string sessionId, string turnId, string type, string transcript)
            => JsonUtility.ToJson(new AsrTranscriptMessage
            {
                type = type,
                session_id = sessionId,
                turn_id = turnId,
                transcript = transcript,
                confidence = 0.9f,
                provider = "whisper"
            });

        private static string ClarificationJson(string sessionId, string turnId, string reason)
            => JsonUtility.ToJson(new ClarificationRequiredMessage
            {
                type = "clarification.required",
                session_id = sessionId,
                turn_id = turnId,
                reason = reason
            });

        // ---- harness helpers: connect through the real controller to Ready ----

        private static (VoiceSessionController controller, ProjectionFakeSocket socket, VoiceViewModel vm) NewHarness()
        {
            var socket = new ProjectionFakeSocket();
            var controller = new VoiceSessionController(Cfg, socket);
            var vm = new VoiceViewModel(controller);
            return (controller, socket, vm);
        }

        private static void ConnectToReady(VoiceSessionController c, ProjectionFakeSocket s)
        {
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            Assert.AreEqual(VoiceClientState.Connecting, c.State);
            s.DispatchMessageQueue(); // Opened -> session.start sent
            s.SimulatePayload(StartedJson(c.SessionId, c.TurnId));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);
        }

        // =============================== connect -> Ready ===============================

        [Test]
        public void Ui_ConnectToReady_ProjectsReady()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            Assert.AreEqual(VoiceUiState.Ready, vm.State);
            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ============================ record -> Awaiting ================================

        [Test]
        public void Ui_RecordThenAwait_ProjectsCapturingThenAwaiting()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);

            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceUiState.Capturing, vm.State);

            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            Assert.AreEqual(VoiceUiState.AwaitingAnswer, vm.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ============ answer projected once, from answer.content, current turn ==========

        [Test]
        public void Ui_Answer_ProjectsContent_FromGoldCarrier_Once()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            const string content = "手套箱内冷却液位于副翼下方的仪表区。";
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, content));
            s.DispatchMessageQueue();

            // Gold carrier is answer.content — never a fabricated short/main_answer.
            Assert.AreEqual(VoiceUiState.AnswerDisplayed, vm.State);
            Assert.AreEqual(content, vm.AnswerText);
            Assert.IsTrue(vm.AnswerFixed, "answer must be fixed once for the current turn");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_DuplicateAnswerForCurrentTurn_IsRejected_NotReprojected()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            const string first = "第一条回答内容。";
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, first));
            s.DispatchMessageQueue();
            Assert.AreEqual(first, vm.AnswerText);

            // A second answer.display for the SAME turn after fixation must be rejected and
            // must NOT overwrite the fixed content (承接 T07 的 LateAnswer/StaleTurn 校验).
            var rejections = new List<VoiceCommandResult>();
            c.MessageRejected += r => rejections.Add(r);
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, "应被拒绝的替换内容。", "ans-2"));
            s.DispatchMessageQueue();

            Assert.AreEqual(first, vm.AnswerText, "fixed answer must not be overwritten by a duplicate");
            Assert.IsTrue(rejections.Count >= 1, "a duplicated/out-of-order answer must be rejected by the controller");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_OldTurnAnswer_IsRejected_NotProjected()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            const string content = "当前轮次回答。";
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, content));
            s.DispatchMessageQueue();
            Assert.AreEqual(content, vm.AnswerText);

            // Deliver an answer.display labelled with an OLD turn id (previous round).
            var rejections = new List<VoiceCommandResult>();
            c.MessageRejected += r => rejections.Add(r);
            s.SimulatePayload(AnswerJson(c.SessionId, "turn-0000", "旧轮次回答。"));
            s.DispatchMessageQueue();

            Assert.AreEqual(content, vm.AnswerText, "an old-round answer must not be projected");
            Assert.IsTrue(rejections.Count >= 1, "an old-round answer must be rejected by the controller");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ==================== transcripts (P1-2: asr.partial / asr.final) ===============

        [Test]
        public void Ui_Transcripts_ProjectPartialThenFinal_ClearedOnNewRound()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            s.SimulatePayload(AsrJson(c.SessionId, c.TurnId, "asr.partial", "实时字幕"));
            s.DispatchMessageQueue();
            Assert.AreEqual("实时字幕", vm.TranscriptText);
            Assert.IsFalse(vm.TranscriptIsFinal, "asr.partial projects as a non-final subtitle");

            s.SimulatePayload(AsrJson(c.SessionId, c.TurnId, "asr.final", "最终转写文本"));
            s.DispatchMessageQueue();
            Assert.AreEqual("最终转写文本", vm.TranscriptText);
            Assert.IsTrue(vm.TranscriptIsFinal, "asr.final projects as the final transcript");
            Assert.AreEqual(VoiceUiState.AwaitingAnswer, vm.State, "transcripts are informational only");

            // Complete the round, then a NEW round must clear the old transcript.
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, "回答。"));
            s.DispatchMessageQueue();
            s.SimulatePayload(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, true));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            c.RecordStartAsync();
            Assert.AreEqual(string.Empty, vm.TranscriptText, "a new round must clear the old transcript");
            Assert.IsFalse(vm.TranscriptIsFinal);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ============ clarification from the SERVER event (P1-6 / VRR3) =================

        [Test]
        public void Ui_Clarification_FromServerEvent_ProjectsWithoutManualNotify()
        {
            // VRR3/P1-6: the server's clarification.required must reach the projection through
            // the controller event — the manual NotifyClarification hook is no longer the
            // production path for server-driven clarifications.
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            Assert.AreEqual(VoiceUiState.AwaitingAnswer, vm.State);

            s.SimulatePayload(ClarificationJson(c.SessionId, c.TurnId, "voice_input_unclear"));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceUiState.Clarification, vm.State);
            Assert.AreEqual("voice_input_unclear", vm.ClarificationReason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.ClarificationPrompt), "local prompt copy must be present");
            Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State,
                "the controller deliberately stays in AwaitingAnswer (recovery policy belongs to the caller)");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_Clarification_OldTurn_IsRejected_NotProjected()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            // A clarification labelled with an OLD turn id must be rejected at the binding
            // layer and never reach the projection.
            s.SimulatePayload(ClarificationJson(c.SessionId, "turn-0000", "voice_input_unclear"));
            s.DispatchMessageQueue();

            Assert.AreNotEqual(VoiceUiState.Clarification, vm.State, "an old-turn clarification must not project");
            Assert.AreEqual(string.Empty, vm.ClarificationReason);
            Assert.AreEqual(VoiceUiState.AwaitingAnswer, vm.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ========================== clarification (independent) =========================

        [Test]
        public void Ui_Clarification_IsIndependentState_WithLocalPrompt()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);

            // NotifyClarification remains as the EXPLICIT local hint hook (e.g. client-side
            // pre-flight messages); server-driven clarifications are covered by
            // Ui_Clarification_FromServerEvent_ProjectsWithoutManualNotify above.
            vm.NotifyClarification("voice_input_unclear");

            Assert.AreEqual(VoiceUiState.Clarification, vm.State);
            Assert.AreEqual("voice_input_unclear", vm.ClarificationReason);
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.ClarificationPrompt), "local prompt copy must be present");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ============================ error -> Faulted ==================================

        [Test]
        public void Ui_VoiceError_ProjectsFaulted()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();

            s.SimulatePayload(VoiceErrorJson("VOICE_SESSION_TIMEOUT", "session.start"));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceUiState.Faulted, vm.State);
            Assert.AreEqual(VoiceClientState.Faulted, c.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ====================== connection-lost / degraded ==============================

        [Test]
        public void Ui_ConnectionLost_ProjectsConnectionLost()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);

            vm.NotifyConnectionLost();
            Assert.AreEqual(VoiceUiState.ConnectionLost, vm.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_Degraded_ProjectsDegraded()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);

            vm.NotifyDegraded();
            Assert.AreEqual(VoiceUiState.Degraded, vm.State);
            Assert.IsTrue(vm.IsDegraded);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_AbnormalClose_ReachesControllerFaulted()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();

            // Abnormal transport drop (1006) is a recoverable close: controller enters bounded
            // Reconnecting (policy retries Abnormal/ServerError) and ViewModel projects Degraded.
            s.SimulateServerClose(1006, "connection dropped");
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Reconnecting, c.State);
            Assert.AreEqual(VoiceUiState.Degraded, vm.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ============================ playing projection ================================

        [Test]
        public void Ui_Playing_ProjectsPlaying()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, "播放回答。"));
            s.DispatchMessageQueue();
            // After the answer is fixed, the controller is in Playing (TTS) and the ViewModel
            // keeps AnswerDisplayed as the prominent state carrying the content.
            Assert.AreEqual(VoiceUiState.AnswerDisplayed, vm.State);
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ==================== cancel (spec step 1 required state) =====================

        [Test]
        public void Ui_Cancel_ProjectsCancelling()
        {
            // VR9: only the Playing wire path keeps a cancel IN FLIGHT (the controller
            // waits for barge_in.accepted), so drive to Playing and disable the watchdog
            // to observe the projected Cancelling state. Capturing/AwaitingAnswer cancels
            // settle synchronously to Ready and are covered by
            // Ui_CancelFromCapturing_SettlesToReady.
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, "播放中的回答。"));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            c.CancelAckDelayAsync = _ => new TaskCompletionSource<object>().Task; // watchdog never fires
            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(VoiceUiState.Cancelling, vm.State, "cancel must project the distinct Cancelling UI state");
            Assert.AreEqual(VoiceClientState.Cancelling, c.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_CancelFromCapturing_SettlesToReady()
        {
            // VR9: a cancel while the server is LISTENING settles locally to Ready — the
            // projection must follow immediately so the panel returns to the recordable
            // state without a reconnect.
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            Assert.AreEqual(VoiceUiState.Capturing, vm.State);

            c.CancelAckDelayAsync = _ => new TaskCompletionSource<object>().Task;
            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreEqual(VoiceUiState.Ready, vm.State, "a mid-recording cancel projects straight back to Ready");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ============== tts sequence out-of-order (independent out-of-order) ==========

        [Test]
        public void Ui_TtsOutOfOrder_RejectedByController_NotProjected()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            const string content = "已固定的回答内容。";
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, content));
            s.DispatchMessageQueue();
            Assert.AreEqual(content, vm.AnswerText);

            // Deliver a tts.frame with a non-monotonic sequence (jump from expected 0 to 5).
            // The real controller's T07 sequence validation must reject it (SequenceMismatch ->
            // Faulted) and it must never re-project / alter the fixed answer.
            var rejections = new List<VoiceCommandResult>();
            c.MessageRejected += r => rejections.Add(r);
            s.SimulatePayload(TtsJson(c.SessionId, c.TurnId, "playback_server1", 5, false));
            s.DispatchMessageQueue();

            Assert.IsTrue(rejections.Contains(VoiceCommandResult.SequenceMismatch),
                "an out-of-order tts.frame must be rejected as SequenceMismatch by the controller");
            Assert.AreEqual(VoiceClientState.Faulted, c.State, "protocol violation must fault the session");
            Assert.AreEqual(VoiceUiState.Faulted, vm.State);
            Assert.AreEqual(content, vm.AnswerText, "fixed answer stays intact despite the out-of-order frame");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ============== VR10: session-timeout settle / Faulted reset exit =============

        [Test]
        public void Ui_Closed4408_ProjectsIdleWithTimeoutHint()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);

            // 4408 VOICE_SESSION_TIMEOUT settles to Idle (not Faulted) and the projection
            // carries the recovery hint copy.
            s.SimulateServerClose(4408, "voice session timeout");
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Idle, c.State);
            Assert.AreEqual(VoiceUiState.Idle, vm.State, "4408 must not project Faulted");
            Assert.AreEqual("会话超时，点击重新提问", vm.HintText);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_Faulted_ShowsResetVisible_ResetClearsIt()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            s.SimulatePayload(VoiceErrorJson("VOICE_PROTOCOL_INVALID", "audio.end"));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceUiState.Faulted, vm.State);
            Assert.IsTrue(vm.ResetVisible, "Faulted must expose the reset exit");

            Assert.AreEqual(VoiceCommandResult.Ok, c.Reset());
            Assert.AreEqual(VoiceUiState.Idle, vm.State);
            Assert.IsFalse(vm.ResetVisible, "after Reset the exit must disappear");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        [Test]
        public void Ui_TtsDegradedAfterAnswer_KeepsAnswer_ShowsDegradationHint()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);
            c.RecordStartAsync();
            c.RecordEndAsync();

            const string content = "已显示的文字回答。";
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, content));
            s.DispatchMessageQueue();
            s.SimulatePayload(VoiceErrorJson("VOICE_TTS_ERROR", "tts"));
            s.DispatchMessageQueue();

            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreEqual(content, vm.AnswerText, "the fixed answer stays on screen");
            Assert.AreEqual("语音播报不可用，已显示文字回答", vm.HintText);
            Assert.IsFalse(vm.ResetVisible, "a degradation is not Faulted — no reset exit");

            // A new round clears the hint.
            c.RecordStartAsync();
            Assert.AreEqual(string.Empty, vm.HintText, "a new round must clear the hint");

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }

        // ====== second round clears the fixed answer, then projects the new one =======

        [Test]
        public void Ui_SecondRound_ClearsFixedAnswer_ThenProjectsNewContent()
        {
            var (c, s, vm) = NewHarness();
            ConnectToReady(c, s);

            // ---- Round 1: answer fixed once ----
            c.RecordStartAsync();
            c.RecordEndAsync();
            const string first = "第一轮回答。";
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, first));
            s.DispatchMessageQueue();
            Assert.AreEqual(first, vm.AnswerText);
            Assert.IsTrue(vm.AnswerFixed);

            // Complete playback (final tts) so the controller returns to Ready.
            s.SimulatePayload(TtsJson(c.SessionId, c.TurnId, "playback_server1", 0, true));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            // ---- Round 2: a new turn must CLEAR the previous fixed answer, then project fresh ----
            c.RecordStartAsync();
            Assert.AreEqual(VoiceUiState.Capturing, vm.State);
            Assert.IsFalse(vm.AnswerFixed, "a new turn must clear the previously fixed answer");
            Assert.AreEqual(string.Empty, vm.AnswerText);

            c.RecordEndAsync();
            const string second = "第二轮回答。";
            s.SimulatePayload(AnswerJson(c.SessionId, c.TurnId, second));
            s.DispatchMessageQueue();

            Assert.AreEqual(second, vm.AnswerText, "the new round's answer must be projected");
            Assert.IsTrue(vm.AnswerFixed);
            Assert.AreEqual(VoiceUiState.AnswerDisplayed, vm.State);

            c.Dispose();
            vm.Dispose();
            s.Dispose();
        }
    }
}
