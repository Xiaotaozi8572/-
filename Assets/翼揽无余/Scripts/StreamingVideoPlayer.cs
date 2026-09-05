using System.IO;
using UnityEngine;
using UnityEngine.Video;

[RequireComponent(typeof(VideoPlayer))]
public class StreamingVideoPlayer : MonoBehaviour
{
    [SerializeField] private string videoFileName = "AircraftIntro.mp4";
    [SerializeField] private bool playAutomatically = true;

    private VideoPlayer videoPlayer;

    private void Awake()
    {
        videoPlayer = GetComponent<VideoPlayer>();

        string videoPath = Path.Combine(
            Application.streamingAssetsPath,
            "Video",
            videoFileName
        );

        videoPlayer.url = videoPath;
        videoPlayer.prepareCompleted += OnVideoPrepared;
        videoPlayer.Prepare();
    }

    private void OnVideoPrepared(VideoPlayer player)
    {
        if (playAutomatically)
            player.Play();
    }

    public void PlayVideo()
    {
        videoPlayer.Play();
    }

    public void PauseVideo()
    {
        videoPlayer.Pause();
    }

    public void StopVideo()
    {
        videoPlayer.Stop();
    }
}
