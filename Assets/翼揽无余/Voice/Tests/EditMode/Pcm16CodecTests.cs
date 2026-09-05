using NUnit.Framework;
using Yilan.Voice.Runtime.Capture;

namespace Yilan.Voice.Capture
{
    /// <summary>
    /// EditMode tests for <see cref="Yilan.Voice.Runtime.Capture.Pcm16Codec"/> and the
    /// single-source constants (VR3-T08). Pure numeric/synchronous assertions only —
    /// no async NUnit APIs (ext.nunit 3.5 omits DoesNotThrowAsync / ThrowsAsync).
    /// </summary>
    [TestFixture]
    public class Pcm16CodecTests
    {
        // ---- Boundary saturation ----

        [Test]
        public void FloatToShort_Boundaries_NegativeOneZeroPositiveOne()
        {
            Assert.AreEqual(short.MinValue, Pcm16Codec.FloatToShort(-1f));
            Assert.AreEqual(0, Pcm16Codec.FloatToShort(0f));
            Assert.AreEqual(short.MaxValue, Pcm16Codec.FloatToShort(1f));
        }

        [Test]
        public void FloatToShort_OutOfRange_Clamps()
        {
            // Any value below -1 or above +1 must clamp to the short bounds, incl. large values.
            Assert.AreEqual(short.MinValue, Pcm16Codec.FloatToShort(-2f));
            Assert.AreEqual(short.MinValue, Pcm16Codec.FloatToShort(-10f));
            Assert.AreEqual(short.MaxValue, Pcm16Codec.FloatToShort(2f));
            Assert.AreEqual(short.MaxValue, Pcm16Codec.FloatToShort(12345f));
        }

        [Test]
        public void FloatToShort_NaNInfinity_HaveExplicitPolicy()
        {
            // NaN -> 0, +Inf -> 32767, -Inf -> -32768 (documented, testable).
            Assert.AreEqual(0, Pcm16Codec.FloatToShort(float.NaN));
            Assert.AreEqual(short.MaxValue, Pcm16Codec.FloatToShort(float.PositiveInfinity));
            Assert.AreEqual(short.MinValue, Pcm16Codec.FloatToShort(float.NegativeInfinity));
        }

        [Test]
        public void FloatToShort_NormalValue_RoundsToNearest()
        {
            Assert.AreEqual(16384, Pcm16Codec.FloatToShort(0.5f));   // 0.5 * 32768 = 16384
            Assert.AreEqual(-16384, Pcm16Codec.FloatToShort(-0.5f)); // -0.5 * 32768 = -16384
            Assert.AreEqual(8192, Pcm16Codec.FloatToShort(0.25f));
        }

        // ---- Little-endian byte order ----

        [Test]
        public void Encode_Boundaries_LittleEndianBytes()
        {
            byte[] b = new byte[2];
            Pcm16Codec.Encode(-1f, b, 0); // -32768 = 0x8000 -> low 0x00, high 0x80
            Assert.AreEqual(new byte[] { 0x00, 0x80 }, b);

            Pcm16Codec.Encode(1f, b, 0);  // 32767 = 0x7FFF -> low 0xFF, high 0x7F
            Assert.AreEqual(new byte[] { 0xFF, 0x7F }, b);

            Pcm16Codec.Encode(0f, b, 0);
            Assert.AreEqual(new byte[] { 0x00, 0x00 }, b);
        }

        [Test]
        public void Encode_NegativeSmallValue_LittleEndian()
        {
            // -1 => -1 = 0xFFFF -> low 0xFF, high 0xFF
            byte[] b = new byte[2];
            Pcm16Codec.Encode(-1f / 32768f, b, 0);
            Assert.AreEqual(new byte[] { 0xFF, 0xFF }, b);
        }

        // ---- Round-trip decode ----

        [Test]
        public void Decode_RoundTrips_EncodedChunk()
        {
            float[] mono = { -1f, -0.5f, 0f, 0.5f, 1f };
            byte[] pcm = Pcm16Codec.EncodeChunk(mono);
            Assert.AreEqual(mono.Length * 2, pcm.Length);
            float[] back = Pcm16Codec.Decode(pcm);
            Assert.IsNotNull(back);
            Assert.AreEqual(-1f, back[0], 1e-4f); // -1 -> -32768 -> -1.0
            Assert.AreEqual(-0.5f, back[1], 1e-4f);
            Assert.AreEqual(0f, back[2], 1e-5f);
            Assert.AreEqual(0.5f, back[3], 1e-4f);
            Assert.AreEqual(1f, back[4], 1e-4f);
        }

        // ---- Multi-channel averaged down-mix ----

        [Test]
        public void EncodeInterleaved_Stereo_AveragesChannels()
        {
            // Two channels interleaved: samples are L,R,L,R...
            float[] stereo = { -1f, 1f, 0.5f, 0.5f, 0f, 0f };
            byte[] pcm = Pcm16Codec.EncodeInterleaved(stereo, 2);
            Assert.AreEqual(3 * 2, pcm.Length); // 3 output samples, 2 bytes each

            float[] mono = Pcm16Codec.Decode(pcm);
            Assert.AreEqual(3, mono.Length);
            Assert.AreEqual(0f, mono[0], 1e-4f);   // avg(-1, 1) = 0
            Assert.AreEqual(0.5f, mono[1], 1e-4f); // avg(0.5, 0.5) = 0.5
            Assert.AreEqual(0f, mono[2], 1e-5f);   // avg(0, 0) = 0
        }

        [Test]
        public void EncodeInterleaved_QuadChannel_AveragesAll()
        {
            // Four channels: all four equal -> average equals the shared value.
            float[] quad = { 0.25f, 0.25f, 0.25f, 0.25f, -0.75f, -0.75f, -0.75f, -0.75f };
            byte[] pcm = Pcm16Codec.EncodeInterleaved(quad, 4);
            float[] mono = Pcm16Codec.Decode(pcm);
            Assert.AreEqual(2, mono.Length);
            Assert.AreEqual(0.25f, mono[0], 1e-4f);
            Assert.AreEqual(-0.75f, mono[1], 1e-4f);
        }

        [Test]
        public void EncodeInterleavedToBuffer_WritesNoAllocation_MatchesAllocatingVersion()
        {
            float[] stereo = { 0.1f, 0.3f, -0.2f, 0.4f, 0.0f, 0.5f };
            byte[] expected = Pcm16Codec.EncodeInterleaved(stereo, 2);

            byte[] dest = new byte[6];
            Pcm16Codec.EncodeInterleavedToBuffer(stereo, 2, 0, 3, dest, 0);
            Assert.AreEqual(expected, dest);
        }

        [Test]
        public void EncodeInterleavedToBuffer_OffsetedStart_RespectsStartSample()
        {
            // Start at sample 1 of a mono stream: only that sample is encoded.
            float[] mono = { 0.0f, 0.5f, 1.0f, -1.0f };
            byte[] dest = new byte[4];
            Pcm16Codec.EncodeInterleavedToBuffer(mono, 1, 1, 2, dest, 0);
            float[] decoded = Pcm16Codec.Decode(dest);
            Assert.AreEqual(2, decoded.Length);
            Assert.AreEqual(0.5f, decoded[0], 1e-4f);
            Assert.AreEqual(1f, decoded[1], 1e-4f);
        }

        // ---- Single-source constants ----

        [Test]
        public void Constants_MatchAuthoritativeProtocolValues()
        {
            Assert.AreEqual(16000, Pcm16AudioConstants.SampleRateHz);
            Assert.AreEqual(20, Pcm16AudioConstants.FrameDurationMs);
            Assert.AreEqual(320, Pcm16AudioConstants.SamplesPerFrame);
            Assert.AreEqual(2, Pcm16AudioConstants.BytesPerSample);
            Assert.AreEqual(640, Pcm16AudioConstants.BytesPerFrame);
            Assert.AreEqual(750, Pcm16AudioConstants.MaxFrames);
            Assert.AreEqual(480000, Pcm16AudioConstants.MaxAudioBytes);

            // Derived relationships that must hold by construction.
            Assert.AreEqual(Pcm16AudioConstants.SampleRateHz * Pcm16AudioConstants.FrameDurationMs / 1000,
                Pcm16AudioConstants.SamplesPerFrame);
            Assert.AreEqual(Pcm16AudioConstants.SamplesPerFrame * Pcm16AudioConstants.BytesPerSample,
                Pcm16AudioConstants.BytesPerFrame);
            Assert.AreEqual(Pcm16AudioConstants.MaxFrames * Pcm16AudioConstants.BytesPerFrame,
                Pcm16AudioConstants.MaxAudioBytes);
        }
    }
}
