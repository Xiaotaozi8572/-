// VoiceMetricRecord.cs
// VR5-T13: the minimal, sanitized metric record that may be written to disk.
//
// DELIBERATE SCHEMA BOUNDARY (single source of the allowed field set):
//   Only hashed identifiers, monotonic timestamps/durations, counts, categories and close codes.
//   There is NO transcript, NO answer text, NO audio bytes, NO full URL, and NO raw
//   session_id/turn_id field. Categories/outcomes are CLOSED ENUMS serialized as integers, so the
//   wire/disk JSONL can never carry an arbitrary substring (e.g. a mis-typed event name) — the
//   ADB sensitive-scan (transcript|answer|audio_bytes|ws://|known-text) is structurally zero-match.
using System;

namespace Yilan.Voice.Runtime.Metrics
{
    /// <summary>Closed set of recordable metric events (serialized as int; no free text).</summary>
    public enum VoiceMetricEvent
    {
        Unknown = 0,
        Connect = 1,
        SessionStarted = 2,
        RecordStart = 3,
        AudioEnd = 4,
        Result = 5,
        TtsFrame = 6,
        PlayComplete = 7,
        Cancel = 8,
        Reconnect = 9,
        StateChange = 10,
        Error = 11
    }

    /// <summary>Closed set of stable outcome classifications (serialized as int; mirrors the
    /// controller's VoiceCommandResult categories, names kept identical where applicable).</summary>
    public enum VoiceMetricOutcome
    {
        Unknown = 0,
        Ok = 1,
        Idempotent = 2,
        InvalidState = 3,
        Faulted = 4,
        TransportFailed = 5,
        SequenceMismatch = 6,
        StaleTurn = 7,
        OtherSession = 8,
        LateAnswer = 9,
        LateTts = 10
    }

    /// <summary>One JSONL line: a sanitized performance/voice-health metric.</summary>
    [Serializable]
    public sealed class VoiceMetricRecord
    {
        /// <summary>Event category (closed enum, numeric on disk).</summary>
        public VoiceMetricEvent evt;

        /// <summary>Monotonic timestamp in millis since UTC epoch (increasing within a session).</summary>
        public long ts_ms;

        /// <summary>Duration of the event in millis (>=0), 0 when not applicable.</summary>
        public long duration_ms;

        /// <summary>Salted hashed session id — NEVER the raw id (see VoiceIdHasher; hex only).</summary>
        public string hashed_session;

        /// <summary>Salted hashed turn id, empty when no turn context (hex only).</summary>
        public string hashed_turn;

        /// <summary>Monotonic record sequence (per recorder instance), for ordering/audit.</summary>
        public long seq;

        /// <summary>Count carried by the event (e.g. audio frames, TTS segments), >=0.</summary>
        public int count;

        /// <summary>WebSocket close code (voice-ws-v1 set 1000/1009/4400/4401/4408) or 0.</summary>
        public int close_code;

        /// <summary>Stable outcome classification (closed enum, numeric on disk).</summary>
        public VoiceMetricOutcome outcome;
    }
}
