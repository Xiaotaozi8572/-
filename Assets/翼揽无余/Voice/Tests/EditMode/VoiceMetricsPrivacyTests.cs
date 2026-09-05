// VoiceMetricsPrivacyTests.cs
// VR5-T13 EditMode tests for the sanitized metrics layer (VoiceIdHasher / VoiceMetricRecord /
// VoiceMetricsRecorder). All tests use an injected DEDICATED temp directory (never the real app
// private dir) and an injected isDevBuild predicate, so no real device data is ever touched.
//
// Coverage:
//   - Default capacity constants (500 records / 1 MiB / 5 files).
//   - SCHEMA: persisted JSONL carries ONLY the sanitized field set; the ADB scan token set
//     (transcript|answer|audio_bytes|ws://|known-problem-text) and raw ids are structurally absent,
//     including across file rotation/prune.
//   - Hashing: deterministic per (install, id), different across installs (per-install salt),
//     non-reversible (raw id is not a substring), hex format, fresh random salt per install.
//   - Development-only: Release builds write nothing (no directory, no bytes).
//   - Capacity: per-file record count AND byte-size limits; at most MaxFiles files kept (oldest
//     pruned); cleanup/prune never crosses the dedicated directory and never deletes unmanaged files.
//
// NOTE: ext.nunit 3.5 has no ThrowsAsync/DoesNotThrowAsync -> synchronous assertions are used.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Metrics;

namespace Yilan.Voice.Metrics
{
    [TestFixture]
    public class VoiceMetricsPrivacyTests
    {
        private static readonly string[] ForbiddenTokens =
        {
            "transcript", "answer", "audio_bytes", "ws://", "飞机的发动机"
        };

        // ============================ Helpers ============================

        private static string TempDir()
            => Path.Combine(Application.temporaryCachePath, "T13Metrics-" + Guid.NewGuid().ToString("N"));

        private static VoiceMetricsRecorder NewRecorder(
            string dir,
            Func<bool> dev = null,
            int maxRecords = VoiceMetricsRecorder.MaxRecordsPerFile,
            long maxBytes = VoiceMetricsRecorder.MaxBytesPerFile,
            int maxFiles = VoiceMetricsRecorder.MaxFiles)
        {
            return new VoiceMetricsRecorder(
                new VoiceIdHasher("test-salt-" + dir.GetHashCode()),
                dir,
                dev ?? (() => true),
                maxRecords,
                maxBytes,
                maxFiles,
                () => new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc));
        }

        private static int CountJsonlFiles(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly).Length;
        }

        /// <summary>All JSONL lines across every file in the dedicated dir, sorted by file name.</summary>
        private static string[] ReadAllLines(string dir)
        {
            if (!Directory.Exists(dir)) return new string[0];
            string[] files = Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);
            var lines = new List<string>();
            foreach (string f in files) lines.AddRange(File.ReadAllLines(f, Encoding.UTF8));
            return lines.ToArray();
        }

        private static void Cleanup(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        // ============================ Defaults ============================

        [Test]
        public void Defaults_500Records_1MiB_5Files()
        {
            Assert.AreEqual(500, VoiceMetricsRecorder.MaxRecordsPerFile);
            Assert.AreEqual(1L * 1024 * 1024, VoiceMetricsRecorder.MaxBytesPerFile);
            Assert.AreEqual(5, VoiceMetricsRecorder.MaxFiles);
            Assert.AreEqual("YilanVoiceMetrics", VoiceMetricsRecorder.FolderName);
        }

        // ============================ Hashing ============================

        [Test]
        public void Hash_DeterministicPerSalt_AndDifferentAcrossSalts_NotReversible()
        {
            var h1 = new VoiceIdHasher("salt-A");
            var h1b = new VoiceIdHasher("salt-A");
            var h2 = new VoiceIdHasher("salt-B");

            string a1 = h1.Hash("vr-session-001");
            string a2 = h1b.Hash("vr-session-001");
            string b = h2.Hash("vr-session-001");

            Assert.AreEqual(a1, a2, "same install + same id must hash identically");
            Assert.AreNotEqual(a1, b, "different install salts must differ for the same id");
            Assert.AreEqual(32, a1.Length, "digest is 32 hex chars");
            Assert.IsFalse(a1.Contains("vr-session-001"), "raw id must never be recoverable as a substring");
        }

        [Test]
        public void CreateRandomSalt_ProducesDifferentPerInstallSalts()
        {
            string s1 = VoiceIdHasher.CreateRandomSalt().Salt;
            string s2 = VoiceIdHasher.CreateRandomSalt().Salt;
            Assert.AreNotEqual(s1, s2, "two installs must get different random salts");
            Assert.That(s1, Is.Not.Null.And.Not.Empty);
        }

        // ============================ Schema / privacy ============================

        [Test]
        public void Schema_OnlySanitizedFields_NeverForbiddenTokensNorRawIds()
        {
            string dir = TempDir();
            var rec = NewRecorder(dir, maxRecords: 2, maxFiles: 3); // force rotation + prune
            string rawSession = "vr-session-001";
            string rawTurn = "turn-0001";
            string urlLike = "ws://127.0.0.1:8765/ws/voice/session?token=abc";

            // Record a realistic worst-case burst (incl. a full-URL-like id and a close code).
            rec.Record(VoiceMetricEvent.Connect, rawSession, null, 12, 0, 0, VoiceMetricOutcome.Ok);
            rec.Record(VoiceMetricEvent.SessionStarted, rawSession, rawTurn, 4, 0, 0, VoiceMetricOutcome.Ok);
            rec.Record(VoiceMetricEvent.TtsFrame, rawSession, rawTurn, 33, 120, 0, VoiceMetricOutcome.Ok);
            rec.Record(VoiceMetricEvent.Error, urlLike, rawTurn, 0, 0, 1009, VoiceMetricOutcome.Faulted);
            rec.Record(VoiceMetricEvent.Cancel, rawSession, rawTurn, 2, 0, 1000, VoiceMetricOutcome.Ok);
            rec.Record(VoiceMetricEvent.Reconnect, rawSession, null, 1500, 3, 1006, VoiceMetricOutcome.TransportFailed);

            string joined = string.Join("\n", ReadAllLines(dir));

            // Field set: allowed keys ARE present, forbidden tokens ABSENT.
            Assert.That(joined, Does.Contain("\"hashed_session\""));
            Assert.That(joined, Does.Contain("\"ts_ms\""));
            foreach (string token in ForbiddenTokens)
            {
                Assert.That(joined, Does.Not.Contain(token),
                    "schema must never persist forbidden content: " + token);
            }
            // Raw identifiers never appear (only their salted hashes).
            Assert.That(joined, Does.Not.Contain(rawSession));
            Assert.That(joined, Does.Not.Contain(rawTurn));

            // Every line round-trips through the schema (still a valid VoiceMetricRecord).
            string[] lines = ReadAllLines(dir);
            Assert.That(lines.Length, Is.GreaterThanOrEqualTo(6), "all records written");
            foreach (string line in lines)
            {
                var parsed = JsonUtility.FromJson<VoiceMetricRecord>(line);
                Assert.That(parsed, Is.Not.Null, "each line must deserialize to the sanitized record");
                Assert.That(parsed.hashed_session, Is.Not.Null);
            }

            rec.Dispose();
            Cleanup(dir);
        }

        // ============================ Development-build gating ============================

        [Test]
        public void ReleaseBuild_RecordIsNoOp_NoDirectoryNoBytes()
        {
            string dir = TempDir();
            var rec = NewRecorder(dir, dev: () => false);

            rec.Record(VoiceMetricEvent.Connect, "vr-session-001", null, 5, 0, 0, VoiceMetricOutcome.Ok);

            Assert.IsFalse(Directory.Exists(dir), "Release builds must never create the metrics dir");
            Assert.AreEqual(0, CountJsonlFiles(dir));
            Assert.AreEqual(0, rec.CurrentFileRecordCount);
            Assert.AreEqual(0, rec.CurrentFileBytes);

            rec.Dispose();
            Cleanup(dir);
        }

        [Test]
        public void DevBuild_WritesSanitizedJsonl_RoundTrips()
        {
            string dir = TempDir();
            var rec = NewRecorder(dir);

            rec.Record(VoiceMetricEvent.SessionStarted, "vr-session-001", "turn-0001", 3, 0, 0, VoiceMetricOutcome.Ok);

            Assert.AreEqual(1, CountJsonlFiles(dir));
            string[] lines = ReadAllLines(dir);
            Assert.AreEqual(1, lines.Length);
            Assert.That(lines[0], Does.Contain("\"hashed_session\""));
            Assert.That(lines[0], Does.Not.Contain("vr-session-001"));

            var parsed = JsonUtility.FromJson<VoiceMetricRecord>(lines[0]);
            Assert.AreEqual(VoiceMetricEvent.SessionStarted, parsed.evt);
            Assert.AreEqual(VoiceMetricOutcome.Ok, parsed.outcome);
            Assert.AreEqual(1, parsed.seq);
            Assert.Greater(parsed.ts_ms, 0);
            Assert.AreEqual(rec.MetricsDirectory, dir);

            rec.Dispose();
            Cleanup(dir);
        }

        // ============================ Capacity / rotation / prune ============================

        [Test]
        public void Rotation_RecordsPerFileLimit_HoldsAcrossFiles()
        {
            string dir = TempDir();
            var rec = NewRecorder(dir, maxRecords: 3, maxFiles: 5);
            for (int i = 0; i < 8; i++)
                rec.Record(VoiceMetricEvent.Connect, "s", null, i, 0, 0, VoiceMetricOutcome.Ok);

            string[] lines = ReadAllLines(dir);
            Assert.AreEqual(8, lines.Length, "no record lost across rotation");
            Assert.Greater(CountJsonlFiles(dir), 1, "rotation must have split into multiple files");
            // Per-file record budget: no file has more than 3 lines.
            Assert.LessOrEqual(MaxLinesInOneFile(dir), 3);

            rec.Dispose();
            Cleanup(dir);
        }

        [Test]
        public void Rotation_BytesPerFileLimit_HoldsAcrossFiles()
        {
            string dir = TempDir();
            // One record is ~190 bytes, so a 500-byte budget fits exactly 2 records per file: the
            // byte boundary (not the record count, maxRecords is huge) drives rolling, producing
            // ~5 files for 10 records — and no prune in this window (< MaxFiles).
            var rec = NewRecorder(dir, maxRecords: 100000, maxBytes: 500, maxFiles: 10);
            for (int i = 0; i < 10; i++)
                rec.Record(VoiceMetricEvent.TtsFrame, "s", "t", 3, i, 0, VoiceMetricOutcome.Ok);

            string[] lines = ReadAllLines(dir);
            Assert.AreEqual(10, lines.Length);
            string[] files = Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly);
            Assert.Greater(files.Length, 1, "byte budget must also force rotation");
            foreach (string f in files)
            {
                long bytes = new FileInfo(f).Length;
                Assert.LessOrEqual(bytes, 500 + 128, "a file may only exceed the byte budget by one record's slack");
            }

            rec.Dispose();
            Cleanup(dir);
        }

        [Test]
        public void Prune_KeepsOnlyNewestMaxFiles_BoundedStorage()
        {
            string dir = TempDir();
            var rec = NewRecorder(dir, maxRecords: 2, maxFiles: 2);
            for (int i = 0; i < 6; i++)
                rec.Record(VoiceMetricEvent.Connect, "s", null, i, 0, 0, VoiceMetricOutcome.Ok);

            // Storage is BOUNDED by design: at most MaxFiles files are kept; the oldest file(s) are
            // pruned (6 records @ 2-per-file -> 3 files -> the oldest is deleted -> 2 newest remain).
            Assert.LessOrEqual(CountJsonlFiles(dir), 2, "at most MaxFiles files are kept");
            Assert.AreEqual(2, CountJsonlFiles(dir), "6 records with 2-per-file prune down to the 2 newest files");
            Assert.AreEqual(4, ReadAllLines(dir).Length,
                "pruning drops only the OLDEST file's records (bounded-capacity design), never the newest");
            // The two newest files are m_000002 and m_000003 (the newest window).
            Assert.IsTrue(File.Exists(Path.Combine(dir, "m_000002.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "m_000003.jsonl")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "m_000001.jsonl")), "oldest pruned file removed");

            rec.Dispose();
            Cleanup(dir);
        }

        [Test]
        public void Cleanup_NeverCrossesDedicatedDirectory_NeverDeletesUnmanaged()
        {
            string root = TempDir();
            string dedicated = Path.Combine(root, "Metrics");
            string otherDir = Path.Combine(root, "OtherCache");
            Directory.CreateDirectory(dedicated);
            Directory.CreateDirectory(otherDir);

            var rec = NewRecorder(dedicated, maxRecords: 2, maxFiles: 2);
            // Foreign artifacts placed BEFORE recording so prune must meet them and leave them intact.
            string foreignBin = Path.Combine(dedicated, "keep.bin");
            string foreignJsonl = Path.Combine(dedicated, "garbage.jsonl"); // unregistered, same dir
            string foreignOther = Path.Combine(otherDir, "other.jsonl");
            File.WriteAllBytes(foreignBin, new byte[] { 9, 9, 9 });
            File.WriteAllText(foreignJsonl, "{\"unmanaged\":true}\n", Encoding.UTF8);
            File.WriteAllText(foreignOther, "not-managed\n", Encoding.UTF8);

            for (int i = 0; i < 5; i++)
                rec.Record(VoiceMetricEvent.Error, "s", null, 0, 0, 4400, VoiceMetricOutcome.InvalidState);

            Assert.IsTrue(File.Exists(foreignBin), "unmanaged .bin in the dedicated dir must be untouched");
            Assert.IsTrue(File.Exists(foreignJsonl), "an UNREGISTERED .jsonl in the dedicated dir must never be pruned");
            Assert.IsTrue(File.Exists(foreignOther), "files in OTHER directories must be untouched");
            Assert.AreEqual(1, ReadAllLines(otherDir).Length, "foreign dir contents untouched");
            // Only the recorder's OWN m_*.jsonl files are managed, and at most MaxFiles of them.
            Assert.LessOrEqual(CountManagedJsonlFiles(dedicated), 2);

            rec.Dispose();
            Cleanup(root);
        }

        // ============================ Small helper ============================

        private static int MaxLinesInOneFile(string dir)
        {
            string[] files = Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly);
            int max = 0;
            foreach (string f in files)
                max = Math.Max(max, File.ReadAllLines(f, Encoding.UTF8).Length);
            return max;
        }

        /// <summary>Count only the recorder's own managed files (m_&lt;6-digit&gt;.jsonl).</summary>
        private static int CountManagedJsonlFiles(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            int n = 0;
            foreach (string f in Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly))
                if (Path.GetFileName(f).StartsWith("m_", StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
