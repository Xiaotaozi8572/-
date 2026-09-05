using System;

namespace Yilan.Voice.Runtime.Protocol
{
    /// <summary>Stable classification code for a tts.frame header/binary pairing event.</summary>
    public enum TtsPairCode
    {
        HeaderAccepted, // a text header was stored as pending (no error)
        HeaderConsumed, // a pending header was consumed and cleared by a binary body (success)
        DoubleHeader,   // a second text header arrived while one was already pending
        BareBinary,     // a binary body arrived with no pending header
        IdMismatch      // header ids disagree with the binding
    }

    /// <summary>Structured outcome of a pairing event; never throws.</summary>
    public sealed class TtsPairResult
    {
        public TtsPairCode Code;
        public TtsFrameMessage Header; // pending header; set on HeaderConsumed (and HeaderAccepted)
        public string Reason;

        public static TtsPairResult Of(TtsPairCode code, TtsFrameMessage header, string reason) =>
            new TtsPairResult { Code = code, Header = header, Reason = reason };
    }

    /// <summary>
    /// Pairs tts.frame TEXT headers with the PCM16 binary body that must IMMEDIATELY follow.
    /// A header sets pending; the next binary consumes and clears it. A second header while
    /// pending -> DoubleHeader; a binary with nothing pending -> BareBinary; an id that
    /// disagrees with the binding -> IdMismatch. All failures return a stable
    /// <see cref="TtsPairCode"/>; nothing is thrown.
    /// </summary>
    public sealed class TtsBinaryPairer
    {
        private readonly VoiceBinding _binding;
        private TtsFrameMessage _pending;

        public TtsBinaryPairer(VoiceBinding binding)
        {
            _binding = binding;
        }

        public bool HasPending => _pending != null;

        /// <summary>Current pending header, or null when nothing is pending.</summary>
        public TtsFrameMessage PendingHeader => _pending;

        /// <summary>Feed a tts.frame JSON header text. Validates bound ids, then stores pending.</summary>
        public TtsPairResult OnTextHeader(TtsFrameMessage header)
        {
            if (header == null)
                return TtsPairResult.Of(TtsPairCode.IdMismatch, null, "null header");

            var idProblem = CheckIds(_binding, header);
            if (idProblem != null)
                return TtsPairResult.Of(TtsPairCode.IdMismatch, null, idProblem);

            if (_pending != null)
                return TtsPairResult.Of(TtsPairCode.DoubleHeader, null, "double header without binary");

            _pending = header;
            return TtsPairResult.Of(TtsPairCode.HeaderAccepted, header, null);
        }

        /// <summary>Feed a binary PCM16 body. Consumes and clears the pending header, or reports bare binary.</summary>
        public TtsPairResult OnBinary(byte[] data)
        {
            if (_pending == null)
                return TtsPairResult.Of(TtsPairCode.BareBinary, null, "bare binary without header");

            var header = _pending;
            _pending = null; // consumed and cleared
            return TtsPairResult.Of(TtsPairCode.HeaderConsumed, header, null);
        }

        /// <summary>Supports ArraySegment bodies for zero-copy wire consumption.</summary>
        public TtsPairResult OnBinary(ArraySegment<byte> data)
        {
            if (data.Count <= 0)
                return TtsPairResult.Of(TtsPairCode.BareBinary, null, "empty binary payload");
            return OnBinary(new byte[data.Count]); // treat as opaque body; only pending state matters
        }

        private static string CheckIds(VoiceBinding b, TtsFrameMessage h)
        {
            if (b == null) return null;
            if (string.IsNullOrEmpty(h.session_id)) return "missing session_id";
            if (b.SessionId != null && h.session_id != b.SessionId) return "session_id mismatch";
            if (b.TurnId != null && h.turn_id != b.TurnId) return "turn_id mismatch";
            if (b.PlaybackId != null && h.playback_id != b.PlaybackId) return "playback_id mismatch";
            return null;
        }
    }
}
