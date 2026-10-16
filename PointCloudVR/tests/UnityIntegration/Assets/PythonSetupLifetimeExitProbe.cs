using System;
using System.Collections;
using System.Linq;
using PointCloudWorkbench;
using UnityEngine;

internal sealed class PythonSetupLifetimeExitProbe : MonoBehaviour
{
    private const string ProbeArgument = "--pcwb-exit-during-python-setup";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartProbeIfRequested()
    {
        if (!Environment.GetCommandLineArgs().Contains(ProbeArgument)) return;
        Application.runInBackground = true;
        new GameObject("PythonSetupLifetimeExitProbe").AddComponent<PythonSetupLifetimeExitProbe>();
    }

    private IEnumerator Start()
    {
        Debug.Log("[PythonSetupLifetimeProbe] Starting isolated environment setup.");
        PythonBridge.StartEnvironmentSetup();
        float deadline = Time.realtimeSinceStartup + 240f;
        while (Time.realtimeSinceStartup < deadline)
        {
            PythonEnvironmentSetupSnapshot snapshot = PythonBridge.GetEnvironmentSetupSnapshot();
            if (snapshot.has_error || snapshot.was_cancelled || snapshot.is_complete)
            {
                Debug.LogError("[PythonSetupLifetimeProbe] Setup ended before dependency installation: " +
                    (snapshot.error ?? snapshot.stage));
                Application.Quit(2);
                yield break;
            }
            if (snapshot.stage == "依存ライブラリの導入")
            {
                Debug.Log("[PythonSetupLifetimeProbe] Dependency stage reached; Player will exit with setup running.");
                yield return new WaitForSecondsRealtime(1f);
                Application.Quit(0);
                yield break;
            }
            yield return null;
        }

        Debug.LogError("[PythonSetupLifetimeProbe] Timed out waiting for dependency installation.");
        Application.Quit(3);
    }
}
