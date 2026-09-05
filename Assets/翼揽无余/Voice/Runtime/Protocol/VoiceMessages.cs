using System;

namespace Yilan.Voice.Runtime.Protocol
{
    /// <summary>session.start — client -> server, establishes session/turn/stream binding.</summary>
    [Serializable]
    public class SessionStartMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string audio_stream_id;
        public string user_id; // optional
    }

    /// <summary>voice.state — server -> client. protocol_version present ONLY for status == session.started.</summary>
    [Serializable]
    public class VoiceStateMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string state;
        public string status;
        public string protocol_version;
    }

    /// <summary>audio.frame — client -> server, TEXT header immediately followed by a PCM16 binary body.</summary>
    [Serializable]
    public class AudioFrameMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string audio_stream_id;
        public int sequence;
        public int sample_rate;
        public int channels;
        public int timestamp_ms;
        public float energy;
    }

    /// <summary>audio.end — client -> server, marks end of audio input for the bound turn.</summary>
    [Serializable]
    public class AudioEndMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string audio_stream_id;
    }

    /// <summary>Public answer envelope carried inside answer.display.</summary>
    [Serializable]
    public class PublicAnswer
    {
        public string content;
        public string source;
    }

    /// <summary>answer.display — server -> client, final answer for status == answered.</summary>
    [Serializable]
    public class AnswerDisplayMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string answer_id;
        public string evidence_package_id;
        public PublicAnswer answer;
    }

    /// <summary>clarification.required — server -> client, asks for clarification or reports too-long input.</summary>
    [Serializable]
    public class ClarificationRequiredMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string reason;
    }

    /// <summary>session.cancel — client -> server, barge-in cancel of the bound turn.</summary>
    [Serializable]
    public class SessionCancelMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string audio_stream_id;
        public string feedback; // optional
    }

    /// <summary>tts.frame — server -> client, TEXT header immediately followed by ONE binary PCM16 body.</summary>
    [Serializable]
    public class TtsFrameMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string playback_id;
        public int sequence;
        public string codec;
        public int duration_ms;
        public bool is_final;
    }

    /// <summary>asr.partial / asr.final — server -> client, streaming / final transcript for the bound
    /// turn. partial is a live subtitle only; only asr.final is the authoritative question text
    /// (docs pico4_unity_voice_integration.md §7.2 — never re-feed partial into another Q&amp;A path).</summary>
    [Serializable]
    public class AsrTranscriptMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string transcript;
        public float confidence;
        public string provider;
    }

    /// <summary>barge_in.accepted — server -> client, acknowledges our session.cancel and carries the
    /// outcome (status: stopped/clarification/rewritten/preference_recorded) and parsed intent.</summary>
    [Serializable]
    public class BargeInAcceptedMessage : VoiceEnvelope
    {
        public string session_id;
        public string turn_id;
        public string status;
        public string intent;
    }

    /// <summary>voice.error — server -> client, protocol/transport/session error before a close.</summary>
    [Serializable]
    public class VoiceErrorMessage : VoiceEnvelope
    {
        public string code;
        public string field;
    }

    /// <summary>
    /// Immutable identity binding used to validate session/turn/audio-stream/playback ids.
    /// A null field means "not bound / don't enforce" for that id.
    /// </summary>
    [Serializable]
    public class VoiceBinding
    {
        public string SessionId;
        public string TurnId;
        public string AudioStreamId;
        public string PlaybackId;
    }
}
