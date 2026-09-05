using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// 视频播放完毕后，允许用户点击指定 UI 区域进入下一个场景。
/// 将本脚本与 Button 挂在显示视频的 RawImage 上即可。
/// </summary>
[DisallowMultipleComponent]
public sealed class IntroVideoClickToContinue : MonoBehaviour
{
    [Header("Required References")]
    [SerializeField] private VideoPlayer videoPlayer;
    [SerializeField] private Button clickAreaButton;

    [Header("Scene")]
    [SerializeField] private string nextSceneName = "02-机型选择页面共用";

    [Header("Optional")]
    [Tooltip("视频播放完毕后显示的提示，例如“点击任意位置继续”。")]
    [SerializeField] private GameObject continueHint;
    [Tooltip("视频解码失败时是否也允许点击继续，避免用户卡在片头。")]
    [SerializeField] private bool allowContinueOnVideoError = true;

    private bool canContinue;
    private bool isLoading;

    private void Reset()
    {
        videoPlayer = GetComponentInChildren<VideoPlayer>(true);
        clickAreaButton = GetComponent<Button>();
    }

    private void Awake()
    {
        if (videoPlayer == null)
            videoPlayer = GetComponentInChildren<VideoPlayer>(true);

        if (clickAreaButton == null)
            clickAreaButton = GetComponent<Button>();

        SetContinueEnabled(false);

        if (clickAreaButton != null)
            clickAreaButton.onClick.AddListener(TryContinue);
        else
            Debug.LogError("IntroVideoClickToContinue 需要一个 Button 作为点击区域。", this);
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
    }

    private void OnDestroy()
    {
        if (clickAreaButton != null)
            clickAreaButton.onClick.RemoveListener(TryContinue);
    }

    private void OnVideoFinished(VideoPlayer player)
    {
        SetContinueEnabled(true);
    }

    private void OnVideoError(VideoPlayer player, string message)
    {
        Debug.LogError($"片头视频播放失败：{message}", this);

        if (allowContinueOnVideoError)
            SetContinueEnabled(true);
    }

    private void SetContinueEnabled(bool enabled)
    {
        canContinue = enabled;

        if (clickAreaButton != null)
            clickAreaButton.interactable = enabled;

        if (continueHint != null)
            continueHint.SetActive(enabled);
    }

    private void TryContinue()
    {
        if (!canContinue || isLoading)
            return;

        if (string.IsNullOrWhiteSpace(nextSceneName))
        {
            Debug.LogError("没有配置要进入的场景名称。", this);
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded(nextSceneName))
        {
            Debug.LogError($"场景未加入 Build Settings，无法加载：{nextSceneName}", this);
            return;
        }

        isLoading = true;
        if (clickAreaButton != null)
            clickAreaButton.interactable = false;

        SceneManager.LoadSceneAsync(nextSceneName);
    }
}
