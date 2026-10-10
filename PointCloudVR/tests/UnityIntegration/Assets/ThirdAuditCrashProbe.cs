using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using PointCloudWorkbench;
using UnityEngine;

internal sealed class ThirdAuditCrashProbe : MonoBehaviour
{
    private const string ExpectedFileName = "third-audit-crash.expected";
    private const string StatusFileName = "third-audit-status.txt";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartProbeIfRequested()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (!arguments.Any(argument => argument == "--pcwb-smoke" ||
            argument == "--pcwb-crash-write" || argument == "--pcwb-crash-verify"))
            return;

        new GameObject("ThirdAuditCrashProbe").AddComponent<ThirdAuditCrashProbe>();
    }

    private IEnumerator Start()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--pcwb-smoke"))
            yield return RunSmoke();
        else if (arguments.Contains("--pcwb-crash-write"))
            yield return WriteCrashCheckpoint();
        else if (arguments.Contains("--pcwb-crash-verify"))
            yield return VerifyCrashCheckpoint();
    }

    private IEnumerator RunSmoke()
    {
        PointCloudRenderer renderer = null;
        float deadline = Time.realtimeSinceStartup + 30f;
        while (renderer == null || renderer.GetPointData() == null || renderer.GetPointData().Length == 0)
        {
            renderer = FindAnyObjectByType<PointCloudRenderer>();
            if (Time.realtimeSinceStartup >= deadline) break;
            yield return null;
        }

        bool cloudLoaded = renderer != null && renderer.GetPositions() != null && renderer.GetPositions().Length > 0;
        string pythonFailure = string.Empty;
        bool pythonReady = false;
        bool rendererDisabledBeforeQuit = false;
        if (cloudLoaded)
        {
            Task validation = PythonBridge.EnsureEnvironmentReadyAsync(CancellationToken.None);
            deadline = Time.realtimeSinceStartup + 120f;
            while (!validation.IsCompleted && Time.realtimeSinceStartup < deadline)
                yield return null;
            pythonReady = validation.IsCompleted && !validation.IsFaulted && !validation.IsCanceled;
            if (!pythonReady)
                pythonFailure = validation.Exception != null ? validation.Exception.ToString() : "timeout or cancellation";
        }

        if (cloudLoaded && Environment.GetCommandLineArgs().Contains("--pcwb-smoke-disable-renderer"))
        {
            UnityEngine.Debug.Log("[ThirdAuditPlayer] Disabling PointCloudRenderer before shutdown to isolate GPU teardown.");
            renderer.enabled = false;
            rendererDisabledBeforeQuit = true;
            yield return null;
            yield return new WaitForEndOfFrame();
            UnityEngine.Debug.Log("[ThirdAuditPlayer] PointCloudRenderer disabled; shutdown probe continues.");
        }

        string status = $"cloud_loaded={cloudLoaded};points={(cloudLoaded ? renderer.GetPositions().Length : 0)};" +
                        $"octree_ready={(cloudLoaded && renderer.IsOctreeReady)};python_ready={pythonReady};" +
                        $"renderer_disabled_before_quit={rendererDisabledBeforeQuit};python_error={pythonFailure}";
        WriteStatus(status);
        UnityEngine.Debug.Log("[ThirdAuditPlayer] " + status);
        Application.Quit();
    }

    private IEnumerator WriteCrashCheckpoint()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../PointCloudData/sample.ply"));
        PointCloudLoader loader = null;
        PointCloudRenderer renderer = null;
        float deadline = Time.realtimeSinceStartup + 30f;
        while (Time.realtimeSinceStartup < deadline)
        {
            loader = FindAnyObjectByType<PointCloudLoader>();
            renderer = loader != null ? loader.targetRenderer : null;
            bool expectedSourceLoaded = loader != null && !string.IsNullOrWhiteSpace(loader.CurrentFilePath) &&
                string.Equals(Path.GetFullPath(loader.CurrentFilePath), sourcePath, StringComparison.OrdinalIgnoreCase);
            if (expectedSourceLoaded && renderer != null && renderer.GetPointData() != null && renderer.GetPointData().Length > 0)
                break;
            yield return null;
        }

        if (renderer == null || renderer.GetPointData() == null || renderer.GetPointData().Length == 0)
        {
            WriteStatus("FAIL: no loaded cloud for crash fixture");
            Application.Quit();
            yield break;
        }

        int pointCount = renderer.GetPointData().Length;
        if (!File.Exists(sourcePath))
        {
            WriteStatus("FAIL: synthetic source PLY is missing: " + sourcePath);
            Application.Quit();
            yield break;
        }

        string sourceHash = PointCloudSessionRecoveryStore.ComputeFileSha256(sourcePath);
        int[] labels = new int[pointCount];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = (i % 9) | (i % 7 == 0 ? 0x20000 : 0) | (i % 11 == 0 ? 0x80000 : 0);
        string checkpoint = PointCloudSessionRecoveryStore.GetCheckpointPath(Application.persistentDataPath, sourcePath);
        PointCloudSessionRecoveryStore.WriteAtomic(checkpoint, sourceHash, labels, 901L);
        File.WriteAllLines(Path.Combine(Application.persistentDataPath, ExpectedFileName), new[]
        {
            sourcePath,
            sourceHash,
            pointCount.ToString(CultureInfo.InvariantCulture),
            "901"
        });
        WriteStatus($"CHECKPOINT_WRITTEN;points={pointCount};source_sha256={sourceHash};" +
                    $"checkpoint_sha256={PointCloudSessionRecoveryStore.ComputeFileSha256(checkpoint)}");
        UnityEngine.Debug.Log("[ThirdAuditPlayer] Test-only crash injection: terminating only this disposable Player.");
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    private IEnumerator VerifyCrashCheckpoint()
    {
        PointCloudLoader loader = FindAnyObjectByType<PointCloudLoader>();
        PointCloudRenderer renderer = loader != null
            ? loader.targetRenderer
            : FindAnyObjectByType<PointCloudRenderer>();
        string expectedPath = Path.Combine(Application.persistentDataPath, ExpectedFileName);
        if (!File.Exists(expectedPath))
        {
            WriteStatus("FAIL: crash fixture manifest is missing");
            Application.Quit();
            yield break;
        }

        string[] expected = File.ReadAllLines(expectedPath);
        if (expected.Length != 4 || !int.TryParse(expected[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pointCount))
        {
            WriteStatus("FAIL: crash fixture manifest is invalid");
            Application.Quit();
            yield break;
        }

        PointCloudEditor editor = null;
        float deadline = Time.realtimeSinceStartup + 45f;
        while (Time.realtimeSinceStartup < deadline)
        {
            editor = FindAnyObjectByType<PointCloudEditor>();
            if (loader == null) loader = FindAnyObjectByType<PointCloudLoader>();
            if (renderer == null && loader != null) renderer = loader.targetRenderer;
            if (renderer == null) renderer = FindAnyObjectByType<PointCloudRenderer>();
            if (editor != null && editor.HasPendingRecovery) break;
            yield return null;
        }

        string sourcePath = expected[0];
        string checkpoint = PointCloudSessionRecoveryStore.GetCheckpointPath(Application.persistentDataPath, sourcePath);
        bool unclean = PointCloudSessionRecoveryStore.PreviousSessionWasUnclean;
        bool read = PointCloudSessionRecoveryStore.TryRead(checkpoint, expected[1], pointCount,
            out int[] labels, out long revision, out string failureReason);
        bool labelsCorrect = read && revision == 901L && labels.Length == pointCount;
        if (labelsCorrect)
            for (int i = 0; i < labels.Length; i++)
            {
                int expectedLabel = (i % 9) | (i % 7 == 0 ? 0x20000 : 0) | (i % 11 == 0 ? 0x80000 : 0);
                if (labels[i] != expectedLabel) { labelsCorrect = false; break; }
            }

        bool candidatePending = editor != null && editor.HasPendingRecovery;
        bool applied = false;
        bool appliedLabelsCorrect = false;
        if (candidatePending)
        {
            applied = editor.ApplyPendingRecovery();
            if (renderer == null) renderer = FindAnyObjectByType<PointCloudRenderer>();
            PointData[] data = renderer != null ? renderer.GetPointData() : null;
            appliedLabelsCorrect = applied && data != null && data.Length == pointCount;
            if (appliedLabelsCorrect)
                for (int i = 0; i < data.Length; i++)
                {
                    int expectedLabel = (i % 9) | (i % 7 == 0 ? 0x20000 : 0) | (i % 11 == 0 ? 0x80000 : 0);
                    if (data[i].label != expectedLabel) { appliedLabelsCorrect = false; break; }
                }
        }

        string currentSourceHash = File.Exists(sourcePath)
            ? PointCloudSessionRecoveryStore.ComputeFileSha256(sourcePath)
            : "missing";
        bool sourceUnchanged = string.Equals(currentSourceHash, expected[1], StringComparison.OrdinalIgnoreCase);
        string status = $"unclean_marker={unclean};checkpoint_read={read};labels={labelsCorrect};" +
                        $"candidate_pending={candidatePending};applied={applied};applied_labels={appliedLabelsCorrect};" +
                        $"source_unchanged={sourceUnchanged};failure={failureReason}";
        string continuationStatus = "not_run";
        if (appliedLabelsCorrect && editor != null && loader != null)
        {
            ContinuationResult continuation = null;
            yield return VerifyRecoveryContinuation(editor, loader, renderer, sourcePath, expected[1], pointCount,
                value => continuation = value);
            continuationStatus = continuation != null ? continuation.ToString() : "missing_result";
            if (continuation == null || !continuation.Passed) Environment.ExitCode = 1;
        }
        string fullStatus = status + ";continuation=" + continuationStatus;
        WriteStatus(fullStatus);
        UnityEngine.Debug.Log("[ThirdAuditPlayer] " + fullStatus);
        Application.Quit();
    }

    private sealed class ContinuationResult
    {
        public bool Classified;
        public bool DeletedAndRestored;
        public bool Saved;
        public bool Switched;
        public bool ReloadedLabels;
        public bool LoadedSavedCloud;
        public bool ReloadedPositionsMatch;
        public int ReloadedPointCount;
        public bool SourceUnchanged;
        public string OutputPath;
        public int ExportedPointCount;
        public int ExpectedVisiblePointCount;

        public bool Passed => Classified && DeletedAndRestored && Saved && Switched && ReloadedLabels && SourceUnchanged;

        public override string ToString() =>
            $"passed={Passed};classified={Classified};delete_restore={DeletedAndRestored};saved={Saved};" +
            $"switched={Switched};loaded_saved_cloud={LoadedSavedCloud};reloaded_labels={ReloadedLabels};" +
            $"reloaded_positions={ReloadedPositionsMatch};reloaded_points={ReloadedPointCount};source_unchanged={SourceUnchanged};" +
            $"exported_points={ExportedPointCount};expected_visible_points={ExpectedVisiblePointCount};output={OutputPath};" +
            RuntimeMemorySummary();
    }

    private IEnumerator VerifyRecoveryContinuation(PointCloudEditor editor, PointCloudLoader loader,
        PointCloudRenderer renderer, string sourcePath, string sourceHash, int pointCount,
        Action<ContinuationResult> completed)
    {
        ContinuationResult result = new ContinuationResult();
        PointData[] points = renderer.GetPointData();
        int[] classifyIndices = Enumerable.Range(0, points.Length)
            .Where(index => (points[index].label & 0x20000) == 0).Take(32).ToArray();
        int[] deleteIndices = Enumerable.Range(0, points.Length)
            .Where(index => (points[index].label & 0x20000) == 0 && !classifyIndices.Contains(index))
            .Take(16).ToArray();
        int deletedBefore = points.Count(point => (point.label & 0x20000) != 0);

        if (classifyIndices.Length == 32 && deleteIndices.Length == 16 && renderer.TryUpdatePointBuffer())
        {
            foreach (int index in classifyIndices)
            {
                PointData point = points[index];
                point.label |= 0x10000;
                points[index] = point;
            }
            editor.activeLabelClass = 6;
            editor.AssignLabelToSelected();
            result.Classified = classifyIndices.All(index => (points[index].label & 0xff) == 6 &&
                (points[index].label & 0x10000) == 0);

            foreach (int index in deleteIndices)
            {
                PointData point = points[index];
                point.label |= 0x10000;
                points[index] = point;
            }
            bool selectionSynced = renderer.TryUpdatePointBuffer();
            if (selectionSynced) editor.DeleteSelected();
            int deletedAfter = points.Count(point => (point.label & 0x20000) != 0);
            editor.RestoreDeleted();
            int deletedRestored = points.Count(point => (point.label & 0x20000) != 0);
            result.DeletedAndRestored = selectionSynced && deletedAfter == deletedBefore + deleteIndices.Length &&
                deletedRestored == 0;
        }

        PointData[] expectedVisible = points.Where(point => (point.label & (0x20000 | 0x80000)) == 0).ToArray();
        result.ExpectedVisiblePointCount = expectedVisible.Length;
        int[] expectedLabels = expectedVisible.Select(point => point.label & 0xff).ToArray();
        string expectedPositions = HashPositions(expectedVisible);
        string qaRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../"));
        string outputDirectory = Path.Combine(qaRoot, "Artifacts", "PlayerRuns");
        Directory.CreateDirectory(outputDirectory);
        result.OutputPath = Path.Combine(outputDirectory, "recovery-continuation-" + Guid.NewGuid().ToString("N") + ".ply");
        Task export = editor.ExportLabeledPointsAsync(result.OutputPath, true, CancellationToken.None);
        float deadline = Time.realtimeSinceStartup + 120f;
        while (!export.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        result.ExportedPointCount = File.Exists(result.OutputPath)
            ? PointCloudPlyReader.Validate(result.OutputPath)
            : -1;
        result.Saved = export.IsCompletedSuccessfully && result.ExportedPointCount == expectedVisible.Length;

        if (result.Saved)
        {
            string otherCloud = Path.Combine(qaRoot, "PointCloudData", "cloud_B_100k.ply");
            bool loadedOther = false;
            yield return LoadAndWait(loader, renderer, otherCloud, pointCount, ok => loadedOther = ok);
            result.Switched = loadedOther && FirstPointMatches(renderer.GetPointData(),
                0.0011118031f, 0.02499975f, 0f);

            bool loadedSaved = false;
            yield return LoadAndWait(loader, renderer, result.OutputPath, expectedVisible.Length, ok => loadedSaved = ok);
            result.LoadedSavedCloud = loadedSaved;
            PointData[] reloaded = renderer.GetPointData();
            result.ReloadedPointCount = reloaded?.Length ?? 0;
            result.ReloadedPositionsMatch = reloaded != null && HashPositions(reloaded) == expectedPositions;
            result.ReloadedLabels = loadedSaved && reloaded != null &&
                expectedLabels.SequenceEqual(reloaded.Select(point => point.label)) && result.ReloadedPositionsMatch;
        }
        result.SourceUnchanged = File.Exists(sourcePath) &&
            string.Equals(PointCloudSessionRecoveryStore.ComputeFileSha256(sourcePath), sourceHash,
                StringComparison.OrdinalIgnoreCase);
        completed(result);
    }

    private IEnumerator LoadAndWait(PointCloudLoader loader, PointCloudRenderer renderer, string path,
        int expectedCount, Action<bool> completed)
    {
        float deadline = Time.realtimeSinceStartup + 120f;
        while (PointCloudProgressManager.Instance.IsRunning && Time.realtimeSinceStartup < deadline)
            yield return null;
        int oldRevision = loader.SuccessfulLoadRevision;
        long oldGeneration = renderer.DatasetGeneration;
        loader.LoadPointCloud(path);
        deadline = Time.realtimeSinceStartup + 120f;
        while ((loader.SuccessfulLoadRevision <= oldRevision || renderer.DatasetGeneration <= oldGeneration ||
                !renderer.IsOctreeReady || PointCloudProgressManager.Instance.IsRunning) &&
               Time.realtimeSinceStartup < deadline)
            yield return null;
        completed(loader.SuccessfulLoadRevision > oldRevision && renderer.DatasetGeneration > oldGeneration &&
            string.Equals(Path.GetFullPath(loader.CurrentFilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
            renderer.GetPointData()?.Length == expectedCount && renderer.IsOctreeReady &&
            renderer.GetActiveDrawCount() > 0 && renderer.GetActiveDrawCount() <= expectedCount &&
            !PointCloudProgressManager.Instance.IsRunning);
    }

    private static bool FirstPointMatches(PointData[] points, float x, float y, float z)
    {
        if (points == null || points.Length == 0) return false;
        Vector3 value = points[0].position;
        return Math.Abs(value.x - x) < 0.00001f && Math.Abs(value.y - y) < 0.00001f &&
            Math.Abs(value.z - z) < 0.00001f;
    }

    private static string HashPositions(PointData[] points)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] bytes = new byte[12];
            foreach (PointData point in points)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(point.position.x), 0, bytes, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(point.position.y), 0, bytes, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(point.position.z), 0, bytes, 8, 4);
                sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    private static string RuntimeMemorySummary() =>
        $"gc_bytes={GC.GetTotalMemory(false)}; unity_total_allocated_bytes={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()}; " +
        $"unity_total_reserved_bytes={UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong()}; " +
        $"unity_graphics_driver_bytes={UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver()}; " +
        $"graphics_device_memory_bytes={(long)SystemInfo.graphicsMemorySize * 1024 * 1024}";

    private static void WriteStatus(string status)
    {
        Directory.CreateDirectory(Application.persistentDataPath);
        File.WriteAllText(Path.Combine(Application.persistentDataPath, StatusFileName), status);
    }
}
