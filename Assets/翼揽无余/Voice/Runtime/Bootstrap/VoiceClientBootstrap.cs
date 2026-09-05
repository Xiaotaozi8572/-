using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Yilan.Voice.Runtime.Capture;
using Yilan.Voice.Runtime.Metrics;
using Yilan.Voice.Runtime.Pico;
using Yilan.Voice.Runtime.Playback;
using Yilan.Voice.Runtime.Session;
using Yilan.Voice.Runtime.Transport;
using Yilan.Voice.Runtime.UI;

namespace Yilan.Voice.Runtime.Bootstrap
{
    [DisallowMultipleComponent]
    public sealed class VoiceClientBootstrap : MonoBehaviour
    {
        [SerializeField] private VoicePanel panel;
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private string wsHost = "127.0.0.1";
        [SerializeField] private int wsPort = 8765;
        [SerializeField] private string wsPath = "/ws/voice/session";
        [SerializeField] private int maxFramesPerUpdate = 4;

        private VoiceClientRuntime _runtime;
        private UnityTtsAudioPlayer _ttsPlayer;
        private bool _pumpInFlight;

        public bool IsValid { get; private set; }
        public VoiceClientRuntime Runtime => _runtime;

        private void Awake()
        {
            NormalizeCanvasForXr();
            IsValid = ValidateReferences(out _);
            if (!IsValid)
            {
                return;
            }

            var config = new VoiceClientConfig
            {
                WsHost = wsHost,
                WsPort = wsPort,
                WsPath = wsPath,
                UseWss = false
            };

            var permission = new PicoMicrophonePermission(new AndroidMicrophonePermissionPlatform());
            var microphone = new UnityMicrophoneSource(new UnityMicrophoneDevice(), () => permission.HasPermission);
            var playback = new Mp3PlaybackQueue(new UnityMp3Decoder());
            var metrics = new VoiceMetricsRecorder(VoiceIdHasher.CreateRandomSalt());
            _runtime = new VoiceClientRuntime(
                config,
                new NativeVoiceSocket(config.BuildWsUri()),
                ProbeHealthAsync,
                permission,
                microphone,
                playback,
                metrics);

            // P0-5: bridge decoded TTS clips onto the AudioSource. Mp3PlaybackQueue only
            // decodes; without this subscription the clips were silently dropped.
            _ttsPlayer = new UnityTtsAudioPlayer(audioSource);
            playback.FramePlayed += OnTtsFramePlayed;

            // P0-2: visible recording entry. The panel only raises intents; the actions
            // here are the single place that drives the runtime from UI input.
            panel.RecordRequested += OnRecordRequested;
            panel.StopRequested += OnStopRequested;
            panel.CancelRequested += OnCancelRequested;
            // VR10: Faulted finally has a UI exit — the reset button (visible only while the
            // ViewModel is Faulted) settles the controller back to Idle; the next record
            // click reconnects through the existing ConnectAsync path.
            panel.ResetRequested += OnResetRequested;
        }

        // DEVICE-TEST FIX (first PICO 4 session): Screen Space - Overlay canvases do
        // not render into XR eye buffers — on device the user saw only the main
        // camera's solid blue background while the panel stayed invisible (it renders
        // fine in Editor PlayMode, which is why unit/PlayMode tests never caught it).
        // Normalize the prefab's canvas to Screen Space - Camera so the panel renders
        // through the head-tracked main camera at a comfortable 2 m plane distance.
        // No-op when the canvas is missing, already not overlay, or no main camera
        // exists (stripped test harnesses keep their own canvas setup untouched).
        private void NormalizeCanvasForXr()
        {
            var canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                return;
            }
            var camera = Camera.main;
            if (camera == null)
            {
                return;
            }
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 2f;
        }

        private async void Start()
        {
            if (!IsValid || _runtime == null) return;

            await _runtime.StartAsync();
            panel.Bind(_runtime.ViewModel);

            // Barge-in / pause / reconnect / fault must silence the audible channel: the
            // queue-level Cancel() only stops later frames, the clip already sounding on
            // the AudioSource is owned by _ttsPlayer.
            if (_runtime.Controller != null)
            {
                _runtime.Controller.StateChanged += OnControllerStateChanged;
            }
        }

        private void Update()
        {
            if (!IsValid || _runtime == null) return;

            _runtime.Socket?.DispatchMessageQueue();
            _ttsPlayer?.Tick();
            _ = PumpGuardedAsync();
        }

        private async void OnApplicationPause(bool pauseStatus)
        {
            if (_runtime == null) return;
            if (pauseStatus)
            {
                await _runtime.PauseAsync();
                return;
            }
            // P1-5: app foregrounded again — re-open the WebSocket on the SAME
            // session id. PauseAsync settled the controller to Idle; ResumeAsync
            // re-validates the permission session and reconnects from there
            // (a controller that was never paused reports InvalidState, no-op).
            await _runtime.ResumeAsync();
        }

        private void OnDestroy()
        {
            if (_runtime?.Controller != null)
            {
                _runtime.Controller.StateChanged -= OnControllerStateChanged;
            }
            if (panel != null)
            {
                panel.RecordRequested -= OnRecordRequested;
                panel.StopRequested -= OnStopRequested;
                panel.CancelRequested -= OnCancelRequested;
                panel.ResetRequested -= OnResetRequested;
            }
            _runtime?.Dispose();
            _runtime = null;
            _ttsPlayer?.Dispose();
            _ttsPlayer = null;
        }

        public bool ValidateReferences(out string code)
        {
            bool valid = panel != null
                && audioSource != null
                && HasBoundText(panel, "stateText")
                && HasBoundText(panel, "answerText")
                && HasBoundText(panel, "clarificationText")
                && !string.IsNullOrWhiteSpace(wsHost)
                && wsPort > 0
                && !string.IsNullOrWhiteSpace(wsPath)
                && maxFramesPerUpdate > 0;

            IsValid = valid;
            code = valid ? "ok" : "bootstrap_invalid";
            return valid;
        }

        private void OnTtsFramePlayed(TtsPlayedItem item)
        {
            if (item == null) return;
            _ttsPlayer?.Enqueue(item.Clip);
        }

        private void OnRecordRequested()
        {
            _ = _runtime?.BeginCaptureAsync();
        }

        private void OnStopRequested()
        {
            _ = _runtime?.EndCaptureAsync();
        }

        private void OnCancelRequested()
        {
            _ = _runtime?.CancelAsync();
        }

        private void OnResetRequested()
        {
            var controller = _runtime?.Controller;
            if (controller != null && controller.State == VoiceClientState.Faulted)
            {
                // Faulted -> Idle; per-session state is cleared by Reset() so the next
                // record click starts a fresh session id + turn counter.
                controller.Reset();
            }
        }

        private void OnControllerStateChanged(VoiceClientState state)
        {
            if (state == VoiceClientState.Cancelling
                || state == VoiceClientState.Reconnecting
                || state == VoiceClientState.Faulted)
            {
                _ttsPlayer?.Cancel();
            }
        }

        private async Task PumpGuardedAsync()
        {
            if (_pumpInFlight || _runtime == null) return;

            _pumpInFlight = true;
            try
            {
                await _runtime.PumpAsync(maxFramesPerUpdate);
            }
            finally
            {
                _pumpInFlight = false;
            }
        }

        private async Task<bool> ProbeHealthAsync(VoiceClientConfig config)
        {
            var ws = config.BuildWsUri();
            var baseUri = new UriBuilder(config.UseWss ? "https" : "http", ws.Host, ws.Port).Uri;
            var result = await VoiceHealthClient.CheckAsync(
                VoiceHealthClient.HealthUrlFromBase(baseUri),
                config.HealthProbeTimeoutMs).ConfigureAwait(false);
            return result.Status == VoiceHealthStatus.Ok;
        }

        private static bool HasBoundText(VoicePanel voicePanel, string fieldName)
        {
            var field = typeof(VoicePanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && field.GetValue(voicePanel) is Text text && text != null;
        }
    }
}
