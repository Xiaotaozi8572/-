// UnityMicrophoneSource.cs
// VR3-T09: wraps UnityEngine.Microphone (UnityMicrophoneDevice) behind a narrow
// IMicrophoneDevice, then converts the real-time device stream into protocol frames
// (16 kHz mono PCM16, 320 samples / 640 bytes) via the existing LinearResampler +
// AudioFrameAssembler pipeline.
//
// Pointer wrap-around: the device exposes a raw write cursor (GetPosition) that wraps
// within [0, CapacitySamples). This source keeps the previous cursor, computes the
// newly-written interleaved window spanning the wrap boundary, reads it, down-mixes it to
// mono and feeds the resampler/assembler. A mic whose position wraps is therefore handled
// the same as a normal one, and frame counts stay exact.
//
// Privacy: this source NEVER writes raw audio to disk, NEVER logs frame payload content and
// logs only state transitions / frame counts / sizes. There is intentionally no file I/O
// anywhere in this file.
using System;
using UnityEngine;

namespace Yilan.Voice.Runtime.Capture
{
    /// <summary>
    /// Minimal normalized view over a real-time capturing device. The device records into an
    /// internal ring of <see cref="CapacitySamples"/> interleaved float samples; its write
    /// cursor (<see cref="GetPosition"/>) wraps in [0, CapacitySamples). <see cref="GetData"/>
    /// reads a contiguous interleaved ring segment (it must handle wrapping internally so the
    /// source can issue plain non-wrapping reads).
    /// </summary>
    public interface IMicrophoneDevice : IDisposable
    {
        string DeviceName { get; }

        int Channels { get; }

        int DeviceSampleRate { get; }

        bool IsRecording { get; }

        /// <summary>Start the device; returns true when it is actually recording.</summary>
        bool Start(string deviceName, int sampleRate, int channels, int lengthSec);

        void Stop();

        /// <summary>Raw write cursor in interleaved samples; wraps in [0, CapacitySamples).</summary>
        int GetPosition();

        /// <summary>Capacity of the internal ring in interleaved samples.</summary>
        int CapacitySamples { get; }

        /// <summary>Copy <paramref name="sampleCount"/> interleaved samples from ring index <paramref name="startSample"/> (mod capacity) into <paramref name="dest"/> at <paramref name="destOffset"/>.</summary>
        void GetData(float[] dest, int destOffset, int startSample, int sampleCount);
    }

    /// <summary>
    /// Real microphone wrapper around UnityEngine.Microphone. Unity's microphone ring buffer
    /// length is <c>sampleRate * lengthSec</c>, GetPosition returns the write cursor which
    /// wraps across that boundary, and the audio samples are read back through the
    /// AudioClip returned by <c>Microphone.Start</c> (Unity's Microphone exposes no GetData
    /// of its own; the clip produced is mono on mobile).
    ///
    /// Unity Mobile microphones are mono: the clip has one channel, and the effective sample
    /// rate is the one requested after being snapped to the device's supported range reported
    /// by Microphone.GetDeviceCaps.
    /// </summary>
    public sealed class UnityMicrophoneDevice : IMicrophoneDevice
    {
        public string DeviceName { get; private set; } = string.Empty;

        public int Channels { get; private set; } = 1;

        public int DeviceSampleRate { get; private set; } = 0;

        public bool IsRecording { get; private set; }

        public int CapacitySamples { get; private set; }

        private AudioClip _clip;

        public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
        {
            try
            {
                string chosen = NormalizeDeviceName(deviceName);
                DeviceName = chosen;
                // Unity's Microphone clip is always mono regardless of the requested channel
                // count, so pin the exposed channel count to 1 (down-mix then degrades to a
                // pass-through, which is exactly what a mono clip needs).
                Channels = 1;

                if (Microphone.IsRecording(chosen))
                {
                    Microphone.End(chosen);
                }

                int capsFreq = SnapFrequency(chosen, sampleRate);
                DeviceSampleRate = capsFreq;

                // loop=true: the ring must wrap continuously for streaming capture. With
                // loop=false Unity stops writing after lengthSec and the cursor freezes,
                // which silently ends the audio feed mid-utterance.
                _clip = Microphone.Start(chosen, true, Math.Max(1, lengthSec), capsFreq);
                IsRecording = Microphone.IsRecording(chosen) && _clip != null;
                if (IsRecording)
                {
                    // The ring's sample capacity is the produced clip length (mono => per-channel
                    // samples == total frames).
                    CapacitySamples = Math.Max(1, _clip != null ? _clip.samples : capsFreq * Math.Max(1, lengthSec));
                }
                return IsRecording;
            }
            catch (Exception)
            {
                IsRecording = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!string.IsNullOrEmpty(DeviceName) && Microphone.IsRecording(DeviceName))
            {
                Microphone.End(DeviceName);
            }
            IsRecording = false;
            _clip = null;
        }

        public int GetPosition()
        {
            return string.IsNullOrEmpty(DeviceName) ? 0 : Microphone.GetPosition(DeviceName);
        }

        public void GetData(float[] dest, int destOffset, int startSample, int sampleCount)
        {
            if (_clip == null || sampleCount <= 0) return;
            // AudioClip.GetData copies sampleCount samples starting at the clip offset into a
            // temporary, then we splice into the caller's ring window. The source only ever asks
            // for windows fully inside [0, CapacitySamples), so no clip-end wrap is triggered.
            float[] temp = new float[sampleCount];
            _clip.GetData(temp, Math.Max(0, startSample));
            Array.Copy(temp, 0, dest, destOffset, sampleCount);
        }

        public void Dispose()
        {
            Stop();
        }

        private static string NormalizeDeviceName(string deviceName)
        {
            if (!string.IsNullOrEmpty(deviceName)) return deviceName;
            var devices = Microphone.devices;
            if (devices != null && devices.Length > 0) return devices[0];
            return string.Empty;
        }

        private static int SnapFrequency(string deviceName, int requested)
        {
            Microphone.GetDeviceCaps(deviceName, out int minF, out int maxF);
            if (minF <= 0 || maxF <= 0 || maxF < minF)
            {
                // No usable caps reported: keep the requested rate (Microphone will fall back).
                return requested;
            }
            if (requested < minF) return minF;
            if (requested > maxF) return maxF;
            return requested;
        }
    }

    /// <summary>
    /// Converts the device's raw float stream into protocol frames at 16 kHz mono.
    ///
    /// Typical use: call <see cref="Start(IMicrophoneDevice)"/> once permission is granted,
    /// then call <see cref="PollOnce"/> once per frame window (e.g. in Update) and drain
    /// ready frames with <see cref="TryTakeFrame"/>. Start refuses to begin when the
    /// <paramref name="isRecordingAllowed"/> gate is false (permission not granted), so the
    /// denied / permanently-denied paths never start capture.
    /// </summary>
    public sealed class UnityMicrophoneSource : IDisposable
    {
        private readonly IMicrophoneDevice _device;
        private readonly Func<bool> _isRecordingAllowed;
        private readonly AudioFrameAssembler _assembler;
        private LinearResampler _resampler; // null when the device is already 16 kHz

        private float[] _block;       // reused interleaved read block (bounded by capacity)
        private float[] _mono;        // reused mono (down-mixed) buffer
        private float[] _resampled;   // reused post-resample buffer
        private int _lastPos = -1;    // previous device write cursor
        private bool _started;

        public UnityMicrophoneSource(IMicrophoneDevice device, Func<bool> isRecordingAllowed)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _isRecordingAllowed = isRecordingAllowed ?? throw new ArgumentNullException(nameof(isRecordingAllowed));
            _assembler = new AudioFrameAssembler();
        }

        /// <summary>Total frames produced by the framer (unconsumed + already taken).</summary>
        public int FrameCount => _assembler.FrameCount;

        public bool IsRecording => _started && _device.IsRecording;

        /// <summary>True once the 750-frame / 15 s assembly cap is reached.</summary>
        public bool IsFull => _assembler.IsFull;

        /// <summary>
        /// Start capture. Returns false (and does not start) when the recording gate is not
        /// allowed (permission not granted) or the underlying device fails to start.
        /// </summary>
        public bool Start(string deviceName, int sampleRate, int channels, int lengthSec)
        {
            if (!_isRecordingAllowed())
            {
                _started = false;
                _lastPos = -1;
                return false;
            }
            if (!_device.Start(deviceName, sampleRate, channels, lengthSec))
            {
                _started = false;
                _lastPos = -1;
                return false;
            }
            _started = true;
            // Establish the write-cursor baseline immediately so the first PollOnce does not
            // discard a whole window of real data (right after start the cursor is 0).
            _lastPos = _device.GetPosition();

            int channels_ = Math.Max(1, _device.Channels);
            int capacity = Math.Max(1, _device.CapacitySamples);
            _block = new float[capacity];

            _resampler = _device.DeviceSampleRate == Pcm16AudioConstants.SampleRateHz
                ? null
                : new LinearResampler(_device.DeviceSampleRate, Pcm16AudioConstants.SampleRateHz);
            _assembler.Reset();
            return true;
        }

        public void Stop()
        {
            if (_started)
            {
                _device.Stop();
                _started = false;
                _lastPos = -1;
                LogState("stop");
            }
        }

        /// <summary>
        /// Read newly-available device samples, convert them and assemble frames. Call once per
        /// frame window. Returns the number of frames made ready by this poll (callers use
        /// <see cref="TryTakeFrame"/> to drain them).
        /// </summary>
        public int PollOnce()
        {
            if (!_started || !_device.IsRecording)
            {
                return 0;
            }

            int capacity = Math.Max(1, _device.CapacitySamples);
            int pos = _device.GetPosition();
            if (_lastPos < 0)
            {
                _lastPos = pos;
                return 0; // first observation: only establish the baseline cursor
            }

            int delta = (pos - _lastPos + capacity) % capacity;
            int consumedPos = _lastPos;
            _lastPos = pos;
            if (delta <= 0)
            {
                return 0;
            }

            ReadInterleaved(consumedPos, pos, capacity, delta);

            int channels = Math.Max(1, _device.Channels);
            int monoCount = delta / channels;
            if (monoCount <= 0)
            {
                return 0;
            }

            ResizeToExact(ref _mono, monoCount);
            DownmixToMono(_block, channels, _mono, monoCount);

            int before = _assembler.FrameCount;
            if (_resampler != null)
            {
                _resampler.Feed(_mono, 1);
                int avail = _resampler.Available;
                if (avail > 0)
                {
                    ResizeToExact(ref _resampled, avail);
                    _resampler.Read(_resampled, 0, avail);
                    _assembler.Push(_resampled);
                }
            }
            else
            {
                _assembler.Push(_mono);
            }

            return _assembler.FrameCount - before;
        }

        /// <summary>Take the oldest ready 640-byte protocol frame. Returns false when none is ready.</summary>
        public bool TryTakeFrame(out byte[] frame)
        {
            return _assembler.TryNextFrame(out frame);
        }

        /// <summary>Clear partial samples, buffered frames and the assembly cap without stopping the device.</summary>
        public void Reset()
        {
            _assembler.Reset();
            var r = _resampler;
            if (r != null) r.Reset();
            _lastPos = -1;
        }

        public void Dispose()
        {
            Stop();
        }

        private void ReadInterleaved(int lastPos, int pos, int capacity, int delta)
        {
            int first = Math.Min(delta, capacity - lastPos);
            if (first > 0)
            {
                _device.GetData(_block, 0, lastPos, first);
            }
            int rest = delta - first;
            if (rest > 0)
            {
                _device.GetData(_block, first, 0, rest);
            }
        }

        private static void DownmixToMono(float[] interleaved, int channels, float[] mono, int monoCount)
        {
            for (int s = 0; s < monoCount; s++)
            {
                int baseIdx = s * channels;
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    sum += interleaved[baseIdx + c];
                }
                mono[s] = sum / channels;
            }
        }

        private static void ResizeToExact(ref float[] buffer, int needed)
        {
            if (buffer == null || buffer.Length != needed)
            {
                Array.Resize(ref buffer, needed);
            }
        }

        private void LogState(string phase)
        {
            // Status-level logging only: never payload, never audio content, never frame bytes.
            Debug.Log($"[Yilan.Voice] capture {phase}: frames={_assembler.FrameCount}, bytes={_assembler.FrameCount * _assembler.BytesPerFrame}");
        }
    }
}
