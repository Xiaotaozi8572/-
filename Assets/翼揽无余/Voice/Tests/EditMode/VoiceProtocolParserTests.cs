using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Protocol;

namespace Yilan.Voice.Protocol
{
    /// <summary>
    /// EditMode tests for the voice-ws-v1 protocol DTO + parser against the shared
    /// fixtures (read-only) under Assets/翼揽无余/Voice/Fixtures/voice_ws_v1.
    /// </summary>
    [TestFixture]
    public class VoiceProtocolParserTests
    {
        private const string ForbiddenAnswerDelta = "answer.delta";
        private const string ForbiddenAnswerDone = "answer.done";
        private const string ForbiddenTurnCancel = "turn.cancel";

        // ---- Fixture wrapper DTOs (shape of the shared gold JSON), [Serializable]
        // so Unity JsonUtility can project the `example`/`payload` sub-objects.
        [Serializable] private class SessionStartWrapper { public SessionStartMessage example; }
        [Serializable] private class VoiceStateWrapper { public VoiceStateMessage example; }
        [Serializable] private class AudioFrameWrapper { public AudioFrameMessage example; }
        [Serializable] private class AudioEndWrapper { public AudioEndMessage example; }
        [Serializable] private class AnswerDisplayWrapper { public AnswerDisplayMessage example; }
        [Serializable] private class ClarificationWrapper { public ClarificationRequiredMessage example; }
        [Serializable] private class SessionCancelWrapper { public SessionCancelMessage example; }
        [Serializable] private class TtsFrameWrapper { public TtsFrameMessage example; }
        [Serializable] private class VoiceErrorWrapper { public VoiceErrorMessage example; }

        private static string FixturesDir =>
            Path.Combine(Application.dataPath, "翼揽无余", "Voice", "Fixtures", "voice_ws_v1");

        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(FixturesDir, name));

        // ---- Legal gold samples all parse OK ----

        [Test]
        public void SessionStart_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<SessionStartWrapper>(Fixture("session_start.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var m = (SessionStartMessage)res.Message;
            Assert.AreEqual("vr-test-session-001", m.session_id);
            Assert.AreEqual("turn-0001", m.turn_id);
            Assert.AreEqual("stream-audio-001", m.audio_stream_id);
        }

        [Test]
        public void VoiceStateSessionStarted_Gold_ParsesOk_WithProtocolVersion()
        {
            var w = JsonUtility.FromJson<VoiceStateWrapper>(Fixture("voice_state_session_started.json"));
            var json = JsonUtility.ToJson(w.example);
            var res = VoiceProtocolParser.Parse(json);
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var m = (VoiceStateMessage)res.Message;
            Assert.AreEqual("session.started", m.status);
            Assert.AreEqual("voice-ws-v1", m.protocol_version);
        }

        [Test]
        public void AudioFrame_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<AudioFrameWrapper>(Fixture("audio_frame.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
        }

        [Test]
        public void AudioEnd_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<AudioEndWrapper>(Fixture("audio_end.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
        }

        [Test]
        public void AnswerDisplay_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<AnswerDisplayWrapper>(Fixture("answer_display.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var m = (AnswerDisplayMessage)res.Message;
            Assert.IsNotNull(m.answer);
            Assert.IsNotEmpty(m.answer.source);
        }

        [Test]
        public void ClarificationRequired_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<ClarificationWrapper>(Fixture("clarification_required.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var m = (ClarificationRequiredMessage)res.Message;
            Assert.AreEqual("voice_input_unclear", m.reason);
        }

        [Test]
        public void SessionCancel_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<SessionCancelWrapper>(Fixture("session_cancel.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
        }

        [Test]
        public void TtsFrame_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<TtsFrameWrapper>(Fixture("tts_frame.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var m = (TtsFrameMessage)res.Message;
            Assert.AreEqual("playback-0501", m.playback_id);
        }

        [Test]
        public void VoiceError_Gold_ParsesOk()
        {
            var w = JsonUtility.FromJson<VoiceErrorWrapper>(Fixture("voice_error.json"));
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(w.example));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var m = (VoiceErrorMessage)res.Message;
            Assert.AreEqual("VOICE_PROTOCOL_INVALID", m.code);
        }

        // ---- P1-2: asr.partial / asr.final / barge_in.accepted (documented voice-ws-v1
        //      downstream types; no authoritative Python fixture exists, so samples are
        //      built inline from the schema in api_contracts.md §7.2 / §7.6) ----

        [Test]
        public void AsrPartial_And_AsrFinal_ParseOk()
        {
            var partial = new AsrTranscriptMessage
            {
                type = "asr.partial",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                transcript = "飞机的发动机",
                confidence = 0.82f,
                provider = "whisper"
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(partial));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var m = (AsrTranscriptMessage)res.Message;
            Assert.AreEqual("asr.partial", m.type);
            Assert.AreEqual("飞机的发动机", m.transcript);
            Assert.AreEqual(0.82f, m.confidence);

            var final = new AsrTranscriptMessage
            {
                type = "asr.final",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                transcript = "飞机的发动机是做什么的",
                confidence = 0.95f,
                provider = "whisper"
            };
            var res2 = VoiceProtocolParser.Parse(JsonUtility.ToJson(final));
            Assert.AreEqual(VoiceParseCode.Ok, res2.Code, res2.Reason);
            Assert.AreEqual("asr.final", res2.Message.type);
        }

        [Test]
        public void AsrTranscript_MissingTranscript_Rejected()
        {
            var m = new AsrTranscriptMessage
            {
                type = "asr.final",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001"
                // transcript omitted
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.MissingRequiredField, res.Code, res.Reason);
            StringAssert.Contains("transcript", res.Reason);
        }

        [Test]
        public void BargeInAccepted_ParsesOk()
        {
            var m = new BargeInAcceptedMessage
            {
                type = "barge_in.accepted",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                status = "stopped",
                intent = "stop"
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
            var b = (BargeInAcceptedMessage)res.Message;
            Assert.AreEqual("stopped", b.status);
            Assert.AreEqual("stop", b.intent);
        }

        [Test]
        public void BargeInAccepted_MissingStatus_Rejected()
        {
            var m = new BargeInAcceptedMessage
            {
                type = "barge_in.accepted",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                intent = "stop"
                // status omitted
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.MissingRequiredField, res.Code, res.Reason);
            StringAssert.Contains("status", res.Reason);
        }

        [Test]
        public void AsrTranscript_SessionIdMismatch_Rejected()
        {
            var binding = new VoiceBinding
            {
                SessionId = "vr-test-session-001",
                TurnId = "turn-0001"
            };
            var m = new AsrTranscriptMessage
            {
                type = "asr.final",
                session_id = "OTHER-SESSION-999",
                turn_id = "turn-0001",
                transcript = "跨会话转写",
                confidence = 0.9f,
                provider = "whisper"
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m), binding);
            Assert.AreEqual(VoiceParseCode.BindingMismatch, res.Code, res.Reason);
        }

        // ---- Version gate: only status==session.started requires voice-ws-v1 ----

        [Test]
        public void SessionStarted_UnknownVersion_Rejected()
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                state = "listening",
                status = "session.started",
                protocol_version = "voice-ws-v2"
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.VersionMismatch, res.Code, res.Reason);
        }

        [Test]
        public void SessionStarted_MissingVersion_Rejected()
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                state = "listening",
                status = "session.started"
                // protocol_version omitted
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.VersionMismatch, res.Code, res.Reason);
        }

        [Test]
        public void NonSessionStarted_State_DoesNotRequireVersion()
        {
            var m = new VoiceStateMessage
            {
                type = "voice.state",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                state = "listening",
                status = "frame_accepted"
                // no protocol_version -> must still parse OK
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.Ok, res.Code, res.Reason);
        }

        // ---- Missing required fields ----

        [Test]
        public void MissingRequiredField_Rejected()
        {
            // session.start missing session_id
            var m = new SessionStartMessage
            {
                type = "session.start",
                turn_id = "turn-0001",
                audio_stream_id = "stream-audio-001"
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.MissingRequiredField, res.Code, res.Reason);
        }

        // ---- Unknown type ----

        [Test]
        public void UnknownType_Rejected()
        {
            var m = new VoiceEnvelope { type = "something.unknown" };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m));
            Assert.AreEqual(VoiceParseCode.UnknownType, res.Code, res.Reason);
        }

        [Test]
        public void ForbiddenWireTypes_NotPresentInFixtures()
        {
            // Every fixture file, when projected, must not contain forbidden wire types.
            var names = new[]
            {
                "answer_display.json", "audio_end.json", "audio_frame.json",
                "clarification_required.json", "session_cancel.json", "session_start.json",
                "tts_frame.json", "voice_error.json", "voice_state_session_started.json"
            };
            foreach (var n in names)
            {
                var raw = Fixture(n);
                Assert.IsTrue(!raw.Contains(ForbiddenAnswerDelta), $"{n} must not contain {ForbiddenAnswerDelta}");
                Assert.IsTrue(!raw.Contains(ForbiddenAnswerDone), $"{n} must not contain {ForbiddenAnswerDone}");
                Assert.IsTrue(!raw.Contains(ForbiddenTurnCancel), $"{n} must not contain {ForbiddenTurnCancel}");
            }
        }

        // ---- Binding mismatch ----

        [Test]
        public void SessionIdMismatch_Rejected()
        {
            var binding = new VoiceBinding
            {
                SessionId = "vr-test-session-001",
                TurnId = "turn-0001",
                AudioStreamId = "stream-audio-001"
            };
            var m = new AudioFrameMessage
            {
                type = "audio.frame",
                session_id = "OTHER-SESSION-999",
                turn_id = "turn-0001",
                audio_stream_id = "stream-audio-001",
                sequence = 0,
                sample_rate = 16000,
                channels = 1,
                timestamp_ms = 0,
                energy = 0f
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m), binding);
            Assert.AreEqual(VoiceParseCode.BindingMismatch, res.Code, res.Reason);
        }

        [Test]
        public void PlaybackIdMismatch_RejectedForTtsFrame()
        {
            var binding = new VoiceBinding
            {
                SessionId = "vr-test-session-001",
                TurnId = "turn-0001",
                PlaybackId = "playback-0501"
            };
            var m = new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                playback_id = "playback-wrong-999",
                sequence = 0,
                codec = "pcm16",
                duration_ms = 20,
                is_final = false
            };
            var res = VoiceProtocolParser.Parse(JsonUtility.ToJson(m), binding);
            Assert.AreEqual(VoiceParseCode.BindingMismatch, res.Code, res.Reason);
        }
    }
}
