using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class PythonBackendBuildPostprocessor : IPostprocessBuildWithReport
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".venv", "tests", "output", "output_generations", "bench_output", "__pycache__", "wheelhouse"
    };

    public int callbackOrder => 1000;

    public void OnPostprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.StandaloneWindows64) return;

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string source = Path.Combine(projectRoot, "python_backend");
        string playerDirectory = Path.GetDirectoryName(report.summary.outputPath);
        string destination = Path.Combine(playerDirectory, "python_backend");
        if (!Directory.Exists(source))
            throw new BuildFailedException("Windows Player用のpython_backendフォルダーが見つかりません。");

        Directory.CreateDirectory(destination);
        CopyBackendFiles(source, destination);

        string requirements = Path.Combine(destination, "requirements.txt");
        string setup = Path.Combine(destination, "Setup-Python.ps1");
        string entryPoint = Path.Combine(destination, "run_noise_filter.py");
        if (!File.Exists(requirements) || !File.Exists(setup) || !File.Exists(entryPoint))
            throw new BuildFailedException("Windows Playerのpython_backend配置が不完全です。");

        Debug.Log($"[PythonBackendBuild] Python source staged beside the Player: {destination}");
    }

    private static void CopyBackendFiles(string source, string destination)
    {
        foreach (string directory in Directory.GetDirectories(source))
        {
            if (ExcludedDirectoryNames.Contains(Path.GetFileName(directory))) continue;
            string child = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(child);
            CopyBackendFiles(directory, child);
        }

        foreach (string file in Directory.GetFiles(source))
        {
            string extension = Path.GetExtension(file);
            if (!string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
    }
}
