using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class ThirdAuditBuild
{
    public static void BuildWindows()
    {
        PlayerSettings.companyName = Environment.GetEnvironmentVariable("PCWB_QA_COMPANY_NAME") ?? "PCWB_ThirdAudit";
        PlayerSettings.productName = Environment.GetEnvironmentVariable("PCWB_QA_PRODUCT_NAME") ?? "PCWB_ThirdAudit_20261009";
        string output = Environment.GetEnvironmentVariable("PCWB_QA_BUILD_PATH");
        if (string.IsNullOrWhiteSpace(output))
            output = "E:/pcwb-qa-20261009/PlayerBuild/PointCloudVR.exe";

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();
        if (scenes.Length == 0)
        {
            Debug.LogError("[ThirdAuditBuild] No enabled scenes in EditorBuildSettings.");
            EditorApplication.Exit(1);
            return;
        }

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.StrictMode
        });

        Debug.Log($"[ThirdAuditBuild] result={report.summary.result} errors={report.summary.totalErrors} " +
                  $"warnings={report.summary.totalWarnings} size={report.summary.totalSize} path={output}");
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
