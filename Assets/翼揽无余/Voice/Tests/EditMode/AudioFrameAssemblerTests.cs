using NUnit.Framework;
using Yilan.Voice.Runtime.Capture;

namespace Yilan.Voice.Capture
{
    /// <summary>
    /// EditMode tests for <see cref="Yilan.Voice.Runtime.Capture.AudioFrameAssembler"/>
    /// (VR3-T08). Synchronous numeric assertions only. Verifies exact 320-sample framing,
    /// cross-Push accumulation, 640-byte frames, the 750-frame / 480000-byte cap, and full
    /// rejection of frame 751 without out-of-bounds writes.
    /// </summary>
    [TestFixture]
    public class AudioFrameAssemblerTests
    {
        private const int FrameSize = Pcm16AudioConstants.SamplesPerFrame; // 320
        private const int BytesPerFrame = Pcm16AudioConstants.BytesPerFrame; // 640
        private const int MaxFrames = Pcm16AudioConstants.MaxFrames; // 750

        // ---- Helpers ----

        private static float[] Ramp(int count)
        {
            var a = new float[count];
            for (int i = 0; i < count; i++) a[i] = (i - count / 2f) / (count / 2f);
            return a;
        }

        private static byte[] DrainAll(AudioFrameAssembler a)
        {
            // Collect all ready frames into a single byte buffer (test-side convenience).
            var result = new byte[BytesPerFrame * a.FrameCount];
            int pos = 0;
            byte[] f;
            while (a.TryNextFrame(out f))
            {
                Assert.AreEqual(BytesPerFrame, f.Length);
                for (int i = 0; i < f.Length; i++) result[pos++] = f[i];
            }
            return result;
        }

        // ---- Exact 320-sample framing ----

        [Test]
        public void PushExactly320Samples_ProducesSingle640ByteFrame()
        {
            var a = new AudioFrameAssembler();
            var mono = Ramp(FrameSize);

            Assert.AreEqual(PushResult.Ok, a.Push(mono));
            Assert.AreEqual(1, a.FrameCount);
            Assert.IsFalse(a.IsFull);

            byte[] frame;
            Assert.IsTrue(a.TryNextFrame(out frame));
            Assert.AreEqual(BytesPerFrame, frame.Length);
            // No more frames ready.
            Assert.IsFalse(a.TryNextFrame(out frame));
        }

        // ---- Cross-Push accumulation ----

        [Test]
        public void Push100Then220_AccumulatesIntoOneFrameAcrossCalls()
        {
            var a = new AudioFrameAssembler();
            var first = Ramp(100);
            var second = Ramp(220);

            Assert.AreEqual(PushResult.Ok, a.Push(first));
            Assert.AreEqual(0, a.FrameCount); // not yet a full frame

            byte[] frame;
            Assert.IsFalse(a.TryNextFrame(out frame));

            Assert.AreEqual(PushResult.Ok, a.Push(second));
            Assert.AreEqual(1, a.FrameCount); // 100 + 220 = 320 -> one frame
            Assert.IsTrue(a.TryNextFrame(out frame));
            Assert.AreEqual(BytesPerFrame, frame.Length);
            Assert.IsFalse(a.TryNextFrame(out frame)); // exhausted
        }

        // ---- Partial remainder across frame boundary ----

        [Test]
        public void Push641Samples_YieldsTwoFramesAndOneLeftoverSample()
        {
            var a = new AudioFrameAssembler();
            var mono = Ramp(641);

            Assert.AreEqual(PushResult.Ok, a.Push(mono));
            Assert.AreEqual(2, a.FrameCount); // floor(641/320) = 2

            byte[] frame;
            Assert.IsTrue(a.TryNextFrame(out frame));
            Assert.AreEqual(640, frame.Length);
            Assert.IsTrue(a.TryNextFrame(out frame));
            Assert.AreEqual(640, frame.Length);
            // The 1 leftover sample is not a frame yet.
            Assert.IsFalse(a.TryNextFrame(out frame));
        }

        // ---- Content round-trip through PCM16 ----

        [Test]
        public void FrameContent_EncodesMono_Pcm16DecodesBackApproximately()
        {
            var a = new AudioFrameAssembler();
            var mono = Ramp(FrameSize);
            a.Push(mono);

            byte[] frame;
            Assert.IsTrue(a.TryNextFrame(out frame));
            float[] decoded = Pcm16Codec.Decode(frame);
            Assert.AreEqual(FrameSize, decoded.Length);
            for (int i = 0; i < FrameSize; i++)
            {
                // 16-bit quantization tolerance (~1/32768 well under 1e-3).
                Assert.AreEqual(mono[i], decoded[i], 1e-3f, "sample " + i);
            }
        }

        // ---- 750-frame / 480000-byte cap ----

        [Test]
        public void Cap_750Frames_Then751stPush_IsRejectedFull_NoOverflow()
        {
            var a = new AudioFrameAssembler();
            var chunk = Ramp(FrameSize);

            // Push 750 full frames.
            for (int i = 0; i < MaxFrames; i++)
            {
                Assert.AreEqual(PushResult.Ok, a.Push(chunk), "frame index " + i);
            }
            Assert.AreEqual(MaxFrames, a.FrameCount);
            Assert.IsFalse(a.IsFull); // reached the exact cap, still "ok"; flag flips on the rejected one

            // The 751st frame must be rejected with Full, and must not add a frame.
            Assert.AreEqual(PushResult.Full, a.Push(chunk));
            Assert.IsTrue(a.IsFull);
            Assert.AreEqual(MaxFrames, a.FrameCount); // still 750

            // No out-of-bounds: exactly 750 usable 640-byte frames remain readable.
            byte[] frame;
            int readable = 0;
            while (a.TryNextFrame(out frame))
            {
                Assert.AreEqual(BytesPerFrame, frame.Length);
                readable++;
            }
            Assert.AreEqual(MaxFrames, readable);
        }

        [Test]
        public void Cap_ByteTotal_Equals750Times640_MatchesAuthoritativeMaxAudioBytes()
        {
            var a = new AudioFrameAssembler();
            var chunk = Ramp(FrameSize);
            for (int i = 0; i < MaxFrames; i++) a.Push(chunk);

            byte[] all = DrainAll(a);
            Assert.AreEqual(Pcm16AudioConstants.MaxAudioBytes, all.Length); // 480000
        }

        // ---- Empty assembler ----

        [Test]
        public void TryNextFrame_OnEmpty_ReturnsFalseAndNull()
        {
            var a = new AudioFrameAssembler();
            byte[] frame;
            Assert.IsFalse(a.TryNextFrame(out frame));
            Assert.IsNull(frame);
            Assert.AreEqual(0, a.FrameCount);
        }

        // ---- Reset clears state ----

        [Test]
        public void Reset_ClearsProducedFramesAndFullFlag()
        {
            var a = new AudioFrameAssembler();
            var chunk = Ramp(FrameSize);
            for (int i = 0; i < MaxFrames; i++) a.Push(chunk);
            Assert.AreEqual(PushResult.Full, a.Push(chunk)); // confirm we're full
            Assert.IsTrue(a.IsFull);

            a.Reset();
            Assert.AreEqual(0, a.FrameCount);
            Assert.IsFalse(a.IsFull);
            byte[] frame;
            Assert.IsFalse(a.TryNextFrame(out frame));

            // Usable again.
            Assert.AreEqual(PushResult.Ok, a.Push(chunk));
            Assert.AreEqual(1, a.FrameCount);
        }

        // ---- Single-source constant wiring / injection ----

        [Test]
        public void DefaultConstructor_UsesAuthoritativeProtocolParameters()
        {
            var a = new AudioFrameAssembler();
            Assert.AreEqual(Pcm16AudioConstants.SamplesPerFrame, a.FrameSize);
            Assert.AreEqual(Pcm16AudioConstants.BytesPerFrame, a.BytesPerFrame);
            Assert.AreEqual(Pcm16AudioConstants.MaxFrames, a.MaxFrames);
            Assert.AreEqual(Pcm16AudioConstants.MaxAudioBytes, a.MaxAudioBytes);
        }

        [Test]
        public void CustomConstructor_InjectsFrameAndCapParameters()
        {
            var a = new AudioFrameAssembler(frameSize: 8, bytesPerFrame: 16, maxFrames: 3, maxAudioBytes: 48);
            Assert.AreEqual(8, a.FrameSize);
            Assert.AreEqual(16, a.BytesPerFrame);
            Assert.AreEqual(3, a.MaxFrames);
            Assert.AreEqual(48, a.MaxAudioBytes);

            // 3 frames of 8 samples cap.
            var chunk = new float[8];
            for (int i = 0; i < 8; i++) chunk[i] = 0f;
            Assert.AreEqual(PushResult.Ok, a.Push(chunk));
            Assert.AreEqual(PushResult.Ok, a.Push(chunk));
            Assert.AreEqual(PushResult.Ok, a.Push(chunk));
            Assert.AreEqual(PushResult.Full, a.Push(chunk)); // frame 4 rejected
            Assert.AreEqual(3, a.FrameCount);
        }
    }
}
