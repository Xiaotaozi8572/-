using System;

namespace Yilan.Voice.Runtime.Capture
{
    /// <summary>
    /// Single source of the authoritative audio-frame constants for the voice capture
    /// pipeline. All three Capture components (Pcm16Codec, LinearResampler,
    /// AudioFrameAssembler) reference this one place; never re-hardcode these numbers
    /// anywhere else.
    ///
    /// Mirror sources:
    ///   - tests/fixtures/voice_ws_v1/audio_frame.json -> binary_spec
    ///       format PCM16 signed little-endian, sample_rate_hz 16000, channels 1,
    ///       frame_duration_ms 20, samples_per_frame 320, bytes_per_frame 640,
    ///       bytes_per_sample 2, max_payload_bytes 65536.
    ///   - configs/voice.yaml -> sample_rate 16000, frame_duration_ms 20,
    ///       max_frames 750, max_audio_bytes 480000.
    /// </summary>
    public static class Pcm16AudioConstants
    {
        /// <summary>Protocol sample rate in Hz (16000). Source: audio_frame.json binary_spec / voice.yaml.</summary>
        public const int SampleRateHz = 16000;

        /// <summary>Frame duration in milliseconds (20). Source: audio_frame.json / voice.yaml.</summary>
        public const int FrameDurationMs = 20;

        /// <summary>Samples per 20 ms protocol frame (16000 * 0.020 = 320). Source: audio_frame.json.</summary>
        public const int SamplesPerFrame = 320;

        /// <summary>Bytes per PCM16 mono sample (2). Source: audio_frame.json binary_spec.</summary>
        public const int BytesPerSample = 2;

        /// <summary>Bytes per 20 ms protocol frame (320 * 2 = 640). Source: audio_frame.json binary_spec.</summary>
        public const int BytesPerFrame = 640;

        /// <summary>Hard cap of 15 seconds of 20 ms frames (750). Source: voice.yaml max_frames.</summary>
        public const int MaxFrames = 750;

        /// <summary>Hard byte cap (750 * 640 = 480000). Source: voice.yaml max_audio_bytes.</summary>
        public const int MaxAudioBytes = 480000;
    }

    /// <summary>
    /// Float sample <-> PCM16 signed little-endian conversion, with multi-channel
    /// averaged down-mix. Hot-path methods never allocate (they fill caller buffers);
    /// allocating convenience overloads exist for tests / non-hot call sites.
    ///
    /// Saturation policy (mirrors the authoritative boundary expectations):
    ///   -1 -> -32768, 0 -> 0, +1 -> 32767.
    ///   out-of-range values clamp to the same short bounds.
    ///   NaN -> 0, +Inf -> 32767, -Inf -> -32768 (explicit, testable policy).
    /// Scaling uses 32768.0 so that a full-scale -1.0 maps symmetrically to
    /// short.MinValue and +1.0 rounds to short.MaxValue.
    /// </summary>
    public static class Pcm16Codec
    {
        /// <summary>Convert a single float sample to a clamped PCM16 short.</summary>
        public static short FloatToShort(float sample)
        {
            if (float.IsNaN(sample)) return 0;
            if (float.IsPositiveInfinity(sample)) return short.MaxValue;
            if (float.IsNegativeInfinity(sample)) return short.MinValue;
            if (sample <= -1f) return short.MinValue;
            if (sample >= 1f) return short.MaxValue;
            // Symmetric scaling: -1*32768 = -32768, +1*32768 = 32768 (clamped to 32767 by the >=1 guard above).
            double scaled = sample * 32768.0;
            long rounded = (long)Math.Round(scaled);
            if (rounded < short.MinValue) return short.MinValue;
            if (rounded > short.MaxValue) return short.MaxValue;
            return (short)rounded;
        }

        /// <summary>Write one PCM16 little-endian sample (2 bytes) into <paramref name="dest"/> at <paramref name="offset"/>.</summary>
        public static void Encode(float sample, byte[] dest, int offset)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (offset < 0 || offset + 1 >= dest.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            short v = FloatToShort(sample);
            dest[offset] = (byte)(v & 0xFF);          // low byte first (little-endian)
            dest[offset + 1] = (byte)((v >> 8) & 0xFF); // high byte
        }

        /// <summary>
        /// Down-mix an interleaved multi-channel float buffer to averaged mono and PCM16-encode it,
        /// writing <paramref name="sampleCount"/> * 2 little-endian bytes into <paramref name="dest"/>
        /// starting at <paramref name="destOffset"/>. Allocates nothing. This is the hot-path encoder
        /// used by AudioFrameAssembler against its pre-allocated frame buffer.
        /// </summary>
        public static void EncodeInterleavedToBuffer(
            float[] interleaved, int channelCount, int startSample, int sampleCount,
            byte[] dest, int destOffset)
        {
            if (interleaved == null) throw new ArgumentNullException(nameof(interleaved));
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
            if (startSample < 0 || sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(startSample));
            for (int s = 0; s < sampleCount; s++)
            {
                float sum = 0f;
                int baseIdx = (startSample + s) * channelCount;
                for (int ch = 0; ch < channelCount; ch++)
                {
                    sum += interleaved[baseIdx + ch];
                }
                float avg = sum / channelCount;
                Encode(avg, dest, destOffset + s * 2);
            }
        }

        /// <summary>Mono float[] -> PCM16 little-endian byte[] (allocating convenience for tests / non-hot paths).</summary>
        public static byte[] EncodeChunk(float[] mono)
        {
            if (mono == null) throw new ArgumentNullException(nameof(mono));
            byte[] outBytes = new byte[mono.Length * 2];
            EncodeInterleavedToBuffer(mono, 1, 0, mono.Length, outBytes, 0);
            return outBytes;
        }

        /// <summary>Interleaved multi-channel float[] -> averaged-mono PCM16 little-endian byte[] (allocating convenience).</summary>
        public static byte[] EncodeInterleaved(float[] interleaved, int channelCount)
        {
            if (interleaved == null) throw new ArgumentNullException(nameof(interleaved));
            if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
            int frames = interleaved.Length / channelCount;
            byte[] outBytes = new byte[frames * 2];
            EncodeInterleavedToBuffer(interleaved, channelCount, 0, frames, outBytes, 0);
            return outBytes;
        }

        /// <summary>Convert a PCM16 short back to a float sample (lossy inverse of FloatToShort).</summary>
        public static float ShortToFloat(short v) => (float)v / 32768f;

        /// <summary>Decode mono PCM16 little-endian bytes to float samples (test / playback side).</summary>
        public static float[] Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            return Decode(data, 0, data.Length / 2);
        }

        /// <summary>Decode <paramref name="sampleCount"/> mono PCM16 little-endian samples starting at byte <paramref name="offset"/>.</summary>
        public static float[] Decode(byte[] data, int offset, int sampleCount)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || sampleCount < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            float[] result = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                int b = offset + i * 2;
                short v = (short)(data[b] | (data[b + 1] << 8));
                result[i] = ShortToFloat(v);
            }
            return result;
        }
    }
}
