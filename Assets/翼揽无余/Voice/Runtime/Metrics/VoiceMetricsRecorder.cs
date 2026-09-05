// VoiceMetricsRecorder.cs
// VR5-T13: app-private JSONL metrics recorder with strict capacity bounds.
//
// Contract:
//   * DEVELOPMENT-BUILD ONLY: records are written only when IsDevBuild() is true (default
//     Debug.isDebugBuild). In a Release build Record() is a hard no-op — the dedicated directory
//     is never created and not a single byte is written.
//   * Dedicated app-private directory: <persistentDataPath>/YilanVoiceMetrics (injectable for
//     tests). Cleanup/prune only ever lists and deletes *.jsonl inside THAT exact directory —
//     never recurses, never touches other directories, never deletes unmanaged files.
//   * Capacity bounds: a file rolls when it reaches MaxRecordsPerFile records OR MaxBytesPerFile
//     bytes (whichever comes first); at most MaxFiles files are kept, oldest pruned first.
//   * Every line is a JSON `VoiceMetricRecord` (schema-defined/sanitized via VoiceIdHasher). The
//     API exposes NO transcript/answer/audio/url parameter, so none can be persisted.
//   * Record() never throws — metrics must never break the voice session.
//
// Threading: single-threaded (voice main thread / test thread); no locking required.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Yilan.Voice.Runtime.Metrics
{
    /// <summary>Development-build JSONL metrics recorder with strict capacity and directory bounds.</summary>
    public sealed class VoiceMetricsRecorder : IDisposable
    {
        public const string FolderName = "YilanVoiceMetrics";
        public const int MaxRecordsPerFile = 500;
        public const long MaxBytesPerFile = 1L * 1024 * 1024; // 1 MiB
        public const int MaxFiles = 5;

        private readonly VoiceIdHasher _hasher;
        private readonly string _directory;
        private readonly Func<bool> _isDevBuild;
        private readonly int _maxRecords;
        private readonly long _maxBytes;
        private readonly int _maxFiles;
        private readonly Func<DateTime> _now;

        private long _seq;
        private int _fileIndex;
        private string _currentFilePath;
        private int _currentRecords;
        private long _currentBytes;
        private bool _disposed;

        public VoiceMetricsRecorder(
            VoiceIdHasher hasher,
            string directory = null,
            Func<bool> isDevBuild = null,
            int maxRecords = MaxRecordsPerFile,
            long maxBytes = MaxBytesPerFile,
            int maxFiles = MaxFiles,
            Func<DateTime> now = null)
        {
            _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
            _directory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(Application.persistentDataPath, FolderName)
                : directory;
            _isDevBuild = isDevBuild ?? (() => Debug.isDebugBuild);
            _maxRecords = maxRecords > 0 ? maxRecords : MaxRecordsPerFile;
            _maxBytes = maxBytes > 0 ? maxBytes : MaxBytesPerFile;
            _maxFiles = maxFiles > 0 ? maxFiles : MaxFiles;
            _now = now ?? (() => DateTime.UtcNow);
        }

        /// <summary>Exact app-private directory this recorder owns (test-injectable).</summary>
        public string MetricsDirectory => _directory;

        /// <summary>Whether recording is enabled in the current build context.</summary>
        public bool IsEnabled => _isDevBuild();

        /// <summary>Records currently buffered in the active file.</summary>
        public int CurrentFileRecordCount => _currentRecords;

        /// <summary>Bytes currently buffered in the active file (JSON + newline).</summary>
        public long CurrentFileBytes => _currentBytes;

        /// <summary>The active file path, or null before the first record.</summary>
        public string CurrentFilePath => _currentFilePath;

        /// <summary>
        /// Record one sanitized metric. No-op in a Release (non-development) build. Never throws.
        /// Rotates / prunes as needed so the capacity budget (records, bytes, file count) holds.
        /// <paramref name="evt"/> and <paramref name="outcome"/> are CLOSED enums — arbitrary text
        /// cannot enter the record.
        /// </summary>
        public void Record(
            VoiceMetricEvent evt,
            string sessionId = null,
            string turnId = null,
            long durationMs = 0,
            int count = 0,
            int closeCode = 0,
            VoiceMetricOutcome outcome = VoiceMetricOutcome.Unknown)
        {
            if (_disposed) return;
            if (!_isDevBuild()) return; // Release: hard no-op, no dir, no bytes

            try
            {
                EnsureDirectory();
                _seq++;
                var rec = new VoiceMetricRecord
                {
                    evt = evt,
                    ts_ms = EpochMs(_now()),
                    duration_ms = durationMs > 0 ? durationMs : 0,
                    hashed_session = _hasher.Hash(sessionId),
                    hashed_turn = _hasher.Hash(turnId),
                    seq = _seq,
                    count = count > 0 ? count : 0,
                    close_code = closeCode,
                    outcome = outcome
                };
                string line = JsonUtility.ToJson(rec, false);
                int bytes = Encoding.UTF8.GetByteCount(line) + 1; // + '\n'

                string path = EnsureActiveFile(bytes);
                File.AppendAllText(path, line + "\n", Encoding.UTF8);
                _currentRecords++;
                _currentBytes += bytes;
                Prune();
            }
            catch
            {
                // Metrics must never break the voice session; a failed write is silently dropped.
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }

        // ---------------- internal ----------------

        private static long EpochMs(DateTime utc) =>
            (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

        private string MetricFilePath(int index) =>
            Path.Combine(_directory, "m_" + index.ToString("D6") + ".jsonl");

        private void EnsureDirectory()
        {
            if (!Directory.Exists(_directory))
                Directory.CreateDirectory(_directory);
        }

        /// <summary>Return the active file path, rolling to a new file when the budget is exhausted.</summary>
        private string EnsureActiveFile(int incomingBytes)
        {
            bool exhausted = _currentRecords >= _maxRecords || _currentBytes + incomingBytes > _maxBytes;
            if (exhausted || _currentFilePath == null || !File.Exists(_currentFilePath))
            {
                _fileIndex++;
                _currentFilePath = MetricFilePath(_fileIndex);
                _currentRecords = 0;
                _currentBytes = 0;
            }
            return _currentFilePath;
        }

        /// <summary>Keep the newest <c>_maxFiles</c> managed JSONL files in the dedicated dir; delete
        /// older ones. Only EVER considers files matching this recorder's own naming pattern
        /// (<c>m_&lt;6-digit&gt;.jsonl</c>) — an unregistered .jsonl placed in the dedicated dir by
        /// anything else is never touched. Never recurses, never lists other directories.</summary>
        private void Prune()
        {
            string[] all;
            try { all = Directory.GetFiles(_directory, "*.jsonl", SearchOption.TopDirectoryOnly); }
            catch { return; }

            var files = new List<string>(all.Length);
            foreach (string f in all)
                if (IsManagedName(Path.GetFileName(f)))
                    files.Add(f);
            if (files.Count <= _maxFiles) return;

            files.Sort(StringComparer.Ordinal); // fixed-width numeric names -> ascending age
            for (int i = 0; i < files.Count - _maxFiles; i++)
            {
                string f = files[i];
                try { File.Delete(f); } catch { }
            }
        }

        /// <summary>This recorder's file naming contract: <c>m_&lt;6-digit&gt;.jsonl</c>.
        /// "m_" (2) + six digits (6) + ".jsonl" (6) = 14 characters.</summary>
        private static bool IsManagedName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Length != 14) return false; // m_XXXXXX.jsonl
            if (!fileName.StartsWith("m_", StringComparison.Ordinal)) return false;
            if (!fileName.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
            for (int i = 2; i < 8; i++)
            {
                char ch = fileName[i];
                if (ch < '0' || ch > '9') return false;
            }
            return true;
        }
    }
}
