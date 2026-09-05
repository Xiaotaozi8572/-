using System;

namespace Yilan.Voice.Runtime.Capture
{
    /// <summary>
    /// Simple linear interpolating resampler with cross-block phase continuity.
    ///
    /// The resampler keeps a fractional source position <c>_srcPos</c> (in source-sample
    /// units) in instance state, so feeding the same stream in arbitrarily sized chunks and
    /// feeding it all at once produce the SAME output sample sequence (chunk/whole count
    /// difference is exactly 0, comfortably within the required &lt;= 1). A reusable float
    /// buffer backs the source samples; no per-call large allocation is made.
    ///
    /// Interpolation rule: an output sample is produced only when both required source
    /// samples (i0 and i0+1) are REAL fed samples (i1 &lt; _srcFed, where _srcFed is the
    /// authoritative count of samples ever fed). Positions whose second neighbour is not yet
    /// available are deferred until the next Feed or the final Flush. Flush emits the tail by
    /// holding the last source sample, so the set of absolute output positions is purely a
    /// function of (source-rate/target-rate ratio, total fed samples) - identical for any
    /// chunking. _srcBase/_srcLen only index into the buffer and NEVER enlarge the authoritative
    /// fed boundary, so a chunk boundary can never fabricate an out-of-range output position.
    ///
    /// Target rate defaults to the protocol 16 kHz (Pcm16AudioConstants.SampleRateHz).
    /// </summary>
    public sealed class LinearResampler
    {
        private readonly double _ratio; // source samples per output sample

        private float[] _src = new float[256];
        private int _srcBase;   // absolute index of _src[0]
        private int _srcLen;    // number of currently buffered source samples
        private int _srcFed;    // authoritative total count of samples fed so far (never prunes backwards)

        private float[] _out = new float[256];
        private int _outStart;  // ring read cursor
        private int _outLen;    // produced-but-not-yet-read count

        private double _srcPos; // absolute fractional source position for the NEXT output sample

        public LinearResampler(int sourceRate, int targetRate)
        {
            if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
            if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
            SourceRate = sourceRate;
            TargetRate = targetRate;
            _ratio = (double)sourceRate / targetRate;
        }

        /// <summary>Resample to the protocol 16 kHz by default.</summary>
        public LinearResampler(int sourceRate)
            : this(sourceRate, Pcm16AudioConstants.SampleRateHz)
        {
        }

        public int SourceRate { get; }

        public int TargetRate { get; }

        /// <summary>Number of produced samples available to read right now.</summary>
        public int Available => _outLen;

        /// <summary>Feed source samples for one channel; <paramref name="channelStride"/> = 1 for mono.</summary>
        public void Feed(float[] input, int channelStride)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (channelStride < 1) throw new ArgumentOutOfRangeException(nameof(channelStride));
            int n = input.Length / channelStride; // full source samples in this channel
            EnsureSrc(_srcLen + n);
            for (int i = 0; i < n; i++)
            {
                _src[_srcLen++] = input[i * channelStride];
            }
            _srcFed += n; // authoritative: total real samples fed (cannot go backwards)
            TryProduce();
        }

        /// <summary>Copy up to <paramref name="count"/> produced samples into <paramref name="dest"/>, returning how many were copied.</summary>
        public int Read(float[] dest, int offset, int count)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            int n = Math.Min(count, _outLen);
            int cap = _out.Length;
            for (int i = 0; i < n; i++)
            {
                dest[offset + i] = _out[(_outStart + i) % cap];
            }
            _outStart = (_outStart + n) % cap;
            _outLen -= n;
            return n;
        }

        /// <summary>
        /// Emit the trailing (tail) output samples, holding the last source sample for the
        /// final fractional position. Returns how many samples were appended.
        /// </summary>
        public int Flush()
        {
            int added = 0;
            while (true)
            {
                int i0 = (int)Math.Floor(_srcPos);
                if (i0 >= _srcFed) break; // authoritative fed boundary: never emit beyond real data
                int local = i0 - _srcBase;
                if (local < 0 || local >= _srcLen) break; // sample no longer buffered
                double frac = _srcPos - i0;
                float v0 = _src[local];
                int i1 = i0 + 1;
                // Hold the last available real sample at the tail, but only read a buffered
                // real neighbour (never a stale/pruned slot).
                float v1 = (i1 < _srcFed && i1 - _srcBase >= 0 && i1 - _srcBase < _srcLen)
                    ? _src[i1 - _srcBase]
                    : v0;
                AppendOut((float)(v0 * (1.0 - frac) + v1 * frac));
                added++;
                _srcPos += _ratio;
            }
            return added;
        }

        /// <summary>Clear all phase, buffered source samples and pending output.</summary>
        public void Reset()
        {
            _srcBase = 0;
            _srcLen = 0;
            _srcFed = 0;
            _srcPos = 0.0;
            _outStart = 0;
            _outLen = 0;
        }

        private void TryProduce()
        {
            int guard = 0;
            while (true)
            {
                if (++guard > 1000000) { _srcPos = _srcFed; break; } // defensive: cannot make progress
                int i0 = (int)Math.Floor(_srcPos);
                int i1 = i0 + 1;
                // Need a REAL source sample at i1 (besides i0): the authoritative _srcFed
                // boundary makes this check independent of chunking, so chunked feeding can
                // never fabricate an out-of-range output position.
                if (i1 >= _srcFed) break;
                int local0 = i0 - _srcBase;
                if (local0 < 0) break;      // sample already pruned (defensive; cannot happen normally)
                if (local0 + 1 >= _srcLen) break; // not buffered yet (defensive)
                double frac = _srcPos - i0;
                float v0 = _src[local0];
                float v1 = _src[local0 + 1];
                AppendOut((float)(v0 * (1.0 - frac) + v1 * frac));
                _srcPos += _ratio;
                Prune();
            }
        }

        /// <summary>Drop source samples strictly older than the current interpolation foot, clamped to the real fed boundary.</summary>
        private void Prune()
        {
            int keepBase = (int)Math.Floor(_srcPos); // i0 of the next output
            if (keepBase > _srcFed) keepBase = _srcFed; // NEVER let the buffer base float past real data
            if (keepBase > _srcBase)
            {
                int drop = keepBase - _srcBase;
                if (drop < _srcLen)
                {
                    Array.Copy(_src, drop, _src, 0, _srcLen - drop);
                    _srcLen -= drop;
                }
                else
                {
                    _srcLen = 0;
                }
                _srcBase = keepBase;
            }
            if (_srcLen == 0) _srcBase = (int)Math.Floor(_srcPos);
            if (_srcBase > _srcFed) _srcBase = _srcFed; // defensive clamp
        }

        private void AppendOut(float val)
        {
            EnsureOut(_outLen + 1);
            _out[(_outStart + _outLen) % _out.Length] = val;
            _outLen++;
        }

        private void EnsureSrc(int needed)
        {
            if (needed <= _src.Length) return;
            int cap = _src.Length;
            while (cap < needed) cap *= 2;
            Array.Resize(ref _src, cap);
        }

        private void EnsureOut(int needed)
        {
            if (needed <= _out.Length) return;
            int cap = _out.Length;
            while (cap < needed) cap *= 2;
            // Rebase the ring so data stays contiguous before growing.
            if (_outLen > 0)
            {
                float[] grown = new float[cap];
                int capOld = _out.Length;
                for (int i = 0; i < _outLen; i++)
                {
                    grown[i] = _out[(_outStart + i) % capOld];
                }
                _out = grown;
                _outStart = 0;
            }
            else
            {
                Array.Resize(ref _out, cap);
                _outStart = 0;
            }
        }
    }
}
