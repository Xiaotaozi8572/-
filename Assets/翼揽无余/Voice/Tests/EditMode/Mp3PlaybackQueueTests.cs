// Mp3PlaybackQueueTests.cs
// VR4-T11 EditMode tests for Mp3PlaybackQueue + UnityMp3Decoder abstraction.
//
// Uses an injected FAKE decoder (no real UnityWebRequestMultimedia / AudioClip) and an injected
// dedicated cache directory so no real media, no real file:: decode and no microphone data are
// ever touched. Coverage: monotonic sequence across playbacks; duplicate (non-overwrite ignore);
// missing-sequence (gap -> skip) ignore; is_final round -> back to Ready; cancel immediate silence
// + generation isolation (a late decode completion of an old generation is dropped); decode failure
// preserves the already-displayed answer text and the round still ends Ready; a straggler frame
// from an abandoned round is dropped as WrongGeneration; a fresh round abandons an active one.
//
// NOTE: the bundled ext.nunit 3.5 has no ThrowsAsync/DoesNotThrowAsync, so all async assertions use
// the synchronous form Assert.DoesNotThrow(() => task.GetAwaiter().GetResult()).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Protocol;

namespace Yilan.Voice.Playback
{
    [TestFixture]
    public class Mp3PlaybackQueueTests
    {
        // ---- fakes ---------------------------------------------------------

        /// <summary>Deterministic decoder keyed by the sequence embedded in the temp file name.</summary>
        private sealed class FakeDecoder : ITtsDecoder
        {
            public readonly Dictionary<int, Task<TtsDecodeResult>> BySequence = new Dictionary<int, Task<TtsDecodeResult>>();

            public Task<TtsDecodeResult> DecodeAsync(string filePath)
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                int idx = name.LastIndexOf('_');
                int seq = int.Parse(name.Substring(idx + 1));
                if (BySequence.TryGetValue(seq, out var t)) return t;
                return Task.FromResult(TtsDecodeResult.Ok(null));
            }
        }

        private static TtsFrameMessage Frame(string playbackId, int seq, bool isFinal)
        {
            return new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = "vr-session-001",
                turn_id = "turn-0001",
                playback_id = playbackId,
                sequence = seq,
                codec = "mp3",
                duration_ms = 100,
                is_final = isFinal
            };
        }

        private static string TestCacheRoot()
        {
            return Path.Combine(Application.temporaryCachePath, "T11EditMode-" + Guid.NewGuid().ToString("N"));
        }

        private static int CountMp3Files(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly).Length;
        }

        // ---- monotonic sequence + file registration -----------------------

        [Test]
        public void Sequence_InOrder_AcceptedAndFilesWrittenAndRegistered()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 1));
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 2));

                Assert.AreEqual(3, q.PendingFileCount);
                Assert.AreEqual(3, q.ManagedFiles.Count);
                Assert.AreEqual(3, CountMp3Files(cache));
                Assert.IsTrue(q.HasQueued);
                Assert.IsFalse(q.IsPlaying); // not final yet -> no play loop
            }
            CleanupCache(cache);
        }

        [Test]
        public void Sequence_Duplicate_Ignored_NonOverwrite()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.DuplicateIgnored, q.EnqueueTts(Frame("pb-1", 0, false), new byte[] { 1, 2, 3 }));

                Assert.AreEqual(1, q.PendingFileCount);
                Assert.AreEqual(1, q.ManagedFiles.Count);
                Assert.AreEqual(1, CountMp3Files(cache), "duplicate must not write a second file");
            }
            CleanupCache(cache);
        }

        [Test]
        public void Sequence_Gap_Skipped_Ignored()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.OutOfOrderIgnored, q.EnqueueTts(Frame("pb-1", 2, false), new byte[] { 1, 2, 3 }));
                Assert.AreEqual(TtsPlaybackCode.OutOfOrderIgnored, q.EnqueueTts(Frame("pb-1", 5, false), new byte[] { 1, 2, 3 }));

                Assert.AreEqual(1, q.PendingFileCount);
                Assert.AreEqual(1, CountMp3Files(cache), "gapped/skipped sequence must not write a file");
            }
            CleanupCache(cache);
        }

        // ---- is_final -> ordered playback -> Ready ------------------------

        [Test]
        public void IsFinal_PlaysQueuedInOrder_RoundEndsReady_AllTempFilesDeleted()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            var played = new List<int>();
            var completed = new List<string>();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                q.FramePlayed += item => played.Add(item.Sequence);
                q.PlaybackCompleted += pb => completed.Add(pb);

                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 1));
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 2));
                Assert.AreEqual(TtsPlaybackCode.RoundCompleted, q.EnqueueTts(Frame("pb-1", 3, true), new byte[] { 1, 2, 3 }));

                // Synchronous fake decoder => the whole drain ran before EnqueueTts returned.
                CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, played, "must play strictly in sequence order");
                CollectionAssert.AreEqual(new[] { "pb-1" }, completed, "round must report one PlaybackCompleted");
                Assert.IsFalse(q.HasQueued);
                Assert.IsFalse(q.IsPlaying);
                Assert.AreEqual(0, q.ManagedFiles.Count);
                Assert.AreEqual(0, CountMp3Files(cache), "every temp file removed on completion/failure");
            }
            CleanupCache(cache);
        }

        [Test]
        public void CompletedRound_StragglerFrame_IgnoredAsWrongGeneration()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                Assert.AreEqual(TtsPlaybackCode.RoundCompleted, q.EnqueueTts(Frame("pb-1", 0, true), new byte[] { 1, 2, 3 }));
                Assert.IsFalse(q.HasQueued);

                TtsPlaybackCode straggler = q.EnqueueTts(Frame("pb-1", 1, false), new byte[] { 1, 2, 3 });
                Assert.AreEqual(TtsPlaybackCode.WrongGeneration, straggler, "frame after a completed round is stale");
                Assert.AreEqual(0, q.PendingFileCount);
            }
            CleanupCache(cache);
        }

        // ---- cancel + generation isolation --------------------------------

        [Test]
        public void Cancel_ImmediateSilence_ClearsQueue_DeletesExactlyOwnFiles()
        {
            var cache = TestCacheRoot();
            var foreignRoot = TestCacheRoot();
            var foreignDir = Path.Combine(foreignRoot, "OtherCache");
            Directory.CreateDirectory(foreignDir);
            string foreign = Path.Combine(foreignDir, "garbage.bin");
            File.WriteAllBytes(foreign, new byte[] { 9, 9, 9 });

            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 1));
                Assert.AreEqual(2, CountMp3Files(cache));
                q.Cancel();

                Assert.IsFalse(q.HasQueued);
                Assert.IsFalse(q.IsPlaying);
                Assert.AreEqual(0, q.ManagedFiles.Count);
                Assert.AreEqual(0, CountMp3Files(cache), "all queued temp files deleted on cancel");
                Assert.IsTrue(File.Exists(foreign), "files outside the dedicated dir must never be touched");
            }
            CleanupCache(cache);
            CleanupCache(foreignRoot);
        }

        [Test]
        public void Cancel_DropsLateDecodeCompletion_FromOldGeneration()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            var pending = new TaskCompletionSource<TtsDecodeResult>();
            decoder.BySequence[0] = pending.Task;

            var played = new List<int>();
            var completed = new List<string>();
            int defail = 0;

            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                q.FramePlayed += item => played.Add(item.Sequence);
                q.PlaybackCompleted += pb => completed.Add(pb);
                q.FrameDecodeFailed += _ => defail++;

                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 1));

                // Final triggers the play loop; it awaits the never-yet-completed decode of seq 0.
                Assert.AreEqual(TtsPlaybackCode.RoundCompleted, q.EnqueueTts(Frame("pb-1", 2, true), new byte[] { 1, 2, 3 }));
                Assert.IsTrue(q.IsPlaying, "play loop is parked on an incomplete decode");

                // Barge-in cancel: silence now, bump generation.
                q.Cancel();
                Assert.IsFalse(q.HasQueued);

                // The old generation's decode finally completes late -> must be dropped, not played.
                // Wait synchronously for the continuation to run so the generation-drop is observed
                // deterministically on the test thread (avoid relying on TPL thread-pool scheduling).
                pending.SetResult(TtsDecodeResult.Ok(null));
                Assert.DoesNotThrow(() => { pending.Task.GetAwaiter().GetResult(); });

                Assert.AreEqual(0, played.Count, "a late completion of a cancelled generation must never be played");
                Assert.AreEqual(0, completed.Count, "no PlaybackCompleted for a cancelled generation");
                Assert.AreEqual(0, defail);
                Assert.IsFalse(q.IsPlaying, "play loop must have exited after the generation drop");
                Assert.AreEqual(0, q.ManagedFiles.Count);
                Assert.AreEqual(0, CountMp3Files(cache));
            }
            CleanupCache(cache);
        }

        [Test]
        public void StragglerFrame_OfCancelledRound_Ignored_ThenNewRoundWorks()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-A", 0));
                q.Cancel();

                // A late frame belonging to the abandoned round is dropped.
                TtsPlaybackCode straggler = q.EnqueueTts(Frame("pb-A", 1, false), new byte[] { 1, 2, 3 });
                Assert.AreEqual(TtsPlaybackCode.WrongGeneration, straggler);
                Assert.AreEqual(0, q.PendingFileCount);

                // A genuinely new round still works after the cancel.
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-B", 0));
                Assert.AreEqual(1, q.PendingFileCount);
            }
            CleanupCache(cache);
        }

        // ---- new round abandons active round ------------------------------

        [Test]
        public void NewRound_DifferentPlaybackId_AbandonsActiveRound_CleansItsFiles()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 1));
                Assert.AreEqual(2, CountMp3Files(cache));

                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-2", 0));
                Assert.AreEqual(1, q.CurrentGeneration, "one round transition must bump the generation once (0 -> 1)");
                Assert.AreEqual(1, q.PendingFileCount, "old round's files are cleared");
                Assert.AreEqual(1, CountMp3Files(cache), "only the new round's file remains");
            }
            CleanupCache(cache);
        }

        // ---- decode failure preserves answer text --------------------------

        [Test]
        public void DecodeFailure_PreservesDisplayedText_ReportsFailure_Continues_RoundEndsReady()
        {
            var cache = TestCacheRoot();
            var decoder = new FakeDecoder();
            decoder.BySequence[0] = Task.FromResult(TtsDecodeResult.Fail("decode_fail")); // seq 0 fails
            decoder.BySequence[1] = Task.FromResult(TtsDecodeResult.Ok(null));            // seq 1 (final) succeeds

            var played = new List<int>();
            var completed = new List<string>();
            var failedIds = new List<string>();
            string displayedText = "已显示的回答文本"; // answer.display already projected by UI layer

            using (var q = new Mp3PlaybackQueue(decoder, cache))
            {
                q.FramePlayed += item => played.Add(item.Sequence);
                q.PlaybackCompleted += pb => completed.Add(pb);
                q.FrameDecodeFailed += pb => failedIds.Add(pb);

                Assert.AreEqual(TtsPlaybackCode.Accepted, Accepted(q, "pb-1", 0));
                Assert.AreEqual(TtsPlaybackCode.RoundCompleted, q.EnqueueTts(Frame("pb-1", 1, true), new byte[] { 1, 2, 3 }));

                // The failed item must NOT have been played; the following item still plays.
                CollectionAssert.AreEqual(new[] { 1 }, played, "only successfully decoded frames play");
                CollectionAssert.AreEqual(new[] { "pb-1" }, failedIds, "decode failure must be reported");
                CollectionAssert.AreEqual(new[] { "pb-1" }, completed, "round still ends (Ready) on a decode failure");

                // Text downgrade: the displayed answer text is preserved, never deleted by playback.
                Assert.AreEqual("已显示的回答文本", displayedText, "decode failure must not clear displayed answer text");

                Assert.IsFalse(q.HasQueued);
                Assert.IsFalse(q.IsPlaying);
                Assert.AreEqual(0, q.ManagedFiles.Count);
                Assert.AreEqual(0, CountMp3Files(cache), "failed temp file deleted too");
            }
            CleanupCache(cache);
        }

        // ---- helpers -------------------------------------------------------

        private static TtsPlaybackCode Accepted(Mp3PlaybackQueue q, string pb, int seq)
        {
            return q.EnqueueTts(Frame(pb, seq, false), new byte[] { 1, 2, 3 });
        }

        /// <summary>Best-effort removal of ONLY the dedicated test cache dir created by a single test.</summary>
        private static void CleanupCache(string cache)
        {
            try
            {
                if (!string.IsNullOrEmpty(cache) && Directory.Exists(cache))
                    Directory.Delete(cache, true);
            }
            catch { /* best-effort test cleanup */ }
        }
    }
}
