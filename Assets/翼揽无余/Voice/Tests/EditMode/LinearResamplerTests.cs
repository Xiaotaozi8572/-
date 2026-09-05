using System;
using System.Collections.Generic;
using NUnit.Framework;
using Yilan.Voice.Runtime.Capture;

namespace Yilan.Voice.Capture
{
    /// <summary>
    /// EditMode tests for <see cref="Yilan.Voice.Runtime.Capture.LinearResampler"/>
    /// (VR3-T08). Synchronous numeric assertions only (ext.nunit 3.5 lacks async APIs).
    /// Deterministic controls: 48k->16k is an exact integer 3:1 decimation, a constant
    /// signal is reconstructed exactly by linear interpolation, and the chunked-vs-whole
    /// comparison is the primary cross-block phase-continuity check.
    /// </summary>
    [TestFixture]
    public class LinearResamplerTests
    {
        private const int Target = Pcm16AudioConstants.SampleRateHz; // 16000

        // ---- Helpers ----

        private static float[] ResampleWhole(float[] input, int sourceRate, int targetRate)
        {
            var r = new LinearResampler(sourceRate, targetRate);
            r.Feed(input, 1);
            r.Flush();
            return Drain(r, input.Length, sourceRate, targetRate);
        }

        private static float[] ResampleChunked(float[] input, int sourceRate, int targetRate, int[] chunkSizes)
        {
            var r = new LinearResampler(sourceRate, targetRate);
            int pos = 0;
            foreach (int c in chunkSizes)
            {
                var slice = new float[c];
                Array.Copy(input, pos, slice, 0, c);
                r.Feed(slice, 1);
                pos += c;
            }
            r.Flush();
            return Drain(r, input.Length, sourceRate, targetRate);
        }

        private static float[] Drain(LinearResampler r, int inLen, int sourceRate, int targetRate)
        {
            int cap = (int)Math.Ceiling(inLen * ((double)targetRate / sourceRate)) + 4;
            var buf = new float[cap];
            int n = 0;
            int read;
            while (n < buf.Length && (read = r.Read(buf, n, buf.Length - n)) > 0)
            {
                n += read;
            }
            var outArr = new float[n];
            Array.Copy(buf, outArr, n);
            return outArr;
        }

        private static int[] BuildChunkSizes(int total)
        {
            int[] pattern = { 1, 7, 320, 13, 512, 5, 64, 9, 33, 1024 };
            var sizes = new List<int>();
            int acc = 0;
            int pi = 0;
            while (acc < total)
            {
                int s = pattern[pi % pattern.Length];
                pi++;
                if (acc + s > total) s = total - acc;
                if (s <= 0) break;
                sizes.Add(s);
                acc += s;
            }
            return sizes.ToArray();
        }

        private static void AssertArraysEqualWithin(float[] a, float[] b, float tol)
        {
            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i], b[i], tol, "index " + i);
            }
        }

        // ---- 48k -> 16k exact integer decimation ----

        [Test]
        public void Downsample48kTo16k_IntegerRatio3_ExactDecimation()
        {
            int nIn = 48000; // 1 second of 48 kHz
            var input = new float[nIn];
            for (int i = 0; i < nIn; i++) input[i] = i; // distinct integers, exactly representable

            float[] outArr = ResampleWhole(input, 48000, Target);

            Assert.AreEqual(16000, outArr.Length); // 1 s of 48k -> 1 s of 16k = 16000
            for (int o = 0; o < outArr.Length; o++)
            {
                Assert.AreEqual(input[3 * o], outArr[o], "decimation sample " + o); // 3:1, frac = 0
            }
        }

        // ---- 44.1k -> 16k constant (linear interpolation reconstructs constants exactly) ----

        [Test]
        public void Downsample44k1To16k_Constant_AllOutputsEqualConstant_OneSecondCount()
        {
            int nIn = 44100; // 1 second of 44.1 kHz
            var input = new float[nIn];
            for (int i = 0; i < nIn; i++) input[i] = 0.42f;

            float[] outArr = ResampleWhole(input, 44100, Target);

            // 44.1k has a non-integer 441/160 ratio that is NOT exactly representable in a
            // binary double (441/160 denominator contains factor 5), so the accumulated
            // _srcPos drifts ~ -1.3e-8 below the 44100 boundary and Flush emits ONE extra
            // tail sample via its hold-the-last-sample rule -> expected 16001, not 16000.
            // Per spec the resampler must be accurate to <= 1 sample (chunked==whole
            // |count diff| <= 1), so assert the [16000, 16001] range, not an exact count.
            Assert.GreaterOrEqual(outArr.Length, 16000);
            Assert.LessOrEqual(outArr.Length, 16001);
            foreach (float v in outArr)
            {
                Assert.AreEqual(0.42f, v, 1e-6f);
            }
        }

        // ---- Cross-block phase continuity: chunked feeding == whole feeding ----

        [Test]
        public void ChunkedVsWhole_44k1To16k_SamplesAreBitIndependentOfChunking()
        {
            int nIn = 44100;
            var input = new float[nIn];
            for (int i = 0; i < nIn; i++) input[i] = (float)(Math.Sin(i * 0.01) * 0.9);

            float[] whole = ResampleWhole(input, 44100, Target);
            float[] chunked = ResampleChunked(input, 44100, Target, BuildChunkSizes(nIn));

            // Primary requirement: chunked and whole produce the same sample sequence.
            AssertArraysEqualWithin(whole, chunked, 0f);
            Assert.AreEqual(whole.Length, chunked.Length);
        }

        [Test]
        public void ChunkedVsWhole_48kTo16k_SamplesAreBitIndependentOfChunking()
        {
            int nIn = 48000;
            var input = new float[nIn];
            for (int i = 0; i < nIn; i++) input[i] = (float)(Math.Sin(i * 0.005) * 0.8);

            float[] whole = ResampleWhole(input, 48000, Target);
            float[] chunked = ResampleChunked(input, 48000, Target, BuildChunkSizes(nIn));

            AssertArraysEqualWithin(whole, chunked, 0f);
            Assert.AreEqual(whole.Length, chunked.Length);
        }

        [Test]
        public void ChunkedVsWhole_CountError_IsZero_WithinOneSampleRequirement()
        {
            int nIn = 44100 + 400; // a non-1s length exercising the tail / remainder path
            var input = new float[nIn];
            var rnd = new System.Random(123);
            for (int i = 0; i < nIn; i++) input[i] = (float)(rnd.NextDouble() * 2.0 - 1.0);

            float[] whole = ResampleWhole(input, 44100, Target);
            float[] chunked = ResampleChunked(input, 44100, Target, BuildChunkSizes(nIn));

            Assert.AreEqual(whole.Length, chunked.Length);
            // Explicit rendering of the <= 1 sample-count requirement (we achieve exactly 0).
            Assert.LessOrEqual(Math.Abs(whole.Length - chunked.Length), 1);
        }

        // ---- Remainder / tail handling ----

        [Test]
        public void Remainder_TailFlush_EmitsTrailingSamples_WithinExpectedBounds()
        {
            int nIn = 48050; // 48000 + 50: leaves a partial output that only Flush can emit
            var input = new float[nIn];
            for (int i = 0; i < nIn; i++) input[i] = (float)(i * 0.0001);

            float[] outArr = ResampleWhole(input, 48000, Target);

            int analytical = nIn / 3; // floor(48050/3) = 16016
            Assert.LessOrEqual(outArr.Length, analytical + 2);
            Assert.GreaterOrEqual(outArr.Length, analytical);
            // The remainder must still be chunk/whole-consistent.
            float[] chunked = ResampleChunked(input, 48000, Target, BuildChunkSizes(nIn));
            AssertArraysEqualWithin(outArr, chunked, 0f);
        }

        // ---- Reset clears phase ----

        [Test]
        public void Reset_ClearsPhase_SecondRunMatchesFreshStream()
        {
            int nIn = 8820; // 0.2 s of 44.1k
            var input = new float[nIn];
            for (int i = 0; i < nIn; i++) input[i] = (float)(Math.Sin(i * 0.02) * 0.7);

            // First run on a fresh instance.
            float[] fresh = ResampleWhole(input, 44100, Target);

            // Same input through a reused instance after Reset must equal the fresh run.
            var r = new LinearResampler(44100, Target);
            r.Feed(input, 1);
            r.Flush();
            r.Reset(); // simulate the phase/state being cleared
            r.Feed(input, 1); // feed the same stream again
            r.Flush();
            float[] reused = Drain(r, nIn, 44100, Target);

            AssertArraysEqualWithin(fresh, reused, 0f);
        }

        // ---- Channel stride (interleaved stereo -> resample one channel) ----

        [Test]
        public void StereoChannelStride_ResamplesOnlyRequestedChannel()
        {
            int perChannel = 4800; // 0.1 s of 48k
            // Channel 0 = constant 0.25, Channel 1 = constant -0.5 (interleaved L,R,L,R...).
            var stereo = new float[perChannel * 2];
            for (int i = 0; i < perChannel; i++)
            {
                stereo[i * 2] = 0.25f;      // channel 0
                stereo[i * 2 + 1] = -0.5f;  // channel 1
            }

            var r = new LinearResampler(48000, Target);
            r.Feed(stereo, 2); // stride 2 selects channel 0
            r.Flush();

            var buf = new float[2048];
            int n = 0;
            int read;
            while (n < buf.Length && (read = r.Read(buf, n, buf.Length - n)) > 0) n += read;

            Assert.AreEqual(perChannel / 3, n); // 4800 -> 1600
            for (int i = 0; i < n; i++)
            {
                // Constant channel 0 reconstructs to 0.25 (channel 1's -0.5 must never leak in).
                Assert.AreEqual(0.25f, buf[i], 1e-6f, "channel-0 sample " + i);
            }
        }

        // ---- Single-source constant wiring ----

        [Test]
        public void DefaultConstructor_TargetsProtocol16kHz()
        {
            var r = new LinearResampler(48000); // no target -> 16 kHz
            Assert.AreEqual(16000, r.TargetRate);
            Assert.AreEqual(48000, r.SourceRate);
            Assert.AreEqual(Pcm16AudioConstants.SampleRateHz, r.TargetRate);
        }
    }
}
