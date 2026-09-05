using System.Collections;
using UnityEngine;

/// <summary>
/// Plays cockpit narration and shows the matching operation hint panel.
/// Only one operation narration and one hint panel are active at a time.
/// </summary>
public sealed class CockpitOperationFeedback : MonoBehaviour
{
    public enum StickDirection
    {
        Neutral,
        Forward,
        Backward,
        Left,
        Right
    }

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip intro15;
    [SerializeField] private AudioClip leftPedal16;
    [SerializeField] private AudioClip rightPedal17;
    [SerializeField] private AudioClip stickForward18;
    [SerializeField] private AudioClip stickBackward19;
    [SerializeField] private AudioClip stickLeft20;
    [SerializeField] private AudioClip stickRight21;

    [Header("Matching UI panels (optional)")]
    [SerializeField] private GameObject introPanel15;
    [SerializeField] private GameObject leftPedalPanel16;
    [SerializeField] private GameObject rightPedalPanel17;
    [SerializeField] private GameObject stickForwardPanel18;
    [SerializeField] private GameObject stickBackwardPanel19;
    [SerializeField] private GameObject stickLeftPanel20;
    [SerializeField] private GameObject stickRightPanel21;

    [Header("Behaviour")]
    [SerializeField] private bool playIntroOnStart = true;
    [Min(0f)] [SerializeField] private float introDelay = 0.3f;
    [Min(0.1f)] [SerializeField] private float fallbackPanelDuration = 3f;

    private GameObject currentPanel;
    private Coroutine feedbackRoutine;

    private void Awake()
    {
        if (audioSource == null)
            audioSource = GetComponent<AudioSource>();

        HideAllPanels();
    }

    private void Start()
    {
        if (playIntroOnStart)
            feedbackRoutine = StartCoroutine(PlayIntroAfterDelay());
    }

    public void PlayLeftPedal() => PlayFeedback(leftPedal16, leftPedalPanel16);
    public void PlayRightPedal() => PlayFeedback(rightPedal17, rightPedalPanel17);

    public void PlayStickDirection(StickDirection direction)
    {
        switch (direction)
        {
            case StickDirection.Forward:
                PlayFeedback(stickForward18, stickForwardPanel18);
                break;
            case StickDirection.Backward:
                PlayFeedback(stickBackward19, stickBackwardPanel19);
                break;
            case StickDirection.Left:
                PlayFeedback(stickLeft20, stickLeftPanel20);
                break;
            case StickDirection.Right:
                PlayFeedback(stickRight21, stickRightPanel21);
                break;
        }
    }

    private IEnumerator PlayIntroAfterDelay()
    {
        if (introDelay > 0f)
            yield return new WaitForSeconds(introDelay);

        PlayFeedback(intro15, introPanel15);
    }

    private void PlayFeedback(AudioClip clip, GameObject panel)
    {
        if (feedbackRoutine != null)
            StopCoroutine(feedbackRoutine);

        HideCurrentPanel();

        if (audioSource != null)
        {
            audioSource.Stop();
            audioSource.clip = clip;
            if (clip != null)
                audioSource.Play();
        }

        currentPanel = panel;
        if (currentPanel != null)
            currentPanel.SetActive(true);

        float duration = clip != null ? clip.length : fallbackPanelDuration;
        feedbackRoutine = StartCoroutine(HidePanelAfter(duration));
    }

    private IEnumerator HidePanelAfter(float duration)
    {
        yield return new WaitForSeconds(Mathf.Max(0.1f, duration));
        HideCurrentPanel();
        feedbackRoutine = null;
    }

    private void HideCurrentPanel()
    {
        if (currentPanel != null)
            currentPanel.SetActive(false);
        currentPanel = null;
    }

    private void HideAllPanels()
    {
        SetInactive(introPanel15);
        SetInactive(leftPedalPanel16);
        SetInactive(rightPedalPanel17);
        SetInactive(stickForwardPanel18);
        SetInactive(stickBackwardPanel19);
        SetInactive(stickLeftPanel20);
        SetInactive(stickRightPanel21);
    }

    private static void SetInactive(GameObject panel)
    {
        if (panel != null)
            panel.SetActive(false);
    }
}
