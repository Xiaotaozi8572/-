using System.Collections;
using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// 场景开始时隐藏指定对象，并在视频真实播放结束后显示它们。
/// 适用于讲解面板、指示线、热点按钮等内容。
/// </summary>
[DisallowMultipleComponent]
public sealed class ShowObjectsAfterVideo : MonoBehaviour
{
    [Header("Video")]
    [SerializeField] private VideoPlayer videoPlayer;

    [Header("Objects To Show")]
    [SerializeField] private GameObject[] objectsToShow;

    [Header("Timing")]
    [Min(0f)]
    [Tooltip("视频结束后再等待多少秒显示UI。通常保持为0。")]
    [SerializeField] private float delayAfterVideo;

    [Header("Fallback")]
    [Tooltip("视频播放失败时仍显示UI，避免用户一直看不到操作面板。")]
    [SerializeField] private bool showOnVideoError = true;

    private Coroutine showRoutine;
    private bool hasShown;

    private void Reset()
    {
        videoPlayer = GetComponent<VideoPlayer>();
    }

    private void Awake()
    {
        if (videoPlayer == null)
            videoPlayer = GetComponent<VideoPlayer>();

        SetObjectsActive(false);
    }

    private void OnEnable()
    {
        if (videoPlayer == null)
            return;

        videoPlayer.loopPointReached += OnVideoFinished;
        videoPlayer.errorReceived += OnVideoError;
    }

    private void OnDisable()
    {
        if (videoPlayer != null)
        {
            videoPlayer.loopPointReached -= OnVideoFinished;
            videoPlayer.errorReceived -= OnVideoError;
        }

        if (showRoutine != null)
        {
            StopCoroutine(showRoutine);
            showRoutine = null;
        }
    }

    private void OnVideoFinished(VideoPlayer player)
    {
        BeginShow();
    }

    private void OnVideoError(VideoPlayer player, string message)
    {
        Debug.LogError($"视频播放失败：{message}", this);

        if (showOnVideoError)
            BeginShow();
    }

    private void BeginShow()
    {
        if (hasShown || showRoutine != null)
            return;

        showRoutine = StartCoroutine(ShowAfterDelay());
    }

    private IEnumerator ShowAfterDelay()
    {
        if (delayAfterVideo > 0f)
            yield return new WaitForSeconds(delayAfterVideo);

        SetObjectsActive(true);
        hasShown = true;
        showRoutine = null;
    }

    private void SetObjectsActive(bool active)
    {
        if (objectsToShow == null)
            return;

        foreach (GameObject target in objectsToShow)
        {
            if (target != null)
                target.SetActive(active);
        }
    }
}
