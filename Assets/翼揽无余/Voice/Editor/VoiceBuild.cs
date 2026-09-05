// VoiceBuild.cs - Editor-only build entrypoint for the "Yilan Wuyu" voice real-device
// verification special. Builds a minimal ARM64 / IL2CPP Development APK that contains
// only the Pico voice integration test scene.
//
// This is intentionally minimal: it does not touch EditorBuildSettings or any existing
// business scene. The integration scene is passed directly to BuildPipeline.BuildPlayer.

using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Yilan.Voice.Editor
{
    public static class VoiceBuild
    {
        // Absolute output path for the Development APK (repo tmp dir).
        private const string OutputApkPath = @"D:\APP\Python 3.13\挑战杯\tmp\voice-vr\PicoVoice.apk";

        // The single integration scene explicitly passed to BuildPlayer.
        private const string IntegrationScenePath = @"Assets/翼揽无余/Voice/Scenes/PicoVoiceIntegrationTest.unity";

        /// <summary>
        /// Builds a Development APK for Android targeting ARM64 with the IL2CPP scripting
        /// backend, containing only the PicoVoiceIntegrationTest scene.
        /// Invoked from the command line via:
        ///   Unity -batchmode -quit -projectPath &lt;proj&gt; -executeMethod Yilan.Voice.Editor.VoiceBuild.BuildDevelopmentPico
        /// </summary>
        public static void BuildDevelopmentPico()
        {
            const BuildTargetGroup targetGroup = BuildTargetGroup.Android;
            const BuildTarget target = BuildTarget.Android;

            // Explicitly target ARM64. (The whitelisted, non-Arm architect case is intentionally
            // not supported for device deployment.)
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Use the IL2CPP scripting backend for Android.
            PlayerSettings.SetScriptingBackend(targetGroup, ScriptingImplementation.IL2CPP);

            // Strip everything except the integration scene; never pull in business scenes.
            string[] scenes = { IntegrationScenePath };

            var options = BuildOptions.Development;

            // The development build must be a plain APK (not Android App Bundle).
            EditorUserBuildSettings.buildAppBundle = false;

            Debug.Log($"[VoiceBuild] Building Development APK -> {OutputApkPath}");
            Debug.Log($"[VoiceBuild] scenes: {string.Join(", ", scenes)}");

            var report = BuildPipeline.BuildPlayer(scenes, OutputApkPath, target, options);

            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.LogError($"[VoiceBuild] Build FAILED: {report.summary.result}");
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log($"[VoiceBuild] Build SUCCEEDED, apk size={new FileInfo(OutputApkPath).Length} bytes");
            EditorApplication.Exit(0);
        }
    }
}
