// VoiceSceneSetup.cs - Editor-only scene wiring for the "Yilan Wuyu" voice integration
// scene (P0-2 fix). Ensures PicoVoiceIntegrationTest.unity contains exactly one
// PicoVoiceClient prefab instance so the record/stop/cancel buttons are reachable
// on device; the scene previously had no voice UI at all.
//
// BATCH-MODE PITFALL (root cause of the collapsed UI): in batch mode the overlay
// Canvas drives its RectTransform to degenerate values (pivot/anchorMax/scale = 0),
// and any SaveScene then serializes those values as prefab overrides, collapsing
// the whole UI. This tool therefore NEVER relies on the in-memory Canvas state:
// after any save it strips Canvas RectTransform layout overrides directly from the
// scene file on disk, then verifies the file. The on-disk strip is idempotent.
//
// Invoked from the command line via:
//   Unity -batchmode -quit -projectPath <proj> -executeMethod Yilan.Voice.Editor.VoiceSceneSetup.EnsureIntegrationSceneWired

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Yilan.Voice.Editor
{
    public static class VoiceSceneSetup
    {
        private const string IntegrationScenePath = @"Assets/翼揽无余/Voice/Scenes/PicoVoiceIntegrationTest.unity";
        private const string PrefabPath = @"Assets/翼揽无余/Voice/Prefabs/PicoVoiceClient.prefab";
        private const string InstanceName = "PicoVoiceClient";

        public static void EnsureIntegrationSceneWired()
        {
            var scene = EditorSceneManager.OpenScene(IntegrationScenePath, OpenSceneMode.Single);

            GameObject existing = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == InstanceName)
                {
                    existing = root;
                    break;
                }
            }

            if (existing == null)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (prefab == null)
                {
                    Debug.LogError($"[VoiceSceneSetup] prefab not found: {PrefabPath}");
                    EditorApplication.Exit(1);
                    return;
                }
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                if (instance == null)
                {
                    Debug.LogError("[VoiceSceneSetup] InstantiatePrefab failed");
                    EditorApplication.Exit(1);
                    return;
                }
                instance.name = InstanceName;
                instance.transform.position = Vector3.zero;
                existing = instance;
                if (!EditorSceneManager.SaveScene(scene))
                {
                    Debug.LogError("[VoiceSceneSetup] SaveScene failed");
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log("[VoiceSceneSetup] instantiated and saved PicoVoiceClient prefab instance");
            }
            else
            {
                Debug.Log("[VoiceSceneSetup] PicoVoiceClient instance already present");
                // Intentionally NO SaveScene in this branch: in batch mode a save would
                // re-serialize the driven (collapsed) Canvas values as overrides.
            }

            var canvas = existing.GetComponentInChildren<Canvas>(true);
            if (canvas == null)
            {
                Debug.LogError("[VoiceSceneSetup] prefab instance has no Canvas");
                EditorApplication.Exit(1);
                return;
            }

            var sourceCanvasRect = PrefabUtility.GetCorrespondingObjectFromSource(canvas.transform) as RectTransform;
            if (sourceCanvasRect == null
                || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceCanvasRect, out string srcGuid, out long srcLocalId))
            {
                Debug.LogError("[VoiceSceneSetup] failed to resolve prefab-side Canvas RectTransform identifier");
                EditorApplication.Exit(1);
                return;
            }

            string sceneAbsPath = Path.GetFullPath(IntegrationScenePath);
            Debug.Log($"[VoiceSceneSetup] sceneAbsPath={sceneAbsPath} canvasSrcGuid={srcGuid} canvasSrcLocalId={srcLocalId} exists={File.Exists(sceneAbsPath)}");
            int removed = StripCanvasLayoutOverridesOnDisk(sceneAbsPath, srcGuid, srcLocalId);
            Debug.Log($"[VoiceSceneSetup] on-disk strip removed {removed} Canvas layout override entries");

            int remaining = CountCanvasLayoutOverridesOnDisk(sceneAbsPath, srcGuid, srcLocalId);
            if (remaining != 0)
            {
                Debug.LogError($"[VoiceSceneSetup] verification failed: {remaining} Canvas layout overrides still present in {sceneAbsPath}");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("[VoiceSceneSetup] verification passed: no Canvas layout overrides in scene file");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// Removes prefab-instance override entries that target the Canvas RectTransform's
        /// layout properties (scale/pivot/anchors/position/size) directly from the scene
        /// file. The Canvas of a Screen Space Overlay canvas owns its layout; any stored
        /// override on it either collapses the UI (the historical bug) or fights the
        /// driven layout. Returns the number of removed entries.
        /// </summary>
        private static int StripCanvasLayoutOverridesOnDisk(string scenePath, string guid, long localId)
        {
            string marker = BuildTargetLine(guid, localId);
            string[] lines = File.ReadAllLines(scenePath);
            var kept = new List<string>(lines.Length);
            int removed = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Equals(marker) && i + 1 < lines.Length)
                {
                    string propertyLine = lines[i + 1].Trim();
                    if (IsCanvasLayoutPropertyPath(propertyLine))
                    {
                        removed++;
                        i += 3; // skip target / propertyPath / value / objectReference
                        continue;
                    }
                }
                kept.Add(lines[i]);
            }
            if (removed > 0)
            {
                File.WriteAllLines(scenePath, kept);
            }
            return removed;
        }

        private static int CountCanvasLayoutOverridesOnDisk(string scenePath, string guid, long localId)
        {
            string marker = BuildTargetLine(guid, localId);
            string[] lines = File.ReadAllLines(scenePath);
            int count = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Equals(marker) && i + 1 < lines.Length)
                {
                    if (IsCanvasLayoutPropertyPath(lines[i + 1].Trim())) count++;
                }
            }
            return count;
        }

        private static string BuildTargetLine(string guid, long localId)
        {
            return $"- target: {{fileID: {localId}, guid: {guid}, type: 3}}";
        }

        private static bool IsCanvasLayoutPropertyPath(string propertyLine)
        {
            const string prefix = "propertyPath: ";
            if (!propertyLine.StartsWith(prefix)) return false;
            string path = propertyLine.Substring(prefix.Length);
            return path.StartsWith("m_LocalScale")
                || path.StartsWith("m_Pivot")
                || path.StartsWith("m_AnchorMin")
                || path.StartsWith("m_AnchorMax")
                || path.StartsWith("m_AnchoredPosition")
                || path.StartsWith("m_SizeDelta");
        }
    }
}
