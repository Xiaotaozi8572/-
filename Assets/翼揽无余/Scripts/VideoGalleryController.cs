using UnityEngine;
using UnityEngine.Video;

/// <summary>
/// Controls a gallery of video thumbnails using one shared VideoPlayer.
/// </summary>
[RequireComponent(typeof(VideoPlayer))]
public sealed class VideoGalleryController : MonoBehaviour
{
    [Header("Pages")]
    [Tooltip("The page containing all thumbnail buttons.")]
    [SerializeField] private GameObject galleryPage;
    [Tooltip("The full video page containing the RawImage and close button.")]
    [SerializeField] private GameObject videoPage;

    [Header("Videos")]
    [SerializeField] private VideoClip[] videoClips;
    [Tooltip("Return to the gallery automatically when a video finishes.")]
    [SerializeField] private bool returnWhenVideoEnds;

    private VideoPlayer videoPlayer;

    private void Awake()
    {
        videoPlayer = GetComponent<VideoPlayer>();
        videoPlayer.playOnAwake = false;
        videoPlayer.loopPointReached += OnVideoFinished;

        ShowGallery();
    }

    private void OnDestroy()
    {
        if (videoPlayer != null)
            videoPlayer.loopPointReached -= OnVideoFinished;
    }

    public void PlayVideo(int videoIndex)
    {
        if (videoClips == null || videoIndex < 0 || videoIndex >= videoClips.Length)
        {
            Debug.LogWarning($"Video index {videoIndex} is not configured.", this);
            return;
        }

        VideoClip clip = videoClips[videoIndex];
        if (clip == null)
        {
            Debug.LogWarning($"Video {videoIndex + 1} has no clip assigned.", this);
            return;
        }

        videoPlayer.Stop();
        videoPlayer.clip = clip;
        videoPlayer.time = 0d;

        if (galleryPage != null)
            galleryPage.SetActive(false);
        if (videoPage != null)
            videoPage.SetActive(true);

        videoPlayer.Play();
    }

    public void CloseVideo()
    {
        if (videoPlayer != null)
        {
            videoPlayer.Stop();
            videoPlayer.time = 0d;
        }

        ShowGallery();
    }

    private void ShowGallery()
    {
        if (videoPage != null)
            videoPage.SetActive(false);
        if (galleryPage != null)
            galleryPage.SetActive(true);
    }

    private void OnVideoFinished(VideoPlayer player)
    {
        if (returnWhenVideoEnds)
            CloseVideo();
    }
}
