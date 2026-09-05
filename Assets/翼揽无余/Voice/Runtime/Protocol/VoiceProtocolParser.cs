using UnityEngine;

namespace Yilan.Voice.Runtime.Protocol
{
    /// <summary>Stable classification code for a parse outcome.</summary>
    public enum VoiceParseCode
    {
        Ok,
        InvalidJson,        // unparseable JSON or missing type
        UnknownType,        // reserved/nonexistent type
        MissingRequiredField, // a required string field is absent/empty
        VersionMismatch,    // status==session.started but protocol_version != voice-ws-v1
        BindingMismatch     // session/turn/audio-stream/playback id disagrees with binding
    }

    /// <summary>Structured result of a parse; never throws.</summary>
    public sealed class VoiceParseResult
    {
        public VoiceParseCode Code;
        public VoiceEnvelope Message;   // typed payload when Code == Ok, else null
        public string Reason;           // human-readable detail / offending field

        public bool IsOk => Code == VoiceParseCode.Ok;

        public static VoiceParseResult Ok(VoiceEnvelope m) =>
            new VoiceParseResult { Code = VoiceParseCode.Ok, Message = m };

        public static VoiceParseResult Fail(VoiceParseCode code, string reason) =>
            new VoiceParseResult { Code = code, Reason = reason };
    }

    /// <summary>
    /// JsonUtility-based parser for voice-ws-v1. First parses the base {type} envelope,
    /// then dispatches strong-typed parsing per type. Only voice.state with
    /// status==session.started is gated on protocol_version == voice-ws-v1.
    /// All failures return a stable <see cref="VoiceParseCode"/>; nothing is thrown.
    /// </summary>
    public static class VoiceProtocolParser
    {
        public const string WireVersion = "voice-ws-v1";

        // Named-field labels for the P1-2 downstream types whose required-field order does not
        // match the positional name table of the legacy FirstMissing(params string[]) overload.
        private static readonly string[] AsrFieldNames = { "session_id", "turn_id", "transcript" };
        private static readonly string[] BargeInFieldNames = { "session_id", "turn_id", "status", "intent" };

        public static VoiceParseResult Parse(string json, VoiceBinding binding = null)
        {
            if (string.IsNullOrEmpty(json))
                return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "empty payload");

            // 1) base envelope only: {type}
            VoiceEnvelope env;
            try { env = JsonUtility.FromJson<VoiceEnvelope>(json); }
            catch { return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "malformed JSON"); }

            if (env == null || string.IsNullOrEmpty(env.type))
                return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "missing type field");

            switch (env.type)
            {
                case "session.start":              return ParseSessionStart(json, binding);
                case "voice.state":                return ParseVoiceState(json, binding);
                case "audio.frame":                return ParseAudioFrame(json, binding);
                case "audio.end":                  return ParseAudioEnd(json, binding);
                case "answer.display":             return ParseAnswerDisplay(json, binding);
                case "clarification.required":     return ParseClarification(json, binding);
                case "session.cancel":             return ParseSessionCancel(json, binding);
                case "tts.frame":                  return ParseTtsFrame(json, binding);
                case "asr.partial":                return ParseAsrTranscript(json, binding);
                case "asr.final":                  return ParseAsrTranscript(json, binding);
                case "barge_in.accepted":          return ParseBargeInAccepted(json, binding);
                case "voice.error":                return ParseVoiceError(json, binding);
                default:
                    return VoiceParseResult.Fail(VoiceParseCode.UnknownType, "unknown type: " + env.type);
            }
        }

        private static VoiceParseResult ParseSessionStart(string json, VoiceBinding binding)
        {
            var m = FromJson<SessionStartMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "session.start malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.audio_stream_id);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "session.start missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, m.audio_stream_id, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "session.start " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseVoiceState(string json, VoiceBinding binding)
        {
            var m = FromJson<VoiceStateMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "voice.state malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.state, m.status);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "voice.state missing " + missing);
            // Version gate: only status==session.started requires protocol_version == voice-ws-v1
            if (m.status == "session.started" && m.protocol_version != WireVersion)
                return VoiceParseResult.Fail(VoiceParseCode.VersionMismatch, "protocol_version expected " + WireVersion);
            var id = CheckIds(binding, m.session_id, m.turn_id, null, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "voice.state " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseAudioFrame(string json, VoiceBinding binding)
        {
            var m = FromJson<AudioFrameMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "audio.frame malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.audio_stream_id);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "audio.frame missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, m.audio_stream_id, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "audio.frame " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseAudioEnd(string json, VoiceBinding binding)
        {
            var m = FromJson<AudioEndMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "audio.end malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.audio_stream_id);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "audio.end missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, m.audio_stream_id, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "audio.end " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseAnswerDisplay(string json, VoiceBinding binding)
        {
            var m = FromJson<AnswerDisplayMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "answer.display malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.answer_id, m.evidence_package_id);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "answer.display missing " + missing);
            if (m.answer == null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "answer.display missing answer");
            var id = CheckIds(binding, m.session_id, m.turn_id, null, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "answer.display " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseClarification(string json, VoiceBinding binding)
        {
            var m = FromJson<ClarificationRequiredMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "clarification.required malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.reason);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "clarification.required missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, null, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "clarification.required " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseSessionCancel(string json, VoiceBinding binding)
        {
            var m = FromJson<SessionCancelMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "session.cancel malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.audio_stream_id);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "session.cancel missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, m.audio_stream_id, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "session.cancel " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseTtsFrame(string json, VoiceBinding binding)
        {
            var m = FromJson<TtsFrameMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "tts.frame malformed");
            var missing = FirstMissing(m.session_id, m.turn_id, m.playback_id, m.codec);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "tts.frame missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, null, m.playback_id);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "tts.frame " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseVoiceError(string json, VoiceBinding binding)
        {
            var m = FromJson<VoiceErrorMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "voice.error malformed");
            var missing = FirstMissing(m.code, m.field);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "voice.error missing " + missing);
            return VoiceParseResult.Ok(m); // carries no session/turn/stream identity
        }

        private static VoiceParseResult ParseAsrTranscript(string json, VoiceBinding binding)
        {
            var m = FromJson<AsrTranscriptMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "asr transcript malformed");
            var missing = FirstMissing(AsrFieldNames, m.session_id, m.turn_id, m.transcript);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, m.type + " missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, null, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, m.type + " " + id);
            return VoiceParseResult.Ok(m);
        }

        private static VoiceParseResult ParseBargeInAccepted(string json, VoiceBinding binding)
        {
            var m = FromJson<BargeInAcceptedMessage>(json);
            if (m == null) return VoiceParseResult.Fail(VoiceParseCode.InvalidJson, "barge_in.accepted malformed");
            var missing = FirstMissing(BargeInFieldNames, m.session_id, m.turn_id, m.status, m.intent);
            if (missing != null) return VoiceParseResult.Fail(VoiceParseCode.MissingRequiredField, "barge_in.accepted missing " + missing);
            var id = CheckIds(binding, m.session_id, m.turn_id, null, null);
            if (id != null) return VoiceParseResult.Fail(VoiceParseCode.BindingMismatch, "barge_in.accepted " + id);
            return VoiceParseResult.Ok(m);
        }

        private static T FromJson<T>(string json) where T : class
        {
            try { return JsonUtility.FromJson<T>(json); }
            catch { return null; }
        }

        /// <summary>Returns the name of the first missing/empty required string field, or null.</summary>
        private static string FirstMissing(params string[] fields)
        {
            var names = new[] { "session_id", "turn_id", "audio_stream_id", "state", "status",
                                "answer_id", "evidence_package_id", "reason", "playback_id",
                                "codec", "code", "field" };
            for (int i = 0; i < fields.Length; i++)
                if (string.IsNullOrEmpty(fields[i]))
                    return names[i];
            return null;
        }

        /// <summary>Named variant: names[i] labels fields[i]; returns the first missing/empty
        /// field's name, or null. Used by types whose field order differs from the legacy table.</summary>
        private static string FirstMissing(string[] names, params string[] fields)
        {
            for (int i = 0; i < fields.Length && i < names.Length; i++)
                if (string.IsNullOrEmpty(fields[i]))
                    return names[i];
            return null;
        }

        /// <summary>
        /// Returns a short mismatch description when any non-null binding id disagrees with
        /// the message's ids, otherwise null. A null binding (or a null binding slot) skips
        /// enforcement for that slot.
        /// </summary>
        private static string CheckIds(VoiceBinding b, string sessionId, string turnId, string audioStreamId, string playbackId)
        {
            if (b == null) return null;
            if (b.SessionId != null && sessionId != b.SessionId) return "session_id mismatch";
            if (b.TurnId != null && turnId != b.TurnId) return "turn_id mismatch";
            if (b.AudioStreamId != null && audioStreamId != b.AudioStreamId) return "audio_stream_id mismatch";
            // P1-2 regression: playback-slot enforcement is OPT-IN — only a message that
            // actually CARRIES a playback_id (tts.frame, whose field is required) is checked
            // against the learned slot. Turn-scoped types (asr.partial/final,
            // clarification.required, barge_in.accepted, voice.state) pass null here because
            // the wire schema has no playback field for them; they must not be rejected as
            // "playback_id mismatch" merely because the current round's binding already
            // learned a playback id from the TTS stream. Cross-round bleed for those types
            // is still caught by the session_id/turn_id slots above.
            if (playbackId != null && b.PlaybackId != null && playbackId != b.PlaybackId)
                return "playback_id mismatch";
            return null;
        }
    }
}
