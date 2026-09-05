using System;
using UnityEngine;
using UnityEngine.UI;

namespace Yilan.Voice.Runtime.UI
{
    /// <summary>
    /// Display layer for <see cref="VoiceViewModel"/>. This is a MonoBehaviour that lives on the
    /// integration scene's VoiceRoot / the PicoVoiceClient prefab and re-renders whatever
    /// ViewModel it is bound to.
    ///
    /// DESIGN (VR4-T10): the ViewModel is the pure projection layer; the Panel only observes it
    /// and pushes values into optional <see cref="UnityEngine.UI.Text"/> components. All UI Text
    /// references are optional (null-safe) so the prefab is valid with no missing scripts and no
    /// null references even before a Canvas/Text is wired in the editor. The Panel never mutates
    /// controller state and never opens a network connection itself.
    ///
    /// INPUT (P0-2 fix, VRR3 design): optional record/stop/cancel <see cref="Button"/> references.
    /// Clicks only raise intent events (<see cref="RecordRequested"/> etc.); the composition root
    /// (bootstrap) subscribes and drives the runtime. The Panel never touches socket/controller.
    ///
    /// BINDING: call <see cref="Bind(VoiceViewModel)"/> with a ViewModel owned by the caller
    /// (e.g. created by a bootstrap that also owns the controller + socket). When bound, the
    /// panel reflects its projected state. See design-notes.md for the precise editor wiring of
    /// Canvas/Text and the bootstrap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VoicePanel : MonoBehaviour
    {
        [Header("Optional text targets (null-safe)")]
        [Tooltip("State line, e.g. '状态: 等待回答'.")]
        [SerializeField] private Text stateText;

        [Tooltip("Answer content line (gold carrier answer.content).")]
        [SerializeField] private Text answerText;

        [Tooltip("Clarification prompt line.")]
        [SerializeField] private Text clarificationText;

        [Tooltip("Transcript subtitle line (asr.partial streaming / asr.final final, P1-2).")]
        [SerializeField] private Text transcriptText;

        [Header("Optional answer scrolling (null-safe)")]
        [Tooltip("Fixed viewport used to clip and vertically scroll long answers.")]
        [SerializeField] private ScrollRect answerScrollRect;

        [Header("Optional input buttons (null-safe, P0-2)")]
        [Tooltip("Hold-to-talk entry: starts capture (BeginCaptureAsync) via the bootstrap.")]
        [SerializeField] private Button recordButton;

        [Tooltip("Ends capture and submits the utterance (EndCaptureAsync) via the bootstrap.")]
        [SerializeField] private Button stopButton;

        [Tooltip("Barge-in cancel for the active turn (CancelAsync) via the bootstrap.")]
        [SerializeField] private Button cancelButton;

        [Header("Optional VR10 recovery UI (null-safe)")]
        [Tooltip("Recovery hint line, e.g. '会话超时，点击重新提问' (SessionExpired) or '语音播报不可用' (TtsDegraded).")]
        [SerializeField] private Text hintText;

        [Tooltip("VR10: shown only while the ViewModel is Faulted; wired to controller Reset() by the bootstrap. Leave empty to keep the old behaviour.")]
        [SerializeField] private Button resetButton;

        [Header("Optional record button icons (state-driven)")]
        [Tooltip("Image used to display the record button icon. If omitted, the record button's own Image is used.")]
        [SerializeField] private Image recordButtonIcon;

        [Tooltip("Icon displayed while the voice client is idle, ready, connecting, reconnecting or faulted.")]
        [SerializeField] private Sprite readyIcon;

        [Tooltip("Icon displayed while recording or handling an active question.")]
        [SerializeField] private Sprite activeIcon;

        private VoiceViewModel _vm;
        private string _lastRenderedAnswer = string.Empty;

        /// <summary>Read-only access to the bound ViewModel (null until bound).</summary>
        public VoiceViewModel ViewModel => _vm;

        /// <summary>Raised when the record button is clicked (intent only; no direct capture start).</summary>
        public event Action RecordRequested;

        /// <summary>Raised when the stop button is clicked (intent only; no direct capture stop).</summary>
        public event Action StopRequested;

        /// <summary>Raised when the cancel button is clicked (intent only; no direct turn cancel).</summary>
        public event Action CancelRequested;

        /// <summary>VR10: raised when the reset button is clicked (Faulted -> Idle via the bootstrap).</summary>
        public event Action ResetRequested;

        private void OnEnable()
        {
            Hook(recordButton, RaiseRecordRequested);
            Hook(stopButton, RaiseStopRequested);
            Hook(cancelButton, RaiseCancelRequested);
            Hook(resetButton, RaiseResetRequested);
        }

        private void OnDisable()
        {
            Unhook(recordButton, RaiseRecordRequested);
            Unhook(stopButton, RaiseStopRequested);
            Unhook(cancelButton, RaiseCancelRequested);
            Unhook(resetButton, RaiseResetRequested);
        }

        /// <summary>
        /// Bind a ViewModel and start reflecting its projected state. Re-binding replaces the
        /// previous subscription. Passing null unbinds and clears the visible state.
        /// </summary>
        public void Bind(VoiceViewModel viewModel)
        {
            if (ReferenceEquals(_vm, viewModel)) return;
            if (_vm != null) _vm.Changed -= OnViewModelChanged;
            _vm = viewModel;
            if (_vm != null) _vm.Changed += OnViewModelChanged;
            OnViewModelChanged();
        }

        private void OnDestroy()
        {
            if (_vm != null)
            {
                _vm.Changed -= OnViewModelChanged;
                _vm = null;
            }
        }

        private static void Hook(Button button, Action handler)
        {
            if (button == null) return;
            button.onClick.AddListener(new UnityEngine.Events.UnityAction(handler));
        }

        private static void Unhook(Button button, Action handler)
        {
            if (button == null) return;
            button.onClick.RemoveListener(new UnityEngine.Events.UnityAction(handler));
        }

        private void RaiseRecordRequested() => RecordRequested?.Invoke();
        private void RaiseStopRequested() => StopRequested?.Invoke();
        private void RaiseCancelRequested() => CancelRequested?.Invoke();
        private void RaiseResetRequested() => ResetRequested?.Invoke();

        private void OnViewModelChanged()
        {
            if (_vm == null)
            {
                ClearTexts();
                if (resetButton != null) resetButton.gameObject.SetActive(false);
                UpdateRecordButtonIcon(VoiceUiState.Idle);
                return;
            }

            SetText(stateText, "状态: " + _vm.State);
            string renderedAnswer = string.IsNullOrEmpty(_vm.AnswerText) ? string.Empty : _vm.AnswerText;
            SetText(answerText, renderedAnswer);
            RefreshAnswerScroll(renderedAnswer);
            SetText(clarificationText, _vm.State == VoiceUiState.Clarification ? _vm.ClarificationPrompt : string.Empty);
            SetText(transcriptText, string.IsNullOrEmpty(_vm.TranscriptText)
                ? string.Empty
                : (_vm.TranscriptIsFinal ? "你: " : "… ") + _vm.TranscriptText);
            SetText(hintText, string.IsNullOrEmpty(_vm.HintText) ? string.Empty : _vm.HintText);
            // VR10: the reset exit only exists in Faulted; hide the wired button otherwise.
            if (resetButton != null) resetButton.gameObject.SetActive(_vm.ResetVisible);
            UpdateRecordButtonIcon(_vm.State);
        }

        private void UpdateRecordButtonIcon(VoiceUiState state)
        {
            Image target = recordButtonIcon != null
                ? recordButtonIcon
                : recordButton != null ? recordButton.image : null;

            if (target == null) return;

            bool isActive = state == VoiceUiState.Capturing
                || state == VoiceUiState.AwaitingAnswer
                || state == VoiceUiState.AnswerDisplayed
                || state == VoiceUiState.Clarification
                || state == VoiceUiState.Playing
                || state == VoiceUiState.Cancelling;

            Sprite targetSprite = isActive ? activeIcon : readyIcon;
            if (targetSprite != null)
                target.sprite = targetSprite;
        }

        private void RefreshAnswerScroll(string renderedAnswer)
        {
            if (answerText == null) return;

            if (answerScrollRect == null)
                answerScrollRect = answerText.GetComponentInParent<ScrollRect>(true);

            if (answerScrollRect == null || answerScrollRect.content == null) return;

            RectTransform content = answerScrollRect.content;
            RectTransform textRect = answerText.rectTransform;
            RectTransform viewport = answerScrollRect.viewport;

            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, content.sizeDelta.y);

            textRect.anchorMin = new Vector2(0f, 1f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.pivot = new Vector2(0.5f, 1f);
            textRect.anchoredPosition = Vector2.zero;
            textRect.sizeDelta = new Vector2(0f, textRect.sizeDelta.y);

            Canvas.ForceUpdateCanvases();
            float viewportHeight = viewport != null ? viewport.rect.height : 0f;
            float requiredHeight = Mathf.Max(viewportHeight, answerText.preferredHeight);
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);
            textRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, requiredHeight);
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            if (!string.Equals(_lastRenderedAnswer, renderedAnswer, StringComparison.Ordinal))
            {
                _lastRenderedAnswer = renderedAnswer;
                answerScrollRect.StopMovement();
                answerScrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private static void SetText(Text t, string value)
        {
            if (t != null) t.text = value ?? string.Empty;
        }

        private void ClearTexts()
        {
            SetText(stateText, string.Empty);
            SetText(answerText, string.Empty);
            SetText(clarificationText, string.Empty);
            SetText(transcriptText, string.Empty);
            SetText(hintText, string.Empty);
        }
    }
}
