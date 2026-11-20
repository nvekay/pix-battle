using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace BattlePix.Editor
{
    /// <summary>Сборка ПК-плеера: -executeMethod BattlePix.Editor.BuildScript.BuildWindows → Builds/Windows/BattlePix.exe</summary>
    public static class BuildScript
    {
        [MenuItem("BattlePix/Build Windows Player")]
        public static void BuildWindows()
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/BattlePix/Scenes/Main.unity" },
                locationPathName = "Builds/Windows/BattlePix.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[BattlePix] build {report.summary.result}, {report.summary.totalSize / 1024} KB, {report.summary.totalErrors} errors");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
