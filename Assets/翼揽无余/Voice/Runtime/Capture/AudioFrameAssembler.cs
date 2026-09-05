using System;

namespace Yilan.Voice.Runtime.Capture
{
    /// <summary>
    /// Slices a mono float stream (already down-mixed and resampled to 16 kHz) into
    /// protocol audio frames of 320 samples / 640 PCM16 little-endian bytes, with a
    /// hard cap of 750 frames / 480000 bytes (15 s).
    ///
    /// No per-frame managed allocation: the 640-byte frame buffers are pre-allocated
    /// once and recycled through a ring; the partial-frame accumulator is a reused
    /// float[FrameSize]. The audio thread only writes into this assembler while a
    /// separate (main) thread takes ready frames via TryNextFrame to send header+binary
    /// — this component does not touch the socket.
    /// </summary>
    public sealed class AudioFrameAssembler
    {
        private readonly byte[][] _buffers;   // ring of pre-allocated 640-byte frame buffers (size = MaxFrames = 750)
        private readonly float[] _partial;    // reused accumulator for partial frames
        private int _partialCount;

        private int _produceHead; // next free ring slot to write a completed frame
        private int _consumeHead; // next ready ring slot to hand out
        private int _readyCount;  // completed, not-yet-consumed frames
        private int _producedTotal; // total frames produced in the current session (capped at MaxFrames)
        private bool _isFull;

        public AudioFrameAssembler()
            : this(
                Pcm16AudioConstants.SamplesPerFrame,
                Pcm16AudioConstants.BytesPerFrame,
                Pcm16AudioConstants.MaxFrames,
                Pcm16AudioConstants.MaxAudioBytes)
        {
        }

        public AudioFrameAssembler(int frameSize, int bytesPerFrame, int maxFrames, int maxAudioBytes)
        {
            if (frameSize <= 0) throw new ArgumentOutOfRangeException(nameof(frameSize));
            if (bytesPerFrame < frameSize * 2) throw new ArgumentOutOfRangeException(nameof(bytesPerFrame),
                "bytesPerFrame must be at least frameSize * 2 (PCM16 = 2 bytes per sample).");
            if (maxFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
            FrameSize = frameSize;
            BytesPerFrame = bytesPerFrame;
            MaxFrames = maxFrames;
            MaxAudioBytes = maxAudioBytes;

            _buffers = new byte[maxFrames][];
            for (int i = 0; i < maxFrames; i++)
            {
                _buffers[i] = new byte[bytesPerFrame];
            }
            _partial = new float[frameSize];
        }

        public int FrameSize { get; }

        public int BytesPerFrame { get; }

        public int MaxFrames { get; }

        public int MaxAudioBytes { get; }

        /// <summary>Total completed (but possibly not yet consumed) frames produced in this session.</summary>
        public int FrameCount => _producedTotal;

        /// <summary>True once the 750-frame cap has been reached; further Push is rejected.</summary>
        public bool IsFull => _isFull;

        /// <summary>
        /// Accumulate mono float samples. Returns <see cref="PushResult.Ok"/> when accepted,
        /// or <see cref="PushResult.Full"/> when the 750-frame cap is reached and samples had
        /// to be rejected (state is left at the cap, nothing overflows or is written out of bounds).
        /// </summary>
        public PushResult Push(float[] mono)
        {
            if (mono == null) throw new ArgumentNullException(nameof(mono));
            if (_isFull)
            {
                // Already at cap: reject the whole input without touching state.
                return PushResult.Full;
            }

            AccumulateRaw(mono, mono.Length);

            // If the cap was reached WHILE accumulating (a frame boundary could not be
            // committed), the trailing samples of this push were rejected/rolled back:
            // report Full so the caller stops feeding (750 exactly => still Ok).
            return _isFull ? PushResult.Full : PushResult.Ok;
        }

        /// <summary>Take the oldest ready frame into <paramref name="frame"/> (a recycled 640-byte buffer). Returns false when none is ready.</summary>
        public bool TryNextFrame(out byte[] frame)
        {
            if (_readyCount == 0)
            {
                frame = null;
                return false;
            }
            frame = _buffers[_consumeHead];
            _consumeHead = (_consumeHead + 1) % _buffers.Length;
            _readyCount--;
            return true;
        }

        /// <summary>Clear all accumulated samples, produced frames and the full flag.</summary>
        public void Reset()
        {
            _partialCount = 0;
            _producedTotal = 0;
            _readyCount = 0;
            _produceHead = 0;
            _consumeHead = 0;
            _isFull = false;
        }

        private int AccumulateRaw(float[] mono, int count)
        {
            int idx = 0;
            while (idx < count)
            {
                int need = FrameSize - _partialCount;
                int take = Math.Min(need, count - idx);
                Array.Copy(mono, idx, _partial, _partialCount, take);
                _partialCount += take;
                idx += take;

                if (_partialCount == FrameSize)
                {
                    if (!TryAddFrame())
                    {
                        // Cap reached at this frame boundary; leave what remains unconsumed.
                        // Drop the just-assembled un-committed frame so the partial buffer does not
                        // masquerade as a pending frame (IsFull makes all subsequent Push rejected anyway).
                        _partialCount = 0;
                        return idx;
                    }
                    _partialCount = 0;
                }
            }
            return idx;
        }

        private bool TryAddFrame()
        {
            if (_producedTotal >= MaxFrames)
            {
                _isFull = true;
                return false;
            }
            // Encode the completed mono frame into the pre-allocated 640-byte buffer (no allocation).
            Pcm16Codec.EncodeInterleavedToBuffer(_partial, 1, 0, FrameSize, _buffers[_produceHead], 0);
            _produceHead = (_produceHead + 1) % _buffers.Length;
            _readyCount++;
            _producedTotal++;
            return true;
        }
    }

    /// <summary>Result of pushing a batch of mono samples into the frame assembler.</summary>
    public enum PushResult
    {
        /// <summary>All samples accepted; zero or more complete frames may now be available.</summary>
        Ok = 0,

        /// <summary>The 750-frame / 480000-byte cap was reached and some samples were rejected.</summary>
        Full = 1,
    }
}
