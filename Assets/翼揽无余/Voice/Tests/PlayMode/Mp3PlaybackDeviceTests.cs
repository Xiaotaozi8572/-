// Mp3PlaybackDeviceTests.cs
// VR4-T11 PlayMode tests for the app-dedicated temporary cache lifecycle.
//
// These verify the EXACT cache boundary guarantees of Mp3PlaybackQueue without touching a real
// decoder: an injected FAKE decoder is used and the queue is pointed at an injected dedicated test
// directory. Assertions cover: (1) the production default cache dir is
// Application.temporaryCachePath/YilanVoiceTts; (2) the queue only ever registers/deletes the
// exact files it wrote into its own dedicated dir and never recursively clears other caches or
// deletes unmanaged/foreign files; (3) startup cleanup scans ONLY the dedicated dir for expired
// .mp3 and never touches other dirs; (4) a playback never persists microphone-like audio files and
// leaves the dedicated dir empty after the round ends; (5) precise deletion on decode failure.
//
// NOTE: ext.nunit 3.5 has no ThrowsAsync/DoesNotThrowAsync -> synchronous assertions only.
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
    public class Mp3PlaybackDeviceTests
    {
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

        private static string TestRoot()
        {
            return Path.Combine(Application.temporaryCachePath, "T11PlayMode-" + Guid.NewGuid().ToString("N"));
        }

        private static int CountMp3(string dir)
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly).Length : 0;
        }

        private static int CountAudioLike(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            int n = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".wav" || ext == ".raw" || ext == ".pcm" || ext == ".bin" || ext == ".mp3" || ext == ".ogg")
                    n++;
            }
            return n;
        }

        // ---- production default cache dir --------------------------------

        [Test]
        public void ProductionDefaultCacheDir_IsApplicationTemporaryCachePathYilanVoiceTts()
        {
            using (var q = new Mp3PlaybackQueue(new FakeDecoder()))
            {
                string expected = Path.Combine(Application.temporaryCachePath, Mp3PlaybackQueue.CacheFolderName);
                Assert.AreEqual(expected, q.CacheDirectory);
            }
            // Dispose with no managed files leaves no .mp3 behind.
            Assert.AreEqual(0, CountMp3(Path.Combine(Application.temporaryCachePath, Mp3PlaybackQueue.CacheFolderName)));
        }

        // ---- precise ownership: only own files in the dedicated dir --------

        [Test]
        public void WritesAndDeletesOnlyOwnFiles_NeverTouchesForeignOrOtherDir()
        {
            string root = TestRoot();
            string dedicated = Path.Combine(root, "YilanVoiceTts");
            string otherDir = Path.Combine(root, "OtherCache");
            Directory.CreateDirectory(otherDir);

            // Foreign artifacts placed AFTER construction so startup cleanup cannot meet them.
            var decoder = new FakeDecoder();
            using (var q = new Mp3PlaybackQueue(decoder, dedicated))
            {
                string foreignInSameDir = Path.Combine(dedicated, "not_mine.mp3");
                string foreignInOtherDir = Path.Combine(otherDir, "garbage.bin");
                File.WriteAllBytes(foreignInSameDir, new byte[] { 8, 8, 8 });
                File.WriteAllBytes(foreignInOtherDir, new byte[] { 9, 9, 9 });

                var played = new List<int>();
                q.FramePlayed += item => played.Add(item.Sequence);

                Assert.AreEqual(TtsPlaybackCode.Accepted, q.EnqueueTts(Frame("pb-1", 0, false), new byte[] { 1, 2, 3 }));
                Assert.AreEqual(TtsPlaybackCode.Accepted, q.EnqueueTts(Frame("pb-1", 1, false), new byte[] { 1, 2, 3 }));
                Assert.IsTrue(q.ContainsManagedFile(Path.Combine(dedicated, "pb-1_0.mp3")));
                Assert.IsTrue(q.ContainsManagedFile(Path.Combine(dedicated, "pb-1_1.mp3")));
                Assert.AreEqual(2, q.ManagedFiles.Count, "exactly our two temp files are registered");
                Assert.AreEqual(3, CountMp3(dedicated), "2 of ours + 1 unmanaged foreign .mp3 present");

                Assert.AreEqual(TtsPlaybackCode.RoundCompleted, q.EnqueueTts(Frame("pb-1", 2, true), new byte[] { 1, 2, 3 }));

                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, played);
                Assert.AreEqual(1, CountMp3(dedicated), "only the unmanaged foreign .mp3 remains; ours are all deleted");
                Assert.AreEqual(0, q.ManagedFiles.Count);

                Assert.IsTrue(File.Exists(foreignInSameDir), "queue must never delete a file it did not register");
                Assert.IsTrue(File.Exists(foreignInOtherDir), "queue must never touch other directories");
            }

            Cleanup(root);
        }

        // ---- startup cleanup scans only the dedicated dir ------------------

        [Test]
        public void StartupCleanup_ScansOnlyDedicatedDir_RemovesOnlyExpiredMp3()
        {
            string root = TestRoot();
            string dedicated = Path.Combine(root, "YilanVoiceTts");
            string otherDir = Path.Combine(root, "OtherCache");
            Directory.CreateDirectory(dedicated);
            Directory.CreateDirectory(otherDir);

            var now = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

            string expiredInDedicated = Path.Combine(dedicated, "stale.mp3");
            string freshInDedicated = Path.Combine(dedicated, "fresh.mp3");
            string expiredInOtherDir = Path.Combine(otherDir, "stale-other.mp3");
            File.WriteAllBytes(expiredInDedicated, new byte[] { 1 });
            File.WriteAllBytes(freshInDedicated, new byte[] { 2 });
            File.WriteAllBytes(expiredInOtherDir, new byte[] { 3 });

            // Age the two "expired" files well beyond the max age.
            File.SetLastWriteTimeUtc(expiredInDedicated, now - TimeSpan.FromHours(48));
            File.SetLastWriteTimeUtc(expiredInOtherDir, now - TimeSpan.FromHours(48));
            File.SetLastWriteTimeUtc(freshInDedicated, now - TimeSpan.FromHours(1));

            using (var q = new Mp3PlaybackQueue(new FakeDecoder(), dedicated, () => now, TimeSpan.FromHours(24)))
            {
                // Constructor ran startup cleanup.
                Assert.IsFalse(File.Exists(expiredInDedicated), "expired .mp3 in the dedicated dir must be removed");
                Assert.IsTrue(File.Exists(freshInDedicated), "fresh .mp3 in the dedicated dir must be kept");
                Assert.IsTrue(File.Exists(expiredInOtherDir), "cleanup must NEVER recurse into other directories");
                Assert.AreEqual(1, CountMp3(dedicated));
            }

            Cleanup(root);
        }

        // ---- no microphone-like audio file is ever persisted --------------

        [Test]
        public void Playback_NeverPersistsMicrophoneAudio_LeavesDedicatedDirEmpty()
        {
            string root = TestRoot();
            string dedicated = Path.Combine(root, "YilanVoiceTts");
            var decoder = new FakeDecoder();

            var logs = new List<string>();
            Application.logMessageReceived += CaptureLog;
            void CaptureLog(string cond, string stack, LogType type) => logs.Add(cond);

            try
            {
                using (var q = new Mp3PlaybackQueue(decoder, dedicated))
                {
                    Assert.AreEqual(TtsPlaybackCode.RoundCompleted, q.EnqueueTts(Frame("pb-1", 0, true), new byte[] { 1, 2, 3 }));
                    Assert.AreEqual(0, CountAudioLike(dedicated), "no .wav/.raw/.pcm/.bin/.mp3/.ogg may remain after a round");
                }

                // And nothing ever appeared in the dedicated dir during the round.
                Assert.AreEqual(0, CountAudioLike(dedicated));
                foreach (var line in logs)
                {
                    Assert.That(line.ToLowerInvariant(), Does.Not.Contain("payload"), "must never log frame payload");
                }
            }
            finally
            {
                Application.logMessageReceived -= CaptureLog;
            }

            Cleanup(root);
        }

        // ---- precise deletion on decode failure ----------------------------

        [Test]
        public void DecodeFailure_DeletesExactlyItsOwnFile_LeavesForeignUntouched()
        {
            string root = TestRoot();
            string dedicated = Path.Combine(root, "YilanVoiceTts");
            Directory.CreateDirectory(dedicated);
            string foreign = Path.Combine(dedicated, "unmanaged.mp3");
            File.WriteAllBytes(foreign, new byte[] { 7, 7 });

            var decoder = new FakeDecoder();
            decoder.BySequence[0] = Task.FromResult(TtsDecodeResult.Fail("decode_fail"));

            int failed = 0;
            using (var q = new Mp3PlaybackQueue(decoder, dedicated))
            {
                q.FrameDecodeFailed += _ => failed++;
                Assert.AreEqual(TtsPlaybackCode.RoundCompleted, q.EnqueueTts(Frame("pb-1", 0, true), new byte[] { 1, 2, 3 }));

                Assert.AreEqual(1, failed);
                Assert.AreEqual(0, q.ManagedFiles.Count, "its own failed temp file is deleted");
                Assert.IsTrue(File.Exists(foreign), "unmanaged file in the same dir must be untouched");
            }

            Cleanup(root);
        }

        private static void Cleanup(string root)
        {
            try
            {
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch { /* best-effort test cleanup */ }
        }
    }
}
