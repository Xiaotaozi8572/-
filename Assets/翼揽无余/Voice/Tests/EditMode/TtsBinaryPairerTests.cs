using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yilan.Voice.Runtime.Protocol;

namespace Yilan.Voice.Protocol
{
    /// <summary>
    /// EditMode tests for TtsBinaryPairer using the shared fixtures (read-only).
    /// </summary>
    [TestFixture]
    public class TtsBinaryPairerTests
    {
        [Serializable] private class TtsSeqWrapper
        {
            [Serializable] public class Item { public string kind; public TtsFrameMessage payload; public string base64_pcm16_640_bytes_zero; }
            public Item[] sequence;
        }

        [Serializable] private class MismatchWrapper
        {
            [Serializable] public class Examples
            {
                [Serializable] public class BindingData
                {
                    public string session_id;
                    public string turn_id;
                    public string audio_stream_id;
                }
                [Serializable] public class PlaybackPayload { public TtsFrameMessage payload; }
                public BindingData binding;
                public PlaybackPayload mismatched_playback_id;
            }
            public Examples examples;
        }

        private static string FixturesDir =>
            Path.Combine(Application.dataPath, "翼揽无余", "Voice", "Fixtures", "voice_ws_v1");

        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(FixturesDir, name));

        // ---- Legal header -> binary consume + clear pending ----

        [Test]
        public void LegalHeaderBinary_ConsumesAndClearsPending()
        {
            var h = new TtsFrameMessage
            {
                type = "tts.frame",
                session_id = "vr-test-session-001",
                turn_id = "turn-0001",
                playback_id = "playback-0501",
                sequence = 0,
                codec = "pcm16",
                duration_ms = 20,
                is_final = false
            };
            var pairer = new TtsBinaryPairer(new VoiceBinding
            {
                SessionId = "vr-test-session-001",
                TurnId = "turn-0001",
                PlaybackId = "playback-0501"
            });

            var set = pairer.OnTextHeader(h);
            Assert.AreEqual(TtsPairCode.HeaderAccepted, set.Code, set.Reason);
            Assert.IsTrue(pairer.HasPending, "header must be pending before binary");

            var consume = pairer.OnBinary(new byte[640]);
            Assert.AreEqual(TtsPairCode.HeaderConsumed, consume.Code, consume.Reason);
            Assert.IsFalse(pairer.HasPending, "pending must be cleared after binary consumed");
            Assert.AreEqual(h.playback_id, consume.Header.playback_id);
        }

        [Test]
        public void CanonicalSequence_PairsAllHeaders()
        {
            var w = JsonUtility.FromJson<TtsSeqWrapper>(Fixture("tts_header_binary_sequence.json"));
            var binding = new VoiceBinding
            {
                SessionId = "vr-test-session-001",
                TurnId = "turn-0001",
                PlaybackId = "playback-0501"
            };
            var pairer = new TtsBinaryPairer(binding);
            int consumed = 0;
            foreach (var item in w.sequence)
            {
                if (item.kind == "text")
                {
                    var r = pairer.OnTextHeader(item.payload);
                    Assert.AreEqual(TtsPairCode.HeaderAccepted, r.Code, r.Reason);
                }
                else // binary
                {
                    var r = pairer.OnBinary(new byte[640]);
                    Assert.AreEqual(TtsPairCode.HeaderConsumed, r.Code, r.Reason);
                    consumed++;
                }
            }
            Assert.AreEqual(2, consumed);
            Assert.IsFalse(pairer.HasPending);
        }

        // ---- Double header -> classified ----

        [Test]
        public void DoubleHeader_Classified()
        {
            var w = JsonUtility.FromJson<TtsSeqWrapper>(Fixture("invalid_tts_double_header.json"));
            var pairer = new TtsBinaryPairer(null);

            var first = pairer.OnTextHeader(w.sequence[0].payload);
            Assert.AreEqual(TtsPairCode.HeaderAccepted, first.Code, first.Reason);
            Assert.IsTrue(pairer.HasPending);

            var second = pairer.OnTextHeader(w.sequence[1].payload);
            Assert.AreEqual(TtsPairCode.DoubleHeader, second.Code, second.Reason);
        }

        // ---- Bare binary -> classified ----

        [Test]
        public void BareBinary_Classified()
        {
            var pairer = new TtsBinaryPairer(null);
            var r = pairer.OnBinary(new byte[640]);
            Assert.AreEqual(TtsPairCode.BareBinary, r.Code, r.Reason);
            Assert.IsFalse(pairer.HasPending);
        }

        // ---- Playback id mismatch -> rejected ----

        [Test]
        public void PlaybackIdMismatch_Rejected()
        {
            var w = JsonUtility.FromJson<MismatchWrapper>(Fixture("mismatch_ids.json"));
            var binding = new VoiceBinding
            {
                SessionId = w.examples.binding.session_id,
                TurnId = w.examples.binding.turn_id,
                PlaybackId = "playback-0501"
            };
            var pairer = new TtsBinaryPairer(binding);
            var h = w.examples.mismatched_playback_id.payload;
            var r = pairer.OnTextHeader(h);
            Assert.AreEqual(TtsPairCode.IdMismatch, r.Code, r.Reason);
        }
    }
}
