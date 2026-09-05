using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Yilan.Voice.Runtime.Bootstrap;
using Yilan.Voice.Runtime.Capture;
using Yilan.Voice.Runtime.Metrics;
using Yilan.Voice.Runtime.Pico;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Transport;

namespace Yilan.Voice.Remediation
{
    [TestFixture]
    public class VoiceClientRuntimeTests
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

        [Test]
        public void StartAsync_HealthFails_DoesNotConnect()
        {
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: false);

            Assert.AreEqual(VoiceCommandResult.TransportFailed, runtime.StartAsync().GetAwaiter().GetResult());
            Assert.AreEqual(0, socket.ConnectCalls);
            Assert.IsNull(runtime.Controller);
            Assert.IsNull(runtime.ViewModel);
        }

        [Test]
        public void StartAsync_HealthSucceeds_ConnectsAndCreatesProjection()
        {
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            Assert.AreEqual(1, socket.ConnectCalls);
            Assert.IsNotNull(runtime.Controller);
            Assert.IsNotNull(runtime.ViewModel);
        }

        [Test]
        public void Dispose_ReleasesDependenciesInReverseOwnershipOrder()
        {
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true, out _, out _, out _);
            var order = new List<string>();
            runtime.DisposeObserver = order.Add;

            runtime.StartAsync().GetAwaiter().GetResult();
            runtime.Dispose();

            CollectionAssert.AreEqual(
                new[] { "metrics", "viewModel", "controller", "playback", "microphone", "socket" },
                order);
        }

        [Test]
        public void PumpAsync_SendsCapturedFramesUpstreamAsHeaderBinaryPairs()
        {
            // P0-1 regression: PumpAsync used to drain the microphone and DISCARD
            // every frame, so no audio ever reached the server. Each 20ms PCM16
            // frame must leave as one audio.frame header + one binary body.
            var socket = new FakeVoiceSocket();
            var device = new SteppingMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true, CanAskAgain = true });
            var microphone = new UnityMicrophoneSource(device, () => permission.HasPermission);
            // playback/metrics are owned and disposed by the runtime itself.
            var playback = new RecordingPlayback();
            var metrics = new VoiceMetricsRecorder(new VoiceIdHasher("vrr1-test-salt"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false);
            using var runtime = new VoiceClientRuntime(Cfg, socket, _ => Task.FromResult(true), permission, microphone, playback, metrics);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue(); // Opened -> session.start sent

            // Replay the server's session.started(v1) to reach Ready.
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.BeginCaptureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Capturing, runtime.Controller.State);

            // One 20ms window of mono 16 kHz audio = 320 samples = one 640-byte frame.
            device.Advance(Pcm16AudioConstants.SamplesPerFrame);
            Assert.AreEqual(VoiceCommandResult.Ok, runtime.PumpAsync(4).GetAwaiter().GetResult());

            Assert.AreEqual(2, socket.SentTexts.Count, "session.start + one audio.frame header");
            StringAssert.Contains("\"type\":\"audio.frame\"", socket.SentTexts[1]);
            Assert.AreEqual(1, socket.SentBinaries.Count, "the PCM body must actually be sent");
            Assert.AreEqual(Pcm16AudioConstants.BytesPerFrame, socket.SentBinaries[0].Length);
            Assert.AreEqual(VoiceClientState.Capturing, runtime.Controller.State);
        }

        [Test]
        public void BeginCaptureAsync_MicStartFails_StaysReadyAndRetrySucceeds()
        {
            // §12.3 fix 1 regression: a microphone that fails to open used to advance
            // the state machine to Capturing FIRST and then strand the turn there
            // with zero frames (no local exit — the UI waits forever). The capture
            // start must open the mic BEFORE RecordStartAsync; on failure the session
            // stays Ready and a retry with a working mic reaches Capturing normally.
            var socket = new FakeVoiceSocket();
            var device = new FlakyMicrophoneDevice { FailStart = true };
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true, CanAskAgain = true });
            var microphone = new UnityMicrophoneSource(device, () => permission.HasPermission);
            var playback = new RecordingPlayback();
            var metrics = new VoiceMetricsRecorder(new VoiceIdHasher("vrr1-test-salt"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false);
            using var runtime = new VoiceClientRuntime(Cfg, socket, _ => Task.FromResult(true), permission, microphone, playback, metrics);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue(); // Opened -> session.start sent
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);

            // Mic fails to open: the turn must NOT start — state stays Ready and no
            // turn-level traffic leaves the socket.
            Assert.AreEqual(VoiceCommandResult.InvalidState, runtime.BeginCaptureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State,
                "mic failure must not strand the turn in Capturing");
            Assert.AreEqual(1, socket.SentTexts.Count, "no turn-level traffic on a failed capture start");

            // Retry with a working mic: normal capture start.
            device.FailStart = false;
            Assert.AreEqual(VoiceCommandResult.Ok, runtime.BeginCaptureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Capturing, runtime.Controller.State);
        }

        [Test]
        public void PauseThenResumeAsync_ReconnectsSameSessionAndReachesReady()
        {
            // P1-5 regression: OnApplicationPause(true) tears the session down to
            // Idle; the foreground callback previously had NO path back. ResumeAsync
            // must re-open the WebSocket on the SAME session id (server-side
            // conversation context survives) and the controller must reach Ready
            // again after the server's fresh session.started.
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.StartAsync().GetAwaiter().GetResult());
            socket.DispatchMessageQueue(); // Opened -> session.start
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);
            string sessionId = runtime.Controller.SessionId;

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.PauseAsync().GetAwaiter().GetResult());
            Assert.AreEqual(VoiceClientState.Idle, runtime.Controller.State);

            Assert.AreEqual(VoiceCommandResult.Ok, runtime.ResumeAsync().GetAwaiter().GetResult());
            Assert.AreEqual(2, socket.ConnectCalls, "resume must re-open the socket");
            socket.DispatchMessageQueue(); // Opened -> fresh session.start
            Assert.AreEqual(sessionId, runtime.Controller.SessionId, "same session id across pause/resume");

            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);
        }

        [Test]
        public void ResumeAsync_WithoutPause_IsRejectedAsNoOp()
        {
            // Resume is only legal from the Idle state PauseAsync settles into; from
            // Ready (never paused) it must be a rejected no-op, never a second
            // concurrent connection attempt.
            var socket = new FakeVoiceSocket();
            using var runtime = CreateRuntime(socket, healthOk: true);

            runtime.StartAsync().GetAwaiter().GetResult();
            socket.DispatchMessageQueue();
            socket.SimulatePayload(System.Text.Encoding.UTF8.GetBytes(StartedJson(runtime)));
            socket.DispatchMessageQueue();
            Assert.AreEqual(VoiceClientState.Ready, runtime.Controller.State);

            Assert.AreEqual(VoiceCommandResult.InvalidState, runtime.ResumeAsync().GetAwaiter().GetResult());
            Assert.AreEqual(1, socket.ConnectCalls, "no second connection may be opened");
        }

        private static VoiceClientRuntime CreateRuntime(
            FakeVoiceSocket socket,
            bool healthOk,
            out RecordingPlayback playback,
            out UnityMicrophoneSource microphone,
            out VoiceMetricsRecorder metrics)
        {
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true, CanAskAgain = true });
            microphone = new UnityMicrophoneSource(new FakeMicrophoneDevice(), () => permission.HasPermission);
            playback = new RecordingPlayback();
            metrics = new VoiceMetricsRecorder(new VoiceIdHasher("vrr1-test-salt"), directory: System.IO.Path.GetTempPath(), isDevBuild: () => false);
            return new VoiceClientRuntime(Cfg, socket, _ => Task.FromResult(healthOk), permission, microphone, playback, metrics);
        }

        private static VoiceClientRuntime CreateRuntime(FakeVoiceSocket socket, bool healthOk)
        {
            return CreateRuntime(socket, healthOk, out _, out _, out _);
        }

        /// <summary>Server-style voice.state(session.started, voice-ws-v1) wire frame for the
        /// runtime's own controller (ids are echoed from the session.start it sent).</summary>
        private static string StartedJson(VoiceClientRuntime runtime)
        {
            var controller = runtime.Controller;
            var m = new Yilan.Voice.Runtime.Protocol.VoiceStateMessage
            {
                type = "voice.state",
                session_id = controller.SessionId,
                turn_id = controller.TurnId,
                state = "listening",
                status = "session.started",
                protocol_version = Yilan.Voice.Runtime.Protocol.VoiceProtocolParser.WireVersion
            };
            return UnityEngine.JsonUtility.ToJson(m);
        }

        private sealed class FakePermissionPlatform : IMicrophonePermissionPlatform
        {
            public bool Granted;
            public bool CanAskAgain;

            public bool HasPermission() => Granted;
            public bool CanRequestAgain() => CanAskAgain;
            public void RequestPermission() { }
        }

        private sealed class FakeMicrophoneDevice : IMicrophoneDevice
        {
            public string DeviceName => "fake";
            public int Channels => 1;
            public int DeviceSampleRate => Pcm16AudioConstants.SampleRateHz;
            public bool IsRecording { get; private set; }
            public int CapacitySamples => Pcm16AudioConstants.SampleRateHz;

            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                IsRecording = true;
                return true;
            }

            public void Stop() => IsRecording = false;
            public int GetPosition() => 0;
            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount) { }
            public void Dispose() => Stop();
        }

        /// <summary>Fake capture device whose open can be made to fail on demand
        /// (§12.3 fix 1: mic-start failure must not strand the turn in Capturing).</summary>
        private sealed class FlakyMicrophoneDevice : IMicrophoneDevice
        {
            public bool FailStart;

            public string DeviceName => "fake-flaky";
            public int Channels => 1;
            public int DeviceSampleRate => Pcm16AudioConstants.SampleRateHz;
            public bool IsRecording { get; private set; }
            public int CapacitySamples => Pcm16AudioConstants.SampleRateHz;

            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                if (FailStart) return false;
                IsRecording = true;
                return true;
            }

            public void Stop() => IsRecording = false;
            public int GetPosition() => 0;
            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount) { }
            public void Dispose() => Stop();
        }

        /// <summary>Fake capture device whose write cursor the test advances explicitly;
        /// every requested read region is filled with a constant non-silent sample so
        /// the framer actually produces PCM16 frames.</summary>
        private sealed class SteppingMicrophoneDevice : IMicrophoneDevice
        {
            private int _pos;

            public string DeviceName => "fake-stepping";
            public int Channels => 1;
            public int DeviceSampleRate => Pcm16AudioConstants.SampleRateHz;
            public bool IsRecording { get; private set; }
            public int CapacitySamples => Pcm16AudioConstants.SampleRateHz;

            public void Advance(int samples)
            {
                _pos = (_pos + samples) % CapacitySamples;
            }

            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                IsRecording = true;
                _pos = 0;
                return true;
            }

            public void Stop() => IsRecording = false;
            public int GetPosition() => _pos;

            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    dest[destOffset + i] = 0.25f;
                }
            }

            public void Dispose() => Stop();
        }

        private sealed class RecordingPlayback : ITtsPlayback
        {
            public bool HasQueued => false;
            public bool IsPlaying => false;
            public event Action<TtsPlayedItem> FramePlayed;
            public event Action<string> FrameDecodeFailed;
            public event Action<string> PlaybackCompleted;

            public TtsPlaybackCode EnqueueTts(Yilan.Voice.Runtime.Protocol.TtsFrameMessage header, byte[] mp3)
                => TtsPlaybackCode.Accepted;

            public void Cancel() { }

            public void Dispose()
            {
                FramePlayed = null;
                FrameDecodeFailed = null;
                PlaybackCompleted = null;
            }
        }
    }
}
