using System;
using Yilan.Voice.Runtime.Protocol;
using Yilan.Voice.Runtime.Session;

namespace Yilan.Voice.Runtime.UI
{
    /// <summary>
    /// UI-facing projection of the voice session. This is a pure (non-MonoBehaviour) class so
    /// it is directly testable in PlayMode against the real <see cref="VoiceSessionController"/>.
    ///
    /// SCOPE: it is a read-only projection layer. It never mutates controller state; it only
    /// subscribes to the controller's <see cref="VoiceSessionController.StateChanged"/>,
    /// <see cref="VoiceSessionController.AnswerReceived"/> and
    /// <see cref="VoiceSessionController.MessageRejected"/> events and maps them onto the
    /// richer <see cref="VoiceUiState"/> set the UI needs.
    ///
    /// ANSWER PROJECTION (VR4-T10): the authoritative gold carrier for an answer is
    /// <c>answer.content</c> (see Fixtures/voice_ws_v1/answer_display.json and
    /// <see cref="PublicAnswer"/>), NOT a fabricated short/main_answer. The controller already
    /// validates turn/round via the VR2-T07 rules and only fires <see cref="VoiceSessionController.AnswerReceived"/>
    /// for an accepted current-turn, once-fixed answer; this ViewModel therefore projects the
    /// received content and keeps it fixed (does not overwrite on any later message) until a
    /// new turn clears it.
    ///
    /// CLARIFICATION (P1-6 / VRR3): the controller fires
    /// <see cref="VoiceSessionController.ClarificationRequired"/> for a live AwaitingAnswer
    /// turn; this ViewModel subscribes symmetrically and projects the Clarification UI state
    /// from the server event. CONNECTION-LOST / DEGRADED still have no controller event (an
    /// abnormal close routes to Faulted/Reconnecting), so the explicit notification hooks
    /// (<see cref="NotifyConnectionLost"/>, <see cref="NotifyDegraded"/>) remain for the
    /// integration scene / panel / test to drive. <see cref="NotifyClarification"/> is kept
    /// only for explicit LOCAL UI hints and must not be used to fake server events in tests.
    /// </summary>
    public enum VoiceUiState
    {
        Idle,
        Ready,
        Connecting,
        Capturing,
        AwaitingAnswer,
        AnswerDisplayed,
        Clarification,
        Playing,
        Cancelling,
        Reconnecting,
        Degraded,
        Faulted,
        ConnectionLost
    }

    public sealed class VoiceViewModel : IDisposable
    {
        private static readonly string ClarificationDefaultPrompt = "未听清，请重新说一遍或表述更具体一些。";
        private const string SessionExpiredHint = "会话超时，点击重新提问";
        private const string TtsDegradedHint = "语音播报不可用，已显示文字回答";

        private readonly VoiceSessionController _controller;
        private bool _answerFixed;
        private bool _disposed;

        /// <summary>Current UI state for the voice panel.</summary>
        public VoiceUiState State { get; private set; } = VoiceUiState.Idle;

        /// <summary>Once-fixed answer text (gold carrier <c>answer.content</c>).</summary>
        public string AnswerText { get; private set; } = string.Empty;

        /// <summary>True once the current turn's answer has been fixed; cleared on a new turn.</summary>
        public bool AnswerFixed => _answerFixed;

        /// <summary>Clarification reason (e.g. voice_input_unclear) when actively clarifying.</summary>
        public string ClarificationReason { get; private set; } = string.Empty;

        /// <summary>Local, non-localized user-facing prompt copy shown during clarification.</summary>
        public string ClarificationPrompt { get; private set; } = ClarificationDefaultPrompt;

        /// <summary>True when the session is operating in a degraded/reconnecting mode.</summary>
        public bool IsDegraded { get; private set; }

        /// <summary>Latest transcript text for the current turn (P1-2): asr.partial streams a
        /// live subtitle, asr.final fixes the final question text. Display-only by contract.</summary>
        public string TranscriptText { get; private set; } = string.Empty;

        /// <summary>True once the projected transcript is an asr.final (final) result.</summary>
        public bool TranscriptIsFinal { get; private set; }

        /// <summary>VR10: recovery hint copy (session timeout / TTS degradation). Set via the
        /// controller's SessionExpired / TtsDegraded events; cleared when a new round starts.</summary>
        public string HintText { get; private set; } = string.Empty;

        /// <summary>VR10: the reset exit is only meaningful in Faulted — the panel shows its
        /// reset button (if wired) exclusively for this state.</summary>
        public bool ResetVisible => State == VoiceUiState.Faulted;

        /// <summary>Fired whenever any projected value changes; the display layer re-renders on this.</summary>
        public event Action Changed;

        public VoiceViewModel(VoiceSessionController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _controller.StateChanged += OnStateChanged;
            _controller.AnswerReceived += OnAnswerReceived;
            _controller.MessageRejected += OnMessageRejected;
            _controller.TranscriptReceived += OnTranscriptReceived;
            _controller.ClarificationRequired += OnClarificationRequired;
            // VR10: recovery hints are projected straight from the controller events (the
            // settle transitions fire before them, so the hint lands on the settled state).
            _controller.SessionExpired += OnSessionExpired;
            _controller.TtsDegraded += OnTtsDegraded;
        }

        private void OnSessionExpired()
        {
            ShowHint(SessionExpiredHint);
        }

        private void OnTtsDegraded()
        {
            ShowHint(TtsDegradedHint);
        }

        private void OnStateChanged(VoiceClientState s)
        {
            switch (s)
            {
                case VoiceClientState.Idle:
                    State = VoiceUiState.Idle;
                    break;
                case VoiceClientState.HealthChecking:
                    State = VoiceUiState.Connecting;
                    break;
                case VoiceClientState.Connecting:
                    State = VoiceUiState.Connecting;
                    break;
                case VoiceClientState.Ready:
                    State = VoiceUiState.Ready;
                    break;
                case VoiceClientState.Capturing:
                    // A NEW turn begins: clear any previously fixed answer / clarification so a
                    // fresh round can project its own answer once (承接 T07 一次固化语义).
                    State = VoiceUiState.Capturing;
                    ClearRound();
                    break;
                case VoiceClientState.AwaitingAnswer:
                    State = VoiceUiState.AwaitingAnswer;
                    break;
                case VoiceClientState.Playing:
                    // AnswerReceived (==> AnswerDisplayed) fires right after this transition in
                    // the controller; keep Playing transient so the answer text is the prominent
                    // UI state. If no answer text is present yet, surface Playing.
                    if (!_answerFixed)
                        State = VoiceUiState.Playing;
                    break;
                case VoiceClientState.Cancelling:
                    State = VoiceUiState.Cancelling;
                    break;
                case VoiceClientState.Reconnecting:
                    State = VoiceUiState.Degraded;
                    IsDegraded = true;
                    break;
                case VoiceClientState.Faulted:
                    State = VoiceUiState.Faulted;
                    break;
                default:
                    State = VoiceUiState.Idle;
                    break;
            }
            RaiseChanged();
        }

        private void OnAnswerReceived(string content)
        {
            // The controller only fires AnswerReceived for an accepted, once-fixed current-turn
            // answer. Guard here so a stray re-entrant event can never double-project.
            if (_answerFixed) return;
            _answerFixed = true;
            AnswerText = content ?? string.Empty;
            State = VoiceUiState.AnswerDisplayed;
            RaiseChanged();
        }

        private void OnMessageRejected(VoiceCommandResult result)
        {
            // Rejections (stale/other/late/sequence) never project an answer and never change
            // the fixed answer text. Currently informational for the projection layer; kept as
            // a no-op hook so the subscription is meaningful and future UI can surface rejects.
            // No state mutation here by design.
        }

        private void OnTranscriptReceived(AsrTranscriptMessage m)
        {
            // asr.partial is a live subtitle; asr.final fixes the final question text. Both are
            // display-only (docs §7.2: partial must never be re-fed into another Q&A path).
            TranscriptText = m.transcript ?? string.Empty;
            TranscriptIsFinal = m.type == "asr.final";
            RaiseChanged();
        }

        private void OnClarificationRequired(string reason)
        {
            // P1-6 / VRR3: the server-driven clarification.required now reaches the projection
            // through the controller event; NotifyClarification remains available for explicit
            // LOCAL UI hints only (tests must not use it to fake server events).
            NotifyClarification(reason);
        }

        /// <summary>Drive an independent clarification state with a stable local prompt.</summary>
        public void NotifyClarification(string reason)
        {
            ClarificationReason = reason ?? string.Empty;
            ClarificationPrompt = ClarificationDefaultPrompt;
            State = VoiceUiState.Clarification;
            RaiseChanged();
        }

        /// <summary>Drive the distinct connection-lost UI state.</summary>
        public void NotifyConnectionLost()
        {
            State = VoiceUiState.ConnectionLost;
            RaiseChanged();
        }

        /// <summary>Drive the degraded UI state (e.g. degraded network / reconnecting projection).</summary>
        public void NotifyDegraded()
        {
            IsDegraded = true;
            State = VoiceUiState.Degraded;
            RaiseChanged();
        }

        /// <summary>VR10: surface a recovery hint (timeout / TTS degradation) without changing
        /// the projected state; the hint persists until the next round starts.</summary>
        public void ShowHint(string hint)
        {
            HintText = hint ?? string.Empty;
            RaiseChanged();
        }

        private void ClearRound()
        {
            _answerFixed = false;
            AnswerText = string.Empty;
            ClarificationReason = string.Empty;
            ClarificationPrompt = ClarificationDefaultPrompt;
            IsDegraded = false;
            TranscriptText = string.Empty;
            TranscriptIsFinal = false;
            HintText = string.Empty;
        }

        private void RaiseChanged() => Changed?.Invoke();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_controller != null)
            {
                _controller.StateChanged -= OnStateChanged;
                _controller.AnswerReceived -= OnAnswerReceived;
                _controller.MessageRejected -= OnMessageRejected;
                _controller.TranscriptReceived -= OnTranscriptReceived;
                _controller.ClarificationRequired -= OnClarificationRequired;
                _controller.SessionExpired -= OnSessionExpired;
                _controller.TtsDegraded -= OnTtsDegraded;
            }
            Changed = null;
        }
    }
}
