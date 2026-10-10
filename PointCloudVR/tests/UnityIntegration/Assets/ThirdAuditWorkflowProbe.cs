using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PointCloudWorkbench;
using UnityEngine;

internal sealed class ThirdAuditWorkflowProbe : MonoBehaviour
{
    private const int SelectedBit = 0x10000;
    private const int DeletedBit = 0x20000;
    private string reportPath;
    private string fixtureRoot;
    private string artifactRoot;
    private string runId;
    private PointCloudLoader loader;
    private PointCloudEditor editor;
    private PointCloudRenderer renderer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartProbeIfRequested()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (!args.Contains("--pcwb-fourth-player-workflow") && !args.Contains("--pcwb-e2e-order2") &&
            !args.Contains("--pcwb-fourth-load-race") && !args.Contains("--pcwb-fourth-load-tiers")) return;
        new GameObject("FourthAuditWorkflowProbe").AddComponent<ThirdAuditWorkflowProbe>();
    }

    private IEnumerator Start()
    {
        runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        string qaRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../../"));
        string sharedFixtureRoot = Path.Combine(qaRoot, "PointCloudData");
        fixtureRoot = Path.Combine(qaRoot, "Artifacts", "FixtureRuns", runId);
        CopyDirectory(sharedFixtureRoot, fixtureRoot);
        artifactRoot = Path.Combine(qaRoot, "Artifacts", "PlayerRuns");
        Directory.CreateDirectory(artifactRoot);
        reportPath = Path.Combine(artifactRoot, "fourth-player-" + runId + ".tsv");
        File.WriteAllText(reportPath, "utc\ttest_id\tresult\tdetail\n", Encoding.UTF8);
        loader = FindAnyObjectByType<PointCloudLoader>();
        editor = FindAnyObjectByType<PointCloudEditor>();
        renderer = loader != null ? loader.targetRenderer : FindAnyObjectByType<PointCloudRenderer>();

        bool raceMode = Environment.GetCommandLineArgs().Contains("--pcwb-fourth-load-race");
        bool loadTierMode = Environment.GetCommandLineArgs().Contains("--pcwb-fourth-load-tiers");
        bool order2Mode = Environment.GetCommandLineArgs().Contains("--pcwb-e2e-order2");
        yield return Guard(raceMode ? RunLoadRace() : loadTierMode ? RunLoadTiers() :
            order2Mode ? RunAlternateWorkflow() : RunWorkflow());
        Application.Quit(Environment.ExitCode);
    }

    private IEnumerator Guard(IEnumerator routine)
    {
        Stack<IEnumerator> stack = new Stack<IEnumerator>();
        stack.Push(routine);
        while (stack.Count > 0)
        {
            IEnumerator current = stack.Peek();
            bool moved;
            object yielded = null;
            try
            {
                moved = current.MoveNext();
                if (moved) yielded = current.Current;
            }
            catch (Exception exception)
            {
                Record("F4-PLAYER-DRIVER", "FAIL", exception.ToString());
                Environment.ExitCode = 1;
                yield break;
            }

            if (!moved)
            {
                (current as IDisposable)?.Dispose();
                stack.Pop();
                continue;
            }
            if (yielded is IEnumerator nested) stack.Push(nested);
            else yield return yielded;
        }
    }

    private IEnumerator RunLoadRace()
    {
        yield return WaitForInitialCloud(90f);
        string a = Fixture("cloud_A_100k.ply");
        string b = Fixture("cloud_B_100k.ply");
        string c = Fixture("cloud_C_100k.ply");
        int oldRevision = loader.SuccessfulLoadRevision;
        long oldGeneration = renderer.DatasetGeneration;
        loader.LoadPointCloud(a);
        loader.LoadPointCloud(b);
        loader.LoadPointCloud(c);
        yield return WaitForRevision(oldRevision, 90f);
        yield return WaitForOctree(90f);

        bool firstRequestWon = PathsEqual(loader.CurrentFilePath, a);
        bool dataMatches = renderer.GetPointData()?.Length == 100_000 &&
            FirstPointMatches(renderer.GetPointData(), 0.00011180312f, 0.02499975f, 0f);
        bool generationSingle = renderer.DatasetGeneration == oldGeneration + 1;
        bool coherent = firstRequestWon && dataMatches && generationSingle && renderer.IsOctreeReady;
        Record("F4-LOAD-RACE-ABC", coherent ? "PASS" : "FAIL",
            $"path={loader.CurrentFilePath}; points={renderer.GetPointData()?.Length}; generation_delta={renderer.DatasetGeneration - oldGeneration}; " +
            $"octree={renderer.IsOctreeReady}; draw_count={renderer.GetActiveDrawCount()}; first_xyz={FormatFirstPoint(renderer.GetPointData())}; " +
            $"xyz_sha256={HashPositions(renderer.GetPointData())}");

        bool loadedB = false;
        yield return LoadAndWait(b, 100_000, ok => loadedB = ok);
        bool bMatches = loadedB && PathsEqual(loader.CurrentFilePath, b) &&
            FirstPointMatches(renderer.GetPointData(), 0.0011118031f, 0.02499975f, 0f) && renderer.IsOctreeReady;
        bool loadedC = false;
        yield return LoadAndWait(c, 100_000, ok => loadedC = ok);
        bool cMatches = loadedC && PathsEqual(loader.CurrentFilePath, c) &&
            FirstPointMatches(renderer.GetPointData(), 0.00011180312f, 0.02399975f, 0f) && renderer.IsOctreeReady;
        bool switched = bMatches && cMatches;
        Record("F4-LOAD-SEQUENTIAL-BC", switched ? "PASS" : "FAIL",
            $"B_then_C={bMatches}/{cMatches}; final={loader.CurrentFilePath}; points={renderer.GetPointData()?.Length}; " +
            $"first_xyz={FormatFirstPoint(renderer.GetPointData())}; " +
            $"generation={renderer.DatasetGeneration}; octree={renderer.IsOctreeReady}; draw_count={renderer.GetActiveDrawCount()}");
        yield return RunRepeatedCloudSwitches();
        if (!coherent || !switched) Environment.ExitCode = 1;
    }

    private IEnumerator RunRepeatedCloudSwitches()
    {
        string[] names = { "cloud_A_100k.ply", "cloud_B_100k.ply", "cloud_C_100k.ply" };
        float[][] firstPoints =
        {
            new[] { 0.00011180312f, 0.02499975f, 0f },
            new[] { 0.0011118031f, 0.02499975f, 0f },
            new[] { 0.00011180312f, 0.02399975f, 0f }
        };
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        int passed = 0;
        for (int i = 0; i < 100; i++)
        {
            int index = i % names.Length;
            float operationDeadline = Time.realtimeSinceStartup + 60f;
            while (PointCloudProgressManager.Instance.IsRunning && Time.realtimeSinceStartup < operationDeadline)
                yield return null;
            int oldRevision = loader.SuccessfulLoadRevision;
            long oldGeneration = renderer.DatasetGeneration;
            loader.LoadPointCloud(Fixture(names[index]));
            yield return WaitForRevision(oldRevision, 60f);
            float deadline = Time.realtimeSinceStartup + 60f;
            while ((loader.SuccessfulLoadRevision <= oldRevision || renderer.DatasetGeneration <= oldGeneration ||
                    !renderer.IsOctreeReady) && Time.realtimeSinceStartup < deadline)
                yield return null;
            bool valid = loader.SuccessfulLoadRevision > oldRevision && renderer.DatasetGeneration > oldGeneration &&
                PathsEqual(loader.CurrentFilePath, Fixture(names[index])) && renderer.IsOctreeReady &&
                renderer.GetPointData()?.Length == 100_000 && FirstPointMatches(renderer.GetPointData(),
                    firstPoints[index][0], firstPoints[index][1], firstPoints[index][2]) &&
                renderer.GetActiveDrawCount() > 0 && renderer.GetActiveDrawCount() <= 100_000;
            if (valid) passed++;
            else
            {
                Record("F4-LOAD-SWITCH-100", "FAIL",
                    $"iteration={i + 1}; path={loader.CurrentFilePath}; generation_delta={renderer.DatasetGeneration - oldGeneration}; " +
                    $"revision_delta={loader.SuccessfulLoadRevision - oldRevision}; points={renderer.GetPointData()?.Length}; " +
                    $"first_xyz={FormatFirstPoint(renderer.GetPointData())}; octree={renderer.IsOctreeReady}; draw_count={renderer.GetActiveDrawCount()}");
                break;
            }
            if ((i + 1) % 10 == 0) yield return null;
        }
        elapsed.Stop();
        Record("F4-LOAD-SWITCH-100", passed == 100 ? "PASS" : "FAIL",
            $"passed={passed}/100; elapsed_ms={elapsed.ElapsedMilliseconds}; points={renderer.GetPointData()?.Length}; " +
            $"generation={renderer.DatasetGeneration}; octree={renderer.IsOctreeReady}; draw_count={renderer.GetActiveDrawCount()}; " +
            RuntimeMemorySummary());
        if (passed != 100) Environment.ExitCode = 1;
    }

    private IEnumerator RunLoadTiers()
    {
        int[] tiers = { 10_000, 100_000, 500_000, 1_000_000, 2_200_000 };
        bool allPassed = true;
        foreach (int count in tiers)
        {
            string path = Fixture("stress_" + count + ".ply");
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool loaded = false;
            yield return LoadAndWait(path, count, ok => loaded = ok);
            elapsed.Stop();
            bool passed = loaded && renderer.GetPointData()?.Length == count && renderer.IsOctreeReady &&
                renderer.GetActiveDrawCount() > 0 && renderer.GetActiveDrawCount() <= count;
            Record("F4-LOAD-TIER-" + count, passed ? "PASS" : "FAIL",
                $"points={renderer.GetPointData()?.Length}; elapsed_ms={elapsed.ElapsedMilliseconds}; " +
                $"generation={renderer.DatasetGeneration}; octree={renderer.IsOctreeReady}; draw_count={renderer.GetActiveDrawCount()}; " +
                RuntimeMemorySummary() + "; " +
                $"source_sha256={HashFile(path)}");
            allPassed &= passed;
            if (!passed) break;
        }
        if (!allPassed) Environment.ExitCode = 1;
    }

    private IEnumerator RunWorkflow()
    {
        yield return WaitForInitialCloud(90f);
        if (loader == null || editor == null || renderer == null)
            throw new InvalidOperationException("Startup scene lacks a loader, editor, or renderer.");

        string source = Fixture("cloud_A_100k.ply");
        bool loaded = false;
        yield return LoadAndWait(source, 100_000, ok => loaded = ok);
        if (!loaded) throw new InvalidOperationException("Could not load the synthetic workflow cloud.");

        PointData[] points = renderer.GetPointData();
        string sourceHash = HashFile(source);
        string originalPositions = HashPositions(points);
        string originalLabelHash = HashLabels(points);
        RecordCloudState("F4-E2E-START", source, points);
        int[] originalLabels = points.Take(64).Select(point => point.label).ToArray();
        for (int i = 0; i < originalLabels.Length; i++)
        {
            PointData point = points[i];
            point.label |= SelectedBit;
            points[i] = point;
        }
        int[] selectedLabels = originalLabels.Select(label => label | SelectedBit).ToArray();
        if (!renderer.TryUpdatePointBuffer()) throw new InvalidOperationException("Could not sync test selection to the Player buffer.");
        editor.activeLabelClass = 4;
        editor.AssignLabelToSelected();
        yield return null;
        bool classified = Enumerable.Range(0, 64).All(i => (points[i].label & 0xff) == 4 && (points[i].label & SelectedBit) == 0);
        bool undoApplied = editor.AnnotationUndo();
        yield return null;
        bool undoRestored = undoApplied && Enumerable.Range(0, 64).All(i => points[i].label == selectedLabels[i]);
        bool redoApplied = editor.AnnotationRedo();
        yield return null;
        bool redoRestored = redoApplied && Enumerable.Range(0, 64).All(i => (points[i].label & 0xff) == 4 && (points[i].label & SelectedBit) == 0);
        bool editUndoRedo = classified && undoRestored && redoRestored;
        Record("F4-EDIT-CLASS-UNDO-REDO", editUndoRedo ? "PASS" : "FAIL",
            $"selected=64; classified={classified}; undo_applied={undoApplied}; undo_restored={undoRestored}; " +
            $"redo_applied={redoApplied}; redo_restored={redoRestored}; " +
            $"content_revision={renderer.ContentRevision}; generation={renderer.DatasetGeneration}");
        if (!editUndoRedo) Environment.ExitCode = 1;

        for (int i = 64; i < 128; i++)
        {
            PointData point = points[i];
            point.label |= SelectedBit;
            points[i] = point;
        }
        editor.DeleteSelected();
        int deletedAfter = points.Count(point => (point.label & DeletedBit) != 0);
        editor.RestoreDeleted();
        int deletedRestored = points.Count(point => (point.label & DeletedBit) != 0);
        editor.ClearSelection();
        bool deletionWorks = deletedAfter == 64 && deletedRestored == 0;
        Record("F4-EDIT-DELETE-RESTORE", deletionWorks ? "PASS" : "FAIL",
            $"deleted={deletedAfter}; after_restore={deletedRestored}; content_revision={renderer.ContentRevision}");

        string outputPath = Path.Combine(artifactRoot, "workflow-roundtrip-" + runId + ".ply");
        Task export = editor.ExportLabeledPointsAsync(outputPath, true, CancellationToken.None);
        yield return WaitForTask(export, 90f);
        bool exported = File.Exists(outputPath) && !export.IsFaulted;
        int outputCount = exported ? PointCloudPlyReader.Validate(outputPath) : -1;
        bool originalUnchanged = HashFile(source) == sourceHash;
        bool expectedExport = exported && outputCount == points.Length && HashPositions(points) == originalPositions;
        bool reload = false;
        if (exported) yield return LoadAndWait(outputPath, outputCount, ok => reload = ok);
        bool xyzRoundTrip = reload && HashPositions(renderer.GetPointData()) == originalPositions;
        Record("F4-SAVE-RELOAD-PLY", exported && originalUnchanged && expectedExport && xyzRoundTrip ? "PASS" : "FAIL",
            $"exported={exported}; points={outputCount}; source_unchanged={originalUnchanged}; " +
            $"source_xyz_stable={expectedExport}; reload={reload}; xyz_roundtrip={xyzRoundTrip}; output={outputPath}");

        yield return RunSphereAndDownsample();
        yield return RunC2C();

        bool stemLoaded = false;
        yield return LoadAndWait(Fixture("stem12mm_100k.ply"), 100_000, ok => stemLoaded = ok);
        if (!stemLoaded) throw new InvalidOperationException("Could not load the synthetic stem cloud.");
        yield return RunStemDiameterAndCancellation();

        bool continued = false;
        yield return LoadAndWait(source, 100_000, ok => continued = ok);
        bool canContinue = continued && PathsEqual(loader.CurrentFilePath, source) &&
            renderer.GetPointData()?.Length == 100_000 && renderer.IsOctreeReady;
        Record("F4-POST-ANALYSIS-RELOAD", canContinue ? "PASS" : "FAIL",
            $"loaded={continued}; current={loader.CurrentFilePath}; points={renderer.GetPointData()?.Length}; " +
            $"generation={renderer.DatasetGeneration}; octree={renderer.IsOctreeReady}");
        if (!classified || !undoRestored || !redoRestored || !deletionWorks || !originalUnchanged || !xyzRoundTrip || !canContinue)
            Environment.ExitCode = 1;
        yield return RunNoisePreviewCommitUndoRedo();
        yield return RunPlyExportMatrix(source);
        yield return RunMeasurementPersistence(source);
        yield return RunBoundaryAndSameNameCases();
        bool restoredSourceAfterBoundary = false;
        yield return LoadAndWait(source, 100_000, ok => restoredSourceAfterBoundary = ok);
        bool sourceStillUnchanged = HashFile(source) == sourceHash;
        bool dataSpaceStillUnchanged = restoredSourceAfterBoundary && HashPositions(renderer.GetPointData()) == originalPositions &&
            HashLabels(renderer.GetPointData()) == originalLabelHash;
        Record("F4-E2E-POST-CROSS-FUNCTION-STATE", sourceStillUnchanged && dataSpaceStillUnchanged ? "PASS" : "FAIL",
            $"source_sha256_unchanged={sourceStillUnchanged}; coordinates_match_start={HashPositions(renderer.GetPointData()) == originalPositions}; " +
            $"labels_match_start={HashLabels(renderer.GetPointData()) == originalLabelHash}; source_restored_after_boundary={restoredSourceAfterBoundary}; " +
            $"current_path={loader.CurrentFilePath}; " +
            $"generation={renderer.DatasetGeneration}; revision={renderer.ContentRevision}");
        if (!sourceStillUnchanged || !dataSpaceStillUnchanged) Environment.ExitCode = 1;
        yield return RunEditPersistenceStress();
    }

    private IEnumerator RunAlternateWorkflow()
    {
        yield return WaitForInitialCloud(90f);
        string source = Fixture("cloud_A_100k.ply");
        bool loaded = false;
        yield return LoadAndWait(source, 100_000, ok => loaded = ok);
        if (!loaded) throw new InvalidOperationException("Could not load the alternate E2E fixture.");

        yield return RunMeasurementPersistence(source);
        yield return RunNoisePreviewCommitUndoRedo();
        yield return RunC2C();
        yield return RunSphereAndDownsample();
        yield return RunBoundaryAndSameNameCases();

        bool stemLoaded = false;
        yield return LoadAndWait(Fixture("stem12mm_100k.ply"), 100_000, ok => stemLoaded = ok);
        if (stemLoaded) yield return RunStemDiameterAndCancellation();
        else Record("F4-E2E-ORDER2-STEM-LOAD", "FAIL", "Could not load stem fixture after prior operations.");

        bool restored = false;
        yield return LoadAndWait(source, 100_000, ok => restored = ok);
        bool hasMeasurement = restored && editor.IsMeasurementDocumentReady &&
            !editor.HasMeasurementFingerprintMismatch && editor.MeasurementRecords.Count == 1;
        Record("F4-E2E-ORDER2-RESTORE", hasMeasurement ? "PASS" : "FAIL",
            $"loaded={restored}; path={loader.CurrentFilePath}; points={renderer.GetPointData()?.Length}; " +
            $"measurement_count={editor.MeasurementRecords.Count}; measurement_ready={editor.IsMeasurementDocumentReady}; " +
            $"fingerprint_mismatch={editor.HasMeasurementFingerprintMismatch}; generation={renderer.DatasetGeneration}; " +
            $"revision={renderer.ContentRevision}; xyz_sha256={HashPositions(renderer.GetPointData())}");
        if (!hasMeasurement) Environment.ExitCode = 1;
    }

    private IEnumerator RunNoisePreviewCommitUndoRedo()
    {
        NoiseFilterUI noiseUI = FindAnyObjectByType<NoiseFilterUI>();
        NoiseFilterManager manager = NoiseFilterManager.Instance;
        if (noiseUI == null || renderer == null)
        {
            Record("F4-NOISE-PRODUCTION-ANALYSIS", "FAIL", "NoiseFilterUI or renderer missing.");
            Environment.ExitCode = 1;
            yield break;
        }

        string sourcePath = loader.CurrentFilePath;
        string sourceSha = HashFile(sourcePath);
        long generation = renderer.DatasetGeneration;
        long revision = renderer.ContentRevision;
        noiseUI.RunNoiseFilterAnalysis();
        float deadline = Time.realtimeSinceStartup + 360f;
        while (PointCloudProgressManager.Instance.IsRunning && Time.realtimeSinceStartup < deadline) yield return null;
        NoiseFilterResult result = manager.CurrentResult;
        bool analyzed = result != null && result.HasValidArrayLengths(renderer.GetPointData()?.Length ?? 0) &&
            renderer.DatasetGeneration == generation && renderer.ContentRevision >= revision &&
            !PointCloudProgressManager.Instance.IsRunning && manager.IsPreviewActive;
        int actualCandidates = result?.previewMask?.Count(value => value != 0) ?? 0;
        Record("F4-NOISE-PRODUCTION-ANALYSIS", analyzed ? "PASS" : "FAIL",
            $"entry=NoiseFilterUI.RunNoiseFilterAnalysis (not physical GUI); source={sourcePath}; " +
            $"source_sha256={sourceSha}; result_points={result?.pointCount ?? -1}; backend_candidates={actualCandidates}; " +
            $"preview_active={manager.IsPreviewActive}; generation={renderer.DatasetGeneration}; revision={renderer.ContentRevision}; " +
            $"status={PointCloudProgressManager.Instance.OperationStatus}");
        if (!analyzed)
        {
            Environment.ExitCode = 1;
            yield break;
        }

        PointData[] points = renderer.GetPointData();
        bool injectedPreview = actualCandidates == 0;
        if (injectedPreview)
        {
            for (int i = 0; i < Math.Min(16, result.pointCount); i++)
            {
                result.previewMask[i] = 1;
                result.previewReason[i] = (int)RemovalReason.Manual;
            }
            if (!manager.ApplyPreview(renderer))
            {
                Record("F4-NOISE-PREVIEW-COMMIT-UNDO-REDO", "FAIL", "Could not apply deterministic preview candidate mask.");
                Environment.ExitCode = 1;
                yield break;
            }
        }
        int candidateCount = points.Count(point => (point.label & NoiseFilterManager.NOISE_CANDIDATE_BIT) != 0);
        bool previewCorrect = manager.IsPreviewActive && candidateCount > 0;
        bool committed = previewCorrect && manager.CommitRemoval(renderer);
        int hiddenAfterCommit = points.Count(point => (point.label & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0);
        bool undone = committed && manager.Undo(renderer);
        int candidatesAfterUndo = points.Count(point => (point.label & NoiseFilterManager.NOISE_CANDIDATE_BIT) != 0);
        bool redone = undone && manager.Redo(renderer);
        int hiddenAfterRedo = points.Count(point => (point.label & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0);
        bool stateCorrect = previewCorrect && committed && hiddenAfterCommit == candidateCount && undone &&
            candidatesAfterUndo == candidateCount && redone && hiddenAfterRedo == candidateCount &&
            HashFile(sourcePath) == sourceSha;
        Record("F4-NOISE-PREVIEW-COMMIT-UNDO-REDO", stateCorrect ? "PASS" : "FAIL",
            $"preview_candidates={candidateCount}; backend_candidates={actualCandidates}; deterministic_candidates_injected={injectedPreview}; " +
            $"commit={committed}; hidden_after_commit={hiddenAfterCommit}; undo={undone}; candidates_after_undo={candidatesAfterUndo}; " +
            $"redo={redone}; hidden_after_redo={hiddenAfterRedo}; source_unchanged={HashFile(sourcePath) == sourceSha}; " +
            $"dataset_generation={renderer.DatasetGeneration}; content_revision={renderer.ContentRevision}");
        if (!stateCorrect) Environment.ExitCode = 1;
    }

    private IEnumerator RunPlyExportMatrix(string source)
    {
        PointData[] points = renderer.GetPointData();
        string sourceSha = HashFile(source);
        int[] originalLabels = points.Select(point => point.label).ToArray();
        int[] expectedVisibleLabels = (int[])originalLabels.Clone();
        for (int i = 0; i < points.Length; i++)
            points[i].label &= ~(SelectedBit | DeletedBit | NoiseFilterManager.NOISE_HIDDEN_BIT | NoiseFilterManager.NOISE_CANDIDATE_BIT);
        for (int i = 0; i < 10; i++) points[i].label |= SelectedBit;
        for (int i = 10; i < 12; i++) points[i].label |= SelectedBit | DeletedBit;
        for (int i = 12; i < 14; i++) points[i].label |= SelectedBit | NoiseFilterManager.NOISE_HIDDEN_BIT;
        renderer.TryUpdatePointBuffer();

        string outputDirectory = Path.Combine(artifactRoot, "e2e-ply-" + runId);
        Directory.CreateDirectory(outputDirectory);
        var cases = new[]
        {
            new { Name = "all-visible-ascii", Mode = ExportPointMode.AllVisible, Binary = false, Calibrated = false },
            new { Name = "all-visible-binary-calibrated", Mode = ExportPointMode.AllVisible, Binary = true, Calibrated = true },
            new { Name = "selected-visible-binary", Mode = ExportPointMode.SelectedVisible, Binary = true, Calibrated = false },
            new { Name = "selected-nondeleted-ascii", Mode = ExportPointMode.SelectedNonDeleted, Binary = false, Calibrated = false },
            new { Name = "cleaned-visible-binary", Mode = ExportPointMode.CleanedVisible, Binary = true, Calibrated = false }
        };
        bool allPassed = true;
        foreach (var item in cases)
        {
            string path = Path.Combine(outputDirectory, item.Name + ".ply");
            PlyExportResult written = null;
            Exception failure = null;
            try
            {
                written = PlyExportService.Write(new PlyExportRequest(points, path, item.Binary, item.Calibrated, item.Mode),
                    CancellationToken.None);
            }
            catch (Exception exception) { failure = exception; }

            PointData[] expected = points.Where(point => PlyExportService.IsIncluded(point.label, item.Mode)).ToArray();
            bool decoded = written != null && File.Exists(path) && PointCloudPlyReader.Validate(path) == expected.Length;
            bool marker = decoded && PointCloudPlyReader.HasScaleCalibrationMarker(path) == item.Calibrated;
            bool values = false;
            if (decoded)
            {
                PlyVertex[] actual = PointCloudPlyReader.Read(path, Math.Max(1, expected.Length)).Vertices;
                values = actual.Length == expected.Length;
                for (int i = 0; values && i < actual.Length; i++)
                {
                    PointData point = expected[i];
                    uint color = point.originalColor;
                    values = actual[i].X == point.position.x && actual[i].Y == point.position.y && actual[i].Z == point.position.z &&
                        actual[i].Red == (byte)(color & 0xff) && actual[i].Green == (byte)((color >> 8) & 0xff) &&
                        actual[i].Blue == (byte)((color >> 16) & 0xff) && actual[i].Label == (point.label & 0xff);
                }
            }
            bool passed = decoded && marker && values && written.VertexCount == expected.Length;
            allPassed &= passed;
            Record("F4-PLY-" + item.Name.ToUpperInvariant(), passed ? "PASS" : "FAIL",
                $"mode={item.Mode}; binary={item.Binary}; calibrated_marker={item.Calibrated}; points={expected.Length}; " +
                $"header_marker_ok={marker}; xyz_rgb_classlabel_exact={values}; path={path}; error={failure?.ToString() ?? "none"}");
        }

        int[] labelValues = points.Select(point => point.label).ToArray();
        bool labelBitsRemainTransient = labelValues.Length == expectedVisibleLabels.Length &&
            labelValues.Where((label, index) => (label & 0xff) != (expectedVisibleLabels[index] & 0xff)).Any() == false;
        for (int i = 0; i < points.Length; i++) points[i].label = originalLabels[i];
        bool bufferRestored = renderer.TryUpdatePointBuffer();
        bool sourceStable = File.Exists(source) && HashFile(source) == sourceSha;
        Record("F4-PLY-TRANSIENT-BITS-AND-RESTORE", allPassed && labelBitsRemainTransient && bufferRestored && sourceStable ? "PASS" : "FAIL",
            $"low_class_bits_unchanged={labelBitsRemainTransient}; in_memory_labels_restored={bufferRestored}; source_sha256_unchanged={sourceStable}; " +
            $"label_sha256={HashLabels(points)}; source={source}");
        if (!allPassed || !labelBitsRemainTransient || !bufferRestored || !sourceStable) Environment.ExitCode = 1;
        yield return null;
    }

    private IEnumerator RunMeasurementPersistence(string source)
    {
        string sourceSha = HashFile(source);
        float deadline = Time.realtimeSinceStartup + 60f;
        while ((!editor.IsMeasurementDocumentReady || editor.IsMeasurementFingerprintPending) &&
               Time.realtimeSinceStartup < deadline) yield return null;
        bool ready = editor.IsMeasurementDocumentReady && !editor.HasMeasurementFingerprintMismatch;
        PointData[] points = renderer.GetPointData();
        int priorCount = editor.MeasurementRecords.Count;
        bool began = ready && editor.BeginNewMeasurement(PointCloudEditor.MeasurementMode.TwoPoint);
        if (began)
        {
            editor.measurementPath.AddPoint(points[0].position);
            editor.measurementPath.AddPoint(points[1].position);
        }
        bool finished = began && editor.FinishMeasurement();
        MeasurementRecord record = editor.SelectedMeasurement;
        string measurementId = record?.id;
        MeasurementResult result = record != null ? editor.GetMeasurementResult(record) : null;
        float expectedMm = Vector3.Distance(points[0].position, points[1].position) * renderer.DisplayScale;
        string sidecar = MeasurementDocumentStore.GetSidecarPath(source);
        bool sidecarValid = finished && File.Exists(sidecar) && File.ReadAllText(sidecar).Contains(record.id) &&
            File.ReadAllText(sidecar).Contains("sourceSha256");
        bool resultValid = result != null && Math.Abs(result.length_mm - expectedMm) < 0.001f &&
            Math.Abs(result.chord_length_mm - expectedMm) < 0.001f && result.point_count == 2;
        string csv = string.Empty;
        bool csvValid = false;
        try
        {
            csv = editor.ExportMeasurementResultsCsv();
            string[] rows = File.ReadAllLines(csv);
            string[] header = rows.Length > 0 ? rows[0].TrimStart('\uFEFF').Split(',') : Array.Empty<string>();
            string measurementRow = rows.Skip(1).FirstOrDefault(row => row.Split(',')[0] == measurementId);
            string[] values = measurementRow != null ? measurementRow.Split(',') : Array.Empty<string>();
            csvValid = rows.Length == priorCount + 2 &&
                header.SequenceEqual(new[] { "measurement_id", "name", "mode", "length_mm", "chord_length_mm", "point_count" }) &&
                values.Length == 6 && values[0] == measurementId && values[2] == "two_point" &&
                float.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float csvLength) &&
                float.TryParse(values[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float csvChord) &&
                int.TryParse(values[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int csvPointCount) &&
                Math.Abs(csvLength - expectedMm) < 0.001f && Math.Abs(csvChord - expectedMm) < 0.001f && csvPointCount == 2;
        }
        catch (Exception exception) { Record("F4-MEASUREMENT-CSV", "FAIL", exception.ToString()); }
        bool passed = ready && finished && sidecarValid && resultValid && csvValid;
        Record("F4-MEASUREMENT-JSON-CSV", passed ? "PASS" : "FAIL",
            $"created={finished}; prior_count={priorCount}; count={editor.MeasurementRecords.Count}; sidecar={sidecar}; " +
            $"sidecar_fingerprint_and_id={sidecarValid}; expected_mm={expectedMm:R}; actual_mm={result?.length_mm ?? -1:R}; " +
            $"csv_valid={csvValid}; csv={csv}; cloud_sha256={HashFile(source)}; cloud_id={editor.MeasurementRecords.LastOrDefault()?.id ?? "none"}");
        if (!passed) Environment.ExitCode = 1;

        string calibratedDirectory = Path.Combine(artifactRoot, "calibrated-" + runId);
        Task<string> calibrationTask = editor.ApplyScaleCalibrationAndSaveAsync(0.5f, calibratedDirectory, CancellationToken.None);
        yield return WaitForTask(calibrationTask, 120f);
        string calibratedPly = calibrationTask.Result;
        PointCloudPlyData calibratedData = PointCloudPlyReader.Read(calibratedPly, points.Length);
        bool coordinatesCorrected = calibratedData.Vertices.Length == points.Length;
        for (int i = 0; coordinatesCorrected && i < points.Length; i++)
        {
            Vector3 expected = points[i].position * 0.5f;
            PlyVertex actual = calibratedData.Vertices[i];
            coordinatesCorrected = actual.X == expected.x && actual.Y == expected.y && actual.Z == expected.z;
        }
        bool calibratedOk = calibratedData.ScaleCalibrated && coordinatesCorrected && HashFile(source) == sourceSha;
        Record("F4-SCALE-CALIBRATED-PLY", calibratedOk ? "PASS" : "FAIL",
            $"known_synthetic_factor=0.5; marker={calibratedData.ScaleCalibrated}; count={calibratedData.Vertices.Length}; " +
            $"coordinates_multiplied_once={coordinatesCorrected}; source_unchanged={HashFile(source) == sourceSha}; " +
            $"output={calibratedPly}; sidecar={MeasurementDocumentStore.GetSidecarPath(calibratedPly)}; " +
            $"sidecar_warning={(string.IsNullOrEmpty(editor.LastCalibrationSidecarWarning) ? "none" : editor.LastCalibrationSidecarWarning)}");
        if (!calibratedOk) Environment.ExitCode = 1;

        bool switched = false;
        yield return LoadAndWait(Fixture("cloud_B_100k.ply"), 100_000, ok => switched = ok);
        deadline = Time.realtimeSinceStartup + 60f;
        while (switched && (!editor.IsMeasurementDocumentReady || editor.IsMeasurementFingerprintPending) &&
               Time.realtimeSinceStartup < deadline) yield return null;
        bool switchedIsolated = switched && editor.IsMeasurementDocumentReady && editor.MeasurementRecords.Count == 0;
        bool returned = false;
        yield return LoadAndWait(source, 100_000, ok => returned = ok);
        deadline = Time.realtimeSinceStartup + 60f;
        while ((!editor.IsMeasurementDocumentReady || editor.IsMeasurementFingerprintPending) &&
               Time.realtimeSinceStartup < deadline) yield return null;
        MeasurementRecord reloadedRecord = editor.MeasurementRecords.FirstOrDefault(item => item.id == measurementId);
        MeasurementResult reloadedResult = reloadedRecord != null ? editor.GetMeasurementResult(reloadedRecord) : null;
        bool returnedMeasurement = returned && editor.IsMeasurementDocumentReady && !editor.HasMeasurementFingerprintMismatch &&
            editor.MeasurementRecords.Count == priorCount + 1 && reloadedRecord != null && reloadedResult != null &&
            Math.Abs(reloadedResult.length_mm - expectedMm) < 0.001f && reloadedResult.point_count == 2;
        Record("F4-MEASUREMENT-CLOUD-SWITCH-RELOAD", switchedIsolated && returnedMeasurement ? "PASS" : "FAIL",
            $"B_loaded={switched}; B_measurements_isolated={switchedIsolated}; A_returned={returned}; " +
            $"A_measurement_restored={returnedMeasurement}; selection_restored={editor.SelectedMeasurementId == measurementId}; " +
            $"record_id_restored={reloadedRecord?.id ?? "none"}; result_mm={reloadedResult?.length_mm ?? -1:R}; A_path={loader.CurrentFilePath}; " +
            $"A_json={MeasurementDocumentStore.GetSidecarPath(source)}; B_json={MeasurementDocumentStore.GetSidecarPath(Fixture("cloud_B_100k.ply"))}");
        if (!switchedIsolated || !returnedMeasurement) Environment.ExitCode = 1;
    }

    private IEnumerator RunBoundaryAndSameNameCases()
    {
        string boundary = Path.Combine(fixtureRoot, "boundary");
        var expectedCounts = new Dictionary<string, int> { ["zero_points.ply"] = 0, ["one_point.ply"] = 1 };
        bool countsValid = true;
        foreach (var pair in expectedCounts)
        {
            int actual = PointCloudPlyReader.Validate(Path.Combine(boundary, pair.Key));
            countsValid &= actual == pair.Value;
        }
        string[] invalidFiles = { "malformed_header.ply", "truncated_binary.ply", "non_finite.ply" };
        bool invalidRejected = invalidFiles.All(name => ThrowsPlyValidation(Path.Combine(boundary, name)));
        Record("F4-PLY-BOUNDARY", countsValid && invalidRejected ? "PASS" : "FAIL",
            $"zero_and_one_valid={countsValid}; malformed_truncated_nonfinite_rejected={invalidRejected}; " +
            $"zero=0; one=1; invalid={string.Join(",", invalidFiles)}");

        string duplicateA = Fixture(Path.Combine("same-name-A", "duplicate.ply"));
        string duplicateB = Fixture(Path.Combine("same-name-B", "duplicate.ply"));
        MeasurementDocument fixtureDoc = MeasurementDocumentStore.Create(duplicateA);
        fixtureDoc.sourceSha256 = MeasurementDocumentStore.ComputeSha256(duplicateA);
        fixtureDoc.measurements.Add(new MeasurementRecord
        {
            id = Guid.NewGuid().ToString("N"), name = "QA-only", mode = (int)PointCloudEditor.MeasurementMode.TwoPoint,
            points = new List<Vector3> { Vector3.zero, Vector3.right }
        });
        MeasurementDocumentStore.Save(duplicateA, fixtureDoc);
        string hashA = HashFile(duplicateA);
        string hashB = HashFile(duplicateB);
        bool loadedA = false;
        yield return LoadAndWait(duplicateA, 1_000, ok => loadedA = ok);
        bool aSidecarBound = loadedA && editor.MeasurementRecords.Count == 1 && !editor.HasMeasurementFingerprintMismatch;
        bool loadedB = false;
        yield return LoadAndWait(duplicateB, 1_000, ok => loadedB = ok);
        float deadline = Time.realtimeSinceStartup + 60f;
        while ((!editor.IsMeasurementDocumentReady || editor.IsMeasurementFingerprintPending) &&
               Time.realtimeSinceStartup < deadline) yield return null;
        bool bNotStale = loadedB && hashA != hashB && editor.IsMeasurementDocumentReady &&
            !editor.HasMeasurementFingerprintMismatch && editor.MeasurementRecords.Count == 0;
        Record("F4-SAME-NAME-DIFFERENT-PATH", aSidecarBound && bNotStale ? "PASS" : "FAIL",
            $"same_filename={Path.GetFileName(duplicateA) == Path.GetFileName(duplicateB)}; different_sha256={hashA != hashB}; " +
            $"A_sidecar_bound={aSidecarBound}; B_old_result_not_applied={bNotStale}; A={hashA}; B={hashB}; " +
            $"current_path={loader.CurrentFilePath}; records={editor.MeasurementRecords.Count}");
        if (!countsValid || !invalidRejected || !aSidecarBound || !bNotStale) Environment.ExitCode = 1;
    }

    private static bool ThrowsPlyValidation(string path)
    {
        try { PointCloudPlyReader.Validate(path); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private void RecordCloudState(string id, string path, PointData[] points)
    {
        int selected = points.Count(point => (point.label & SelectedBit) != 0);
        int deleted = points.Count(point => (point.label & DeletedBit) != 0);
        int noiseHidden = points.Count(point => (point.label & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0);
        int classified = points.Count(point => (point.label & 0xff) != 0);
        int visible = points.Length - deleted - noiseHidden;
        Record(id, "PASS", $"point_count={points.Length}; visible_count={visible}; selected_count={selected}; deleted_count={deleted}; " +
            $"classified_count={classified}; noise_hidden_count={noiseHidden}; labels_sha256={HashLabels(points)}; " +
            $"xyz_sha256={HashPositions(points)}; source_sha256={HashFile(path)}; dataset_generation={renderer.DatasetGeneration}; " +
            $"content_revision={renderer.ContentRevision}; path={loader.CurrentFilePath}");
    }

    private IEnumerator RunEditPersistenceStress()
    {
        string source = Fixture("edit_1k.ply");
        string sourceHash = HashFile(source);
        bool loaded = false;
        yield return LoadAndWait(source, 1_000, ok => loaded = ok);
        if (!loaded) throw new InvalidOperationException("Could not load the 1,000-point edit stress fixture.");

        PointData[] points = renderer.GetPointData();
        System.Diagnostics.Stopwatch editTime = System.Diagnostics.Stopwatch.StartNew();
        bool editsValid = true;
        int classificationFailures = 0;
        for (int i = 0; i < 1_000; i++)
        {
            int index = i % points.Length;
            PointData point = points[index];
            point.label |= SelectedBit;
            points[index] = point;
            editor.activeLabelClass = (i % 8) + 1;
            editor.AssignLabelToSelected();
            if ((points[index].label & 0xff) != editor.activeLabelClass || (points[index].label & SelectedBit) != 0)
            {
                editsValid = false;
                classificationFailures++;
            }
            if ((i + 1) % 50 == 0) yield return null;
        }
        editTime.Stop();

        int[] beforeUndo = points.Select(point => point.label).ToArray();
        System.Diagnostics.Stopwatch historyTime = System.Diagnostics.Stopwatch.StartNew();
        bool historyValid = true;
        const int historyCapacity = 5;
        const int historyCycles = 40;
        int undoCalls = 0;
        int redoCalls = 0;
        for (int cycle = 0; cycle < historyCycles; cycle++)
        {
            for (int i = 0; i < historyCapacity; i++)
            {
                bool applied = editor.AnnotationUndo();
                historyValid &= applied;
                if (applied) undoCalls++;
            }
            for (int i = 0; i < historyCapacity; i++)
            {
                bool applied = editor.AnnotationRedo();
                historyValid &= applied;
                if (applied) redoCalls++;
            }
            if ((cycle + 1) % 5 == 0) yield return null;
        }
        historyTime.Stop();
        bool historyRoundTrip = beforeUndo.SequenceEqual(points.Select(point => point.label));

        string output = Path.Combine(artifactRoot, "edit-stress-roundtrip-" + runId + ".ply");
        int[] expectedLabels = points.Select(point => point.label).ToArray();
        string expectedPositions = HashPositions(points);
        bool persistence = true;
        System.Diagnostics.Stopwatch persistenceTime = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            Task export = editor.ExportLabeledPointsAsync(output, true, CancellationToken.None);
            yield return WaitForTask(export, 30f);
            bool exported = File.Exists(output) && PointCloudPlyReader.Validate(output) == points.Length;
            bool reloaded = false;
            if (exported) yield return LoadAndWait(output, points.Length, ok => reloaded = ok);
            PointData[] reloadedPoints = renderer.GetPointData();
            bool roundTrip = reloaded && reloadedPoints != null && HashPositions(reloadedPoints) == expectedPositions &&
                expectedLabels.SequenceEqual(reloadedPoints.Select(point => point.label));
            persistence &= exported && roundTrip;
            if (!exported || !roundTrip)
            {
                Record("F4-SAVE-RELOAD-100", "FAIL",
                    $"iteration={i + 1}; exported={exported}; reloaded={reloaded}; roundtrip={roundTrip}; output={output}");
                break;
            }
        }
        persistenceTime.Stop();
        bool sourceUnchanged = HashFile(source) == sourceHash;
        Record("F4-EDIT-1000-UNDO-REDO-200", editsValid && historyValid && historyRoundTrip ? "PASS" : "FAIL",
            $"edits=1000; classification_failures={classificationFailures}; edit_elapsed_ms={editTime.ElapsedMilliseconds}; " +
            $"undo_calls={undoCalls}/200; redo_calls={redoCalls}/200; history_capacity={historyCapacity}; " +
            $"history_elapsed_ms={historyTime.ElapsedMilliseconds}; undo_redo_roundtrip={historyRoundTrip}; " +
            $"content_revision={renderer.ContentRevision}; {RuntimeMemorySummary()}");
        Record("F4-SAVE-RELOAD-100", persistence && sourceUnchanged ? "PASS" : "FAIL",
            $"cycles={(persistence ? "100" : "<100")}; elapsed_ms={persistenceTime.ElapsedMilliseconds}; " +
            $"source_unchanged={sourceUnchanged}; points={renderer.GetPointData()?.Length}; output={output}; " +
            RuntimeMemorySummary());
        if (!editsValid || !historyValid || !historyRoundTrip || !persistence || !sourceUnchanged) Environment.ExitCode = 1;
    }

    private IEnumerator RunSphereAndDownsample()
    {
        string spherePath = Fixture("sphere60mm_4096.ply");
        string sphereJson = Path.Combine(artifactRoot, "sphere-" + runId + ".json");
        ReferenceSphereOutput sphereResult = null;
        Exception sphereFailure = null;
        using (CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            Task<ReferenceSphereOutput> task = PythonBridge.RunReferenceSphereAsync(
                spherePath, sphereJson, 8, 2.5f, null, cancellation.Token);
            yield return WaitForTask(task, 180f);
            if (task.IsFaulted) sphereFailure = task.Exception;
            else if (task.IsCompletedSuccessfully) sphereResult = task.Result;
        }
        bool sphereOk = sphereResult != null && Math.Abs(sphereResult.diameter - 0.05f) < 0.001f &&
            Math.Abs(renderer.DataLengthToMillimeters(sphereResult.diameter) - 60f) < 1.2f && File.Exists(sphereJson);
        Record("F4-PY-REFERENCE-SPHERE", sphereOk ? "PASS" : "FAIL",
            sphereResult != null
                ? $"diameter_data={sphereResult.diameter:R}; diameter_mm={renderer.DataLengthToMillimeters(sphereResult.diameter):R}; " +
                  $"component={sphereResult.component_point_count}; inliers={sphereResult.fit_inlier_count}; json={sphereJson}"
                : "missing result; error=" + (sphereFailure != null ? sphereFailure.ToString() : "timeout"));

        string inputDirectory = Path.Combine(artifactRoot, "downsample-input-" + runId);
        string outputDirectory = Path.Combine(artifactRoot, "downsample-output-" + runId);
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);
        File.Copy(spherePath, Path.Combine(inputDirectory, "input.ply"));
        string outputPly = Path.Combine(outputDirectory, "downsampled.ply");
        bool returned = false;
        Exception downsampleFailure = null;
        using (CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
        {
            Task<bool> task = PythonBridge.RunDownsamplingAsync(inputDirectory, outputDirectory, 2f,
                renderer.DisplayScale, outputPly, null, 1, cancellation.Token);
            yield return WaitForTask(task, 300f);
            returned = task.IsCompletedSuccessfully && task.Result;
            if (task.IsFaulted) downsampleFailure = task.Exception;
        }
        int outputPoints = returned && File.Exists(outputPly) ? PointCloudPlyReader.Validate(outputPly) : -1;
        bool downsampleOk = returned && outputPoints > 0 && outputPoints < 4096;
        Record("F4-PY-DOWNSAMPLE", downsampleOk ? "PASS" : "FAIL",
            $"returned={returned}; input_points=4096; output_points={outputPoints}; input_hash={HashFile(spherePath)}; " +
            $"output={outputPly}; error={(downsampleFailure != null ? downsampleFailure.Message : "none")}");
        if (!sphereOk || !downsampleOk) Environment.ExitCode = 1;
    }

    private IEnumerator RunC2C()
    {
        PointCloudManager manager = FindAnyObjectByType<PointCloudManager>();
        if (manager == null)
        {
            Record("F4-C2C", "FAIL", "PointCloudManager missing from startup scene.");
            Environment.ExitCode = 1;
            yield break;
        }
        PointCloudRenderer previousReference = manager.referenceCloud;
        PointCloudRenderer previousAligned = manager.alignedCloud;
        bool previousCompared = GetField<bool>(manager, "hasCompared");
        GameObject referenceObject = new GameObject("FourthAuditC2CReference");
        GameObject alignedObject = new GameObject("FourthAuditC2CAligned");
        PointCloudRenderer reference = referenceObject.AddComponent<PointCloudRenderer>();
        PointCloudRenderer aligned = alignedObject.AddComponent<PointCloudRenderer>();
        Vector3[] referencePositions = { Vector3.zero, new Vector3(0.1f, 0f, 0f) };
        Vector3[] alignedPositions = { new Vector3(0f, 0.01f, 0f), new Vector3(0.1f, 0.01f, 0f) };
        Color[] colors = { Color.white, Color.white };
        reference.SetPointCloudData(referencePositions, colors);
        aligned.SetPointCloudData(alignedPositions, colors);
        manager.referenceCloud = reference;
        manager.alignedCloud = aligned;
        SetField(manager, "hasCompared", false);
        manager.CompareClouds();
        float deadline = Time.realtimeSinceStartup + 90f;
        while (!GetField<bool>(manager, "hasCompared") && Time.realtimeSinceStartup < deadline) yield return null;
        float average = GetField<float>(manager, "avgDistance");
        bool success = GetField<bool>(manager, "hasCompared") && Math.Abs(average - 12f) < 0.05f;
        Record("F4-C2C", success ? "PASS" : "FAIL",
            $"theoretical_mm=12; average_mm={average:R}; ref_generation={reference.DatasetGeneration}; " +
            $"aligned_generation={aligned.DatasetGeneration}");
        manager.referenceCloud = previousReference;
        manager.alignedCloud = previousAligned;
        SetField(manager, "hasCompared", previousCompared);
        Destroy(referenceObject);
        Destroy(alignedObject);
        if (!success) Environment.ExitCode = 1;
    }

    private IEnumerator RunStemDiameterAndCancellation()
    {
        StemDiameterUI ui = FindAnyObjectByType<StemDiameterUI>();
        if (ui == null) throw new InvalidOperationException("StemDiameterUI missing from startup scene.");
        Task started = InvokeStemAnalysis(ui);
        yield return WaitForTask(started, 120f);
        float deadline = Time.realtimeSinceStartup + 240f;
        StemDiameterResult result = null;
        while (Time.realtimeSinceStartup < deadline)
        {
            result = GetField<StemDiameterResult>(ui, "result");
            if (result != null && !PointCloudProgressManager.Instance.IsRunning) break;
            yield return null;
        }
        float[] diameters = result?.sections == null ? Array.Empty<float>() : result.sections
            .Where(section => section != null && section.diameter_5mm > 0f && !float.IsNaN(section.diameter_5mm))
            .Select(section => section.diameter_5mm).ToArray();
        float median = Median(diameters);
        StemDiameterVisualizer visualizer = FindAnyObjectByType<StemDiameterVisualizer>();
        bool overlayReady = visualizer != null && GetField<Material>(visualizer, "centerlineMaterial") != null &&
            GetField<Material>(visualizer, "sectionMaterial") != null;
        string analysisStatus = GetField<string>(ui, "status");
        bool success = result != null && result.sections != null && result.sections.Length > 10 &&
            diameters.Length > 0 && Math.Abs(median - 12f) < 1.5f && overlayReady &&
            !analysisStatus.Contains("解析結果を読み込めません");
        Record("F4-STEM-DIAMETER", success ? "PASS" : "FAIL",
            $"expected_5mm_slice_diameter_mm=12; median_mm={median:R}; valid_sections={diameters.Length}; " +
            $"sections={result?.sections?.Length ?? 0}; overlay_ready={overlayReady}; status={analysisStatus}");

        if (!success)
        {
            Environment.ExitCode = 1;
            yield break;
        }

        object previousOverlayRoot = GetField<object>(visualizer, "overlayRoot");
        Task cancelStarted = InvokeStemAnalysis(ui);
        yield return WaitForTask(cancelStarted, 120f);
        yield return new WaitForSecondsRealtime(0.75f);
        PointCloudProgressManager.Instance.Cancel();
        deadline = Time.realtimeSinceStartup + 45f;
        while ((PointCloudProgressManager.Instance.IsRunning || GetField<object>(ui, "process") != null) &&
               Time.realtimeSinceStartup < deadline)
            yield return null;
        StemDiameterResult afterCancel = GetField<StemDiameterResult>(ui, "result");
        bool overlayRetained = ReferenceEquals(previousOverlayRoot, GetField<object>(visualizer, "overlayRoot"));
        bool retained = ReferenceEquals(result, afterCancel) && overlayRetained;
        bool released = !PointCloudProgressManager.Instance.IsRunning && GetField<object>(ui, "process") == null;
        Record("F4-STEM-CANCEL-RETAIN", retained && released ? "PASS" : "FAIL",
            $"old_result_and_overlay_retained={retained}; overlay_retained={overlayRetained}; process_and_ui_released={released}; " +
            $"status={GetField<string>(ui, "status")}; progress={PointCloudProgressManager.Instance.OperationStatus}");

        Task retryStarted = InvokeStemAnalysis(ui);
        yield return WaitForTask(retryStarted, 120f);
        deadline = Time.realtimeSinceStartup + 240f;
        StemDiameterResult retryResult = null;
        while (Time.realtimeSinceStartup < deadline)
        {
            retryResult = GetField<StemDiameterResult>(ui, "result");
            if (retryResult != null && !PointCloudProgressManager.Instance.IsRunning && !ReferenceEquals(retryResult, result)) break;
            yield return null;
        }
        bool retryOk = retryResult != null && !PointCloudProgressManager.Instance.IsRunning;
        Record("F4-STEM-CANCEL-RETRY", retryOk ? "PASS" : "FAIL",
            $"retry_result={retryResult != null}; ui_released={!PointCloudProgressManager.Instance.IsRunning}; " +
            $"status={GetField<string>(ui, "status")}");
        if (!retained || !released || !retryOk) Environment.ExitCode = 1;
    }

    private IEnumerator WaitForInitialCloud(float timeoutSeconds)
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (loader == null) loader = FindAnyObjectByType<PointCloudLoader>();
            if (editor == null) editor = FindAnyObjectByType<PointCloudEditor>();
            if (renderer == null && loader != null) renderer = loader.targetRenderer;
            PointData[] data = renderer != null ? renderer.GetPointData() : null;
            if (loader != null && loader.SuccessfulLoadRevision > 0 && data != null && data.Length > 0 &&
                renderer.IsOctreeReady && !PointCloudProgressManager.Instance.IsRunning)
                yield break;
            yield return null;
        }
        throw new TimeoutException("Initial external point-cloud load did not finish.");
    }

    private IEnumerator LoadAndWait(string path, int expectedCount, Action<bool> completed)
    {
        float deadline = Time.realtimeSinceStartup + 120f;
        while (PointCloudProgressManager.Instance.IsRunning && Time.realtimeSinceStartup < deadline) yield return null;
        if (PointCloudProgressManager.Instance.IsRunning)
        {
            completed(false);
            yield break;
        }
        int oldRevision = loader.SuccessfulLoadRevision;
        long oldGeneration = renderer.DatasetGeneration;
        loader.LoadPointCloud(path);
        yield return WaitForRevision(oldRevision, 120f);
        float completionDeadline = Time.realtimeSinceStartup + 120f;
        while ((loader.SuccessfulLoadRevision <= oldRevision || renderer.DatasetGeneration <= oldGeneration ||
                !renderer.IsOctreeReady || PointCloudProgressManager.Instance.IsRunning) &&
               Time.realtimeSinceStartup < completionDeadline)
            yield return null;
        completed(PathsEqual(loader.CurrentFilePath, path) && renderer.GetPointData() != null &&
            renderer.GetPointData().Length == expectedCount && renderer.IsOctreeReady &&
            renderer.DatasetGeneration > oldGeneration && renderer.GetActiveDrawCount() > 0 &&
            renderer.GetActiveDrawCount() <= expectedCount &&
            !PointCloudProgressManager.Instance.IsRunning);
    }

    private IEnumerator WaitForRevision(int revision, float timeoutSeconds)
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (loader.SuccessfulLoadRevision <= revision && Time.realtimeSinceStartup < deadline) yield return null;
    }

    private IEnumerator WaitForOctree(float timeoutSeconds)
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (!renderer.IsOctreeReady && Time.realtimeSinceStartup < deadline) yield return null;
    }

    private IEnumerator WaitForTask(Task task, float timeoutSeconds)
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        if (!task.IsCompleted) throw new TimeoutException("Async task timed out after " + timeoutSeconds + " seconds.");
        if (task.IsFaulted) throw (Exception)task.Exception ?? new InvalidOperationException("Async task failed.");
        if (task.IsCanceled) throw new OperationCanceledException("Async task was unexpectedly canceled.");
    }

    private Task InvokeStemAnalysis(StemDiameterUI ui)
    {
        MethodInfo method = typeof(StemDiameterUI).GetMethod("StartAnalysisAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) throw new MissingMethodException(typeof(StemDiameterUI).FullName, "StartAnalysisAsync");
        return (Task)method.Invoke(ui, null);
    }

    private string Fixture(string name)
    {
        string path = Path.Combine(fixtureRoot, name);
        if (!File.Exists(path)) throw new FileNotFoundException("Required synthetic fixture is missing.", path);
        return path;
    }

    private void Record(string id, string result, string detail)
    {
        string safe = (detail ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        File.AppendAllText(reportPath, DateTime.UtcNow.ToString("o") + "\t" + id + "\t" + result + "\t" + safe + "\n", Encoding.UTF8);
        Debug.Log("[FourthAudit] " + id + " " + result + " " + safe);
    }

    private static Task InvokeStemAnalysis(object ui)
    {
        MethodInfo method = ui.GetType().GetMethod("StartAnalysisAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        return (Task)method.Invoke(ui, null);
    }

    private static bool PathsEqual(string a, string b)
    {
        return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool FirstPointMatches(PointData[] points, float x, float y, float z)
    {
        if (points == null || points.Length == 0) return false;
        Vector3 value = points[0].position;
        return Math.Abs(value.x - x) < 0.00001f && Math.Abs(value.y - y) < 0.00001f && Math.Abs(value.z - z) < 0.00001f;
    }

    private static string FormatFirstPoint(PointData[] points)
    {
        if (points == null || points.Length == 0) return "none";
        Vector3 value = points[0].position;
        return $"({value.x:R},{value.y:R},{value.z:R})";
    }

    private static string HashFile(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
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

    private static string HashLabels(PointData[] points)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] bytes = new byte[sizeof(int)];
            foreach (PointData point in points)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(point.label), 0, bytes, 0, bytes.Length);
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

    private static T GetField<T>(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null) throw new MissingFieldException(target.GetType().FullName, name);
        return (T)field.GetValue(target);
    }

    private static void SetField<T>(object target, string name, T value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null) throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static float Median(float[] values)
    {
        if (values == null || values.Length == 0) return float.NaN;
        float[] sorted = values.OrderBy(value => value).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) * 0.5f : sorted[mid];
    }
}
