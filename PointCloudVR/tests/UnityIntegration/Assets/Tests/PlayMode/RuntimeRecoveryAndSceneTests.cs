using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class RuntimeRecoveryAndSceneTests
{
    [UnityTest]
    public IEnumerator StartupSceneLoadsWithCameraAndNoMissingScripts()
    {
        const string scenePath = "Assets/VRTestScene.unity";
        AsyncOperation load = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Additive);
        Assert.That(load, Is.Not.Null);
        yield return load;
        yield return null;

        Scene scene = SceneManager.GetSceneByPath(scenePath);
        Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
        GameObject[] roots = scene.GetRootGameObjects();
        Component[] components = roots
            .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
            .SelectMany(transform => transform.GetComponents<Component>())
            .ToArray();

        Assert.That(components.Any(component => component == null), Is.False, "Scene contains a missing script.");
        Assert.That(components.OfType<Camera>().Any(), Is.True, "Startup scene has no camera.");
        yield return SceneManager.UnloadSceneAsync(scene);
    }

    [UnityTest]
    public IEnumerator RecoverySnapshotRoundTripsInPlayerRuntime()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcwb-unity-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "fixture.ply");
        byte[] sourceBytes = System.Text.Encoding.ASCII.GetBytes("synthetic source fixture");
        File.WriteAllBytes(sourcePath, sourceBytes);
        try
        {
            Type store = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("PointCloudWorkbench.PointCloudSessionRecoveryStore", false))
                .FirstOrDefault(candidate => candidate != null);
            Assert.That(store, Is.Not.Null, "Recovery store was not loaded.");

            string sourceHash = (string)store.GetMethod("ComputeFileSha256").Invoke(null, new object[] { sourcePath });
            string checkpoint = (string)store.GetMethod("GetCheckpointPath").Invoke(null, new object[] { directory, sourcePath });
            int[] labels = { 0, 4, 8, 4 };
            store.GetMethod("WriteAtomic").Invoke(null, new object[] { checkpoint, sourceHash, labels, 23L });
            object[] readArguments = { checkpoint, sourceHash, labels.Length, null, 0L, null };
            bool read = (bool)store.GetMethod("TryRead").Invoke(null, readArguments);

            Assert.That(read, Is.True, "Recovery snapshot did not load.");
            Assert.That((int[])readArguments[3], Is.EqualTo(labels));
            Assert.That((long)readArguments[4], Is.EqualTo(23L));
            Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes), "Source fixture changed.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        yield return null;
    }

    [UnityTest]
    public IEnumerator PythonBridgeValidatesEnvironmentAndRequiredImports()
    {
        Type bridge = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("PointCloudWorkbench.PythonBridge", false))
            .FirstOrDefault(candidate => candidate != null);
        Assert.That(bridge, Is.Not.Null, "PythonBridge was not loaded.");
        Task validation = (Task)bridge.GetMethod("EnsureEnvironmentReadyAsync")
            .Invoke(null, new object[] { CancellationToken.None, null });
        float deadline = Time.realtimeSinceStartup + 90f;
        while (!validation.IsCompleted && Time.realtimeSinceStartup < deadline)
            yield return null;

        Assert.That(validation.IsCompleted, Is.True, "Python environment validation timed out.");
        Assert.That(validation.IsFaulted, Is.False,
            validation.Exception != null ? validation.Exception.ToString() : "Python environment validation failed.");
        Assert.That(validation.IsCanceled, Is.False);
    }
}
