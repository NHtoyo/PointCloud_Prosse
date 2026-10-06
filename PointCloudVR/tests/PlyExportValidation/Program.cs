using System.Text;
using PointCloudWorkbench;

PointCloudProgressManager progress = PointCloudProgressManager.Instance;
Assert(progress.Start("test", "running"), "start operation");
Assert(!progress.Start("duplicate", "blocked"), "reject concurrent operation");
progress.Fail("test", "recoverable failure", "full exception detail");
PointCloudProgressSnapshot failed = progress.GetSnapshot();
Assert(!failed.IsRunning && failed.HasError && failed.NotificationMessage == "recoverable failure", "failure unlocks operation");
Assert(failed.Detail == "full exception detail", "failure detail retained");
Assert(progress.Start("next", "running"), "start after failure");
progress.ShowError("validation", "validation message");
Assert(progress.IsRunning && !progress.HasError, "show error does not corrupt running operation");
progress.CompleteCancelled();
Assert(!progress.IsRunning && !progress.HasError, "cancel is distinct from error");

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

    string binaryPath = Path.Combine(directory, "all-binary.ply");
    PlyExportService.Write(new PlyExportRequest(points, binaryPath, true, false, ExportPointMode.AllVisible), default);
    byte[] binary = File.ReadAllBytes(binaryPath);
    int endHeaderStart = Encoding.ASCII.GetString(binary).IndexOf("end_header", StringComparison.Ordinal);
    int headerEnd = Array.IndexOf(binary, (byte)'\n', endHeaderStart);
    string header = Encoding.ASCII.GetString(binary, 0, headerEnd + 1);
    Assert(header.Contains("element vertex 2\n"), "binary vertex count");
    Assert(binary.Length == headerEnd + 1 + 2 * 19, "binary payload length");
    Assert(BitConverter.ToSingle(binary, headerEnd + 1) == 1.25f, "binary x coordinate");
    Assert(binary[headerEnd + 1 + 12] == 0x11 && binary[headerEnd + 1 + 13] == 0x22 && binary[headerEnd + 1 + 14] == 0x33, "binary RGB channels");

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

    Console.WriteLine("PASS ProgressManager state transitions; binary/all-visible; binary-selected; empty selected-non-deleted rejection; cleaned-visible; ASCII-selected; RGB/XYZ/metadata; cancellation cleanup; failure cleanup; existing-final preservation");
}
finally
{
    Directory.Delete(directory, true);
}

static void Assert(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException("FAIL " + check);
}
