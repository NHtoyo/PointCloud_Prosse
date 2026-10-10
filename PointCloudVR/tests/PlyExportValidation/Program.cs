using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using PointCloudWorkbench;

BoundedHistory<int> boundedHistory = new BoundedHistory<int>(3);
for (int i = 1; i <= 5; i++) boundedHistory.Push(i);
Assert(boundedHistory.Pop() == 5 && boundedHistory.Pop() == 4 && boundedHistory.Pop() == 3 &&
    boundedHistory.Count == 0, "bounded history retains newest states in LIFO order");
BoundedHistory<byte[]> byteBoundedHistory = new BoundedHistory<byte[]>(5, 8, bytes => bytes.LongLength);
Assert(byteBoundedHistory.Push(new byte[6]) && byteBoundedHistory.Push(new byte[4]) &&
    byteBoundedHistory.RetainedBytes == 4 && byteBoundedHistory.Count == 1,
    "history byte budget evicts oldest complete snapshots");
Assert(!byteBoundedHistory.Push(new byte[9]) && byteBoundedHistory.Count == 0,
    "oversized history snapshot is rejected without retaining excess memory");

TestResource currentResource = new();
TestResource publishedResource = currentResource;
TestResource failedCandidate = null;
bool commitCalled = false;
try
{
    TestResource candidate = AtomicResourceFactory.CreateInitialized(
        new[] { 1, 2, 3 },
        () => failedCandidate = new TestResource(),
        (_, _) => throw new InvalidOperationException("injected resource initialization failure"),
        resource => resource.Released = true);
    publishedResource = candidate;
    commitCalled = true;
}
catch (InvalidOperationException) { }
Assert(ReferenceEquals(publishedResource, currentResource) && !currentResource.Released &&
    failedCandidate != null && failedCandidate.Released && !commitCalled,
    "failed candidate resource initialization preserves the published resource and releases only the candidate");
TestResource successfulCandidate = AtomicResourceFactory.CreateInitialized(
    new[] { 4, 5 }, () => new TestResource(), (_, _) => { }, resource => resource.Released = true);
publishedResource = successfulCandidate;
currentResource.Released = true;
Assert(ReferenceEquals(publishedResource, successfulCandidate) && currentResource.Released && !successfulCandidate.Released,
    "successfully initialized resource can be published before releasing the previous resource");

BoundedTextBuffer boundedText = new BoundedTextBuffer(32, 8);
boundedText.Append("0123456789abcdefghijklmnopqrstuvwxyz");
Assert(boundedText.Length <= 32 && boundedText.TruncatedCharacters > 0 &&
    boundedText.ToString().StartsWith("01234567", StringComparison.Ordinal) &&
    boundedText.ToString().Contains("...[truncated", StringComparison.Ordinal) &&
    boundedText.ToString().EndsWith("uvwxyz", StringComparison.Ordinal),
    "bounded text buffer retains diagnostic head and tail within capacity");
boundedText.Clear();
Assert(boundedText.Length == 0 && boundedText.TruncatedCharacters == 0, "bounded text buffer can be reused after clear");

List<string> eventOrder = new();
Action<string> handlers = value => eventOrder.Add("first:" + value);
handlers += _ => throw new InvalidOperationException("injected subscriber failure");
handlers += value => eventOrder.Add("last:" + value);
int eventErrors = 0;
SafeEventDispatch.InvokeEach(handlers, "loaded", _ => eventErrors++);
Assert(eventErrors == 1 && eventOrder.SequenceEqual(new[] { "first:loaded", "last:loaded" }),
    "one failing event listener does not block later listeners");

NoiseFilterManager noiseManager = NoiseFilterManager.Instance;
PointCloudRenderer noiseRendererA = new(new PointData(0, 0, 0, 0, 0), new PointData(1, 0, 0, 0, 0));
NoiseFilterResult noiseResult = new NoiseFilterResult(2);
noiseResult.previewMask[0] = 1;
noiseResult.previewReason[0] = (int)RemovalReason.SOR;
noiseResult.removeMask[0] = 1;
noiseManager.ResetForPointCloud(noiseRendererA);
Assert(noiseManager.SetResult(noiseResult, noiseRendererA, noiseRendererA.DatasetGeneration, noiseRendererA.ContentRevision) &&
    noiseManager.ApplyPreview(noiseRendererA), "noise preview binds to its source renderer and revision");
noiseRendererA.ThrowNextBufferUpdate = true;
Assert(!noiseManager.CommitRemoval(noiseRendererA) &&
    (noiseRendererA.GetPointData()[0].label & NoiseFilterManager.NOISE_CANDIDATE_BIT) != 0 &&
    (noiseRendererA.GetPointData()[0].label & NoiseFilterManager.NOISE_HIDDEN_BIT) == 0 &&
    !noiseManager.CanUndo,
    "failed noise commit restores labels and does not publish history");
Assert(noiseManager.CommitRemoval(noiseRendererA) &&
    (noiseRendererA.GetPointData()[0].label & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0 && noiseManager.CanUndo,
    "noise commit records undo only after buffer update succeeds");
PointCloudRenderer noiseRendererSameCount = new(new PointData(2, 0, 0, 0, 0), new PointData(3, 0, 0, 0, 0));
noiseManager.ResetForPointCloud(noiseRendererSameCount);
Assert(!noiseManager.CanUndo && !noiseManager.Undo(noiseRendererSameCount) &&
    noiseRendererSameCount.GetPointData().All(point => point.label == 0),
    "noise history from a different same-sized point cloud is not applied");
PointCloudRenderer noiseRendererDifferentCount = new(new PointData(4, 0, 0, 0, 0));
noiseManager.ResetForPointCloud(noiseRendererDifferentCount);
Assert(!noiseManager.CanUndo && !noiseManager.Undo(noiseRendererDifferentCount) &&
    noiseRendererDifferentCount.GetPointData()[0].label == 0,
    "noise history from a different-sized point cloud is not applied");

PointCloudPoint3[] nearestPoints = new PointCloudPoint3[2000];
for (int i = 0; i < nearestPoints.Length - 1; i++) nearestPoints[i] = new PointCloudPoint3(i, 0, 0);
nearestPoints[nearestPoints.Length - 1] = new PointCloudPoint3(10000, 0, 0);
ExactNearestNeighbor3D nearestTree = new ExactNearestNeighbor3D(nearestPoints);
int nearestIndex = nearestTree.FindNearest(new PointCloudPoint3(15000.5f, 0, 0), out float nearestDistanceSquared);
Assert(nearestIndex == 1999 && Math.Abs(Math.Sqrt(nearestDistanceSquared) - 5000.5) < 0.001,
    "exact nearest-neighbor search finds an unsampled distant reference point");
Random nearestRandom = new Random(42);
PointCloudPoint3[] randomPoints = Enumerable.Range(0, 300)
    .Select(_ => new PointCloudPoint3((float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble()))
    .ToArray();
ExactNearestNeighbor3D randomTree = new ExactNearestNeighbor3D(randomPoints);
for (int q = 0; q < 50; q++)
{
    PointCloudPoint3 query = new PointCloudPoint3((float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble());
    int actualIndex = randomTree.FindNearest(query, out float actualDistanceSquared);
    float bruteForceDistanceSquared = randomPoints.Min(point =>
        (point.X - query.X) * (point.X - query.X) + (point.Y - query.Y) * (point.Y - query.Y) + (point.Z - query.Z) * (point.Z - query.Z));
    Assert(Math.Abs(actualDistanceSquared - bruteForceDistanceSquared) < 1e-6,
        "exact nearest-neighbor matches brute force for random cloud query " + q);
}
PointCloudPoint3[] extremeNearestPoints =
{
    new PointCloudPoint3(2e19f, 0f, 0f),
    new PointCloudPoint3(3e19f, 0f, 0f)
};
ExactNearestNeighbor3D extremeNearestTree = new ExactNearestNeighbor3D(extremeNearestPoints);
int extremeNearestIndex = extremeNearestTree.FindNearest(new PointCloudPoint3(0f, 0f, 0f), out float extremeDistanceSquared);
Assert(extremeNearestIndex == 0 && float.IsPositiveInfinity(extremeDistanceSquared),
    "nearest reference remains selectable when squared distance exceeds float range");
int exactExtremeNearestIndex = extremeNearestTree.FindNearest(new PointCloudPoint3(0f, 0f, 0f), out double exactExtremeDistanceSquared);
Assert(exactExtremeNearestIndex == 0 && double.IsFinite(exactExtremeDistanceSquared) &&
    Math.Abs(Math.Sqrt(exactExtremeDistanceSquared) - (double)2e19f) <= (double)2e19f * 1e-12,
    "double-precision nearest distance remains accurate when squared distance exceeds float range");
PointCloudPoint3[] benchmarkPoints = new PointCloudPoint3[50000];
PointCloudPoint3[] benchmarkQueries = new PointCloudPoint3[5000];
for (int i = 0; i < benchmarkPoints.Length; i++)
    benchmarkPoints[i] = new PointCloudPoint3((float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble());
for (int i = 0; i < benchmarkQueries.Length; i++)
    benchmarkQueries[i] = new PointCloudPoint3((float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble(), (float)nearestRandom.NextDouble());
System.Diagnostics.Stopwatch nearestBenchmark = System.Diagnostics.Stopwatch.StartNew();
ExactNearestNeighbor3D benchmarkTree = new ExactNearestNeighbor3D(benchmarkPoints);
long treeBuildMilliseconds = nearestBenchmark.ElapsedMilliseconds;
for (int i = 0; i < benchmarkQueries.Length; i++)
    benchmarkTree.FindNearest(benchmarkQueries[i], out float _);
nearestBenchmark.Stop();
Console.WriteLine($"BENCH ExactNearestNeighbor3D references={benchmarkPoints.Length} queries={benchmarkQueries.Length} build_ms={treeBuildMilliseconds} query_ms={nearestBenchmark.ElapsedMilliseconds - treeBuildMilliseconds} total_ms={nearestBenchmark.ElapsedMilliseconds}");

PointCloudProgressManager progress = PointCloudProgressManager.Instance;
PointCloudOperation operationA = progress.TryStart("test", "running");
Assert(operationA != null, "start operation A");
Assert(progress.TryStart("duplicate", "blocked") == null, "reject concurrent operation");
Assert(operationA.Update(0.4f, "working"), "current operation updates progress");
Assert(operationA.Fail("test", "recoverable failure", "full exception detail"), "current operation can fail");
PointCloudProgressSnapshot failed = progress.GetSnapshot();
Assert(!failed.IsRunning && failed.HasError && failed.NotificationMessage == "recoverable failure", "failure unlocks operation");
Assert(failed.Detail == "full exception detail", "failure detail retained");
PointCloudOperation operationB = progress.TryStart("next", "running");
Assert(operationB != null, "start after failure");
Assert(!operationA.Update(0.9f, "late A update") && !operationA.Fail("test", "late A failure"),
    "stale operation notifications cannot modify operation B");
Assert(progress.GetSnapshot().Title == "next" && progress.GetSnapshot().IsRunning && progress.GetSnapshot().Progress == 0f,
    "operation B state remains intact after stale A notifications");
progress.ShowError("validation", "validation message");
Assert(progress.IsRunning && !progress.HasError, "show error does not corrupt running operation");
Assert(operationB.Cancel() && operationB.IsCancellationRequested, "cancel request is scoped to operation B");
Assert(operationB.CompleteCancelled(), "cancelled operation B can finish");
Assert(!progress.IsRunning && !progress.HasError && progress.OperationStatus == PointCloudOperationStatus.Cancelled,
    "cancel is distinct from error");
PointCloudOperation operationC = progress.TryStart("after cancel", "running");
Assert(operationC != null && operationC.Complete(), "new operation starts after cancellation");
NoiseFilterResult completeNoiseResult = new NoiseFilterResult(3);
Assert(completeNoiseResult.HasValidArrayLengths(3), "noise result accepts matching point count and score arrays");
Assert(!completeNoiseResult.HasValidArrayLengths(4), "noise result rejects a different point count");
NoiseFilterResult malformedNoiseResult = new NoiseFilterResult(3, new byte[2], new byte[3], new byte[3],
    new float[3], new float[3], new int[3], new float[3], new float[3], new int[3], new int[3], new int[3]);
Assert(!malformedNoiseResult.HasValidArrayLengths(3), "noise result rejects any mismatched parallel array");
object boundCloud = new object();
Assert(!PointCloudRevisionBinding.RequiresReset(boundCloud, 4, 9, boundCloud, 4, 9),
    "noise binding preserves valid history while dataset revision is unchanged");
Assert(PointCloudRevisionBinding.RequiresReset(boundCloud, 4, 9, boundCloud, 4, 10),
    "noise binding invalidates prior undo/redo when edited dataset revision changes");
Assert(PointCloudRevisionBinding.RequiresReset(boundCloud, 4, 9, new object(), 4, 9),
    "noise binding invalidates history when renderer identity changes at equal point count and revision");
Assert(PointCloudRevisionBinding.RequiresReset(boundCloud, 4, 9, boundCloud, 5, 9),
    "noise binding invalidates history when dataset generation changes");

Random operationRandom = new Random(20261009);
for (int cycle = 0; cycle < 100; cycle++)
{
    PointCloudOperation current = progress.TryStart("randomized", "running");
    Assert(current != null, "random operation starts " + cycle);
    for (int step = 0; step < 10; step++)
    {
        if (operationRandom.Next(4) == 0) current.Cancel();
        else current.Update((float)operationRandom.NextDouble(), "random step " + step);
    }
    if (current.IsCancellationRequested) current.CompleteCancelled();
    else if (operationRandom.Next(2) == 0) current.Complete();
    else current.Fail("randomized", "injected failure");
    Assert(!progress.IsRunning, "random operation reaches terminal state " + cycle);
}

string recoveryDirectory = Path.Combine(Path.GetTempPath(), "pcwb-recovery-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(recoveryDirectory);
try
{
    string sourcePly = Path.Combine(recoveryDirectory, "source.ply");
    File.WriteAllText(sourcePly, "immutable source fixture");
    string sourceHash = PointCloudSessionRecoveryStore.ComputeFileSha256(sourcePly);
    string checkpoint = PointCloudSessionRecoveryStore.GetCheckpointPath(recoveryDirectory, sourcePly);
    string sameNameOtherPath = Path.Combine(recoveryDirectory, "other", "source.ply");
    Directory.CreateDirectory(Path.GetDirectoryName(sameNameOtherPath));
    File.WriteAllText(sameNameOtherPath, "different source with same filename");
    string sameNameOtherHash = PointCloudSessionRecoveryStore.ComputeFileSha256(sameNameOtherPath);
    Assert(PointCloudSessionRecoveryStore.GetCheckpointPath(recoveryDirectory, sameNameOtherPath) != checkpoint,
        "same-named source files use different recovery identities");
    int[] labels = { 0, 2, 0x10000, unchecked((int)0x80000201), -1 };
    PointCloudSessionRecoveryStore.WriteAtomic(checkpoint, sourceHash, labels, 17);
    Assert(PointCloudSessionRecoveryStore.TryRead(checkpoint, sourceHash, labels.Length,
        out int[] restoredLabels, out long restoredRevision, out string readFailure),
        "valid recovery snapshot loads: " + readFailure);
    Assert(restoredRevision == 17 && labels.SequenceEqual(restoredLabels), "recovery labels and revision round-trip");
    Assert(!PointCloudSessionRecoveryStore.TryRead(checkpoint, new string('0', 64), labels.Length,
        out _, out _, out _), "recovery snapshot rejects a different source fingerprint");
    Assert(!PointCloudSessionRecoveryStore.TryRead(checkpoint, sameNameOtherHash, labels.Length,
        out _, out _, out _), "recovery snapshot rejects a same-named but different source file");
    Assert(!PointCloudSessionRecoveryStore.TryRead(checkpoint, sourceHash, labels.Length + 1,
        out _, out _, out _), "recovery snapshot rejects a different point count");

    byte[] previousCheckpoint = File.ReadAllBytes(checkpoint);
    int[] retryLabels = { 9, 8, 7, 6, 5 };
    bool replacementBlocked = false;
    using (new FileStream(checkpoint, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { PointCloudSessionRecoveryStore.WriteAtomic(checkpoint, sourceHash, retryLabels, 18); }
        catch (IOException) { replacementBlocked = true; }
    }
    Assert(replacementBlocked, "open checkpoint denies atomic replacement on Windows");
    Assert(File.ReadAllBytes(checkpoint).SequenceEqual(previousCheckpoint),
        "failed checkpoint replacement preserves the previous complete file byte-for-byte");
    Assert(!Directory.GetFiles(Path.GetDirectoryName(checkpoint), Path.GetFileName(checkpoint) + ".*.tmp").Any(),
        "failed checkpoint replacement removes its temporary file");
    Assert(PointCloudSessionRecoveryStore.TryRead(checkpoint, sourceHash, labels.Length,
        out int[] labelsAfterReplacementFailure, out long revisionAfterReplacementFailure, out _)
        && revisionAfterReplacementFailure == 17 && labels.SequenceEqual(labelsAfterReplacementFailure),
        "previous checkpoint remains recoverable after replacement failure");
    PointCloudSessionRecoveryStore.WriteAtomic(checkpoint, sourceHash, retryLabels, 18);
    Assert(PointCloudSessionRecoveryStore.TryRead(checkpoint, sourceHash, retryLabels.Length,
        out int[] labelsAfterRetry, out long revisionAfterRetry, out _)
        && revisionAfterRetry == 18 && retryLabels.SequenceEqual(labelsAfterRetry),
        "checkpoint can be retried successfully after the file lock is released");
    Assert(PointCloudSessionRecoveryStore.ComputeFileSha256(sourcePly) == sourceHash,
        "checkpoint failure and retry leave the source fixture unchanged");

    PointCloudSessionRecoveryStore.WriteAtomic(checkpoint, sourceHash, new[] { 5, 4, 3 }, 18);
    Assert(PointCloudSessionRecoveryStore.TryRead(checkpoint, sourceHash, 3,
        out int[] replacedLabels, out long replacedRevision, out _), "atomic checkpoint replacement remains readable");
    Assert(replacedRevision == 18 && replacedLabels.SequenceEqual(new[] { 5, 4, 3 }), "replacement contains only the new complete snapshot");
    byte[] corrupt = File.ReadAllBytes(checkpoint);
    File.WriteAllBytes(checkpoint, corrupt[..^4]);
    Assert(!PointCloudSessionRecoveryStore.TryRead(checkpoint, sourceHash, 3,
        out _, out _, out _), "truncated recovery payload is rejected");
    File.WriteAllBytes(checkpoint, corrupt);
    corrupt[corrupt.Length - 1] ^= 0x7f;
    File.WriteAllBytes(checkpoint, corrupt);
    Assert(!PointCloudSessionRecoveryStore.TryRead(checkpoint, sourceHash, 3,
        out _, out _, out _), "corrupted recovery payload is rejected");
    Assert(File.ReadAllText(sourcePly) == "immutable source fixture", "recovery never modifies the source PLY");

    Assert(!PointCloudSessionRecoveryStore.BeginSession(recoveryDirectory), "new session has no unclean marker");
    Assert(PointCloudSessionRecoveryStore.EndSession(recoveryDirectory), "clean session removes its marker");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(recoveryDirectory, "PointCloudVR", "Recovery", "session.active")));
    File.WriteAllText(Path.Combine(recoveryDirectory, "PointCloudVR", "Recovery", "session.active"), "simulated crash marker");
    Assert(PointCloudSessionRecoveryStore.BeginSession(recoveryDirectory), "stale marker reports unclean prior session");
    Assert(PointCloudSessionRecoveryStore.EndSession(recoveryDirectory), "recovery session can end cleanly");
}
finally
{
    try { Directory.Delete(recoveryDirectory, true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

string directory = Path.Combine(Path.GetTempPath(), "pcwb-ply-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    PointData[] points =
    {
        new PointData(1.25f, -2.5f, 0.125f, 0xFF332211u, 1),
        new PointData(4f, 5f, 6f, 0xFF665544u, 2 | 0x10000),
        new PointData(7f, 8f, 9f, 0xFF998877u, 3 | 0x20000),
        new PointData(10f, 11f, 12f, 0xFFCCBBAAu, 4 | 0x80000)
    };

    string sourcePlyPath = Path.Combine(directory, "plant-a.ply");
    string dataDirectory = Path.Combine(directory, "PointCloudData");
    string expectedResultDirectory = StemDiameterResultCache.GetResultDirectory(dataDirectory, sourcePlyPath);
    Assert(StemDiameterResultCache.GetResultDirectory(dataDirectory, sourcePlyPath) == expectedResultDirectory,
        "result folder derives from original source path identity");
    string sameNameOtherPath = Path.Combine(directory, "elsewhere", "plant-a.ply");
    Assert(StemDiameterResultCache.GetResultDirectory(dataDirectory, sourcePlyPath) !=
        StemDiameterResultCache.GetResultDirectory(dataDirectory, sameNameOtherPath),
        "same-named point clouds from different paths do not share result cache");
    Assert(StemDiameterResultCache.GetResultJsonPath(dataDirectory, sourcePlyPath) ==
        Path.Combine(expectedResultDirectory, "stem_diameter.json"), "result JSON cache path");
    Assert(StemDiameterResultCache.SourceMatches(sourcePlyPath, "plant-a.ply", sourcePlyPath),
        "cached result matches source identity");
    Assert(!StemDiameterResultCache.SourceMatches(sourcePlyPath, "plant-a.ply", Path.Combine(directory, "plant-b.ply")),
        "cached result rejects different source identity");
    Assert(!StemDiameterResultCache.SourceMatches(null, null, sourcePlyPath), "cache without source identity is not trusted");
    Assert(!StemDiameterResultCache.IsStale(100, 100) && StemDiameterResultCache.IsStale(100, 99) &&
        StemDiameterResultCache.IsStale(0, 100),
        "visible-point count and unknown point count mark stale cache conservatively");
    Assert(PlyExportService.CountIncludedPoints(points, ExportPointMode.AllVisible) == 2,
        "current visible count uses AllVisible export rules");
    PointData[] selectionStates =
    {
        new PointData(0, 0, 0, 0, 0x10001),
        new PointData(1, 0, 0, 0, 0x30001),
        new PointData(2, 0, 0, 0, 0x90001),
        new PointData(3, 0, 0, 0, 0x50001),
        new PointData(4, 0, 0, 0, 1),
    };
    Assert(PlyExportService.CountIncludedPoints(selectionStates, ExportPointMode.SelectedNonDeleted) == 2 &&
        PlyExportService.IsIncluded(selectionStates[0].label, ExportPointMode.SelectedNonDeleted) &&
        !PlyExportService.IsIncluded(selectionStates[1].label, ExportPointMode.SelectedNonDeleted) &&
        !PlyExportService.IsIncluded(selectionStates[2].label, ExportPointMode.SelectedNonDeleted) &&
        PlyExportService.IsIncluded(selectionStates[3].label, ExportPointMode.SelectedNonDeleted) &&
        !PlyExportService.IsIncluded(selectionStates[4].label, ExportPointMode.SelectedNonDeleted),
        "selected non-deleted semantics consistently exclude manual-deleted and noise-hidden points");

    string[] downsampleInputNames =
    {
        "sample.ply", "sample_labeled.ply", "sample_downsampled.ply", "sample_ds5mm.ply",
        "sample_ds1p01mm.ply", "sample_ds1p04mm.ply", "sample_ds1p05mm.ply", "sample_ds0p01mm.ply",
        "sample_ds5p01mm.ply"
    };
    foreach (string inputName in downsampleInputNames)
    {
        string input = Path.Combine(directory, inputName);
        DownsamplePaths paths = PointCloudDownsampleService.BuildPaths(input, 5.01f);
        Assert(!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(input), Path.GetFullPath(paths.TemporaryLabeledPath)) &&
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(input), Path.GetFullPath(paths.StagedOutputPath)) &&
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(input), Path.GetFullPath(paths.CombinedOutputPath)) &&
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(paths.TemporaryLabeledPath), Path.GetFullPath(paths.StagedOutputPath)),
            "downsample input/work/stage/output paths stay distinct for " + inputName);
    }
    DownsamplePaths downsampleA = PointCloudDownsampleService.BuildPaths(Path.Combine(directory, "sample_labeled.ply"), 5f);
    DownsamplePaths downsampleB = PointCloudDownsampleService.BuildPaths(Path.Combine(directory, "sample_labeled.ply"), 5f);
    DownsamplePaths downsampleLowPrecision = PointCloudDownsampleService.BuildPaths(Path.Combine(directory, "sample_labeled.ply"), 1.01f);
    DownsamplePaths downsampleDifferentPrecision = PointCloudDownsampleService.BuildPaths(Path.Combine(directory, "sample_labeled.ply"), 1.04f);
    DownsamplePaths downsampleDifferentFolder = PointCloudDownsampleService.BuildPaths(
        Path.Combine(directory, "other", "sample_labeled.ply"), 5f);
    Assert(downsampleA.StagedOutputPath != downsampleB.StagedOutputPath &&
        downsampleA.CombinedOutputPath != downsampleB.CombinedOutputPath &&
        downsampleLowPrecision.CombinedOutputPath != downsampleDifferentPrecision.CombinedOutputPath &&
        downsampleA.CombinedOutputPath != downsampleDifferentFolder.CombinedOutputPath,
        "downsample output names are unique across repeated runs, close voxel values, and same-named folders");
    string labeledSourcePath = Path.Combine(directory, "sample_labeled.ply");
    PlyExportService.Write(new PlyExportRequest(points, labeledSourcePath, true, false, ExportPointMode.AllVisible), default);
    string labeledSourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(labeledSourcePath)));
    DownsamplePaths labeledPaths = PointCloudDownsampleService.BuildPaths(labeledSourcePath, 5f);
    PlyExportService.Write(new PlyExportRequest(points, labeledPaths.TemporaryLabeledPath, true, false,
        ExportPointMode.AllVisible), default);
    Assert(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(labeledSourcePath))) == labeledSourceHash &&
        File.Exists(labeledPaths.TemporaryLabeledPath),
        "exporting a labeled input to its unique work path leaves the source PLY hash unchanged");
    PointCloudDownsampleService.CleanupWorkDirectory(labeledPaths.WorkDirectory);
    Assert(File.Exists(labeledSourcePath), "downsample work cleanup does not delete its source PLY");
    string protectedDirectory = Path.Combine(directory, "keep-this-directory");
    Directory.CreateDirectory(protectedDirectory);
    string protectedFile = Path.Combine(protectedDirectory, "keep.txt");
    File.WriteAllText(protectedFile, "preserve");
    AssertThrows<InvalidOperationException>(() => PointCloudDownsampleService.CleanupWorkDirectory(protectedDirectory),
        "downsample cleanup rejects directories outside its generated work path");
    Assert(File.Exists(protectedFile), "rejected downsample cleanup leaves unrelated files intact");

    string generationRoot = Path.Combine(directory, "generation-store");
    string generationId = "validation_run_1";
    string generationDirectory = Path.Combine(generationRoot, "runs", generationId);
    Directory.CreateDirectory(generationDirectory);
    string generationArtifactPath = Path.Combine(generationDirectory, "result.bin");
    File.WriteAllBytes(generationArtifactPath, new byte[] { 1, 2, 3, 4 });
    string artifactHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(generationArtifactPath))).ToLowerInvariant();
    string manifestPath = Path.Combine(generationDirectory, "manifest.json");
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
    {
        schema_version = 1, status = "complete", run_id = generationId,
        artifacts = new[] { new { name = "result.bin", size_bytes = 4, sha256 = artifactHash } }
    }), Encoding.UTF8);
    string manifestHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant();
    Directory.CreateDirectory(generationRoot);
    File.WriteAllText(Path.Combine(generationRoot, "current_run.json"), JsonSerializer.Serialize(new
    {
        schema_version = 1, status = "complete", run_id = generationId,
        manifest = $"runs/{generationId}/manifest.json", manifest_sha256 = manifestHash
    }), Encoding.UTF8);
    Assert(OutputGenerationStore.ResolveCurrentGeneration(generationRoot) == generationDirectory,
        "generation pointer resolves only a complete checksummed result set");
    File.AppendAllText(generationArtifactPath, "tampered");
    AssertThrows<InvalidDataException>(() => OutputGenerationStore.ResolveCurrentGeneration(generationRoot),
        "generation validation rejects corrupted output artifacts");
    StemDiameterPointFingerprintResult fingerprint = StemDiameterPointFingerprint.Compute(points, ExportPointMode.AllVisible);
    PointData firstVisible = points[0];
    PointData selectedVisible = points[1];
    selectedVisible.label = 2;
    PointData[] visibleOnly = { firstVisible, selectedVisible };
    StemDiameterPointFingerprintResult referenceFingerprint =
        StemDiameterPointFingerprint.Compute(visibleOnly, ExportPointMode.AllVisible);
    Assert(fingerprint.PointCount == 2 && fingerprint.Fingerprint.StartsWith("sha256:", StringComparison.Ordinal) &&
        fingerprint.Fingerprint.Length == 71, "fingerprint is SHA-256 of visible data-space points");
    Assert(fingerprint.Fingerprint == "sha256:500c56c752502dfca6e9b6865f02acb2e307711d282eeba3888560a7a63af223",
        "fingerprint encodes IEEE-754 XYZ floats in stable little-endian order");
    Assert(fingerprint.Fingerprint == referenceFingerprint.Fingerprint,
        "deleted/noise-hidden points excluded and selected-only point included in fingerprint");
    PointData[] changedVisible = (PointData[])visibleOnly.Clone();
    changedVisible[0].position.x += 0.25f;
    Assert(StemDiameterPointFingerprint.Compute(changedVisible, ExportPointMode.AllVisible).Fingerprint !=
        fingerprint.Fingerprint, "same visible count with changed coordinates changes fingerprint");
    Assert(!StemDiameterResultCache.IsStale(2, 2, fingerprint.Fingerprint, fingerprint.Fingerprint),
        "matching count and fingerprint is current");
    Assert(StemDiameterResultCache.IsStale(2, 2, fingerprint.Fingerprint,
        StemDiameterPointFingerprint.Compute(changedVisible, ExportPointMode.AllVisible).Fingerprint),
        "same count with different fingerprint is stale");
    Assert(StemDiameterResultCache.IsStale(2, 2, null, fingerprint.Fingerprint),
        "legacy JSON without fingerprint is stale because coordinate identity cannot be proven");
    Assert(StemDiameterResultCache.IsStale(2, 1, null, "ignored-for-legacy-json"),
        "legacy JSON still detects visible point-count changes");

    string binaryPath = Path.Combine(directory, "all-binary.ply");
    PlyExportService.Write(new PlyExportRequest(points, binaryPath, true, false, ExportPointMode.AllVisible), default);
    PointCloudPlyData binaryRead = PointCloudPlyReader.Read(binaryPath, 10);
    Assert(binaryRead.Vertices.Length == 2 && binaryRead.Vertices[0].X == 1.25f &&
        binaryRead.Vertices[0].Green == 0x22 && binaryRead.Vertices[1].Label == 2,
        "PLY reader preserves binary little-endian XYZ, RGB, and labels");
    byte[] binary = File.ReadAllBytes(binaryPath);
    int endHeaderStart = Encoding.ASCII.GetString(binary).IndexOf("end_header", StringComparison.Ordinal);
    int headerEnd = Array.IndexOf(binary, (byte)'\n', endHeaderStart);
    string header = Encoding.ASCII.GetString(binary, 0, headerEnd + 1);
    Assert(header.Contains("element vertex 2\n"), "binary vertex count");
    Assert(binary.Length == headerEnd + 1 + 2 * 19, "binary payload length");
    Assert(BitConverter.ToSingle(binary, headerEnd + 1) == 1.25f, "binary x coordinate");
    Assert(binary[headerEnd + 1 + 12] == 0x11 && binary[headerEnd + 1 + 13] == 0x22 && binary[headerEnd + 1 + 14] == 0x33, "binary RGB channels");

    string chunkedBinaryPath = Path.Combine(directory, "chunked-binary.ply");
    PointData[] chunkedPoints = Enumerable.Range(0, 60000)
        .Select(i => new PointData(i, i + 1, i + 2, 0xFF332211u, 1)).ToArray();
    PlyExportService.Write(new PlyExportRequest(chunkedPoints, chunkedBinaryPath, true, false, ExportPointMode.AllVisible), default);
    PointCloudPlyData chunkedRead = PointCloudPlyReader.Read(chunkedBinaryPath, 60000);
    Assert(chunkedRead.Vertices.Length == 60000 && chunkedRead.Vertices[59999].X == 59999f &&
        chunkedRead.Vertices[59999].Label == 1, "binary PLY reader crosses its bounded read-buffer chunks");

    string asciiInputPath = Path.Combine(directory, "ascii-input.ply");
    File.WriteAllText(asciiInputPath,
        "ply\r\nformat ascii 1.0\r\ncomment pcwb_scale_calibrated true\r\nelement vertex 2\r\n" +
        "property uchar red\r\nproperty float x\r\nproperty float y\r\nproperty float z\r\nproperty int label\r\n" +
        "property float extra\r\nelement face 0\r\nproperty list uchar int vertex_indices\r\nend_header\r\n" +
        "128 1.5 2.5 3.5 7 9\r\n1 -1 0 4 8 10\r\n", Encoding.ASCII);
    PointCloudPlyData asciiRead = PointCloudPlyReader.Read(asciiInputPath, 1);
    Assert(asciiRead.ScaleCalibrated && asciiRead.Vertices.Length == 1 && asciiRead.Vertices[0].X == 1.5f &&
        asciiRead.Vertices[0].Red == 128 && asciiRead.Vertices[0].Green == 255 && asciiRead.Vertices[0].Label == 7,
        "PLY reader handles ASCII CRLF, property order, cap, metadata, and ignores trailing face schema");
    PointCloudPlyData asciiColors = PointCloudPlyReader.Read(asciiInputPath, 2);
    Assert(asciiColors.Vertices.Length == 2 && asciiColors.Vertices[1].Red == 1,
        "integer PLY color channels retain byte values instead of being treated as normalized floats");

    string bigEndianPath = Path.Combine(directory, "big-endian.ply");
    using (FileStream stream = File.Create(bigEndianPath))
    {
        byte[] bigEndianHeader = Encoding.ASCII.GetBytes("ply\nformat binary_big_endian 1.0\nelement vertex 1\nproperty float x\nproperty float y\nproperty float z\nend_header\n");
        stream.Write(bigEndianHeader);
        foreach (float value in new[] { 1.25f, -2.5f, 3.75f })
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            stream.Write(bytes);
        }
    }
    PointCloudPlyData bigEndianRead = PointCloudPlyReader.Read(bigEndianPath, 10);
    Assert(bigEndianRead.Vertices.Length == 1 && bigEndianRead.Vertices[0].X == 1.25f &&
        bigEndianRead.Vertices[0].Y == -2.5f && bigEndianRead.Vertices[0].Z == 3.75f,
        "PLY reader supports binary big-endian coordinates");

    string truncatedPath = Path.Combine(directory, "truncated.ply");
    File.WriteAllText(truncatedPath,
        "ply\nformat ascii 1.0\nelement vertex 2\nproperty float x\nproperty float y\nproperty float z\nend_header\n1 2 3\n", Encoding.ASCII);
    AssertThrows<InvalidDataException>(() => PointCloudPlyReader.Read(truncatedPath, 10),
        "truncated PLY payload is rejected instead of returning partial points");
    string truncatedBinaryPath = Path.Combine(directory, "truncated-binary.ply");
    using (FileStream stream = File.Create(truncatedBinaryPath))
    {
        byte[] truncatedBinaryHeader = Encoding.ASCII.GetBytes("ply\nformat binary_little_endian 1.0\nelement vertex 2\nproperty float x\nproperty float y\nproperty float z\nend_header\n");
        stream.Write(truncatedBinaryHeader);
        stream.Write(BitConverter.GetBytes(1f));
        stream.Write(BitConverter.GetBytes(2f));
        stream.Write(BitConverter.GetBytes(3f));
    }
    AssertThrows<InvalidDataException>(() => PointCloudPlyReader.Read(truncatedBinaryPath, 10),
        "truncated binary PLY payload is rejected");
    string nonFinitePath = Path.Combine(directory, "non-finite.ply");
    File.WriteAllText(nonFinitePath,
        "ply\nformat ascii 1.0\nelement vertex 1\nproperty float x\nproperty float y\nproperty float z\nend_header\nNaN 2 3\n", Encoding.ASCII);
    AssertThrows<InvalidDataException>(() => PointCloudPlyReader.Read(nonFinitePath, 10),
        "non-finite PLY coordinates are rejected");
    string missingCoordinatePath = Path.Combine(directory, "missing-coordinate.ply");
    File.WriteAllText(missingCoordinatePath,
        "ply\nformat ascii 1.0\nelement vertex 1\nproperty float x\nproperty float y\nend_header\n1 2\n", Encoding.ASCII);
    AssertThrows<InvalidDataException>(() => PointCloudPlyReader.Read(missingCoordinatePath, 10),
        "PLY without all XYZ properties is rejected");

    PointData[] calibrationPoints = { new PointData(0.125f, -0.25f, 0.5f, 0xFF332211u, 7) };
    string calibratedPlyPath = Path.Combine(directory, "calibrated-output.ply");
    PointCloudScaleService.WriteCalibratedPlyAtomic(calibrationPoints, calibratedPlyPath, default, 2f);
    PointCloudPlyData calibratedPly = PointCloudPlyReader.Read(calibratedPlyPath, 10);
    Assert(calibrationPoints[0].position.x == 0.125f && calibrationPoints[0].position.y == -0.25f &&
        calibrationPoints[0].position.z == 0.5f,
        "calibrated PLY writer leaves the source data-space array unchanged");
    Assert(calibratedPly.ScaleCalibrated && calibratedPly.Vertices.Length == 1 &&
        calibratedPly.Vertices[0].X == 0.25f && calibratedPly.Vertices[0].Y == -0.5f &&
        calibratedPly.Vertices[0].Z == 1f && calibratedPly.Vertices[0].Label == 7,
        "calibrated PLY stores corrected XYZ and an explicit calibration marker");
    string renamedUncalibratedPath = Path.Combine(directory, "renamed_calibrated_mm.ply");
    File.WriteAllText(renamedUncalibratedPath,
        "ply\nformat ascii 1.0\nelement vertex 1\nproperty float x\nproperty float y\nproperty float z\nend_header\n0 0 0\n",
        Encoding.ASCII);
    Assert(!PointCloudScaleService.IsCalibratedPointCloud(renamedUncalibratedPath),
        "calibrated-looking filename without PLY marker is not trusted");
    string protectedOutputPath = Path.Combine(directory, "existing-calibrated.ply");
    File.WriteAllText(protectedOutputPath, "previous valid output");
    AssertThrows<IOException>(() => PointCloudScaleService.WriteCalibratedPlyAtomic(
        calibrationPoints, protectedOutputPath, default, 2f),
        "calibrated writer refuses to replace an existing output name");
    Assert(File.ReadAllText(protectedOutputPath) == "previous valid output" &&
        !Directory.GetFiles(directory, "existing-calibrated.ply.*.tmp").Any(),
        "failed calibrated write preserves prior output and removes its temporary file");

    string selectedBinaryPath = Path.Combine(directory, "selected-binary.ply");
    PlyExportService.Write(new PlyExportRequest(points, selectedBinaryPath, true, false, ExportPointMode.SelectedVisible), default);
    byte[] selectedBinary = File.ReadAllBytes(selectedBinaryPath);
    int selectedHeaderStart = Encoding.ASCII.GetString(selectedBinary).IndexOf("end_header", StringComparison.Ordinal);
    int selectedHeaderEnd = Array.IndexOf(selectedBinary, (byte)'\n', selectedHeaderStart);
    Assert(Encoding.ASCII.GetString(selectedBinary, 0, selectedHeaderEnd + 1).Contains("element vertex 1\n"), "selected binary vertex count");
    Assert(BitConverter.ToSingle(selectedBinary, selectedHeaderEnd + 1) == 4f, "selected binary point coordinates");
    Assert(BitConverter.ToInt32(selectedBinary, selectedHeaderEnd + 1 + 15) == 2, "selected binary label excludes transient flags");

    string emptySelectedPath = Path.Combine(directory, "empty-selected.ply");
    try
    {
        PointData[] deletedSelection = { new PointData(1f, 2f, 3f, 0xFF010203u, 1 | 0x10000 | 0x20000) };
        PlyExportService.Write(new PlyExportRequest(deletedSelection, emptySelectedPath, true, false, ExportPointMode.SelectedNonDeleted), default);
        throw new Exception("expected empty SelectedNonDeleted export rejection");
    }
    catch (InvalidOperationException ex) when (ex.Message == "エクスポート対象の選択された点がありません。") { }
    Assert(!File.Exists(emptySelectedPath), "empty SelectedNonDeleted export creates no PLY");

    string cleanedPath = Path.Combine(directory, "cleaned-visible.ply");
    PlyExportService.Write(new PlyExportRequest(points, cleanedPath, false, false, ExportPointMode.CleanedVisible), default);
    Assert(File.ReadAllText(cleanedPath).Contains("element vertex 2\n"), "cleaned-visible export behavior");

    string originalPath = Path.Combine(directory, "original.ply");
    File.WriteAllText(originalPath, "original source stays unchanged");
    PointData[] stemPoints = Enumerable.Range(0, 103)
        .Select(i => new PointData(i, i + 1, i + 2, 0xFF010203u, 1)).ToArray();
    stemPoints[100].label |= 0x10000;
    stemPoints[101].label |= 0x20000;
    stemPoints[102].label |= 0x80000;
    string originalContents = File.ReadAllText(originalPath);
    StemDiameterInputExport stemExport = StemDiameterInputExport.Create(stemPoints, false);
    string stemTempDirectory = stemExport.TemporaryDirectory;
    PlyExportResult stemExportResult = await stemExport.WriteAsync(default);
    Assert(stemExport.InputPath == Path.Combine(stemTempDirectory, "analysis_input.ply"), "stem input uses unique temp PLY");
    Assert(stemExport.LoadedPointCount == 103 && stemExportResult.VertexCount == 101, "stem AllVisible export excludes deleted and noise-hidden points");
    Assert(stemExportResult.OutputPath == stemExport.InputPath, "stem Python input is the live-point export");
    byte[] stemPly = File.ReadAllBytes(stemExport.InputPath);
    int stemHeaderMarker = Encoding.ASCII.GetString(stemPly).IndexOf("end_header", StringComparison.Ordinal);
    int stemPayloadOffset = Array.IndexOf(stemPly, (byte)'\n', stemHeaderMarker) + 1;
    Assert(BitConverter.ToSingle(stemPly, stemPayloadOffset + 100 * 19) == 100f,
        "selected-but-not-deleted point remains with data-space XYZ");
    Assert(File.ReadAllText(originalPath) == originalContents, "stem export leaves original PLY unchanged");
    stemExport.ValidateComponentK(8);
    try
    {
        stemExport.ValidateComponentK(101);
        throw new Exception("expected K validation against visible export count");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("書き出した可視点数")) { }
    try
    {
        StemDiameterInputExport.ValidatePythonPointCount(101, 100);
        throw new Exception("expected Python/export point-count mismatch rejection");
    }
    catch (InvalidDataException ex) when (ex.Message.Contains("点数が一致しません")) { }
    stemExport.Cleanup();
    Assert(!Directory.Exists(stemTempDirectory), "stem temp directory cleanup");

    StemDiameterInputExport tooSmallExport = StemDiameterInputExport.Create(
        Enumerable.Range(0, StemDiameterInputExport.MinimumPointCount - 1)
            .Select(i => new PointData(i, i + 1, i + 2, 0xFF010203u, 1)).ToArray(), false);
    string tooSmallDirectory = tooSmallExport.TemporaryDirectory;
    try
    {
        await tooSmallExport.WriteAsync(default);
        tooSmallExport.ValidateComponentK(8);
        throw new Exception("expected minimum visible point-count rejection");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("可視点が不足")) { }
    finally { tooSmallExport.Cleanup(); }
    Assert(!Directory.Exists(tooSmallDirectory), "too-small stem input temp directory cleanup");

    PointData[] stemCancellationPoints = Enumerable.Range(0, 100_000)
        .Select(i => new PointData(i, i + 1, i + 2, 0xFF010203u, 1)).ToArray();
    StemDiameterInputExport cancelledStemExport = StemDiameterInputExport.Create(stemCancellationPoints, false);
    string cancelledStemDirectory = cancelledStemExport.TemporaryDirectory;
    using (CancellationTokenSource cancellation = new CancellationTokenSource())
    {
        try
        {
            await cancelledStemExport.WriteAsync(cancellation.Token, (_, _) => cancellation.Cancel());
            throw new Exception("expected stem input export cancellation");
        }
        catch (OperationCanceledException) { }
    }
    cancelledStemExport.Cleanup();
    Assert(!Directory.Exists(cancelledStemDirectory), "cancelled stem input temp directory cleanup");

    string asciiPath = Path.Combine(directory, "selected-ascii.ply");
    PlyExportService.Write(new PlyExportRequest(points, asciiPath, false, true, ExportPointMode.SelectedVisible), default);
    string[] asciiLines = File.ReadAllLines(asciiPath);
    Assert(asciiLines.Any(line => line == "comment pcwb_scale_calibrated true"), "calibration metadata");
    Assert(asciiLines.Any(line => line == "element vertex 1"), "selected vertex count");
    Assert(asciiLines[^1] == "4 5 6 68 85 102 2", "selected point payload");

    string cancelPath = Path.Combine(directory, "cancel.ply");
    File.WriteAllText(cancelPath, "previous-final");
    PointData[] manyPoints = Enumerable.Range(0, 100_000)
        .Select(i => new PointData(i, i + 1, i + 2, 0xFF010203u, 1)).ToArray();
    using (CancellationTokenSource cancellation = new CancellationTokenSource())
    {
        try
        {
            PlyExportService.Write(new PlyExportRequest(manyPoints, cancelPath, true, false, ExportPointMode.AllVisible),
                cancellation.Token, (_, _) => cancellation.Cancel());
            throw new Exception("expected cancellation");
        }
        catch (OperationCanceledException) { }
    }
    Assert(File.ReadAllText(cancelPath) == "previous-final", "cancellation preserves final");
    Assert(!Directory.GetFiles(directory, "cancel.ply.*.tmp").Any(), "cancellation removes temporary file");

    string failurePath = Path.Combine(directory, "failure.ply");
    File.WriteAllText(failurePath, "previous-final");
    try
    {
        PlyExportService.Write(new PlyExportRequest(manyPoints, failurePath, true, false, ExportPointMode.AllVisible),
            default, (_, _) => throw new IOException("injected progress failure"));
        throw new Exception("expected injected failure");
    }
    catch (IOException ex) when (ex.Message == "injected progress failure") { }
    Assert(File.ReadAllText(failurePath) == "previous-final", "failure preserves final");
    Assert(!Directory.GetFiles(directory, "failure.ply.*.tmp").Any(), "failure removes temporary file");
    PlyExportResult retriedExport = PlyExportService.Write(
        new PlyExportRequest(manyPoints, failurePath, true, false, ExportPointMode.AllVisible), default);
    PointCloudPlyData retriedRead = PointCloudPlyReader.Read(failurePath, manyPoints.Length);
    Assert(retriedExport.VertexCount == manyPoints.Length && retriedRead.Vertices.Length == manyPoints.Length &&
        retriedRead.Vertices[^1].X == manyPoints[^1].position.x,
        "PLY save succeeds on the same target after an injected output failure");

    string lockedFinalPath = Path.Combine(directory, "locked-final.ply");
    PlyExportService.Write(new PlyExportRequest(
        new[] { new PointData(123f, 2f, 3f, 0xFF112233u, 1) }, lockedFinalPath, true, false,
        ExportPointMode.AllVisible), default);
    byte[] previousPly = File.ReadAllBytes(lockedFinalPath);
    bool plyReplacementBlocked = false;
    using (new FileStream(lockedFinalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try
        {
            PlyExportService.Write(new PlyExportRequest(manyPoints, lockedFinalPath, true, false,
                ExportPointMode.AllVisible), default);
        }
        catch (IOException) { plyReplacementBlocked = true; }
    }
    Assert(plyReplacementBlocked, "open final PLY denies atomic replacement on Windows");
    Assert(File.ReadAllBytes(lockedFinalPath).SequenceEqual(previousPly),
        "failed PLY replacement preserves the previous final byte-for-byte");
    Assert(!Directory.GetFiles(directory, "locked-final.ply.*.tmp").Any(),
        "failed PLY replacement removes its temporary file");
    PointCloudPlyData previousPlyRead = PointCloudPlyReader.Read(lockedFinalPath, 2);
    Assert(previousPlyRead.Vertices.Length == 1 && previousPlyRead.Vertices[0].X == 123f,
        "previous final PLY remains readable after replacement failure");
    PlyExportService.Write(new PlyExportRequest(manyPoints, lockedFinalPath, true, false,
        ExportPointMode.AllVisible), default);
    Assert(PointCloudPlyReader.Read(lockedFinalPath, manyPoints.Length).Vertices.Length == manyPoints.Length,
        "atomic PLY replacement can be retried after releasing the file lock");

    string alternateFailureRetry = Path.Combine(directory, "failure-retry.ply");
    PlyExportService.Write(new PlyExportRequest(manyPoints, alternateFailureRetry, false, false, ExportPointMode.AllVisible), default);
    Assert(PointCloudPlyReader.Read(alternateFailureRetry, manyPoints.Length).Vertices.Length == manyPoints.Length,
        "PLY save succeeds on a separate target after the failure path");

    PointData[] mutablePoints = Enumerable.Range(0, 5000)
        .Select(i => new PointData(i, i + 1, i + 2, 0xFF010203u, 1)).ToArray();
    string snapshotPath = Path.Combine(directory, "snapshot.ply");
    PlyExportResult snapshotResult = PlyExportService.Write(
        new PlyExportRequest(mutablePoints, snapshotPath, true, false, ExportPointMode.AllVisible), default,
        (_, _) =>
        {
            mutablePoints[^1].position.x = 999999f;
            mutablePoints[^1].label |= 0x20000;
        });
    PointCloudPlyData snapshotRead = PointCloudPlyReader.Read(snapshotPath, 6000);
    Assert(snapshotResult.VertexCount == 5000 && snapshotRead.Vertices.Length == 5000 &&
        snapshotRead.Vertices[^1].X == 4999f,
        "PLY export writes one immutable snapshot despite source mutation during output");

Console.WriteLine("PASS bounded history and text; safe event dispatch; transactional noise labels and dataset binding; output generation integrity; downsample path isolation; exact nearest-neighbor; PLY parsing/export; operation lifecycle; recovery checks; source-keyed stem cache; selection visibility; snapshot export; cancellation/failure preservation");
}
finally
{
    Directory.Delete(directory, true);
}

static void Assert(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException("FAIL " + check);
}

static void AssertThrows<TException>(Action action, string check) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException("FAIL " + check + " (expected " + typeof(TException).Name + ")");
}

sealed class TestResource
{
    public bool Released;
}
