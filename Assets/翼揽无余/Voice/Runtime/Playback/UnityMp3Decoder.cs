// UnityMp3Decoder.cs
// VR4-T11: MPEG decoding wrapper around Unity's built-in UnityWebRequestMultimedia.GetAudioClip
// (AudioType.MPEG). No third-party codec package is introduced and the decoding is deliberately
// abstracted behind ITtsDecoder so EditMode/PlayMode tests inject a fake and never touch real
// media or real UnityWebRequest decoding. Only LOCAL file:// URLs to the app's own temporary MP3
// files are decoded — no network resources are ever opened.
using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Yilan.Voice.Runtime.Playback
{
    /// <summary>Outcome of a decode attempt; never carries payload bytes, only a short classification.</summary>
    public sealed class TtsDecodeResult
    {
        public bool Success;
        /// <summary>Short stable classification (e.g. "decode_fail"); deliberately no payload content.</summary>
        public string Error;
        /// <summary>Decoded clip on success; discarded by the queue shortly after (file is already removed).</summary>
        public AudioClip Clip;

        public static TtsDecodeResult Ok(AudioClip clip) => new TtsDecodeResult { Success = true, Clip = clip };
        public static TtsDecodeResult Fail(string error) => new TtsDecodeResult { Success = false, Error = error };
    }

    /// <summary>Abstract MPEG decoder so the queue is testable with a fake in EditMode/PlayMode.</summary>
    public interface ITtsDecoder
    {
        /// <summary>Decode the local MP3 file at <paramref name="filePath"/> and return the result.</summary>
        Task<TtsDecodeResult> DecodeAsync(string filePath);
    }

    /// <summary>
    /// Real decoder backed by UnityWebRequestMultimedia.GetAudioClip over a local file:// URL.
    /// Only used in a real PlayMode / device context; unit tests substitute a fake ITtsDecoder.
    /// </summary>
    public sealed class UnityMp3Decoder : ITtsDecoder
    {
        /// <summary>Build a local file:// absolute URI for an on-disk path (no network access).</summary>
        public static string LocalFileUri(string filePath)
        {
            try { return new Uri(filePath).AbsoluteUri; }
            catch { return "file://" + filePath.Replace('\\', '/'); }
        }

        public async Task<TtsDecodeResult> DecodeAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return TtsDecodeResult.Fail("missing_path");

            UnityWebRequest request = null;
            try
            {
                request = UnityWebRequestMultimedia.GetAudioClip(LocalFileUri(filePath), AudioType.MPEG);
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                AsyncOperation op = request.SendWebRequest();
                op.completed += _ => tcs.TrySetResult(true);
                await tcs.Task;

                var handler = request.downloadHandler as DownloadHandlerAudioClip;
                if (request.result != UnityWebRequest.Result.Success || handler == null || handler.audioClip == null)
                {
                    // MPEG decode failed. Per spec we DO NOT fall back to MediaPlayer and DO NOT add a
                    // codec library; we record the failure (待确认 for the device) and stop the item while
                    // the already-displayed answer text stays (text downgrade). No raw bytes are logged.
                    return TtsDecodeResult.Fail("decode_fail");
                }

                AudioClip clip = handler.audioClip;
                return TtsDecodeResult.Ok(clip);
            }
            catch
            {
                return TtsDecodeResult.Fail("decode_exception");
            }
            finally
            {
                if (request != null)
                {
                    try { request.Dispose(); } catch { }
                }
            }
        }
    }
}
