// Mp3PlaybackQueue.cs
// VR4-T11: ordered MP3 TTS playback with safe barge-in cancellation and an app-dedicated temporary
// cache whose lifecycle the queue owns precisely.
//
// Responsibilities:
//   - Order frames by playback_id + monotonic sequence (from 0). Duplicates are ignored
//     (non-overwrite); a gapped/future sequence is ignored (skip policy); is_final ends the round
//     and drains the ordered queue back to Ready.
//   - Only ever write server MP3 bytes into the DEDICATED dir (<temporaryCachePath>/YilanVoiceTts)
//     and register the exact files it writes. Files are deleted immediately on decode success or
//     failure, on cancel, and on dispose — never files outside that exact set or other directories.
//   - Cancel() silences now: clears the queue, deletes owned temp files and bumps the generation so
//     any late completion / late frame of the old generation is dropped (no audible leak).
//   - A decode failure stops that item and removes its temp file, but does NOT touch the already
//     displayed answer text (text downgrade); the round still ends Ready.
//   - Startup cleanup scans ONLY the dedicated dir for expired .mp3 files; it never recurses into
//     other caches and never deletes files it is currently managing.
//
// Threading note: the sequential drain awaits the injected ITtsDecoder. With a synchronously
// completing fake (EditMode) the drain runs inline and is deterministic; the real UnityMp3Decoder
// resolves on Unity's request continuation. Overlapping calls after is_final are not expected from
// the upstream controller (VR2-T07 rejects late/stale/out-of-order frames before this layer).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Yilan.Voice.Runtime.Protocol;

namespace Yilan.Voice.Runtime.Playback
{
    /// <summary>Core sequential MP3 playback queue with generation isolation and exact temp-cache ownership.</summary>
    public sealed class Mp3PlaybackQueue : ITtsPlayback
    {
        /// <summary>Name of the app-dedicated temp cache folder written under Application.temporaryCachePath.</summary>
        public const string CacheFolderName = "YilanVoiceTts";

        // Bound on how many finished/cancelled playback ids are remembered as invalidated, so a
        // straggler of an old round is dropped while memory stays bounded.
        private const int MaxRememberedInvalidIds = 32;

        private readonly ITtsDecoder _decoder;
        private readonly string _cacheDirectory;
        private readonly Func<DateTime> _now;
        private readonly TimeSpan _maxCacheAge;

        private readonly List<QueueItem> _queue = new List<QueueItem>();
        private readonly HashSet<string> _managedFiles = new HashSet<string>();
        private readonly List<string> _invalidPlaybackIds = new List<string>();

        private string _currentPlaybackId;
        private int _expectedSeq = -1;
        private int _generation;
        private bool _isPlaying;
        private bool _disposed;

        /// <summary>Fired for every successfully decoded item, strictly in sequence order.</summary>
        public event Action<TtsPlayedItem> FramePlayed;
        /// <summary>Fired per decode failure (text is preserved upstream, never deleted by playback).</summary>
        public event Action<string> FrameDecodeFailed;
        /// <summary>Fired once per round when the ordered drain finishes (round back to Ready).</summary>
        public event Action<string> PlaybackCompleted;

        /// <summary>The dedicated cache directory used by this instance (injected for tests, else the production path).</summary>
        public string CacheDirectory => _cacheDirectory;

        /// <summary>playback_id of the currently active round, or null when idle/Ready.</summary>
        public string CurrentPlaybackId => _currentPlaybackId;

        /// <summary>Current isolation generation; bumped by Cancel() and by round transitions.</summary>
        public int CurrentGeneration => _generation;

        /// <summary>Exact set of temp files this queue created and currently manages (read-only view).</summary>
        public IReadOnlyCollection<string> ManagedFiles => _managedFiles;

        /// <summary>True when the given absolute path is one of the files this queue currently manages.</summary>
        public bool ContainsManagedFile(string path) => _managedFiles.Contains(path);

        /// <summary>Number of MP3 files still queued for the current round.</summary>
        public int PendingFileCount => _queue.Count;

        /// <summary>True while any item is queued or playing.</summary>
        public bool HasQueued => _queue.Count > 0;

        /// <summary>True while the sequential decode/play loop is actively running.</summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// Create a queue owning <paramref name="cacheDirectory"/> (default
        /// <c>temporaryCachePath/YilanVoiceTts</c>). Startup cleanup removes only expired .mp3 files
        /// inside that dedicated dir. <paramref name="now"/> / <paramref name="maxCacheAge"/> are
        /// injectable for deterministic cleanup tests.
        /// </summary>
        public Mp3PlaybackQueue(ITtsDecoder decoder, string cacheDirectory = null, Func<DateTime> now = null, TimeSpan? maxCacheAge = null)
        {
            _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
            _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
                ? Path.Combine(Application.temporaryCachePath, CacheFolderName)
                : cacheDirectory;
            _now = now ?? (() => DateTime.UtcNow);
            _maxCacheAge = maxCacheAge ?? TimeSpan.FromHours(24);

            EnsureCacheDir();
            CleanupExpired();
        }

        // =============================== Feed ===============================

        /// <inheritdoc/>
        public TtsPlaybackCode EnqueueTts(TtsFrameMessage header, byte[] mp3)
        {
            if (_disposed) return TtsPlaybackCode.WrongGeneration;
            if (header == null || mp3 == null || mp3.Length == 0)
                return TtsPlaybackCode.OutOfOrderIgnored;

            string pb = header.playback_id ?? string.Empty;

            // A frame for an already-abandoned / already-completed round is stale: drop it.
            if (pb != _currentPlaybackId && _invalidPlaybackIds.Contains(pb))
                return TtsPlaybackCode.WrongGeneration;

            // Round transition: a DIFFERENT playback_id while a round is active abandons the old one.
            if (_currentPlaybackId != null && pb != _currentPlaybackId)
            {
                AbandonRound();
                _currentPlaybackId = pb;
                _expectedSeq = 0;
            }
            else if (_currentPlaybackId == null)
            {
                _currentPlaybackId = pb;
                _expectedSeq = 0;
            }

            // Sequence policy: monotonic from 0. Duplicate -> ignore (non-overwrite). A gap (future
            // sequence) -> ignore (skip policy).
            if (header.sequence < _expectedSeq)
                return TtsPlaybackCode.DuplicateIgnored;
            if (header.sequence > _expectedSeq)
                return TtsPlaybackCode.OutOfOrderIgnored;

            WriteTempFile(pb, header.sequence, mp3);
            _expectedSeq++;

            if (header.is_final)
            {
                PlayLoop(_generation);
                return TtsPlaybackCode.RoundCompleted;
            }
            return TtsPlaybackCode.Accepted;
        }

        // =============================== Cancel =============================

        /// <inheritdoc/>
        public void Cancel()
        {
            if (_disposed) return;
            AbandonRound(); // generation++, clear queue, delete owned files, silence old content
        }

        // =============================== Drain ==============================

        private async void PlayLoop(int gen)
        {
            _isPlaying = true;
            try
            {
                while (_queue.Count > 0)
                {
                    if (_disposed || gen != _generation)
                        break; // cancelled / superseded while awaiting -> drop everything that remains

                    var item = _queue[0];
                    string path = FullPath(item);

                    TtsDecodeResult result;
                    try { result = await _decoder.DecodeAsync(path); }
                    catch { result = TtsDecodeResult.Fail("decode_exception"); }

                    // Late completion of an old generation must be dropped (never played), and its
                    // temp file is still released regardless.
                    bool stale = _disposed || gen != _generation;
                    if (stale)
                    {
                        DeleteManagedFile(path);
                        RemoveQueued(item);
                        continue;
                    }

                    RemoveQueued(item);
                    DeleteManagedFile(path); // delete immediately on success or failure

                    if (result != null && result.Success)
                    {
                        FramePlayed?.Invoke(new TtsPlayedItem
                        {
                            PlaybackId = item.PlaybackId,
                            Sequence = item.Sequence,
                            Success = true,
                            Clip = result.Clip
                        });
                    }
                    else
                    {
                        // Decode failure: stop this item, keep the already-displayed answer text
                        // (text downgrade), and continue with the next ordered item.
                        FrameDecodeFailed?.Invoke(item.PlaybackId);
                    }
                }

                if (!_disposed && gen == _generation)
                {
                    // The round drained fully -> back to Ready.
                    if (!string.IsNullOrEmpty(_currentPlaybackId))
                        RememberInvalid(_currentPlaybackId);
                    string doneId = _currentPlaybackId;
                    _currentPlaybackId = null;
                    _expectedSeq = -1;
                    PlaybackCompleted?.Invoke(doneId ?? string.Empty);
                }
            }
            catch
            {
                // Never let a drain fault escape; the queue remains coherent for the caller.
            }
            finally
            {
                _isPlaying = false;
            }
        }

        private void RemoveQueued(QueueItem item)
        {
            _queue.Remove(item);
        }

        // ========================= Cache lifecycle ==========================

        private void EnsureCacheDir()
        {
            try { if (!Directory.Exists(_cacheDirectory)) Directory.CreateDirectory(_cacheDirectory); }
            catch { }
        }

        /// <summary>Startup cleanup: delete ONLY expired .mp3 files inside the dedicated dir, and only
        /// files this queue does not currently manage. Never recurses into other directories.</summary>
        private void CleanupExpired()
        {
            if (!Directory.Exists(_cacheDirectory)) return;

            string[] files;
            try { files = Directory.GetFiles(_cacheDirectory, "*.mp3", SearchOption.TopDirectoryOnly); }
            catch { return; }

            DateTime now = (_now != null) ? _now() : DateTime.UtcNow;
            foreach (var f in files)
            {
                if (string.IsNullOrEmpty(f) || _managedFiles.Contains(f))
                    continue; // never delete a file this queue currently owns
                try
                {
                    if (now - File.GetLastWriteTimeUtc(f) > _maxCacheAge)
                        File.Delete(f);
                }
                catch { }
            }
        }

        private void WriteTempFile(string playbackId, int seq, byte[] mp3)
        {
            EnsureCacheDir();
            string name = Sanitize(playbackId) + "_" + seq + ".mp3";
            string path = Path.Combine(_cacheDirectory, name);
            File.WriteAllBytes(path, mp3);
            _managedFiles.Add(path);
            _queue.Add(new QueueItem { PlaybackId = playbackId, Sequence = seq, FileName = name });
        }

        /// <summary>Delete a temp file ONLY if it is one this queue registered (exact ownership).</summary>
        private void DeleteManagedFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !_managedFiles.Contains(path))
                return; // never touch files we did not create
            _managedFiles.Remove(path);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Abandon the current round: silence now, clear queue, delete owned files, bump the
        /// generation, and remember the abandoned playback id so stragglers are dropped.</summary>
        private void AbandonRound()
        {
            _generation++;
            if (!string.IsNullOrEmpty(_currentPlaybackId))
                RememberInvalid(_currentPlaybackId);

            foreach (var path in new List<string>(_managedFiles))
                DeleteManagedFile(path);
            _managedFiles.Clear();
            _queue.Clear();
            _currentPlaybackId = null;
            _expectedSeq = -1;
        }

        private void RememberInvalid(string playbackId)
        {
            if (string.IsNullOrEmpty(playbackId)) return;
            if (_invalidPlaybackIds.Contains(playbackId)) return;
            _invalidPlaybackIds.Add(playbackId);
            if (_invalidPlaybackIds.Count > MaxRememberedInvalidIds)
                _invalidPlaybackIds.RemoveAt(0);
        }

        private static string Sanitize(string s)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            var chars = (s ?? "pb").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                foreach (var b in bad)
                {
                    if (chars[i] == b) { chars[i] = '_'; break; }
                }
            }
            return new string(chars);
        }

        private string FullPath(QueueItem item) => Path.Combine(_cacheDirectory, item.FileName);

        // =============================== Dispose ============================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            AbandonRound();
            // Release event subscriptions so a disposed queue cannot raise into stale listeners.
            FramePlayed = null;
            FrameDecodeFailed = null;
            PlaybackCompleted = null;
        }

        private sealed class QueueItem
        {
            public string PlaybackId;
            public int Sequence;
            public string FileName;
        }
    }
}
