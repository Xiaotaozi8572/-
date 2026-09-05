using System;
using System.Collections.Generic;
using UnityEngine;

namespace Yilan.Voice.Runtime.Playback
{
    /// <summary>
    /// Bridges decoded TTS AudioClips onto one AudioSource (P0-5 fix).
    /// Mp3PlaybackQueue raises FramePlayed with the decoded clip but nothing used to
    /// drive audio output — the clips were decoded and silently dropped. This player
    /// plays them STRICTLY sequentially (in arrival order, never overlapping) and is
    /// driven by the owner's Update via <see cref="Tick"/>.
    ///
    /// Busy detection is "clip assigned AND isPlaying": a freshly created AudioSource
    /// can report isPlaying == true under Unity's dummy audio driver (batchmode /
    /// headless), so isPlaying alone must not be read as "channel busy".
    ///
    /// Barge-in silencing is owned by the composition root: the queue-level
    /// <see cref="Mp3PlaybackQueue.Cancel"/> only stops later frames, so the bootstrap
    /// also calls <see cref="Cancel"/> when the session controller leaves the active
    /// states via cancel / pause / reconnect / fault.
    /// </summary>
    public sealed class UnityTtsAudioPlayer : IDisposable
    {
        private readonly AudioSource _source;
        private readonly Queue<AudioClip> _pending = new Queue<AudioClip>();
        private bool _disposed;

        public UnityTtsAudioPlayer(AudioSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        /// <summary>Number of decoded clips waiting to be played.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>True while this channel is sounding one of our clips.</summary>
        private bool IsChannelBusy => _source.clip != null && _source.isPlaying;

        /// <summary>Queue one decoded clip; starts immediately when the channel is idle.</summary>
        public void Enqueue(AudioClip clip)
        {
            if (_disposed || clip == null) return;
            if (!IsChannelBusy)
            {
                Play(clip);
                return;
            }
            _pending.Enqueue(clip);
        }

        /// <summary>Advance to the next queued clip once the current one finished.
        /// Call from the owner's Update (main thread).</summary>
        public void Tick()
        {
            if (_disposed) return;
            if (_pending.Count == 0) return;
            if (IsChannelBusy) return;
            Play(_pending.Dequeue());
        }

        /// <summary>Stop the sounding clip and drop everything queued (barge-in).</summary>
        public void Cancel()
        {
            if (_disposed) return;
            _pending.Clear();
            if (_source.isPlaying)
            {
                _source.Stop();
            }
            _source.clip = null;
        }

        private void Play(AudioClip clip)
        {
            _source.clip = clip;
            _source.Play();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            if (_source.isPlaying)
            {
                _source.Stop();
            }
            _source.clip = null;
        }
    }
}
