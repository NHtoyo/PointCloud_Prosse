using UnityEngine;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PointCloudWorkbench;

public class PointCloudLoader : MonoBehaviour
{
    private const string CalibratedPointCloudPreferenceKey = "PointCloudLoader.CalibratedPointCloudPath";

    [Header("References")]
    public PointCloudRenderer targetRenderer;

    [Header("Settings")]
    [Tooltip("Path to the point cloud file. Can be absolute or relative to StreamingAssets.")]
    public string fileName = "point_cloud - Cloud.segmented.remaining.segmented.ply";
    
    [Tooltip("If checked, reads from externalFolderPath instead of StreamingAssets. If empty or invalid, defaults to project_root/../PointCloudData.")]
    public bool useExternalPath = true;
    public string externalFolderPath = "";
    public string CurrentFilePath { get; private set; } = "";
    public bool CurrentPointCloudScaleIsCalibrated { get; private set; }
    public int SuccessfulLoadRevision { get; private set; }
    public event System.Func<string, bool> PointCloudChanging;
    public event System.Action<string> PointCloudLoaded;

    [Header("Import Controls")]
    [Tooltip("Maximum points to load to prevent memory issues")]
    public int maxPointsToLoad = 20000000; // Updated default to 20 million

    private bool parsedCoordinatesAreScaleCalibrated;
    private bool isLoading;
    private bool isDestroyed;
    private PointCloudOperation activeLoadOperation;

    private void OnDestroy()
    {
        isDestroyed = true;
        activeLoadOperation?.Cancel();
    }

    void Awake()
    {
        // 過去にUnityインスペクターで設定されたシリアライズ値(200万制限等)を自動検知し、2000万点まで安全に引き上げる
        if (maxPointsToLoad <= 2000000)
        {
            maxPointsToLoad = 20000000;
            Debug.Log($"[PointCloudLoader] Detected legacy/low maxPointsToLoad ({maxPointsToLoad}). Upgraded it dynamically to 20,000,000.");
        }

        // 外部フォルダパスをPC移動時にも動くように相対パス対応する
        if (useExternalPath)
        {
            if (string.IsNullOrEmpty(externalFolderPath))
            {
                externalFolderPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../PointCloudData"));
                Debug.Log($"[PointCloudLoader] Path is empty. Auto-resolved external path to relative: {externalFolderPath}");
            }
            else
            {
                // ドライブが存在しない絶対パス（例: Eドライブが無いPCでの E:\VR\PointCloudData）である場合、自動的に相対パスにフォールバックする
                string drive = Path.GetPathRoot(externalFolderPath);
                if (!string.IsNullOrEmpty(drive) && drive.Contains(":") && !Directory.Exists(drive))
                {
                    string fallback = Path.GetFullPath(Path.Combine(Application.dataPath, "../../PointCloudData"));
                    Debug.LogWarning($"[PointCloudLoader] Drive '{drive}' not found. Auto-fallback external path from '{externalFolderPath}' to '{fallback}'");
                    externalFolderPath = fallback;
                }
            }
        }
    }

    void Start()
    {
        if (targetRenderer == null)
        {
            targetRenderer = GetComponent<PointCloudRenderer>();
        }

        string recoveryPath = PlayerPrefs.GetString("PointCloudVR.LastOpenedPath", "");
        bool useRecoveryPath = PointCloudSessionRecoveryStore.PreviousSessionWasUnclean &&
            !string.IsNullOrWhiteSpace(recoveryPath) && File.Exists(recoveryPath);
        if (useRecoveryPath)
        {
            string fullRecoveryPath = Path.GetFullPath(recoveryPath);
            useExternalPath = true;
            externalFolderPath = Path.GetDirectoryName(fullRecoveryPath);
            fileName = Path.GetFileName(fullRecoveryPath);
            Debug.Log($"[Recovery] 異常終了前の点群を再読み込みします: {fullRecoveryPath}");
        }

        string calibratedPath = PlayerPrefs.GetString(CalibratedPointCloudPreferenceKey, "");
        if (!useRecoveryPath && !string.IsNullOrWhiteSpace(calibratedPath) && File.Exists(calibratedPath))
        {
            string fullCalibratedPath = Path.GetFullPath(calibratedPath);
            useExternalPath = true;
            externalFolderPath = Path.GetDirectoryName(fullCalibratedPath);
            fileName = Path.GetFileName(fullCalibratedPath);
        }
        else if (!useRecoveryPath && !string.IsNullOrWhiteSpace(calibratedPath))
        {
            PlayerPrefs.DeleteKey(CalibratedPointCloudPreferenceKey);
            PlayerPrefs.Save();
        }

        string fullPath = GetFilePath();
        
        // 指定ファイルが見つからない場合、同フォルダ内の最初のPLY/TXTファイルを検索して自動フォールバックする
        if (!File.Exists(fullPath))
        {
            string folder = useExternalPath ? externalFolderPath : Application.streamingAssetsPath;
            if (Directory.Exists(folder))
            {
                string[] plyFiles = Directory.GetFiles(folder, "*.ply");
                if (plyFiles.Length > 0)
                {
                    string bestFile = "";
                    foreach (var f in plyFiles)
                    {
                        if (!Path.GetFileName(f).Equals("sample.ply", System.StringComparison.OrdinalIgnoreCase))
                        {
                            bestFile = f;
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(bestFile))
                    {
                        bestFile = plyFiles[0];
                    }
                    fullPath = bestFile;
                    fileName = Path.GetFileName(bestFile);
                    Debug.Log($"[PointCloudLoader] Default file not found. Auto-fallback to found file: {fullPath}");
                }
                else
                {
                    string[] txtFiles = Directory.GetFiles(folder, "*.txt");
                    if (txtFiles.Length > 0)
                    {
                        fullPath = txtFiles[0];
                        fileName = Path.GetFileName(txtFiles[0]);
                        Debug.Log($"[PointCloudLoader] Default file not found. Auto-fallback to found file: {fullPath}");
                    }
                }
            }
        }

        // それでもファイルが存在しない場合のみ、サンプルファイルを自動生成する
        if (!File.Exists(fullPath))
        {
            GenerateSampleFile(fullPath);
        }

        LoadPointCloud(fullPath);
    }

    public string GetFilePath()
    {
        if (useExternalPath)
        {
            if (!Directory.Exists(externalFolderPath))
            {
                Directory.CreateDirectory(externalFolderPath);
            }
            return Path.Combine(externalFolderPath, fileName);
        }
        else
        {
            string saPath = Application.streamingAssetsPath;
            if (!Directory.Exists(saPath))
            {
                Directory.CreateDirectory(saPath);
            }
            return Path.Combine(saPath, fileName);
        }
    }

    public string GetPointCloudDataDirectory()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, "../../PointCloudData"));
    }

    public bool AdoptSavedCalibratedPointCloud(string filePath)
    {
        return AdoptSavedCalibratedPointCloud(filePath, 1f);
    }

    public bool AdoptSavedCalibratedPointCloud(string filePath, float coordinateCorrection)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) ||
            float.IsNaN(coordinateCorrection) || float.IsInfinity(coordinateCorrection) || coordinateCorrection <= 0f) return false;
        string fullPath = Path.GetFullPath(filePath);
        if (!CanChangePointCloud(fullPath)) return false;
        if (targetRenderer == null) targetRenderer = GetComponent<PointCloudRenderer>();
        if (targetRenderer == null || !targetRenderer.ApplyPointCoordinateCorrection(coordinateCorrection)) return false;

        CurrentFilePath = fullPath;
        CurrentPointCloudScaleIsCalibrated = true;
        fileName = Path.GetFileName(fullPath);
        useExternalPath = true;
        externalFolderPath = Path.GetDirectoryName(fullPath);
        PlayerPrefs.SetString(CalibratedPointCloudPreferenceKey, fullPath);
        PlayerPrefs.Save();
        SuccessfulLoadRevision++;
        NotifyPointCloudLoaded(fullPath);
        return true;
    }

    public async void LoadPointCloud(string filePath)
    {
        if (!File.Exists(filePath))
        {
            const string message = "指定した点群ファイルが見つかりません。ファイル選択を確認してください。";
            PointCloudWorkbench.PointCloudProgressManager.Instance.ShowError("点群読み込み", message);
            Debug.LogWarning($"[RecoverableOperationError] {message} Path: {filePath}");
            return;
        }

        if (isLoading)
        {
            Debug.LogWarning("[PointCloudLoader] A point-cloud load is already in progress.");
            return;
        }
        PointCloudProgressManager progress = PointCloudProgressManager.Instance;
        PointCloudOperation operation = progress.TryStart("点群読み込み", "点群ファイルを検証しています...");
        if (operation == null)
        {
            Debug.LogWarning("[PointCloudLoader] Point-cloud loading was not started because another operation is running.");
            return;
        }
        activeLoadOperation = operation;
        isLoading = true;

        System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();

        Debug.Log($"[PointCloudLoader] Loading file: {filePath}");
        string extension = Path.GetExtension(filePath).ToLower();
        int pointLimit = Mathf.Max(1, maxPointsToLoad);

        PointData[] loadedPoints = null;
        parsedCoordinatesAreScaleCalibrated = false;
        CancellationToken cancellationToken = operation.CancellationToken;
        try
        {
            if (extension == ".ply")
            {
                loadedPoints = await Task.Run(() => ParsePLY(filePath, pointLimit, cancellationToken), cancellationToken);
            }
            else
            {
                loadedPoints = await Task.Run(() => ParseTXT(filePath, pointLimit, cancellationToken), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (System.OperationCanceledException)
        {
            isLoading = false;
            operation.CompleteCancelled();
            activeLoadOperation = null;
            return;
        }
        catch (System.Exception ex)
        {
            isLoading = false;
            string message = $"点群ファイルを読み込めませんでした: {Path.GetFileName(filePath)}";
            operation.Fail("点群読み込み", message, ex.ToString());
            activeLoadOperation = null;
            Debug.LogWarning($"[RecoverableOperationError] {message}{System.Environment.NewLine}{ex}");
            return;
        }

        isLoading = false;
        if (isDestroyed || this == null)
        {
            operation.CompleteCancelled("点群ローダーが破棄されたため読み込みを中止しました。");
            activeLoadOperation = null;
            return;
        }
        operation.Update(0.7f, "点群データを表示領域へ反映しています...");

        try
        {
            bool sourceScaleIsCalibrated = parsedCoordinatesAreScaleCalibrated;
            stopwatch.Stop();

            if (loadedPoints != null && loadedPoints.Length > 0)
            {
                Debug.Log($"[PointCloudLoader] Loaded {loadedPoints.Length} points in {stopwatch.ElapsedMilliseconds} ms.");
                if (targetRenderer != null)
                {
                    if (!CanChangePointCloud(filePath))
                    {
                        Debug.LogWarning("[PointCloudLoader] Point-cloud change canceled because pending measurement data could not be saved.");
                        operation.CompleteCancelled("点群の切替を中止しました。計測データを保存できませんでした。");
                        activeLoadOperation = null;
                        return;
                    }

                    string loadedFilePath = Path.GetFullPath(filePath);
                    targetRenderer.SetPointCloudData(loadedPoints);
                    CurrentFilePath = loadedFilePath;
                    CurrentPointCloudScaleIsCalibrated = sourceScaleIsCalibrated;
                    fileName = Path.GetFileName(CurrentFilePath);
                    if (useExternalPath) externalFolderPath = Path.GetDirectoryName(CurrentFilePath);

                    string preferredCalibratedPath = PlayerPrefs.GetString(CalibratedPointCloudPreferenceKey, "");
                    if (!string.IsNullOrEmpty(preferredCalibratedPath) &&
                        !string.Equals(Path.GetFullPath(preferredCalibratedPath), CurrentFilePath, System.StringComparison.OrdinalIgnoreCase))
                    {
                        PlayerPrefs.DeleteKey(CalibratedPointCloudPreferenceKey);
                        PlayerPrefs.Save();
                    }

                    SuccessfulLoadRevision++;
                    NotifyPointCloudLoaded(filePath);
                    operation.Complete();
                }
                else
                {
                    operation.Fail("点群読み込み", "点群描画先のPointCloudRendererが設定されていません。");
                    Debug.LogError("[PointCloudLoader] Target PointCloudRenderer is not set!");
                }
            }
            else
            {
                operation.Fail("点群読み込み", "点群ファイルに読み込める点がありません。");
                Debug.LogWarning("[PointCloudLoader] No points loaded from file.");
            }
        }
        catch (System.Exception ex)
        {
            const string message = "読み込んだ点群を画面へ反映できませんでした。元の点群状態を確認してください。";
            operation.Fail("点群読み込み", message, ex.ToString());
            Debug.LogWarning($"[RecoverableOperationError] {message}\n{ex}");
        }
        finally
        {
            if (ReferenceEquals(activeLoadOperation, operation)) activeLoadOperation = null;
        }
    }

    private bool CanChangePointCloud(string filePath)
    {
        if (PointCloudChanging == null) return true;

        System.Delegate[] callbacks = PointCloudChanging.GetInvocationList();
        for (int i = 0; i < callbacks.Length; i++)
        {
            try
            {
                if (!((System.Func<string, bool>)callbacks[i])(filePath)) return false;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[RecoverableOperationError] Point-cloud change listener failed: {ex}");
                return false;
            }
        }
        return true;
    }

    private void NotifyPointCloudLoaded(string filePath)
    {
        PointCloudWorkbench.SafeEventDispatch.InvokeEach(PointCloudLoaded, filePath,
            exception => Debug.LogWarning($"[RecoverableOperationError] Point-cloud loaded listener failed: {exception}"));
    }

    private PointData[] ParsePLY(string path, int pointLimit, CancellationToken cancellationToken)
    {
        PointData[] points = PointCloudPlyReader.ReadConverted(path, pointLimit, vertex =>
            new PointData(new Vector3(vertex.X, vertex.Y, vertex.Z),
                new Color32(vertex.Red, vertex.Green, vertex.Blue, 255), vertex.Label, 0f),
            out bool scaleCalibrated, cancellationToken);
        parsedCoordinatesAreScaleCalibrated = scaleCalibrated;
        return points;
    }

    private PointData[] ParseTXT(string path, int pointLimit, CancellationToken cancellationToken)
    {
        List<PointData> list = new List<PointData>();
        using (StreamReader reader = new StreamReader(path))
        {
            string line;
            int loaded = 0;

            while ((line = reader.ReadLine()) != null && loaded < pointLimit)
            {
                if ((loaded & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                line = line.Trim();
                if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;

                string[] tokens = line.Split(new char[] { ',', ' ', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3)
                {
                    if (tokens.Length > 0 && float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        throw new InvalidDataException($"点群テキストの{loaded + 1}行目にXYZが揃っていません。");
                    continue;
                }

                float x = 0, y = 0, z = 0;
                float r = 255, g = 255, b = 255;

                if (!float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                    !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                    !float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z) ||
                    float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) ||
                    float.IsNaN(z) || float.IsInfinity(z))
                    throw new InvalidDataException($"点群テキストの{loaded + 1}行目に不正なXYZ値があります。");

                if (tokens.Length >= 6)
                {
                    if (!float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out r) ||
                        !float.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out g) ||
                        !float.TryParse(tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out b) ||
                        float.IsNaN(r) || float.IsInfinity(r) || float.IsNaN(g) || float.IsInfinity(g) ||
                        float.IsNaN(b) || float.IsInfinity(b))
                        throw new InvalidDataException($"点群テキストの{loaded + 1}行目に不正なRGB値があります。");
                }

                byte rNorm = NormalizeTextColor(r);
                byte gNorm = NormalizeTextColor(g);
                byte bNorm = NormalizeTextColor(b);

                list.Add(new PointData(new Vector3(x, y, z), new Color32(rNorm, gNorm, bNorm, 255), 0, 0f));
                loaded++;
            }
        }
        return list.ToArray();
    }

    private static byte NormalizeTextColor(float value)
    {
        float channel = value <= 1f ? value * 255f : value;
        if (channel < 0f) return 0;
        if (channel > 255f) return 255;
        return (byte)channel;
    }

    private void GenerateSampleFile(string path)
    {
        Debug.Log($"[PointCloudLoader] Generating sample PLY file at: {path}");
        
        int sampleCount = 100000; // Increased sample size to 100k for performance test
        bool isAlignedTarget = path.ToLower().Contains("aligned") || fileName.ToLower().Contains("aligned");

        using (StreamWriter writer = new StreamWriter(path))
        {
            writer.WriteLine("ply");
            writer.WriteLine("format ascii 1.0");
            writer.WriteLine($"element vertex {sampleCount}");
            writer.WriteLine("property float x");
            writer.WriteLine("property float y");
            writer.WriteLine("property float z");
            writer.WriteLine("property uchar red");
            writer.WriteLine("property uchar green");
            writer.WriteLine("property uchar blue");
            writer.WriteLine("property int label"); // Added label to sample generation
            writer.WriteLine("end_header");

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / sampleCount * Mathf.PI * 2f;
                float p = 3f;
                float q = 7f;
                
                float rDist = Mathf.Cos(q * t) + 2f;
                float x = rDist * Mathf.Cos(p * t);
                float y = rDist * Mathf.Sin(p * t);
                float z = Mathf.Sin(q * t);

                int label = 0;

                // Segment sample into different dummy labels for testing
                // e.g., 1: Stem, 2: Leaf, 3: Fruit based on coordinates
                if (z > 0.5f) label = 2; // Leaf (Green)
                else if (z < -0.5f) label = 3; // Fruit (Red)
                else label = 1; // Stem (Brown)

                if (isAlignedTarget)
                {
                    x += Random.Range(-0.02f, 0.02f);
                    y += Random.Range(-0.02f, 0.02f);
                    z += Random.Range(-0.02f, 0.02f);
                    
                    if (i > sampleCount / 2 && i < sampleCount / 2 + 10000)
                    {
                        x += 0.15f;
                    }

                    float angle = 15f * Mathf.Deg2Rad;
                    float newX = x * Mathf.Cos(angle) - z * Mathf.Sin(angle) + 0.6f;
                    float newZ = x * Mathf.Sin(angle) + z * Mathf.Cos(angle) - 0.4f;
                    float newY = y + 0.3f;

                    x = newX;
                    y = newY;
                    z = newZ;
                }

                int redColor = Mathf.RoundToInt((Mathf.Sin(t) * 0.5f + 0.5f) * 255);
                int greenColor = Mathf.RoundToInt((Mathf.Cos(t * 2f) * 0.5f + 0.5f) * 255);
                int blueColor = Mathf.RoundToInt(t / (Mathf.PI * 2f) * 255);

                writer.WriteLine($"{x.ToString(CultureInfo.InvariantCulture)} {y.ToString(CultureInfo.InvariantCulture)} {z.ToString(CultureInfo.InvariantCulture)} {redColor} {greenColor} {blueColor} {label}");
            }
        }
    }
}
