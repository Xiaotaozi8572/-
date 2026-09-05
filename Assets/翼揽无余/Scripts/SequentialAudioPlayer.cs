using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 按数组顺序播放多段音频；每段结束后等待指定时间再播放下一段。
/// 可选择在一次应用运行期间只自动播放一次，场景返回时不再重复。
/// </summary>
public sealed class SequentialAudioPlayer : MonoBehaviour
{
    private static readonly HashSet<string> PlayedSessionKeys = new HashSet<string>();

    [Header("音频")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip[] clips;

    [Header("播放设置")]
    [SerializeField] private bool playOnStart = true;
    [Min(0f)] [SerializeField] private float gapBetweenClips = 2f;
    [SerializeField] private bool loopSequence;

    [Header("场景返回时是否重复")]
    [Tooltip("勾选后，本次打开应用期间只播放一次。进入其他场景再返回时不会重复，重新启动应用后可以再次播放。")]
    [SerializeField] private bool playOnlyOncePerSession;
    [Tooltip("可选。不同机型填写不同标识，例如 C919_CoreIntro。留空时会根据场景和物体路径自动生成。")]
    [SerializeField] private string sessionPlaybackKey;

    private Coroutine playCoroutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetSessionState()
    {
        // 每次真正启动应用（以及编辑器重新进入播放模式）都允许重新播放。
        PlayedSessionKeys.Clear();
    }

    private void Start()
    {
        if (playOnStart)
            PlaySequence();
    }

    /// <summary>
    /// 按当前“一次会话只播放一次”的设置播放。
    /// </summary>
    public void PlaySequence()
    {
        string key = GetSessionKey();
        if (playOnlyOncePerSession && PlayedSessionKeys.Contains(key))
            return;

        if (playOnlyOncePerSession)
            PlayedSessionKeys.Add(key);

        StartSequenceInternal();
    }

    /// <summary>
    /// 忽略“一次会话只播放一次”，强制从第一段重新播放。
    /// 可用于调试按钮或用户主动点击的“重听”按钮。
    /// </summary>
    public void ReplaySequence()
    {
        StartSequenceInternal();
    }

    /// <summary>停止当前声音和播放队列。</summary>
    public void StopSequence()
    {
        if (playCoroutine != null)
        {
            StopCoroutine(playCoroutine);
            playCoroutine = null;
        }

        if (audioSource != null)
            audioSource.Stop();
    }

    private void StartSequenceInternal()
    {
        StopSequence();
        playCoroutine = StartCoroutine(PlayClipsInOrder());
    }

    private IEnumerator PlayClipsInOrder()
    {
        if (audioSource == null)
        {
            Debug.LogError("Sequential Audio Player 没有连接 Audio Source。", this);
            playCoroutine = null;
            yield break;
        }

        if (clips == null || clips.Length == 0)
        {
            Debug.LogWarning("Sequential Audio Player 没有添加音频。", this);
            playCoroutine = null;
            yield break;
        }

        do
        {
            for (int i = 0; i < clips.Length; i++)
            {
                AudioClip clip = clips[i];
                if (clip == null)
                    continue;

                audioSource.clip = clip;
                audioSource.Play();

                yield return new WaitWhile(() => audioSource != null && audioSource.isPlaying);

                bool hasAnotherClip = i < clips.Length - 1;
                if ((hasAnotherClip || loopSequence) && gapBetweenClips > 0f)
                    yield return new WaitForSeconds(gapBetweenClips);
            }
        }
        while (loopSequence);

        playCoroutine = null;
    }

    private string GetSessionKey()
    {
        if (!string.IsNullOrWhiteSpace(sessionPlaybackKey))
            return sessionPlaybackKey.Trim();

        Scene scene = gameObject.scene;
        string sceneId = !string.IsNullOrEmpty(scene.path) ? scene.path : scene.name;
        return sceneId + "/" + GetHierarchyPath(transform);
    }

    private static string GetHierarchyPath(Transform target)
    {
        string path = target.name;
        while (target.parent != null)
        {
            target = target.parent;
            path = target.name + "/" + path;
        }

        return path;
    }

    private void OnDisable()
    {
        StopSequence();
    }
}
