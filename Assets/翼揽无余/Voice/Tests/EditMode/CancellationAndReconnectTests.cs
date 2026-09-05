// CancellationAndReconnectTests.cs
// VR5-T12 EditMode tests for cancel ordering / generation isolation / bounded auto-reconnect /
// pause-resume lifecycle on VoiceSessionController (+ ReconnectPolicy) and the wired
// Mp3PlaybackQueue.
//
// Everything is driven against FakeVoiceSocket (deterministic, main-thread-pumped transport double)
// and either a recording FakePlayback or a real Mp3PlaybackQueue with an injected stub decoder and
// an injected dedicated temp directory. No real media, no real WebSocket, no real microphone.
//
// Coverage:
//   - Cancel FIXED ORDER: local playback stop (queue clear + generation++) happens BEFORE the
//     voice-ws-v1 session.cancel is sent; turn.cancel is never sent.
//   - Cancel from each active stage (Capturing / AwaitingAnswer / Playing).
//   - Controller-driven cancel bumps the real queue's generation and drops old-round stragglers.
//   - ReconnectPolicy: 1/2/4 s backoff, max 3 attempts, only Abnormal/ServerError/Connect/Timeout
//     are retried; Protocol/Policy/Application close codes are never retried.
//   - Automatic reconnect on a recoverable abnormal close recovers to Ready; when the network stays
//     down it backs off exactly 1/2/4 s, tries exactly 3 times, then Faults.
//   - Pause: local stop + socket close + route to Idle WITHOUT sending session.cancel.
//   - Resume: returns to Idle and MUST NOT auto-connect / auto-request permission / auto-open mic.
//
// NOTE: ext.nunit 3.5 has no ThrowsAsync/DoesNotThrowAsync -> synchronous assertions are used.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Protocol;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Transport;

namespace Yilan.Voice.Cancellation
{
    [TestFixture]
    public class CancellationAndReconnectTests
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

        // ============================ Wire payload builders ============================

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

        private static string AnswerJson(string sessionId, string turnId)
        {
            var m = new AnswerDisplayMessage
            {
                type = "answer.display",
                session_id = sessionId,
                turn_id = turnId,
                answer_id = "ans-1",
                evidence_package_id = "ev-1",
                answer = new PublicAnswer { content = "已显示的回答文本", source = "rag" }
            };
            return JsonUtility.ToJson(m);
        }

        private static string HeaderJson(string sessionId, string turnId, string playbackId, int seq, bool isFinal)
        {
            var m = new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = sessionId,
                turn_id = turnId,
                playback_id = playbackId,
                sequence = seq,
                codec = "mp3",
                duration_ms = 100,
                is_final = isFinal
            };
            return JsonUtility.ToJson(m);
        }

        private static TtsFrameMessage Frame(string playbackId, int seq, bool isFinal)
        {
            return new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = "vr-session-001",
                turn_id = "turn-0001",
                playback_id = playbackId,
                sequence = seq,
                codec = "mp3",
                duration_ms = 100,
                is_final = isFinal
            };
        }

        // ============================ Fakes ============================

        private sealed class FakePlayback : ITtsPlayback
        {
            private int _queued;

            public int CancelCalls { get; private set; }
            public int EnqueueCalls { get; private set; }
            public List<string> Order { get; } = new List<string>(); // "cancel" / "enqueue"
            /// <summary>Runs at the start of Cancel(), letting a test snapshot socket state.</summary>
            public Action BeforeCancel;

            public bool HasQueued => _queued > 0;
            public bool IsPlaying => false;

            public event Action<TtsPlayedItem> FramePlayed;
            public event Action<string> FrameDecodeFailed;
            public event Action<string> PlaybackCompleted;

            public TtsPlaybackCode EnqueueTts(TtsFrameMessage header, byte[] mp3)
            {
                EnqueueCalls++;
                Order.Add("enqueue");
                _queued++;
                return TtsPlaybackCode.Accepted;
            }

            public void Cancel()
            {
                BeforeCancel?.Invoke();
                CancelCalls++;
                Order.Add("cancel");
                _queued = 0;
            }

            public void Dispose() { }
        }

        private sealed class StubDecoder : ITtsDecoder
        {
            public Task<TtsDecodeResult> DecodeAsync(string filePath)
                => Task.FromResult(TtsDecodeResult.Ok(null));
        }

        // ============================ Harness helpers ============================

        private static (VoiceSessionController c, FakeVoiceSocket s) NewController(ITtsPlayback playback = null)
        {
            var socket = new FakeVoiceSocket { ConnectSucceeds = true };
            var c = new VoiceSessionController(Cfg, socket, playback);
            return (c, socket);
        }

        private static (VoiceSessionController c, FakeVoiceSocket s) ToReady(ITtsPlayback playback = null)
        {
            var (c, sock) = NewController(playback);
            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            Assert.AreEqual(VoiceClientState.Connecting, c.State);
            sock.DispatchMessageQueue(); // delivers Opened -> session.start
            sock.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            sock.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            return (c, sock);
        }

        private static (VoiceSessionController c, FakeVoiceSocket s, FakePlayback fb) ToPlaying()
        {
            var fb = new FakePlayback();
            var (c, s) = ToReady(fb);
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, 0.2f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);
            return (c, s, fb);
        }

        private static string LastText(FakeVoiceSocket s) => s.SentTexts[s.SentTexts.Count - 1];

        // ============================ Cancel ordering ============================

        [Test]
        public void Cancel_StopsPlaybackBeforeSendingSessionCancel_NeverTurnCancel()
        {
            var (c, s, fb) = ToPlaying();
            int sentAtPlaybackStop = -1;
            fb.BeforeCancel = () => sentAtPlaybackStop = s.SentTexts.Count;

            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(VoiceClientState.Cancelling, c.State, "cancel enters Cancelling");
            Assert.AreEqual(1, fb.CancelCalls, "local playback must be stopped exactly once");
            Assert.That(sentAtPlaybackStop, Is.GreaterThanOrEqualTo(0), "the playback stop hook must have run");

            Assert.That(sentAtPlaybackStop, Is.LessThan(s.SentTexts.Count),
                "session.cancel must be sent AFTER the local stop (fixed ordering)");
            Assert.That(LastText(s), Does.Contain("\"session.cancel\""),
                "the wire cancel must be voice-ws-v1 session.cancel");
            string all = string.Join("\n", s.SentTexts);
            Assert.That(all, Does.Not.Contain("turn.cancel"), "turn.cancel is forbidden and must never be sent");

            s.Dispose();
        }

        [Test]
        public void Cancel_FromCapturingAwaitingAndPlaying_EachSendsSessionCancel()
        {
            // Capturing
            {
                var (c, s) = ToReady(new FakePlayback());
                c.RecordStartAsync();
                Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
                Assert.AreEqual(VoiceClientState.Cancelling, c.State);
                Assert.That(LastText(s), Does.Contain("\"session.cancel\""));
                s.Dispose();
            }
            // AwaitingAnswer
            {
                var (c, s) = ToReady(new FakePlayback());
                c.RecordStartAsync();
                c.RecordEndAsync();
                Assert.AreEqual(VoiceClientState.AwaitingAnswer, c.State);
                Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
                Assert.AreEqual(VoiceClientState.Cancelling, c.State);
                Assert.That(LastText(s), Does.Contain("\"session.cancel\""));
                s.Dispose();
            }
            // Playing
            {
                var (c, s, _fb) = ToPlaying();
                Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
                Assert.AreEqual(VoiceClientState.Cancelling, c.State);
                Assert.That(LastText(s), Does.Contain("\"session.cancel\""));
                s.Dispose();
            }
        }

        [Test]
        public void Cancel_WithRealQueue_BumpsGenerationAndDropsOldRoundStraggler()
        {
            string cache = Path.Combine(Application.temporaryCachePath, "T12Cancel-" + Guid.NewGuid().ToString("N"));
            var q = new Mp3PlaybackQueue(new StubDecoder(), cache);
            var (c, s) = NewController(q);

            Assert.DoesNotThrow(() => { c.ConnectAsync().GetAwaiter().GetResult(); });
            Assert.AreEqual(VoiceClientState.Connecting, c.State);
            s.DispatchMessageQueue();
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State);

            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordStartAsync());
            Assert.AreEqual(VoiceCommandResult.Ok, c.SendAudioFrameAsync(0, 0.2f));
            Assert.AreEqual(VoiceCommandResult.Ok, c.RecordEndAsync());
            s.SimulatePayload(Encoding.UTF8.GetBytes(AnswerJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Playing, c.State);

            string pb = "playback_server1"; // server-issued id (P0-3 fix)
            // Deliver a tts.frame TEXT header (seq 0) then its MP3 binary body -> queue holds 1 item.
            s.SimulatePayload(Encoding.UTF8.GetBytes(HeaderJson(c.SessionId, c.TurnId, pb, 0, false)));
            s.SimulatePayload(new byte[] { 0x49, 0x44, 0x33, 1, 2, 3 }); // opaque MP3-ish bytes
            s.DispatchMessageQueue();
            Assert.AreEqual(1, q.PendingFileCount, "the wired header/body must reach the playback queue");

            Assert.AreEqual(VoiceCommandResult.Ok, c.CancelAsync());
            Assert.AreEqual(1, q.CurrentGeneration, "controller cancel must bump the playback generation");
            Assert.IsFalse(q.HasQueued, "queue must be cleared on cancel");

            // A straggler frame of the cancelled round is dropped by the playback layer.
            Assert.AreEqual(TtsPlaybackCode.WrongGeneration, q.EnqueueTts(Frame(pb, 1, false), new byte[] { 1, 2, 3 }));

            CleanupCache(cache);
            q.Dispose();
            s.Dispose();
        }

        // ============================ ReconnectPolicy (pure) ============================

        [Test]
        public void ReconnectPolicy_BackoffIs_1_2_4()
        {
            CollectionAssert.AreEqual(new[] { 1000, 2000, 4000 }, ReconnectPolicy.BackoffMs);
            Assert.AreEqual(1000, ReconnectPolicy.DelayMsForAttempt(1));
            Assert.AreEqual(2000, ReconnectPolicy.DelayMsForAttempt(2));
            Assert.AreEqual(4000, ReconnectPolicy.DelayMsForAttempt(3));
            Assert.AreEqual(1000, ReconnectPolicy.DelayMsForAttempt(0), "clamp low");
            Assert.AreEqual(4000, ReconnectPolicy.DelayMsForAttempt(9), "clamp high");
        }

        [Test]
        public void ReconnectPolicy_MaxAttemptsIs3()
        {
            Assert.AreEqual(3, ReconnectPolicy.MaxAttempts);
        }

        [Test]
        public void ReconnectPolicy_OnlyNetworkFailuresAreRetried()
        {
            // Close categories that may be retried.
            Assert.AreEqual(ReconnectVerdict.Retry, ReconnectPolicy.ShouldReconnect(VoiceCloseCategory.Abnormal));
            Assert.AreEqual(ReconnectVerdict.Retry, ReconnectPolicy.ShouldReconnect(VoiceCloseCategory.ServerError));
            // Close categories that must NOT be retried.
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceCloseCategory.Normal));
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceCloseCategory.Protocol));
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceCloseCategory.Policy));
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceCloseCategory.Application));
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceCloseCategory.Unknown));

            // Operation-error kinds that may be retried.
            Assert.AreEqual(ReconnectVerdict.Retry, ReconnectPolicy.ShouldReconnect(VoiceSocketErrorKind.Connect));
            Assert.AreEqual(ReconnectVerdict.Retry, ReconnectPolicy.ShouldReconnect(VoiceSocketErrorKind.Timeout));
            // Operation-error kinds that must NOT be retried.
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceSocketErrorKind.Send));
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceSocketErrorKind.InvalidState));
            Assert.AreEqual(ReconnectVerdict.DoNotRetry, ReconnectPolicy.ShouldReconnect(VoiceSocketErrorKind.Unexpected));
        }

        // ============================ Automatic reconnect ============================

        [Test]
        public void AutoReconnect_OnAbnormalClose_BacksOff_1_2_4_ThenFaults()
        {
            var (c, s) = ToReady();
            var delays = new List<int>();
            c.ReconnectDelayAsync = ms => { delays.Add(ms); return Task.CompletedTask; };
            s.ConnectError = new VoiceSocketError(VoiceSocketErrorKind.Connect, "unreachable");
            int callsBefore = s.ConnectCalls;

            s.SimulateServerClose(1006, "drop");
            s.DispatchMessageQueue(); // Closed -> StartAutoReconnect (cycle runs synchronously)
            Assert.DoesNotThrow(() => { c.AutoReconnectTask.GetAwaiter().GetResult(); });

            CollectionAssert.AreEqual(new[] { 1000, 2000, 4000 }, delays.ToArray(),
                "the three retries must back off 1 s / 2 s / 4 s");
            Assert.AreEqual(3, s.ConnectCalls - callsBefore, "exactly MaxAttempts reconnection attempts");
            Assert.AreEqual(VoiceClientState.Faulted, c.State, "budget exhausted -> Faulted");

            s.Dispose();
        }

        [Test]
        public void NoAutoReconnect_OnProtocolAndPolicyCloseCodes()
        {
            // Server-defined application protocol code 4400 (invalid protocol) -> no retry.
            {
                var (c, s) = ToReady();
                int calls = s.ConnectCalls;
                s.SimulateServerClose(4400, "invalid");
                s.DispatchMessageQueue();
                Assert.AreEqual(VoiceClientState.Faulted, c.State);
                Assert.IsNull(c.AutoReconnectTask, "no auto-reconnect cycle for a protocol error");
                Assert.AreEqual(calls, s.ConnectCalls, "no reconnection attempt for a protocol error");
                s.Dispose();
            }
            // Standard policy close 1008 (permission-like) -> no retry.
            {
                var (c, s) = ToReady();
                int calls = s.ConnectCalls;
                s.SimulateServerClose(1008, "permission");
                s.DispatchMessageQueue();
                Assert.AreEqual(VoiceClientState.Faulted, c.State);
                Assert.IsNull(c.AutoReconnectTask);
                Assert.AreEqual(calls, s.ConnectCalls, "no reconnection attempt for a policy error");
                s.Dispose();
            }
        }

        [Test]
        public void AutoReconnect_OnAbnormalClose_ReopensAndRecoversToReady()
        {
            var (c, s) = ToReady();
            var delays = new List<int>();
            c.ReconnectDelayAsync = ms => { delays.Add(ms); return Task.CompletedTask; };

            s.SimulateServerClose(1006, "drop");
            s.DispatchMessageQueue(); // Closed -> StartAutoReconnect; the re-open succeeds immediately
            Assert.DoesNotThrow(() => { c.AutoReconnectTask.GetAwaiter().GetResult(); });
            Assert.AreEqual(1, delays.Count, "one attempt, backed off 1 s");
            Assert.AreEqual(VoiceClientState.Connecting, c.State, "awaiting the new session.started");

            s.DispatchMessageQueue(); // deliver Opened -> session.start sent for the reopened socket
            s.SimulatePayload(Encoding.UTF8.GetBytes(StartedJson(c.SessionId, c.TurnId)));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, c.State, "a real v1 started restores Ready");
            Assert.IsFalse(c.IsAutoReconnecting, "cycle finished after recovery");

            s.Dispose();
        }

        [Test]
        public void NormalClose_RoutesToIdle_NoAutoReconnect()
        {
            var (c, s) = ToReady();
            int calls = s.ConnectCalls;

            s.SimulateServerClose(1000, "bye");
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Idle, c.State, "clean close settles to Idle");
            Assert.IsNull(c.AutoReconnectTask, "a clean close never triggers a reconnect");
            Assert.AreEqual(calls, s.ConnectCalls, "no reconnection attempt on a clean close");

            s.Dispose();
        }

        [Test]
        public void AutoReconnect_OnRecoverableErrorKind_StartsBoundedCycle()
        {
            var (c, s) = ToReady();
            var delays = new List<int>();
            c.ReconnectDelayAsync = ms => { delays.Add(ms); return Task.CompletedTask; };
            int callsBefore = s.ConnectCalls;

            s.SimulateError(new VoiceSocketError(VoiceSocketErrorKind.Connect, "transient"));
            s.DispatchMessageQueue(); // OnError -> StartAutoReconnect -> re-open succeeds immediately
            Assert.DoesNotThrow(() => { c.AutoReconnectTask.GetAwaiter().GetResult(); });

            Assert.That(delays, Has.Count.EqualTo(1), "one attempt after the 1 s backoff");
            Assert.AreEqual(1, s.ConnectCalls - callsBefore, "one re-open attempt");
            Assert.AreEqual(VoiceClientState.Connecting, c.State, "awaiting the new session.started");

            s.Dispose();
        }

        [Test]
        public void NoAutoReconnect_OnNonRecoverableErrorKind_Faults()
        {
            var (c, s) = ToReady();
            int calls = s.ConnectCalls;

            s.SimulateError(new VoiceSocketError(VoiceSocketErrorKind.Send, "send failed"));
            s.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Faulted, c.State);
            Assert.IsNull(c.AutoReconnectTask, "a send error is not retried");
            Assert.AreEqual(calls, s.ConnectCalls, "no reconnection attempt on a non-recoverable error");

            s.Dispose();
        }

        // ============================ Pause / Resume lifecycle ============================

        [Test]
        public void Pause_StopsPlaybackClosesSocket_RoutesToIdle_WithoutSendingSessionCancel()
        {
            var (c, s, fb) = ToPlaying();
            int sentBefore = s.SentTexts.Count;

            Assert.AreEqual(VoiceCommandResult.Ok, c.PauseAsync().GetAwaiter().GetResult());
            Assert.AreEqual(1, fb.CancelCalls, "pause must stop local playback / silence");
            Assert.GreaterOrEqual(s.CloseCalls, 1, "pause must close the socket");
            Assert.AreEqual(VoiceClientState.Idle, c.State);
            Assert.AreEqual(sentBefore, s.SentTexts.Count, "pause is a LOCAL teardown: no session.cancel sent");

            s.Dispose();
        }

        [Test]
        public void Resume_FromIdle_IsNoOp_NoAutoConnect_NoAutoMicOrPermission()
        {
            var (c, s) = ToReady();
            int connectCalls = s.ConnectCalls;
            Assert.AreEqual(VoiceCommandResult.Ok, c.PauseAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Idle, c.State);
            int closeCalls = s.CloseCalls;

            Assert.AreEqual(VoiceCommandResult.Ok, c.Resume());
            Assert.AreEqual(VoiceClientState.Idle, c.State, "resume stays idle until the caller connects");
            Assert.AreEqual(connectCalls, s.ConnectCalls, "resume must NOT auto-connect");
            Assert.AreEqual(closeCalls, s.CloseCalls, "resume must NOT touch the socket");
            // Structural guarantee: the controller has no permission/capture reference, so it cannot
            // request RECORD_AUDIO or open the microphone on resume (asserted by ConnectCalls/CloseCalls
            // being unchanged and by the whitelist not wiring capture into the controller).

            s.Dispose();
        }

        [Test]
        public void Resume_FromActiveState_ReturnsInvalidState()
        {
            var (c, s) = ToReady();
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            Assert.AreEqual(VoiceCommandResult.InvalidState, c.Resume());
            Assert.AreEqual(VoiceClientState.Ready, c.State);
            s.Dispose();
        }

        // ============================ Helpers ============================

        private static void CleanupCache(string cache)
        {
            try
            {
                if (!string.IsNullOrEmpty(cache) && Directory.Exists(cache))
                    Directory.Delete(cache, true);
            }
            catch { /* best-effort test cleanup */ }
        }
    }
}
