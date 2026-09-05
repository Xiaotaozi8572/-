using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

/// <summary>
/// Ensures shared XRI input-action assets are enabled after a scene switch.
/// This prevents an outgoing scene's InputActionManager from disabling the
/// same asset after the incoming scene has already enabled it.
/// </summary>
public sealed class XRInputActionsSceneReloadGuard : MonoBehaviour
{
    private static XRInputActionsSceneReloadGuard instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateInstance()
    {
        if (instance != null)
            return;

        var guardObject = new GameObject(nameof(XRInputActionsSceneReloadGuard));
        instance = guardObject.AddComponent<XRInputActionsSceneReloadGuard>();
        DontDestroyOnLoad(guardObject);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        StartCoroutine(EnableActionsAfterSceneInitialization());
    }

    private static IEnumerator EnableActionsAfterSceneInitialization()
    {
        // Wait until the previous scene has completed OnDisable/OnDestroy.
        yield return null;

        InputActionManager[] managers = FindObjectsOfType<InputActionManager>(true);
        for (int i = 0; i < managers.Length; i++)
        {
            InputActionManager manager = managers[i];
            if (manager == null || !manager.isActiveAndEnabled || manager.actionAssets == null)
                continue;

            for (int j = 0; j < manager.actionAssets.Count; j++)
            {
                InputActionAsset actionAsset = manager.actionAssets[j];
                if (actionAsset != null)
                    actionAsset.Enable();
            }
        }
    }
}
