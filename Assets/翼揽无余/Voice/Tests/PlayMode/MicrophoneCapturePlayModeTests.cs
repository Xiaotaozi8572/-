// MicrophoneCapturePlayModeTests.cs
// VR3-T09 PlayMode tests. These exercises the three-path permission state machine and the
// UnityMicrophoneSource with a FAKE permission platform and a FAKE microphone device — no
// real hardware is touched.
//
// NOTE: the bundled ext.nunit 3.5 does not provide ThrowsAsync / DoesNotThrowAsync, so all
// assertions here are synchronous (Assert.DoesNotThrow(() => ...GetAwaiter().GetResult())).
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Capture;
using Yilan.Voice.Runtime.Pico;

namespace Yilan.Voice.Runtime.Capture
{
    public class MicrophoneCapturePlayModeTests
    {
        // ---- fakes ---------------------------------------------------------

        private sealed class FakePermissionPlatform : IMicrophonePermissionPlatform
        {
            public bool Granted;
            public bool CanAskAgain;
            public int RequestCount;

            public bool HasPermission() => Granted;
            public bool CanRequestAgain() => CanAskAgain;
            public void RequestPermission() => RequestCount++;
        }

        private sealed class FakeMicrophoneDevice : IMicrophoneDevice
        {
            public string DeviceName { get; private set; }
            public int Channels { get; private set; }
            public int DeviceSampleRate { get; private set; }
            public bool IsRecording { get; private set; }

            private float[] _buffer;
            private int _capacity;
            private int _position;   // raw cursor in [0, capacity)
            private long _recorded;  // absolute interleaved samples recorded (never wraps)
            public int WrapCount;    // times the cursor wrapped back to 0

            public int CapacitySamples => _capacity;

            public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
            {
                DeviceName = deviceName;
                Channels = channels;
                DeviceSampleRate = sampleRate;
                _capacity = sampleRate * lengthSec * channels;
                _buffer = new float[_capacity];
                _position = 0;
                _recorded = 0;
                WrapCount = 0;
                IsRecording = true;
                return true;
            }

            public void Stop() => IsRecording = false;

            public int GetPosition() => _position;

            public void Feed(float[] interleaved)
            {
                for (int i = 0; i < interleaved.Length; i++)
                {
                    int idx = (int)(_recorded % _capacity);
                    _buffer[idx] = interleaved[i];
                    _recorded++;
                    int next = (int)(_recorded % _capacity);
                    if (next < _position) WrapCount++;
                    _position = next;
                }
            }

            public void GetData(float[] dest, int destOffset, int startSample, int sampleCount)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    dest[destOffset + i] = _buffer[(startSample + i) % _capacity];
                }
            }

            public void Dispose() => Stop();
        }

        private static float[] ConstantSamples(int count, float value)
        {
            float[] s = new float[count];
            for (int i = 0; i < count; i++) s[i] = value;
            return s;
        }

        private static int DrainFrames(UnityMicrophoneSource source, List<byte[]> into)
        {
            int taken = 0;
            while (source.TryTakeFrame(out byte[] frame))
            {
                into.Add(frame);
                taken++;
            }
            return taken;
        }

        // ---- permission three paths --------------------------------------

        [Test]
        public void Permission_Granted_StatusIsGranted()
        {
            var platform = new FakePermissionPlatform { Granted = true, CanAskAgain = true };
            var permission = new PicoMicrophonePermission(platform);

            Assert.That(permission.Status, Is.EqualTo(MicrophonePermissionStatus.Granted));
            Assert.That(permission.HasPermission, Is.True);
            Assert.That(permission.CanRequest, Is.False);
            Assert.That(platform.RequestCount, Is.EqualTo(0));
        }

        [Test]
        public void Permission_DeniedButReaskable_StatusDenied_AndCanRequest()
        {
            var platform = new FakePermissionPlatform { Granted = false, CanAskAgain = true };
            var permission = new PicoMicrophonePermission(platform);

            Assert.That(permission.Status, Is.EqualTo(MicrophonePermissionStatus.Denied));
            Assert.That(permission.HasPermission, Is.False);
            Assert.That(permission.CanRequest, Is.True);

            // EnsurePermission issues at most one request per session while re-askable.
            Assert.That(permission.EnsurePermission(), Is.EqualTo(MicrophonePermissionStatus.Denied));
            Assert.That(platform.RequestCount, Is.EqualTo(1));
            Assert.That(permission.EnsurePermission(), Is.EqualTo(MicrophonePermissionStatus.Denied));
            Assert.That(platform.RequestCount, Is.EqualTo(1), "must not re-request on repeated calls in same session");
        }

        [Test]
        public void Permission_PermanentlyDenied_StatusPermanent_AndNeverRequest()
        {
            var platform = new FakePermissionPlatform { Granted = false, CanAskAgain = false };
            var permission = new PicoMicrophonePermission(platform);

            Assert.That(permission.Status, Is.EqualTo(MicrophonePermissionStatus.PermanentlyDenied));
            Assert.That(permission.HasPermission, Is.False);
            Assert.That(permission.CanRequest, Is.False);

            // Permanent deny must never fire the request dialog.
            Assert.That(permission.EnsurePermission(), Is.EqualTo(MicrophonePermissionStatus.PermanentlyDenied));
            Assert.That(platform.RequestCount, Is.EqualTo(0));
        }

        // ---- source refuses to start when not granted --------------------

        [Test]
        public void Source_DoesNotStart_WhenPermissionDenied()
        {
            var fakeMic = new FakeMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = false, CanAskAgain = true });
            using var source = new UnityMicrophoneSource(fakeMic, () => permission.HasPermission);

            bool started = source.Start("FakeMic", 16000, 1, 1);
            Assert.That(started, Is.False);
            Assert.That(source.IsRecording, Is.False);
            Assert.That(fakeMic.IsRecording, Is.False);
            Assert.That(source.FrameCount, Is.EqualTo(0));
            Assert.That(source.PollOnce(), Is.EqualTo(0));
        }

        [Test]
        public void Source_DoesNotStart_WhenPermissionPermanentlyDenied()
        {
            var fakeMic = new FakeMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = false, CanAskAgain = false });
            using var source = new UnityMicrophoneSource(fakeMic, () => permission.HasPermission);

            bool started = source.Start("FakeMic", 16000, 1, 1);
            Assert.That(started, Is.False);
            Assert.That(source.IsRecording, Is.False);
            Assert.That(fakeMic.IsRecording, Is.False);
            Assert.That(source.FrameCount, Is.EqualTo(0));
            Assert.That(source.PollOnce(), Is.EqualTo(0));
        }

        [Test]
        public void Source_Starts_WhenPermissionGranted()
        {
            var fakeMic = new FakeMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true });
            using var source = new UnityMicrophoneSource(fakeMic, () => permission.HasPermission);

            bool started = source.Start("FakeMic", 16000, 1, 1);
            Assert.That(started, Is.True);
            Assert.That(source.IsRecording, Is.True);
            Assert.That(fakeMic.IsRecording, Is.True);
        }

        // ---- frame size + 10 s count + wrap ------------------------------

        [Test]
        public void Capture_TenSeconds_Produces_500Frames_Each640Bytes()
        {
            var fakeMic = new FakeMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true });
            using var source = new UnityMicrophoneSource(fakeMic, () => permission.HasPermission);

            Assert.That(source.Start("FakeMic", 16000, 1, 1), Is.True);

            // 10 seconds of 16 kHz mono = 160000 interleaved samples. Feed in 3200-sample
            // chunks (each = 10 frames) and poll after each chunk.
            const int totalSamples = 16000 * 10;
            const int chunk = 3200;
            int produced = 0;
            for (int fed = 0; fed < totalSamples; fed += chunk)
            {
                fakeMic.Feed(ConstantSamples(chunk, 0.25f));
                produced += source.PollOnce();
            }

            Assert.That(produced, Is.InRange(498, 502), "10 s at 16 kHz => 500 frames, allow 500±2");
            Assert.That(source.FrameCount, Is.EqualTo(500));

            var frames = new List<byte[]>();
            int drained = DrainFrames(source, frames);
            Assert.That(drained, Is.EqualTo(500));
            foreach (var frame in frames)
            {
                Assert.That(frame.Length, Is.EqualTo(640), "every protocol frame must be exactly 640 bytes");
            }
        }

        [Test]
        public void Capture_PointerWrap_StillYieldsCorrectFrames()
        {
            var fakeMic = new FakeMicrophoneDevice();
            var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true });
            using var source = new UnityMicrophoneSource(fakeMic, () => permission.HasPermission);

            Assert.That(source.Start("FakeMic", 16000, 1, 1), Is.True);
            // Capacity = 16000*1*1 = 16000, so the cursor wraps roughly every 16000 samples.
            Assert.That(fakeMic.CapacitySamples, Is.EqualTo(16000));

            const int totalSamples = 16000 * 10;
            const int chunk = 1600;
            for (int fed = 0; fed < totalSamples; fed += chunk)
            {
                fakeMic.Feed(ConstantSamples(chunk, -0.5f));
                source.PollOnce();
            }

            // The cursor must have wrapped many times during the run.
            Assert.That(fakeMic.WrapCount, Is.GreaterThanOrEqualTo(1), "the raw pointer must actually wrap");

            // After a wrap the /320 frame-count formula still holds exactly.
            Assert.That(source.FrameCount, Is.EqualTo(totalSamples / 320), "wrap must not corrupt frame counts");
            var frames = new List<byte[]>();
            DrainFrames(source, frames);
            Assert.That(frames.Count, Is.EqualTo(totalSamples / 320));
            foreach (var frame in frames)
            {
                Assert.That(frame.Length, Is.EqualTo(640));
            }
        }

        // ---- privacy: no raw audio file + no per-frame payload log --------

        [Test]
        public void Capture_DoesNotWriteAudioFile_NorLogFramePayload()
        {
            var logs = new List<string>();
            Application.logMessageReceived += CaptureLog;
            void CaptureLog(string cond, string stack, LogType type) => logs.Add(cond);

            try
            {
                var fakeMic = new FakeMicrophoneDevice();
                var permission = new PicoMicrophonePermission(new FakePermissionPlatform { Granted = true });
                using (var source = new UnityMicrophoneSource(fakeMic, () => permission.HasPermission))
                {
                    Assert.That(source.Start("FakeMic", 16000, 1, 1), Is.True);
                    for (int i = 0; i < 20; i++) // 20 polls, ~ handful of frames
                    {
                        fakeMic.Feed(ConstantSamples(3200, 0.1f));
                        source.PollOnce();
                    }
                    var frames = new List<byte[]>();
                    DrainFrames(source, frames);
                    Assert.That(frames.Count, Is.GreaterThan(0));
                }

                // No raw-audio file may appear anywhere under the cache during capture. Only
                // audio-like suffixes are checked so unrelated system files cannot cause a false
                // negative; the requirement is "no raw mic audio persisted".
                var afterFiles = Directory.GetFiles(Application.temporaryCachePath, "*", SearchOption.AllDirectories);
                foreach (var f in afterFiles)
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    bool isAudio = ext == ".wav" || ext == ".raw" || ext == ".pcm" || ext == ".bin" || ext == ".mp3" || ext == ".ogg";
                    Assert.That(isAudio, Is.False, $"capture must not persist audio files, found: {f}");
                }

                // No log line may mention frame payload / raw audio content.
                foreach (var line in logs)
                {
                    Assert.That(line.ToLowerInvariant(), Does.Not.Contain("payload"), "must never log frame payload");
                    Assert.That(line.ToLowerInvariant(), Does.Not.Contain("raw audio"), "must never log raw audio content");
                }
            }
            finally
            {
                // Unsubscribe even if an assertion throws so the test never leaks a handler.
                Application.logMessageReceived -= CaptureLog;
            }
        }
    }
}
