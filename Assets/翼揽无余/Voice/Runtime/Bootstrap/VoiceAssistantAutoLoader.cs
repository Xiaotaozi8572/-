using UnityEngine;

namespace Yilan.Voice.Runtime.Bootstrap
{
    public static class VoiceAssistantAutoLoader
    {
        private const string PrefabResourcePath = "Voice/PicoVoiceClient";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureVoiceAssistantExists()
        {
            PersistentVoiceAssistant existing =
                Object.FindObjectOfType<PersistentVoiceAssistant>(true);

            if (existing != null)
            {
                return;
            }

            GameObject prefab =
                Resources.Load<GameObject>(PrefabResourcePath);

            if (prefab == null)
            {
                Debug.LogError(
                    "[VoiceAssistantAutoLoader] ’“≤ªµΩ Resources/Voice/PicoVoiceClient.prefab");
                return;
            }

            Object.Instantiate(prefab);
        }
    }
}