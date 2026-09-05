using System.Collections;
using UnityEngine;
using Unity.XR.PXR;

/// <summary>
/// Re-applies PICO video see-through after the XR session has had time to start.
/// Some device/runtime combinations reset the passthrough state while entering
/// the READY/FOCUSED session states, which can leave an otherwise valid MR scene black.
/// </summary>
public sealed class PicoPassthroughStartup : MonoBehaviour
{
    [SerializeField, Min(0f)] private float firstRetryDelay = 0.5f;
    [SerializeField, Min(0f)] private float secondRetryDelay = 1.5f;

    private IEnumerator Start()
    {
        PrepareMainCamera();
        EnablePassthrough();

        yield return new WaitForSecondsRealtime(firstRetryDelay);
        PrepareMainCamera();
        EnablePassthrough();

        yield return new WaitForSecondsRealtime(secondRetryDelay);
        PrepareMainCamera();
        EnablePassthrough();
    }

    private static void PrepareMainCamera()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("[PicoPassthroughStartup] No enabled camera tagged MainCamera was found.");
            return;
        }

        mainCamera.enabled = true;
        mainCamera.clearFlags = CameraClearFlags.SolidColor;
        mainCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
    }

    private static void EnablePassthrough()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        PXR_Manager.EnableVideoSeeThrough = true;
#endif
        Debug.Log("[PicoPassthroughStartup] Video see-through enable requested.");
    }
}
