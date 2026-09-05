using System;
using UnityEngine;

namespace Yilan.Voice.Runtime.Session
{
    /// <summary>
    /// Controlled client configuration for a voice session (voice-ws-v1).
    ///
    /// DELIBERATE SCOPE LIMIT: this class stores ONLY the
    ///  (a) controlled service address of the voice WebSocket endpoint, and
    ///  (b) transport-level technical timeouts (connect / send / health / reconnect).
    ///
    /// It must NOT re-hardcode the voice-ws-v1 protocol business thresholds. Those live
    /// single-sourced in the shared backend configuration and gold fixtures:
    ///   - configs/voice.yaml  : vad_threshold 0.2, endpoint_silence_ms 600,
    ///                           max_utterance_ms 15000, max_frames 750, sample_rate 16000
    ///   - Fixtures/voice_ws_v1/audio_frame.json : samples_per_frame 320, bytes_per_frame 640
    ///   - Fixtures/voice_ws_v1/close_codes.json : wire close codes (1000,1009,4400,4401,4408)
    ///   - VoiceProtocolParser.WireVersion        : protocol version name "voice-ws-v1"
    /// Keeping those out of this class prevents the client from drifting from the shared
    /// contract if the server settings change.
    /// </summary>
    [Serializable]
    public class VoiceClientConfig
    {
        // ---------- Controlled service address (no query string, no credentials) ----------
        [Tooltip("Voice WebSocket host (no scheme, no query).")]
        public string WsHost = "127.0.0.1";

        [Tooltip("Voice WebSocket port. <=0 falls back to 443 (wss) / 80 (ws).")]
        public int WsPort;

        [Tooltip("true -> wss://, false -> ws://")]
        public bool UseWss;

        [Tooltip("Optional path segment, e.g. /ws/voice/session. Must not contain '?'.")]
        public string WsPath;

        // ---------- Transport-level technical timeouts (ms, infra, not protocol rules) ----------
        public int ConnectTimeoutMs = 8000;
        public int SendTimeoutMs = 5000;
        public int HealthProbeTimeoutMs = 5000;   // mirrors VoiceHealthClient.DefaultTimeoutMs
        public int ReconnectDelayMs = 2000;

        [Tooltip("Maximum time to wait for barge_in.accepted before a cancel settles locally.")]
        public int CancelAckTimeoutMs = 2000;

        /// <summary>
        /// Build the ws/wss URI for <see cref="Sender"/>. Input fields: host / port / scheme /
        /// path are all caller-controlled constants; nothing here is derived from protocol rules.
        /// </summary>
        public Uri BuildWsUri()
        {
            string host = string.IsNullOrWhiteSpace(WsHost) ? "127.0.0.1" : WsHost.Trim();
            int port = WsPort > 0 ? WsPort : (UseWss ? 443 : 80);
            var builder = new UriBuilder(UseWss ? "wss" : "ws", host, port);
            if (!string.IsNullOrWhiteSpace(WsPath))
                builder.Path = WsPath.Trim();
            return builder.Uri;
        }
    }
}
