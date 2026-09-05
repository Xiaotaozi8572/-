using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// 片头图片/视频混合轮播。
/// 播放列表可自由排列为：图片 → 视频 → 图片 → 视频……
/// 图片可等待旁白结束，视频会等待自身播放结束，最后显示进入按钮。
/// </summary>
public sealed class IntroSlideshowController : MonoBehaviour
{
    public enum MediaType
    {
        Image,
        Video
    }

    [System.Serializable]
    public sealed class MediaItem
    {
        [Tooltip("当前元素播放图片还是视频。")]
        public MediaType mediaType = MediaType.Image;

        [Header("内容（按类型填写其中一个）")]
        public Sprite image;
        public VideoClip video;

        [Header("可选的独立旁白")]
        [Tooltip("图片旁白可在这里填写。若视频已经自带声音，视频元素的这里请留空。")]
        public AudioClip narration;

        [Header("节奏")]
        [Min(0f)]
        [Tooltip("最短显示时间。图片建议填写原来的停留时间；视频会至少完整播放一次。")]
        public float minimumDisplayDuration = 3f;

        [Min(0f)]
        [Tooltip("当前内容结束、淡出前额外停留的时间。")]
        public float afterContentDelay = 0.25f;

        [Tooltip("图片或独立旁白尚未播放完时，不切换到下一项。")]
        public bool waitForNarration = true;
    }

    [Header("显示区域")]
    [SerializeField] private Image imageScreen;
    [SerializeField] private RawImage videoScreen;
    [Tooltip("同时控制图片和视频淡入淡出的 Canvas Group，建议挂在两者共同父物体上。")]
    [SerializeField] private CanvasGroup mediaCanvasGroup;

    [Header("视频播放")]
    [SerializeField] private VideoPlayer videoPlayer;
    [SerializeField] private RenderTexture targetTexture;
    [Tooltip("播放视频内嵌声音。空间混合会被自动设置为2D。")]
    [SerializeField] private AudioSource videoAudioSource;

    [Header("图片/外部旁白")]
    [SerializeField] private AudioSource narrationSource;

    [Header("混合播放列表（按实际播放顺序排列）")]
    [SerializeField] private MediaItem[] playlist;

    [Header("统一转场")]
    [Min(0f)] [SerializeField] private float fadeInDuration = 0.8f;
    [Min(0f)] [SerializeField] private float fadeOutDuration = 0.8f;
    [Min(0f)] [SerializeField] private float enterButtonDelay = 0.5f;

    [Header("点击进入")]
    [SerializeField] private GameObject enterButton;
    [Tooltip("下一个场景名称；该场景必须加入构建设置。")]
    [SerializeField] private string nextSceneName;

    private bool currentVideoFinished;
    private bool currentVideoFailed;

    private void Awake()
    {
        if (videoPlayer == null)
            videoPlayer = GetComponent<VideoPlayer>();

        if (enterButton != null)
            enterButton.SetActive(false);

        SetMediaVisibility(false, false);

        if (mediaCanvasGroup != null)
        {
            mediaCanvasGroup.alpha = 0f;
            mediaCanvasGroup.interactable = false;
            mediaCanvasGroup.blocksRaycasts = false;
        }
    }

    private void OnEnable()
    {
        if (videoPlayer == null)
            return;

        videoPlayer.loopPointReached += HandleVideoFinished;
        videoPlayer.errorReceived += HandleVideoError;
    }

    private IEnumerator Start()
    {
        if (!ValidateReferences())
            yield break;

        ConfigureVideoPlayer();

        for (int i = 0; i < playlist.Length; i++)
        {
            MediaItem item = playlist[i];
            if (item == null || !HasValidContent(item))
                continue;

            bool hasAnotherValidItem = HasValidItemAfter(i);
            yield return PlayItem(item, hasAnotherValidItem);
        }

        if (enterButtonDelay > 0f)
            yield return new WaitForSeconds(enterButtonDelay);

        if (enterButton != null)
            enterButton.SetActive(true);
    }

    private void OnDisable()
    {
        if (videoPlayer != null)
        {
            videoPlayer.loopPointReached -= HandleVideoFinished;
            videoPlayer.errorReceived -= HandleVideoError;
            videoPlayer.Stop();
        }

        if (narrationSource != null)
            narrationSource.Stop();
    }

    private bool ValidateReferences()
    {
        if (imageScreen == null || videoScreen == null || mediaCanvasGroup == null)
        {
            Debug.LogError("片头混合轮播未连接 Image Screen、Video Screen 或 Media Canvas Group。", this);
            return false;
        }

        if (videoPlayer == null)
        {
            Debug.LogError("片头混合轮播未连接 Video Player。", this);
            return false;
        }

        if (playlist == null || playlist.Length == 0)
        {
            Debug.LogWarning("片头混合播放列表还没有添加内容。", this);
            return false;
        }

        return true;
    }

    private void ConfigureVideoPlayer()
    {
        videoPlayer.playOnAwake = false;
        videoPlayer.isLooping = false;
        videoPlayer.waitForFirstFrame = true;
        videoPlayer.skipOnDrop = true;
        videoPlayer.source = VideoSource.VideoClip;

        if (targetTexture == null)
            targetTexture = videoPlayer.targetTexture;

        if (targetTexture != null)
        {
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoPlayer.targetTexture = targetTexture;
            videoScreen.texture = targetTexture;
        }

        if (videoAudioSource != null)
        {
            videoAudioSource.playOnAwake = false;
            videoAudioSource.loop = false;
            videoAudioSource.spatialBlend = 0f;

            videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            videoPlayer.controlledAudioTrackCount = 1;
            videoPlayer.EnableAudioTrack(0, true);
            videoPlayer.SetTargetAudioSource(0, videoAudioSource);
        }
        else
        {
            videoPlayer.audioOutputMode = VideoAudioOutputMode.Direct;
        }
    }

    private IEnumerator PlayItem(MediaItem item, bool fadeOutAtEnd)
    {
        StopCurrentAudioAndVideo();
        mediaCanvasGroup.alpha = 0f;

        if (item.mediaType == MediaType.Image)
        {
            imageScreen.sprite = item.image;
            imageScreen.preserveAspect = true;
            SetMediaVisibility(true, false);
        }
        else
        {
            SetMediaVisibility(false, true);
            yield return PrepareVideo(item.video);

            if (currentVideoFailed)
                yield break;

            currentVideoFinished = false;
            videoPlayer.Play();
        }

        PlayNarration(item.narration);
        float itemStartTime = Time.time;

        yield return Fade(0f, 1f, fadeInDuration);

        if (item.mediaType == MediaType.Image)
        {
            yield return WaitForImageItem(item, itemStartTime);
        }
        else
        {
            yield return WaitForVideoItem(item, itemStartTime);
        }

        if (item.afterContentDelay > 0f)
            yield return new WaitForSeconds(item.afterContentDelay);

        if (fadeOutAtEnd)
        {
            yield return Fade(1f, 0f, fadeOutDuration);
            SetMediaVisibility(false, false);
        }
    }

    private IEnumerator PrepareVideo(VideoClip clip)
    {
        currentVideoFailed = false;
        currentVideoFinished = false;
        videoPlayer.Stop();
        videoPlayer.clip = clip;
        videoPlayer.Prepare();

        const float prepareTimeout = 15f;
        float elapsed = 0f;
        while (!videoPlayer.isPrepared && !currentVideoFailed && elapsed < prepareTimeout)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!videoPlayer.isPrepared && !currentVideoFailed)
        {
            currentVideoFailed = true;
            Debug.LogError($"片头视频准备超时：{clip.name}", this);
        }
    }

    private IEnumerator WaitForImageItem(MediaItem item, float itemStartTime)
    {
        while (true)
        {
            bool minimumTimeReached = Time.time - itemStartTime >= item.minimumDisplayDuration;
            bool narrationDone = !item.waitForNarration ||
                                 item.narration == null ||
                                 narrationSource == null ||
                                 !narrationSource.isPlaying;

            if (minimumTimeReached && narrationDone)
                yield break;

            yield return null;
        }
    }

    private IEnumerator WaitForVideoItem(MediaItem item, float itemStartTime)
    {
        while (true)
        {
            bool minimumTimeReached = Time.time - itemStartTime >= item.minimumDisplayDuration;
            bool narrationDone = !item.waitForNarration ||
                                 item.narration == null ||
                                 narrationSource == null ||
                                 !narrationSource.isPlaying;

            if (minimumTimeReached && narrationDone && currentVideoFinished)
                yield break;

            if (currentVideoFailed)
                yield break;

            yield return null;
        }
    }

    private void PlayNarration(AudioClip clip)
    {
        if (narrationSource == null)
            return;

        narrationSource.Stop();
        narrationSource.clip = clip;

        if (clip != null)
            narrationSource.Play();
    }

    private void StopCurrentAudioAndVideo()
    {
        if (videoPlayer != null)
            videoPlayer.Stop();
        if (narrationSource != null)
            narrationSource.Stop();
    }

    private IEnumerator Fade(float from, float to, float duration)
    {
        if (duration <= 0f)
        {
            mediaCanvasGroup.alpha = to;
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            mediaCanvasGroup.alpha = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }

        mediaCanvasGroup.alpha = to;
    }

    private void SetMediaVisibility(bool showImage, bool showVideo)
    {
        if (imageScreen != null)
            imageScreen.gameObject.SetActive(showImage);
        if (videoScreen != null)
            videoScreen.gameObject.SetActive(showVideo);
    }

    private static bool HasValidContent(MediaItem item)
    {
        return item.mediaType == MediaType.Image ? item.image != null : item.video != null;
    }

    private bool HasValidItemAfter(int currentIndex)
    {
        for (int i = currentIndex + 1; i < playlist.Length; i++)
        {
            if (playlist[i] != null && HasValidContent(playlist[i]))
                return true;
        }

        return false;
    }

    private void HandleVideoFinished(VideoPlayer source)
    {
        currentVideoFinished = true;
    }

    private void HandleVideoError(VideoPlayer source, string message)
    {
        currentVideoFailed = true;
        Debug.LogError($"片头视频播放失败：{message}", this);
    }

    /// <summary>
    /// 在“点击进入”按钮的 On Click 事件中连接此方法。
    /// </summary>
    public void EnterNextScene()
    {
        if (string.IsNullOrWhiteSpace(nextSceneName))
        {
            Debug.LogWarning("片头的下一个场景名称尚未填写。", this);
            return;
        }

        SceneManager.LoadScene(nextSceneName);
    }
}
