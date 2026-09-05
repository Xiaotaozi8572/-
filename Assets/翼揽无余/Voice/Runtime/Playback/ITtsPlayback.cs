// ITtsPlayback.cs
// VR4-T11: the minimal playback contract exposed to upper layers (session controller, UI).
//
// The queue is owned and driven by the session layer in later integration (VR5-T12); this file
// only defines the surface a driver needs: feed paired MP3 TTS frames, observe per-frame playback
// outcomes, and barge-in cancel. All state (ordering / generation / temp-file lifecycle) lives in
// Mp3PlaybackQueue; this interface keeps the driver decoupled from that implementation.
using System;
using UnityEngine;
using Yilan.Voice.Runtime.Protocol;

namespace Yilan.Voice.Runtime.Playback
{
    /// <summary>Stable outcome code for feeding one tts.frame MP3 body into the playback queue.</summary>
    public enum TtsPlaybackCode
    {
        /// <summary>Frame accepted and its MP3 registered (queued for ordered/dumped playback).</summary>
        Accepted,
        /// <summary>Same sequence re-delivered — ignored, non-overwrite (no second file).</summary>
        DuplicateIgnored,
        /// <summary>A future / gapped sequence arrived — skip policy: ignored (missing frame is skipped).</summary>
        OutOfOrderIgnored,
        /// <summary>Frame belongs to an already abandoned / completed generation — dropped, never played.</summary>
        WrongGeneration,
        /// <summary>is_final consumed: this frame was accepted and the round is now back to Ready.</summary>
        RoundCompleted
    }

    /// <summary>Outcome of one successfully processed TTS item during the sequential drain.</summary>
    public sealed class TtsPlayedItem
    {
        public string PlaybackId;
        public int Sequence;
        /// <summary>true when the MP3 decoded successfully; false would be surfaced via FrameDecodeFailed.</summary>
        public bool Success;
        /// <summary>Decoded audio clip (real decoder); null when a fake decoder supplied none.</summary>
        public AudioClip Clip;
    }

    /// <summary>
    /// Minimal TTS playback service. Exposes just what the driver needs; the exact file/decoder
    /// mechanics are encapsulated in <see cref="Mp3PlaybackQueue"/>.
    /// </summary>
    public interface ITtsPlayback : IDisposable
    {
        /// <summary>True while any item is queued or playing (the round is not yet finished).</summary>
        bool HasQueued { get; }

        /// <summary>True while the sequential decode/play loop is actively running.</summary>
        bool IsPlaying { get; }

        /// <summary>Fired for every successfully decoded item, strictly in sequence order.</summary>
        event Action<TtsPlayedItem> FramePlayed;

        /// <summary>Fired per decode failure (MPEG could not be decoded). The item is stopped and its
        /// temp file removed, but the already-displayed answer text is preserved (text downgrade).</summary>
        event Action<string> FrameDecodeFailed;

        /// <summary>Fired once per round after the ordered drain completes (round back to Ready).</summary>
        event Action<string> PlaybackCompleted;

        /// <summary>Feed one paired tts.frame header + its MP3 binary body. Returns a stable code; never throws.</summary>
        TtsPlaybackCode EnqueueTts(TtsFrameMessage header, byte[] mp3);

        /// <summary>Barge-in: silence now, clear the queue, delete owned temp files and bump the
        /// generation so any late completion / frame of the old generation is dropped.</summary>
        void Cancel();
    }
}
