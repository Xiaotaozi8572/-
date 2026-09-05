using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// Switches classroom lesson clips and exposes simple methods for UI buttons.
/// </summary>
[RequireComponent(typeof(VideoPlayer))]
public sealed class ClassroomLessonController : MonoBehaviour
{
    [Header("Lessons")]
    [SerializeField] private VideoClip[] lessonClips;
    [Min(0)] [SerializeField] private int startingLesson;
    [SerializeField] private bool playAfterSwitch = true;
    [SerializeField] private bool loopFromLastToFirst;

    [Header("Optional UI")]
    [Tooltip("The cover/play overlay. It is hidden when a lesson begins playing.")]
    [SerializeField] private GameObject playOverlay;
    [Tooltip("Image component used to display the current lesson cover.")]
    [SerializeField] private Image coverImage;
    [Tooltip("Cover sprites in the same order as Lesson Clips.")]
    [SerializeField] private Sprite[] lessonCovers;
    [Tooltip("One information panel per lesson, in the same order as Lesson Clips.")]
    [SerializeField] private GameObject[] lessonUiPanels;
    [Tooltip("Disabled automatically while the first lesson is selected.")]
    [SerializeField] private Button previousLessonButton;
    [Tooltip("Disabled automatically while the final lesson is selected.")]
    [SerializeField] private Button nextLessonButton;
    [SerializeField] private UnityEvent<int> lessonChanged;

    private VideoPlayer videoPlayer;
    private int currentLesson;

    public int CurrentLesson => currentLesson;
    public int LessonCount => lessonClips == null ? 0 : lessonClips.Length;

    private void Awake()
    {
        videoPlayer = GetComponent<VideoPlayer>();

        if (playOverlay == null)
        {
            Transform overlayTransform = transform.Find("PlayOverlay");
            if (overlayTransform != null)
                playOverlay = overlayTransform.gameObject;
        }

        if (coverImage == null && playOverlay != null)
        {
            Image[] overlayImages = playOverlay.GetComponentsInChildren<Image>(true);
            foreach (Image image in overlayImages)
            {
                if (image.name == "CoverImage")
                {
                    coverImage = image;
                    break;
                }
            }
        }

        if (LessonCount == 0)
            return;

        currentLesson = Mathf.Clamp(startingLesson, 0, LessonCount - 1);
        SetLesson(currentLesson, false);
    }

    public void PlayCurrentLesson()
    {
        if (videoPlayer == null || videoPlayer.clip == null)
            return;

        if (playOverlay != null)
            playOverlay.SetActive(false);

        videoPlayer.Play();
    }

    public void NextLesson()
    {
        if (LessonCount == 0)
            return;

        int next = currentLesson + 1;
        if (next >= LessonCount)
            next = loopFromLastToFirst ? 0 : LessonCount - 1;

        SetLesson(next, playAfterSwitch);
    }

    public void PreviousLesson()
    {
        if (LessonCount == 0)
            return;

        int previous = currentLesson - 1;
        if (previous < 0)
            previous = loopFromLastToFirst ? LessonCount - 1 : 0;

        SetLesson(previous, playAfterSwitch);
    }

    public void SetLesson(int lessonIndex)
    {
        SetLesson(lessonIndex, playAfterSwitch);
    }

    private void SetLesson(int lessonIndex, bool play)
    {
        if (videoPlayer == null || LessonCount == 0)
            return;

        lessonIndex = Mathf.Clamp(lessonIndex, 0, LessonCount - 1);
        VideoClip nextClip = lessonClips[lessonIndex];
        if (nextClip == null)
        {
            Debug.LogWarning($"Lesson {lessonIndex + 1} has no video clip assigned.", this);
            return;
        }

        videoPlayer.Stop();
        currentLesson = lessonIndex;
        videoPlayer.clip = nextClip;
        videoPlayer.time = 0d;

        UpdateLessonVisuals(currentLesson);
        UpdateNavigationButtons();

        if (play)
            PlayCurrentLesson();
        else if (playOverlay != null)
            playOverlay.SetActive(true);

        lessonChanged?.Invoke(currentLesson);
    }

    private void UpdateNavigationButtons()
    {
        if (previousLessonButton != null)
            previousLessonButton.interactable = loopFromLastToFirst || currentLesson > 0;

        if (nextLessonButton != null)
            nextLessonButton.interactable = loopFromLastToFirst || currentLesson < LessonCount - 1;
    }

    private void UpdateLessonVisuals(int lessonIndex)
    {
        if (coverImage != null && lessonCovers != null &&
            lessonIndex >= 0 && lessonIndex < lessonCovers.Length &&
            lessonCovers[lessonIndex] != null)
        {
            coverImage.sprite = lessonCovers[lessonIndex];
            coverImage.preserveAspect = true;
        }

        if (lessonUiPanels == null)
            return;

        for (int i = 0; i < lessonUiPanels.Length; i++)
        {
            if (lessonUiPanels[i] != null)
                lessonUiPanels[i].SetActive(i == lessonIndex);
        }
    }
}
