// VoiceSceneXrSetup.cs - Editor-only scene wiring for the first PICO 4 device
// session. PicoVoiceIntegrationTest.unity shipped with a bare default
// EventSystem (InputSystemUIInputModule with an EMPTY actions asset -> the
// on-device NullReferenceException spam in InputSystem.onBeforeUpdate) and no
// XR Origin at all, so controller/hand rays could never reach the UGUI panel.
// This setup installs the SAME proven interaction stack the business scenes
// use (Assets/Resources/XR Interaction Hands Setup.prefab: PICO Building Block
// XR Origin + XRI ray/hand interactors + correctly configured UI input module
// + Input Action Manager + XR Interaction Manager), adds
// TrackedDeviceGraphicRaycaster to the voice panel canvas, disables the scene's
// standalone Main Camera (the XR Origin camera takes over rendering), and
// guarantees an active MainCamera-tagged camera exists for the bootstrap's
// canvas normalization. Lives beside AircraftInteractionSetupEditor (global
// editor assembly) because the Voice.Editor asmdef does not reference XRI.
// Idempotent; safe to re-run.

using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit.UI;

public static class VoiceSceneXrSetup
{
    private const string ScenePath =
        @"Assets/翼揽无余/Voice/Scenes/PicoVoiceIntegrationTest.unity";

    private const string XrSetupPrefabPath =
        @"Assets/Resources/XR Interaction Hands Setup.prefab";

    /// <summary>
    /// Batch entry point: wire the scene, then build the device APK in one go.
    /// Invoked via:
    ///   Unity -batchmode -quit -projectPath &lt;proj&gt; -executeMethod VoiceSceneXrSetup.SetupAndBuild
    /// </summary>
    public static void SetupAndBuild()
    {
        if (Setup())
        {
            Yilan.Voice.Editor.VoiceBuild.BuildDevelopmentPico();
        }
    }

    /// <summary>Returns true when the scene is wired (or already was).</summary>
    public static bool Setup()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        bool changed = false;

        // 1. Remove the default scene EventSystem: its InputSystemUIInputModule
        //    has no actions asset (on-device NRE spam) and it has no XR support.
        //    The XR setup prefab instantiates its own correctly configured one.
        var defaultEventSystem = Object.FindObjectOfType<EventSystem>();
        if (defaultEventSystem != null
            && defaultEventSystem.GetComponentInParent<XROrigin>() == null)
        {
            Object.DestroyImmediate(defaultEventSystem.gameObject);
            changed = true;
            Debug.Log("[VoiceSceneXrSetup] removed default EventSystem");
        }

        // 2. Instantiate the business-proven XR interaction setup (idempotent).
        if (Object.FindObjectOfType<XROrigin>() == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(XrSetupPrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[VoiceSceneXrSetup] prefab not found: " + XrSetupPrefabPath);
                return false;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            changed = true;
            Debug.Log("[VoiceSceneXrSetup] instantiated XR Interaction Hands Setup");
        }

        // 3. Disable the scene's standalone Main Camera (NOT the XR Origin's):
        //    double rendering + ambiguous Camera.main otherwise.
        foreach (var cam in Object.FindObjectsOfType<Camera>())
        {
            if (cam.CompareTag("MainCamera")
                && cam.GetComponentInParent<XROrigin>() == null)
            {
                cam.gameObject.SetActive(false);
                changed = true;
                Debug.Log("[VoiceSceneXrSetup] disabled scene standalone Main Camera");
                break;
            }
        }

        // 4. Guarantee an active MainCamera-tagged camera for the bootstrap's
        //    Camera.main canvas normalization (some nested XR camera variants
        //    ship untagged).
        if (Camera.main == null)
        {
            foreach (var cam in Object.FindObjectsOfType<Camera>())
            {
                if (cam.isActiveAndEnabled)
                {
                    cam.tag = "MainCamera";
                    changed = true;
                    Debug.Log("[VoiceSceneXrSetup] tagged XR camera as MainCamera: "
                        + cam.gameObject.name);
                    break;
                }
            }
        }

        // 5. Let XRI ray interactors hit the UGUI voice panel.
        var bootstrap =
            Object.FindObjectOfType<Yilan.Voice.Runtime.Bootstrap.VoiceClientBootstrap>();
        var canvas = bootstrap != null
            ? bootstrap.GetComponentInChildren<Canvas>(true)
            : Object.FindObjectOfType<Canvas>();
        if (canvas != null
            && canvas.GetComponent<TrackedDeviceGraphicRaycaster>() == null)
        {
            canvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
            changed = true;
            Debug.Log("[VoiceSceneXrSetup] added TrackedDeviceGraphicRaycaster");
        }

        if (changed)
        {
            EditorSceneManager.SaveScene(scene);
        }
        Debug.Log("[VoiceSceneXrSetup] done, changed=" + changed);
        return true;
    }
}
