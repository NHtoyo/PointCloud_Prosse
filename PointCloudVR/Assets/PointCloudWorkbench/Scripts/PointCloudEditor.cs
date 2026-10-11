using System;
using UnityEngine;
using System.IO;
using System.Threading;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text;
using PointCloudWorkbench;

public class PointCloudEditor : MonoBehaviour
{
    public enum EditTool { None, Brush, Marquee, Lasso, Connect, Measure }

    [Header("References")]
    public PointCloudRenderer targetRenderer;

    [Header("Tool Settings")]
    public EditTool activeTool = EditTool.None;
    public float brushRadius = 200f;
    public bool brushSelectMode = true; // true = select, false = deselect
    public bool selectOnlyUnclassified = false; // true = ignore points with label > 0
    public int activeLabelClass = 2; // Default to Leaf (2) for painting

    [Header("Advanced Selection Settings")]
    public float connectionRadius = 5f;
    public int maxConnectionPoints = 50000;
    public float ransacColorTolerance = 30f; // RGB Euclidean distance tolerance (0 to 441) for cylinder fitting

    public enum RansacType { Plane, Cylinder }
    public RansacType ransacType = RansacType.Plane;
    public float ransacTolerance = 20f;
    public float supportColorTolerance = 90f;
    public float supportTubeMultiplier = 4.0f;
    public float supportHeightBinMultiplier = 4.0f;
    public int supportMaxEmptyBins = 2;

    public enum FilterType { Height, Distance, Redness, Greenness }
    public FilterType filterType = FilterType.Height;
    public float filterMin = 0f;
    public float filterMax = 1f;

    public enum MeasurementMode { TwoPoint, Polyline, SmoothCurve }

    [Header("Measurement Settings")]
    public MeasurementMode measurementMode = MeasurementMode.TwoPoint;
    public bool hasMeasurePoint1 = false;
    public bool hasMeasurePoint2 = false;
    public Vector3 measurePoint1;
    public Vector3 measurePoint2;
    public readonly MeasurementPath measurementPath = new MeasurementPath();
    private readonly Dictionary<string, MeasurementVisual> measurementVisuals = new Dictionary<string, MeasurementVisual>();
    private MeasurementVisual draftVisual;
    private MeasurementDocument measurementDocument;
    private PointCloudLoader pointCloudLoader;
    private string measurementDocumentPath = "";
    private string measurementCloudPath = "";
    private string measurementExpectedHash = "";
    private string measurementActualHash = "";
    private Task<string> measurementFingerprintTask;
    private bool measurementFingerprintPending;
    private bool measurementSidecarExisted;
    private bool measurementDocumentReady;
    private bool measurementFingerprintMismatch;
    private bool measurementDocumentDirty;
    private string measurementStatus = "点群を読み込むと計測データを確認します。";
    private string selectedMeasurementId = "";
    private string editingMeasurementId = "";
    private bool measurementDraftActive;
    private int replaceMeasurementPointIndex = -1;
    private EditTool toolBeforeMeasurement = EditTool.None;
    private int handledPointCloudRevision;
    private readonly Stack<string> measurementUndoStack = new Stack<string>();
    private const int MaxMeasurementUndo = 30;

    private sealed class MeasurementVisual
    {
        public GameObject root;
        public LineRenderer line;
        public Material material;
        public readonly List<GameObject> markers = new List<GameObject>();
        public string sourceId;
    }

    [Header("Visual Elements")]
    public Color brushColor = new Color(1f, 0.9f, 0f, 0.3f);

    private GameObject brushVisual;
    private Material brushMaterial;
    private bool isDrawingMarquee = false;
    private Vector2 marqueeStart;
    private Vector2 marqueeEnd;
    
    // Lasso drawing points
    private List<Vector2> lassoPoints = new List<Vector2>();
    public List<Vector2> LassoPoints => lassoPoints;

    // Statistics
    private int[] labelCounts = new int[7]; // Legacy placeholder
    private Dictionary<int, int> labelCountsMap = new Dictionary<int, int>();
    private int noiseDeletedCount = 0;
    private int visiblePointCount = 0;
    private int selectedPointCount = 0;
    private bool statsDirty = true;
    private bool trackpadSelectionActive;
    private AnnotationPipelineEditorUI annotationUI;

    public Dictionary<int, int> GetLabelCountsMap() => labelCountsMap;
    public int GetNoiseDeletedCount() => noiseDeletedCount;
    public int VisiblePointCount => visiblePointCount;
    public int SelectedPointCount => selectedPointCount;
    public bool IsTrackpadSelectionActive => trackpadSelectionActive;

    private const int SelectedLabelBit = 0x10000;
    private const int DeletedLabelBit = 0x20000;
    private PointLabelEditHistory editHistory => NoiseFilterManager.Instance.SharedEditHistory;
    private long editHistoryGeneration = -1;
    private string LastOperationFailure = string.Empty;
    private bool brushStrokeActive;
    private bool brushStrokeSelecting;
    private bool brushStrokeOnlyUnclassified;
    private int brushStrokeRecordedFrame = -1;
    private readonly List<int> brushStrokeChangedIndices = new List<int>();

    private sealed class RecoveryLoadResult
    {
        public bool Success;
        public int[] Labels;
        public long Revision;
        public string FailureReason;
    }

    private const int RecoveryPointsPerFrame = 100000;
    private const double RecoveryQuietSeconds = 4.0;
    private bool recoveryWasUnclean;
    private string recoverySourcePath = string.Empty;
    private string recoverySourceHash = string.Empty;
    private string recoveryCheckpointPath = string.Empty;
    private long recoveryDatasetGeneration = -1;
    private long recoveryObservedRevision = -1;
    private long recoveryLastSavedRevision = -1;
    private long recoverySnapshotRevision = -1;
    private DateTime recoveryLastEditUtc = DateTime.UtcNow;
    private DateTime recoveryRetryAfterUtc = DateTime.MinValue;
    private Task<string> recoveryHashTask;
    private Task<RecoveryLoadResult> recoveryLoadTask;
    private Task<bool> recoveryWriteTask;
    private string recoveryWriteCheckpointPath;
    private long recoveryWriteRevision;
    private int[] recoveryLabelSnapshot;
    private int recoverySnapshotCursor;
    private bool recoveryRestoreAttempted;
    private int[] pendingRecoveryLabels;
    private string pendingRecoverySavedAtLocalText = string.Empty;
    private long pendingRecoveryGeneration = -1;
    private long pendingRecoveryRevision = -1;
    private string recoveryDecisionStatus = string.Empty;

    public bool HasPendingRecovery => pendingRecoveryLabels != null;
    public int PendingRecoveryPointCount => pendingRecoveryLabels != null ? pendingRecoveryLabels.Length : 0;
    public string PendingRecoverySavedAtLocalText => pendingRecoverySavedAtLocalText;
    public string PendingRecoverySourceName => string.IsNullOrEmpty(recoverySourcePath)
        ? string.Empty
        : Path.GetFileName(recoverySourcePath);
    public string RecoveryDecisionStatus => recoveryDecisionStatus;

    public bool CanAnnotationUndo => editHistory.CanUndo && IsEditHistoryForCurrentCloud() &&
        activeSelectionOperation == null && !PointCloudProgressManager.Instance.IsRunning && !HasPendingRecovery;
    public bool CanAnnotationRedo => editHistory.CanRedo && IsEditHistoryForCurrentCloud() &&
        activeSelectionOperation == null && !PointCloudProgressManager.Instance.IsRunning && !HasPendingRecovery;
    public long AnnotationHistoryRetainedBytes => editHistory.UndoBytes + editHistory.RedoBytes;
    public long AnnotationHistoryStackLimitBytes => editHistory.MaxBytesPerStack;

    private readonly ConcurrentQueue<BackgroundSelectionResult> backgroundSelectionResults = new ConcurrentQueue<BackgroundSelectionResult>();
    private PointCloudOperation activeSelectionOperation;

    private sealed class BackgroundSelectionResult
    {
        public int[] Indices;
        public bool[] Mask;
        public PointData[] SourcePoints;
        public bool Selecting;
        public bool SelectOnlyUnclassified;
        public bool Cancelled;
        public Exception Error;
        public string OperationTitle;
        public PointCloudOperation Operation;
        public long SourceGeneration;
    }

    // UI Component Reference
    private PointCloudEditorUI editorUI;

    // キャッシュ用配列群 (接続探索の GC Alloc / new 回避用)
    private int[] connQueue = null;

    // CloudCompare風のセル単位接続探索用キャッシュ
    private int[] connCellBucketHead = null;
    private int[] connCellNext = null;
    private int[] connCellX = null;
    private int[] connCellY = null;
    private int[] connCellZ = null;
    private int[] connCellPointHead = null;
    private int[] connPointNextInCell = null;
    private bool[] connPointVisited = null;

    [Header("Pick Settings")]
    public bool pickDensityEnabled = true;
    public int pickDensityMinCount = 3;
    private const float PickDensityNeighborRadiusMillimeters = 5f;

    private readonly List<int> neighborIndicesCache = new List<int>();
    private readonly List<PickCandidate> pickCandidates = new List<PickCandidate>();

    private struct PickCandidate
    {
        public int index;
        public Vector3 position;
        public float proj;
    }

    // Properties for UI access
    public bool IsDrawingMarquee => isDrawingMarquee;
    public Vector2 MarqueeStart => marqueeStart;
    public Vector2 MarqueeEnd => marqueeEnd;

    public int[] GetLabelCounts() => labelCounts;
    public void MarkStatsDirty() => statsDirty = true;

    public List<MeasurementRecord> MeasurementRecords => measurementDocument != null
        ? measurementDocument.measurements
        : new List<MeasurementRecord>();
    public string SelectedMeasurementId => selectedMeasurementId;
    public MeasurementRecord SelectedMeasurement => FindMeasurement(selectedMeasurementId);
    public bool HasMeasurementDraft => measurementDraftActive;
    public bool IsEditingMeasurementDraft => measurementDraftActive && !string.IsNullOrEmpty(editingMeasurementId);
    public bool IsMeasurementDocumentReady => measurementDocumentReady;
    public bool IsMeasurementFingerprintPending => measurementFingerprintPending;
    public bool HasMeasurementFingerprintMismatch => measurementFingerprintMismatch;
    public string MeasurementStatus => measurementStatus;
    public bool CanMeasurementUndo => measurementUndoStack.Count > 0 && measurementDocumentReady;
    public int MeasurementPointCount => measurementDraftActive
        ? measurementPath.Points.Count
        : (SelectedMeasurement != null ? SelectedMeasurement.points.Count : 0);

    public int CountSelectedNonDeletedPoints()
    {
        PointData[] data = targetRenderer != null ? targetRenderer.GetPointData() : null;
        if (data == null) return 0;
        int count = 0;
        for (int i = 0; i < data.Length; i++)
        {
            int label = data[i].label;
            if ((label & 0x10000) != 0 && (label & (0x20000 | NoiseFilterManager.NOISE_HIDDEN_BIT)) == 0) count++;
        }
        return count;
    }

    public bool UpsertReferenceDiameterMeasurement(Vector3 point1, Vector3 point2, out bool created, out string error)
    {
        created = false;
        error = string.Empty;
        if (!measurementDocumentReady || measurementDocument == null || measurementFingerprintMismatch)
        {
            error = "点群と計測JSONの照合が完了していません。";
            return false;
        }
        if (measurementDraftActive)
        {
            error = "編集中の計測を確定またはキャンセルしてから実行してください。";
            return false;
        }
        if (!IsFinite(point1) || !IsFinite(point2) || Vector3.Distance(point1, point2) <= 0f)
        {
            error = "推定された直径線の座標が不正です。";
            return false;
        }

        const string recordName = "リファレンス直径";
        string previousDocument = JsonUtility.ToJson(measurementDocument);
        string previousSelectedId = selectedMeasurementId;
        MeasurementRecord record = null;
        for (int i = 0; i < measurementDocument.measurements.Count; i++)
        {
            MeasurementRecord candidate = measurementDocument.measurements[i];
            if (candidate != null && candidate.name == recordName)
            {
                record = candidate;
                break;
            }
        }

        PushMeasurementUndo();
        created = record == null;
        string now = DateTime.UtcNow.ToString("o");
        if (created)
        {
            record = new MeasurementRecord
            {
                id = Guid.NewGuid().ToString("N"),
                name = recordName,
                color = MeasurementPalette[measurementDocument.measurements.Count % MeasurementPalette.Length],
                createdUtc = now
            };
            measurementDocument.measurements.Add(record);
        }

        record.mode = (int)MeasurementMode.TwoPoint;
        record.interpolation = MeasurementPath.CurveAlgorithmId;
        record.points = new List<Vector3> { point1, point2 };
        record.visible = true;
        record.modifiedUtc = now;
        selectedMeasurementId = record.id;
        measurementDocumentDirty = true;

        if (!SaveMeasurementDocument())
        {
            measurementDocument = JsonUtility.FromJson<MeasurementDocument>(previousDocument);
            selectedMeasurementId = previousSelectedId;
            if (measurementUndoStack.Count > 0) measurementUndoStack.Pop();
            measurementVisualsDirty = true;
            SyncLegacyMeasureFields();
            UpdateMeasureVisuals();
            error = measurementStatus;
            created = false;
            return false;
        }

        measurementVisualsDirty = true;
        SyncLegacyMeasureFields();
        UpdateMeasureVisuals();
        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public MeasurementDocument CreateMeasurementSnapshotForExport()
    {
        if (!measurementDocumentReady || measurementFingerprintMismatch) return null;

        MeasurementDocument snapshot = MeasurementDocumentStore.Clone(measurementDocument);
        return snapshot;
    }

    private string GetCalibrationPlyMetadata()
    {
        PointCloudLoader loader = targetRenderer != null ? targetRenderer.GetComponent<PointCloudLoader>() : pointCloudLoader;
        return loader != null && loader.CurrentPointCloudScaleIsCalibrated
            ? "comment pcwb_scale_calibrated true\n"
            : string.Empty;
    }

    public string LastCalibrationSidecarWarning { get; private set; } = string.Empty;

    public async Task<string> ApplyScaleCalibrationAndSaveAsync(
        float correctionFactor,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        LastCalibrationSidecarWarning = string.Empty;
        if (!measurementDocumentReady || measurementDocument == null || measurementFingerprintMismatch)
        {
            throw new System.InvalidOperationException("点群と計測JSONの照合が完了していません。照合後にもう一度実行してください。");
        }
        if (float.IsNaN(correctionFactor) || float.IsInfinity(correctionFactor) || correctionFactor <= 0f)
        {
            throw new System.ArgumentOutOfRangeException(nameof(correctionFactor), "校正値が不正です。");
        }
        if (targetRenderer == null || targetRenderer.GetPointData() == null || targetRenderer.GetPointData().Length == 0)
        {
            throw new System.InvalidOperationException("校正する点群が読み込まれていません。");
        }
        if (measurementDraftActive)
        {
            if (measurementPath.Points.Count >= 2) FinishMeasurement();
            else CancelMeasurementDraft();
        }
        if (!SaveMeasurementDocument())
        {
            throw new System.IO.IOException(measurementStatus);
        }

        string sourcePath = GetLoadedPointCloudPath();
        if (string.IsNullOrEmpty(sourcePath)) throw new System.IO.IOException("現在の点群ファイルパスを取得できません。");
        string outputPath = PointCloudScaleService.BuildCalibratedOutputPath(sourcePath, outputDirectory);
        PointData[] points = targetRenderer.GetPointData();
        MeasurementDocument calibratedMeasurements = MeasurementDocumentStore.Clone(measurementDocument);
        for (int i = 0; i < calibratedMeasurements.measurements.Count; i++)
        {
            MeasurementRecord record = calibratedMeasurements.measurements[i];
            for (int p = 0; p < record.points.Count; p++) record.points[p] *= correctionFactor;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => PointCloudScaleService.WriteCalibratedPlyAtomic(
            points, outputPath, cancellationToken, correctionFactor), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (calibratedMeasurements.measurements.Count > 0)
        {
            try
            {
                MeasurementDocument derived = MeasurementDocumentStore.CreateDerivedDocument(calibratedMeasurements, outputPath);
                derived.sourceSha256 = await Task.Run(() => MeasurementDocumentStore.ComputeSha256(outputPath));
                string json = MeasurementDocumentStore.Serialize(derived);
                await Task.Run(() => MeasurementDocumentStore.WriteSerializedAtomic(
                    MeasurementDocumentStore.GetSidecarPath(outputPath), json));
            }
            catch (Exception ex)
            {
                LastCalibrationSidecarWarning = ex.ToString();
            }
        }

        measurementStatus = $"補正済み点群を保存しました: {Path.GetFileName(outputPath)}";
        return outputPath;
    }

    private string GetLoadedPointCloudPath()
    {
        PointCloudLoader loader = targetRenderer != null ? targetRenderer.GetComponent<PointCloudLoader>() : null;
        if (loader == null) loader = GetComponent<PointCloudLoader>();
        if (loader == null) return "";
        return !string.IsNullOrEmpty(loader.CurrentFilePath) ? loader.CurrentFilePath : loader.GetFilePath();
    }

    void OnEnable()
    {
        recoveryWasUnclean = PointCloudSessionRecoveryStore.PreviousSessionWasUnclean;
        if (targetRenderer == null) targetRenderer = GetComponent<PointCloudRenderer>();
        pointCloudLoader = GetComponent<PointCloudLoader>();
        if (pointCloudLoader != null)
        {
            pointCloudLoader.PointCloudChanging += HandlePointCloudChanging;
            pointCloudLoader.PointCloudLoaded += HandlePointCloudLoaded;
            if (pointCloudLoader.SuccessfulLoadRevision > handledPointCloudRevision &&
                targetRenderer != null && targetRenderer.GetPointData() != null)
            {
                HandlePointCloudLoaded(pointCloudLoader.CurrentFilePath);
            }
        }
    }

    void OnDisable()
    {
        if (pointCloudLoader != null)
        {
            pointCloudLoader.PointCloudChanging -= HandlePointCloudChanging;
            pointCloudLoader.PointCloudLoaded -= HandlePointCloudLoaded;
        }
    }


    void Start()
    {
        recoveryWasUnclean = PointCloudSessionRecoveryStore.PreviousSessionWasUnclean;
        if (targetRenderer == null)
        {
            targetRenderer = GetComponent<PointCloudRenderer>();
        }

        editorUI = GetComponent<PointCloudEditorUI>();
        if (editorUI == null)
        {
            editorUI = gameObject.AddComponent<PointCloudEditorUI>();
        }

        annotationUI = GetComponent<AnnotationPipelineEditorUI>();
        if (annotationUI == null)
        {
            annotationUI = gameObject.AddComponent<AnnotationPipelineEditorUI>();
        }

        connectionRadius = Mathf.Clamp(connectionRadius, 0.05f, 20f);

        CreateBrushVisual();
        statsDirty = true;

        if (pointCloudLoader != null && pointCloudLoader.SuccessfulLoadRevision > 0 &&
            pointCloudLoader.SuccessfulLoadRevision != handledPointCloudRevision &&
            targetRenderer.GetPointData() != null)
        {
            HandlePointCloudLoaded(pointCloudLoader.CurrentFilePath);
        }
    }

    void CreateBrushVisual()
    {
        // 3D Sphere to represent the brush volume in the scene
        brushVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(brushVisual.GetComponent<SphereCollider>()); // No physics interference
        brushVisual.name = "Editor_Brush_Visual";
        
        // Semi-transparent material
        brushMaterial = new Material(Shader.Find("Sprites/Default"));
        brushMaterial.color = brushColor;
        brushVisual.GetComponent<MeshRenderer>().sharedMaterial = brushMaterial;
        
        brushVisual.SetActive(false);
    }

    private void ApplyBackgroundSelectionResult(BackgroundSelectionResult result)
    {
        if (result == null) return;
        PointCloudOperation operation = result.Operation;
        if (operation == null || !operation.IsCurrent) return;
        if (result.Error != null)
        {
            operation.Fail(result.OperationTitle, "選択処理に失敗しました。点群の選択状態は変更していません。", result.Error.ToString());
            ClearActiveSelectionOperation(operation);
            Debug.LogWarning($"[RecoverableOperationError] {result.OperationTitle}: {result.Error}");
            return;
        }
        if (result.Cancelled)
        {
            operation.CompleteCancelled("キャンセルしました。選択状態は変更していません。");
            ClearActiveSelectionOperation(operation);
            return;
        }

        PointData[] points = targetRenderer != null ? targetRenderer.GetPointData() : null;
        if (points == null)
        {
            var error = new InvalidOperationException("結果を反映する点群がありません。");
            operation.Fail(result.OperationTitle, "選択結果を反映できませんでした。", error.ToString());
            ClearActiveSelectionOperation(operation);
            Debug.LogWarning($"[RecoverableOperationError] {result.OperationTitle}: {error}");
            return;
        }
        if ((result.SourcePoints != null && !ReferenceEquals(result.SourcePoints, points)) ||
            (targetRenderer != null && result.SourceGeneration != targetRenderer.DatasetGeneration))
        {
            operation.CompleteCancelled("処理中に点群が切り替わったため、古い選択結果は適用しませんでした。");
            ClearActiveSelectionOperation(operation);
            return;
        }

        if ((result.Indices == null && result.Mask == null) ||
            (result.Mask != null && result.Mask.Length != points.Length) ||
            (result.Indices != null && !HasValidUniqueIndices(result.Indices, points.Length)))
        {
            var error = new InvalidOperationException("選択結果の点インデックスが不正です。");
            operation.Fail(result.OperationTitle, "選択結果を反映できませんでした。選択状態は変更していません。", error.ToString());
            ClearActiveSelectionOperation(operation);
            Debug.LogWarning($"[RecoverableOperationError] {result.OperationTitle}: {error}");
            return;
        }

        IList<int> candidates = result.Indices;
        Func<int, bool> predicate = result.Mask != null
            ? new Func<int, bool>(index => index < result.Mask.Length && result.Mask[index])
            : null;
        List<int> changed = CollectSelectionChanges(points, candidates, result.Selecting,
            result.Selecting && result.SelectOnlyUnclassified, predicate);
        if (changed.Count == 0)
        {
            operation.Complete();
            ClearActiveSelectionOperation(operation);
            return;
        }

        byte beforeValue = (byte)(result.Selecting ? 0 : 1);
        PointLabelDelta delta = PointLabelDelta.ToggleConstant(changed.ToArray(), SelectedLabelBit, 16, beforeValue);
        if (TryCommitEditDelta(delta, result.OperationTitle, allowedOperation: operation))
        {
            operation.Complete();
            ClearActiveSelectionOperation(operation);
            Debug.Log($"[{result.OperationTitle}] 選択結果を反映しました ({changed.Count:N0} 点)。");
        }
        else
        {
            operation.Fail(result.OperationTitle, "選択結果を反映できませんでした。選択状態は変更していません。", LastOperationFailure);
            ClearActiveSelectionOperation(operation);
        }
    }

    private void ClearActiveSelectionOperation(PointCloudOperation operation)
    {
        if (ReferenceEquals(activeSelectionOperation, operation)) activeSelectionOperation = null;
    }

    void Update()
    {
        if (HardwareCompatibilityDiagnostic.HasBlockingGraphicsFailure) return;
        if (targetRenderer == null) return;
        PollMeasurementFingerprint();
        UpdateSessionRecovery();

        while (backgroundSelectionResults.TryDequeue(out BackgroundSelectionResult result))
            ApplyBackgroundSelectionResult(result);

        if (brushStrokeActive && (activeTool != EditTool.Brush || HasPendingRecovery ||
            PointCloudProgressManager.Instance.IsRunning || !IsSelectionPointerHeld()))
            FinishBrushStroke();

        // Lock interactions if a background task is running (modal progress dialog)
        if (HasPendingRecovery)
        {
            if (brushVisual != null && brushVisual.activeSelf) brushVisual.SetActive(false);
            return;
        }

        if (PointCloudProgressManager.Instance.IsRunning)
        {
            if (brushVisual != null && brushVisual.activeSelf)
            {
                brushVisual.SetActive(false);
            }
            return;
        }

        HandleEditHistoryShortcuts();

        // Clean up brush visual if tool changed
        if (activeTool != EditTool.Brush && brushVisual != null && brushVisual.activeSelf)
        {
            brushVisual.SetActive(false);
        }

        UpdateMeasureVisuals();

        // Handle tool interactions
        if (activeTool == EditTool.Brush)
        {
            HandleBrushTool();
        }
        else if (activeTool == EditTool.Marquee)
        {
            HandleMarqueeTool();
        }
        else if (activeTool == EditTool.Lasso)
        {
            HandleLassoTool();
        }
        else if (activeTool == EditTool.Connect)
        {
            HandleConnectTool();
        }
        else if (activeTool == EditTool.Measure)
        {
            HandleMeasureTool();
        }

        if (trackpadSelectionActive && Input.GetMouseButtonUp(0)) trackpadSelectionActive = false;

        // Recalculate stats if marked dirty
        if (statsDirty)
        {
            RecalculateStats();
        }
    }

    private void HandleEditHistoryShortcuts()
    {
        if (HardwareCompatibilityDiagnostic.IsDetailsOpen) return;
        if (GUIUtility.keyboardControl != 0 || (editorUI != null && editorUI.HasKeyboardInputFocus)) return;
        bool controlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (!controlDown) return;
        bool undo = Input.GetKeyDown(KeyCode.Z);
        bool redo = !undo && Input.GetKeyDown(KeyCode.Y);
        if (!undo && !redo) return;

        // A held brush stroke is already applied to the point data but is committed
        // to history only on release. Close it first so Ctrl+Z targets that stroke.
        if (brushStrokeActive)
        {
            bool hadChanges = brushStrokeChangedIndices.Count > 0;
            bool recorded = FinishBrushStroke();
            if (hadChanges)
            {
                if (undo && recorded) AnnotationUndo();
                return;
            }
        }
        else if (undo && brushStrokeRecordedFrame == Time.frameCount)
        {
            AnnotationUndo();
            return;
        }

        // Ctrl+Z/Y always target the chronological point-label edit stack. Measurement
        // documents are an independent data domain and use their explicitly named UI action.
        if (undo) AnnotationUndo();
        else AnnotationRedo();
    }

    private bool IsTrackpadSelectionGesture()
    {
        bool controlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (activeTool != EditTool.None && controlDown && Input.GetMouseButtonDown(0))
            trackpadSelectionActive = true;
        if (Input.GetMouseButtonUp(0)) trackpadSelectionActive = false;
        return trackpadSelectionActive && Input.GetMouseButton(0);
    }

    private bool IsSelectionPointerHeld()
    {
        return Input.GetMouseButton(2) || IsTrackpadSelectionGesture();
    }

    private bool IsSelectionPointerPressed()
    {
        bool trackpadPressed = activeTool != EditTool.None &&
            (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
            Input.GetMouseButtonDown(0);
        if (trackpadPressed) trackpadSelectionActive = true;
        return Input.GetMouseButtonDown(2) || trackpadPressed;
    }

    private bool IsSelectionPointerReleased()
    {
        bool trackpadReleased = trackpadSelectionActive && Input.GetMouseButtonUp(0);
        if (trackpadReleased) trackpadSelectionActive = false;
        return Input.GetMouseButtonUp(2) || trackpadReleased;
    }

    void HandleBrushTool()
    {
        bool pointerOverUI = editorUI != null && editorUI.IsMouseOverUI();
        bool pointerPressed = IsSelectionPointerPressed();
        bool pointerReleased = IsSelectionPointerReleased();
        if (pointerPressed && !pointerOverUI) BeginBrushStroke();
        if (pointerReleased) FinishBrushStroke();

        if (pointerOverUI)
        {
            if (brushVisual != null) brushVisual.SetActive(false);
            return;
        }

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        Vector3 hitPoint;
        bool hit = FindClosestPointOnRay(ray, out hitPoint);

        if (hit)
        {
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    brushRadius = Mathf.Clamp(brushRadius + scroll * 20f, 20f, 200f);
                }
            }

            Transform display = targetRenderer.DisplayTransform;
            Vector3 localBrushCenter = display.InverseTransformPoint(hitPoint);
            float localBrushRadius = targetRenderer.MillimetersToDataLength(brushRadius);
            Vector3 worldCenter = display.TransformPoint(localBrushCenter);
            Vector3 worldEdge = display.TransformPoint(localBrushCenter + Vector3.right * localBrushRadius);
            float worldRadius = Vector3.Distance(worldCenter, worldEdge);

            brushVisual.SetActive(true);
            brushVisual.transform.position = worldCenter;
            brushVisual.transform.localScale = Vector3.one * (worldRadius * 2f);

            if (brushStrokeActive && IsSelectionPointerHeld())
            {
                ApplyBrushSelection(hitPoint, brushStrokeSelecting, brushStrokeOnlyUnclassified,
                    brushStrokeChangedIndices);
            }
        }
        else
        {
            brushVisual.SetActive(false);
        }
    }

    void HandleMarqueeTool()
    {
        if (editorUI != null && editorUI.IsMouseOverUI() && !isDrawingMarquee)
        {
            return;
        }

        if (IsSelectionPointerPressed())
        {
            isDrawingMarquee = true;
            marqueeStart = Input.mousePosition;
        }

        if (isDrawingMarquee)
        {
            marqueeEnd = Input.mousePosition;

            if (IsSelectionPointerReleased())
            {
                isDrawingMarquee = false;
                ApplyMarqueeSelection();
            }
        }
    }

    // Cache list to avoid GC allocation spikes
    private List<int> searchCandidates = new List<int>();

    // High performance point search under mouse ray using local space transformation & Octree
    bool FindClosestPointOnRay(Ray worldRay, out Vector3 hitWorldPoint)
    {
        hitWorldPoint = Vector3.zero;
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return false;
        Vector3[] positions = targetRenderer.GetPositions();

        Matrix4x4 worldToLocal = targetRenderer.DisplayTransform.worldToLocalMatrix;
        Vector3 localOrigin = worldToLocal.MultiplyPoint(worldRay.origin);
        Vector3 localDir = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localOrigin, localDir);

        float localConeAngle = 0.01f; 
        float localCylinderRadius = 10f / targetRenderer.DisplayTransform.lossyScale.x;

        if (Camera.main != null)
        {
            if (Camera.main.orthographic)
            {
                float orthoSize = Camera.main.orthographicSize;
                localCylinderRadius = (orthoSize / Mathf.Max(1f, Screen.height * 0.5f)) * 10f;
                localCylinderRadius /= targetRenderer.DisplayTransform.lossyScale.x;
                localConeAngle = 0f;
            }
            else
            {
                localConeAngle = Mathf.Tan(Camera.main.fieldOfView * 0.5f * Mathf.Deg2Rad) * (10f / Mathf.Max(1f, Screen.height * 0.5f));
                localCylinderRadius = 0f;
            }
        }

        if (!pickDensityEnabled)
        {
            // Search for the nearest point to camera within picking cone (CC-compatible)
            float minProj = float.MaxValue;
            bool found = false;
            Vector3 bestLocalPoint = Vector3.zero;

            // Check if Octree is available and ready
            var octree = targetRenderer.Octree;
            bool useOctree = octree != null && targetRenderer.IsOctreeReady;

            if (useOctree)
            {
                // Traverse Octree recursively to find candidate points close to Ray
                TraverseRay(octree.root, localRay, localConeAngle, localCylinderRadius, ref minProj, ref bestLocalPoint, ref found, points);
            }
            else
            {
                // Fallback to legacy linear search if Octree is still building
                for (int i = 0; i < points.Length; i++)
                {
                    if ((points[i].label & 0x20000) != 0) continue; // skip deleted

                    Vector3 p = points[i].position;
                    Vector3 v = p - localRay.origin;
                    float proj = Vector3.Dot(v, localRay.direction);
                    if (proj < 0 || proj >= minProj) continue;

                    float currentRadius = localCylinderRadius + proj * localConeAngle;
                    float threshSq = currentRadius * currentRadius;

                    Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
                    float distSq = (p - closestPointOnRay).sqrMagnitude;
                    if (distSq < threshSq)
                    {
                        minProj = proj;
                        bestLocalPoint = p;
                        found = true;
                    }
                }
            }

            if (found)
            {
                hitWorldPoint = targetRenderer.DisplayTransform.TransformPoint(bestLocalPoint);
                return true;
            }
            return false;
        }

        // Density-based point picking
        pickCandidates.Clear();

        var octree2 = targetRenderer.Octree;
        bool useOctree2 = octree2 != null && targetRenderer.IsOctreeReady;

        if (useOctree2)
        {
            TraverseRayCandidates(octree2.root, localRay, localConeAngle, localCylinderRadius, points);
        }
        else
        {
            for (int i = 0; i < points.Length; i++)
            {
                if ((points[i].label & 0x20000) != 0) continue;

                Vector3 p = points[i].position;
                Vector3 v = p - localRay.origin;
                float proj = Vector3.Dot(v, localRay.direction);
                if (proj < 0) continue;

                float currentRadius = localCylinderRadius + proj * localConeAngle;
                float threshSq = currentRadius * currentRadius;

                Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
                float distSq = (p - closestPointOnRay).sqrMagnitude;
                if (distSq < threshSq)
                {
                    AddPickCandidate(i, p, proj);
                }
            }
        }

        if (pickCandidates.Count == 0) return false;

        // Sort candidates by distance from camera (proj)
        pickCandidates.Sort((a, b) => a.proj.CompareTo(b.proj));

        float pickRadiusLocal = targetRenderer.MillimetersToDataLength(PickDensityNeighborRadiusMillimeters);

        for (int i = 0; i < pickCandidates.Count; i++)
        {
            var cand = pickCandidates[i];
            int neighbors = CountNeighborsInRadius(cand.index, cand.position, pickRadiusLocal, points, positions);
            if (neighbors >= pickDensityMinCount)
            {
                hitWorldPoint = targetRenderer.DisplayTransform.TransformPoint(cand.position);
                return true;
            }
        }

        // Fallback: if all candidate points fail the density filter, pick the frontmost one
        hitWorldPoint = targetRenderer.DisplayTransform.TransformPoint(pickCandidates[0].position);
        return true;
    }

    private void TraverseRay(PointCloudOctree.Node node, Ray localRay, float localConeAngle, float localCylinderRadius, ref float minProj, ref Vector3 bestLocalPoint, ref bool found, PointData[] points)
    {
        if (node == null) return;

        // Ray vs Sphere check
        float distanceProjAtCenter = Vector3.Dot(node.center - localRay.origin, localRay.direction);
        float currentRadiusAtNode = localCylinderRadius + Mathf.Max(0f, distanceProjAtCenter) * localConeAngle;
        float expandedRadius = node.radius + currentRadiusAtNode;

        float distanceProj;
        if (!RaySphereIntersect(localRay, node.center, expandedRadius, out distanceProj))
        {
            return;
        }

        // Pruning: if the closest possible point of the sphere along the ray is further than minProj, skip
        float minPossibleProj = distanceProj - expandedRadius;
        if (minPossibleProj >= minProj)
        {
            return;
        }

        // Search points in this node
        foreach (int idx in node.pointIndices)
        {
            if ((points[idx].label & 0x20000) != 0) continue;

            Vector3 p = points[idx].position;
            Vector3 v = p - localRay.origin;
            float proj = Vector3.Dot(v, localRay.direction);
            if (proj < 0 || proj >= minProj) continue;

            float currentRadius = localCylinderRadius + proj * localConeAngle;
            float threshSq = currentRadius * currentRadius;

            Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
            float distSq = (p - closestPointOnRay).sqrMagnitude;
            if (distSq < threshSq)
            {
                minProj = proj;
                bestLocalPoint = p;
                found = true;
            }
        }

        // Recursively search children
        if (!node.isLeaf)
        {
            for (int i = 0; i < 8; i++)
            {
                if (node.children[i] != null)
                {
                    TraverseRay(node.children[i], localRay, localConeAngle, localCylinderRadius, ref minProj, ref bestLocalPoint, ref found, points);
                }
            }
        }
    }

    private bool RaySphereIntersect(Ray ray, Vector3 center, float radius, out float distanceProj)
    {
        distanceProj = 0f;
        Vector3 toCenter = center - ray.origin;
        distanceProj = Vector3.Dot(toCenter, ray.direction);

        if (distanceProj < 0)
        {
            // If origin is inside the sphere, it still intersects
            return toCenter.sqrMagnitude <= radius * radius;
        }

        Vector3 closestPoint = ray.origin + ray.direction * distanceProj;
        float distSq = (center - closestPoint).sqrMagnitude;
        return distSq <= radius * radius;
    }

    private void TraverseRayCandidates(PointCloudOctree.Node node, Ray localRay, float localConeAngle, float localCylinderRadius, PointData[] points)
    {
        if (node == null) return;

        float distanceProjAtCenter = Vector3.Dot(node.center - localRay.origin, localRay.direction);
        float currentRadiusAtNode = localCylinderRadius + Mathf.Max(0f, distanceProjAtCenter) * localConeAngle;
        float expandedRadius = node.radius + currentRadiusAtNode;

        float distanceProj;
        if (!RaySphereIntersect(localRay, node.center, expandedRadius, out distanceProj))
        {
            return;
        }

        // Pruning: if the closest possible point of the sphere is further than the max proj of the candidate list when full
        float minPossibleProj = distanceProj - expandedRadius;
        if (pickCandidates.Count >= 100 && minPossibleProj >= GetMaxCandidateProj())
        {
            return;
        }

        foreach (int idx in node.pointIndices)
        {
            if ((points[idx].label & 0x20000) != 0) continue;

            Vector3 p = points[idx].position;
            Vector3 v = p - localRay.origin;
            float proj = Vector3.Dot(v, localRay.direction);
            if (proj < 0) continue;

            if (pickCandidates.Count >= 100 && proj >= GetMaxCandidateProj()) continue;

            float currentRadius = localCylinderRadius + proj * localConeAngle;
            float threshSq = currentRadius * currentRadius;

            Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
            float distSq = (p - closestPointOnRay).sqrMagnitude;
            if (distSq < threshSq)
            {
                AddPickCandidate(idx, p, proj);
            }
        }

        if (!node.isLeaf)
        {
            for (int i = 0; i < 8; i++)
            {
                if (node.children[i] != null)
                {
                    TraverseRayCandidates(node.children[i], localRay, localConeAngle, localCylinderRadius, points);
                }
            }
        }
    }

    private void AddPickCandidate(int index, Vector3 position, float proj)
    {
        PickCandidate cand = new PickCandidate { index = index, position = position, proj = proj };
        if (pickCandidates.Count < 100)
        {
            pickCandidates.Add(cand);
        }
        else
        {
            int maxIdx = 0;
            float maxProj = pickCandidates[0].proj;
            for (int i = 1; i < pickCandidates.Count; i++)
            {
                if (pickCandidates[i].proj > maxProj)
                {
                    maxProj = pickCandidates[i].proj;
                    maxIdx = i;
                }
            }
            if (proj < maxProj)
            {
                pickCandidates[maxIdx] = cand;
            }
        }
    }

    private float GetMaxCandidateProj()
    {
        if (pickCandidates.Count == 0) return float.MaxValue;
        float maxProj = pickCandidates[0].proj;
        for (int i = 1; i < pickCandidates.Count; i++)
        {
            if (pickCandidates[i].proj > maxProj)
            {
                maxProj = pickCandidates[i].proj;
            }
        }
        return maxProj;
    }

    private int CountNeighborsInRadius(int centerIdx, Vector3 localPos, float radius, PointData[] points, Vector3[] positions)
    {
        int count = 0;
        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady && positions != null;

        if (useOctree)
        {
            neighborIndicesCache.Clear();
            octree.FindPointsWithinRadius(octree.root, localPos, radius, neighborIndicesCache, positions);
            for (int i = 0; i < neighborIndicesCache.Count; i++)
            {
                int idx = neighborIndicesCache[i];
                if (idx == centerIdx) continue;
                if ((points[idx].label & 0x20000) != 0) continue;
                count++;
            }
        }
        else
        {
            float rSq = radius * radius;
            for (int i = 0; i < points.Length; i++)
            {
                if (i == centerIdx) continue;
                if ((points[i].label & 0x20000) != 0) continue;

                float distSq = (points[i].position - localPos).sqrMagnitude;
                if (distSq <= rSq)
                {
                    count++;
                }
            }
        }
        return count;
    }

    private void BeginBrushStroke()
    {
        if (brushStrokeActive) FinishBrushStroke();
        brushStrokeActive = true;
        brushStrokeSelecting = brushSelectMode;
        brushStrokeOnlyUnclassified = selectOnlyUnclassified;
        brushStrokeChangedIndices.Clear();
    }

    private bool FinishBrushStroke()
    {
        if (!brushStrokeActive) return false;
        brushStrokeActive = false;
        if (brushStrokeChangedIndices.Count == 0) return false;

        PointLabelDelta delta = PointLabelDelta.ToggleConstant(
            brushStrokeChangedIndices.ToArray(), SelectedLabelBit, 16,
            (byte)(brushStrokeSelecting ? 0 : 1));
        PointData[] points = targetRenderer != null ? targetRenderer.GetPointData() : null;
        if (points == null || editHistoryGeneration != targetRenderer.DatasetGeneration ||
            !delta.MatchesExpected(points, false))
        {
            brushStrokeChangedIndices.Clear();
            PointCloudProgressManager.Instance.ShowError("選択Undo", "ストローク後に選択状態が変わったため、誤って上書きしないよう履歴へ登録しませんでした。");
            return false;
        }
        if (!editHistory.Record(delta))
        {
            bool restored = false;
            if (points != null)
            {
                try
                {
                    delta.Apply(points, false);
                    restored = targetRenderer.TryUpdatePointBuffer();
                    statsDirty = true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[RecoverableOperationError] ブラシ履歴を保存できず、選択状態の復元にも失敗しました: {ex}");
                }
            }
            PointCloudProgressManager.Instance.ShowError("選択Undo", restored
                ? "履歴メモリ上限を超えたため、ブラシストロークを取り消しました。"
                : "履歴保存に失敗し、表示の再同期も確認できません。点群を再読み込みしてください。");
            brushStrokeChangedIndices.Clear();
            return false;
        }
        brushStrokeRecordedFrame = Time.frameCount;
        brushStrokeChangedIndices.Clear();
        return true;
    }

    // Apply only changed indices. The stroke is recorded once when the pointer is released.
    void ApplyBrushSelection(Vector3 brushCenterWorld, bool selecting, bool onlyUnclassified,
        List<int> strokeChangedIndices)
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        Vector3 localBrushCenter = targetRenderer.DisplayTransform.InverseTransformPoint(brushCenterWorld);
        float localBrushRadius = targetRenderer.MillimetersToDataLength(brushRadius);
        float radiusSq = localBrushRadius * localBrushRadius;

        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        if (useOctree)
        {
            searchCandidates.Clear();
            TraverseBrush(octree.root, localBrushCenter, localBrushRadius, searchCandidates);
            ApplySelectionMutation("3Dブラシ選択", selecting, onlyUnclassified,
                searchCandidates, index => (points[index].position - localBrushCenter).sqrMagnitude <= radiusSq,
                strokeChangedIndices);
        }
        else
        {
            ApplySelectionMutation("3Dブラシ選択", selecting, onlyUnclassified, null,
                index => (points[index].position - localBrushCenter).sqrMagnitude <= radiusSq,
                strokeChangedIndices);
        }
    }

    private void TraverseBrush(PointCloudOctree.Node node, Vector3 localBrushCenter, float localBrushRadius, List<int> candidates)
    {
        if (node == null) return;

        // Check if node sphere overlaps with brush sphere
        float dist = Vector3.Distance(node.center, localBrushCenter);
        if (dist > node.radius + localBrushRadius)
        {
            return; // No overlap, prune branch
        }

        candidates.AddRange(node.pointIndices);

        if (node.isLeaf) return;

        for (int i = 0; i < 8; i++)
        {
            if (node.children[i] != null)
            {
                TraverseBrush(node.children[i], localBrushCenter, localBrushRadius, candidates);
            }
        }
    }

    // Apply marquee selection (Multi-threaded Parallel.For with Octree acceleration)
    void ApplyMarqueeSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        Vector2 min = Vector2.Min(marqueeStart, marqueeEnd);
        Vector2 max = Vector2.Max(marqueeStart, marqueeEnd);

        Rect selectRect = new Rect(
            min.x / Screen.width,
            min.y / Screen.height,
            (max.x - min.x) / Screen.width,
            (max.y - min.y) / Screen.height
        );

        Camera camera = Camera.main;
        if (camera == null) return;
        Matrix4x4 localToScreen = camera.projectionMatrix * camera.worldToCameraMatrix * targetRenderer.DisplayTransform.localToWorldMatrix;
        bool selecting = brushSelectMode;

        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        IList<int> candidates = null;
        if (useOctree)
        {
            searchCandidates.Clear();
            TraverseMarquee(octree.root, localToScreen, selectRect, searchCandidates);
            candidates = searchCandidates;
        }
        ApplySelectionMutation("矩形選択", selecting, selectOnlyUnclassified, candidates, index =>
        {
            Vector3 position = points[index].position;
            Vector4 clipPos = localToScreen * new Vector4(position.x, position.y, position.z, 1f);
            if (clipPos.w <= 0.0001f) return false;
            Vector2 screenPos = new Vector2(clipPos.x / clipPos.w * 0.5f + 0.5f,
                clipPos.y / clipPos.w * 0.5f + 0.5f);
            return selectRect.Contains(screenPos);
        });
    }

    private void TraverseMarquee(PointCloudOctree.Node node, Matrix4x4 localToScreen, Rect selectRect, List<int> candidates)
    {
        if (node == null) return;

        // Visual screen-space bounding box check to cull entire node
        if (!BoundsOverlapScreenRect(node.bounds, localToScreen, selectRect))
        {
            return;
        }

        candidates.AddRange(node.pointIndices);

        if (node.isLeaf) return;

        for (int i = 0; i < 8; i++)
        {
            if (node.children[i] != null)
            {
                TraverseMarquee(node.children[i], localToScreen, selectRect, candidates);
            }
        }
    }

    private bool BoundsOverlapScreenRect(Bounds bounds, Matrix4x4 localToScreen, Rect selectRect)
    {
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        Vector3[] corners = new Vector3[8]
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z)
        };

        float scrMinX = float.MaxValue;
        float scrMaxX = float.MinValue;
        float scrMinY = float.MaxValue;
        float scrMaxY = float.MinValue;

        bool anyInFront = false;

        for (int i = 0; i < 8; i++)
        {
            Vector4 clipPos = localToScreen * new Vector4(corners[i].x, corners[i].y, corners[i].z, 1f);
            if (clipPos.w > 0.0001f)
            {
                anyInFront = true;
                float ndcX = clipPos.x / clipPos.w;
                float ndcY = clipPos.y / clipPos.w;
                float scrX = ndcX * 0.5f + 0.5f;
                float scrY = ndcY * 0.5f + 0.5f;

                if (scrX < scrMinX) scrMinX = scrX;
                if (scrX > scrMaxX) scrMaxX = scrX;
                if (scrY < scrMinY) scrMinY = scrY;
                if (scrY > scrMaxY) scrMaxY = scrY;
            }
        }

        if (!anyInFront) return false;

        Rect boundsScreenRect = Rect.MinMaxRect(scrMinX, scrMinY, scrMaxX, scrMaxY);
        return selectRect.Overlaps(boundsScreenRect);
    }

    // --- GLOBAL EDIT OPERATIONS ---

    public void ClearSelection()
    {
        ApplySelectionMutation("選択クリア", false, false, null, null);
    }

    public void InvertSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;
        List<int> indices = CollectMatchingIndices(points,
            index => (points[index].label & DeletedLabelBit) == 0);
        if (indices.Count == 0) return;
        byte[] before = new byte[indices.Count];
        for (int i = 0; i < indices.Count; i++)
            before[i] = (byte)((points[indices[i]].label & SelectedLabelBit) >> 16);
        PointLabelDelta delta = PointLabelDelta.ToggleValues(indices.ToArray(), SelectedLabelBit, 16, before);
        TryCommitEditDelta(delta, "選択反転");
    }

    public void DeleteSelected()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;
        List<int> indices = CollectMatchingIndices(points, index =>
        {
            int label = points[index].label;
            return (label & SelectedLabelBit) != 0 && (label & DeletedLabelBit) == 0;
        });
        if (indices.Count == 0) return;
        TryCommitEditDelta(PointLabelDelta.ToggleConstant(indices.ToArray(),
            SelectedLabelBit | DeletedLabelBit, 16, 1), "選択点の削除");
    }

    public void RestoreDeleted()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;
        List<int> indices = CollectMatchingIndices(points,
            index => (points[index].label & DeletedLabelBit) != 0);
        if (indices.Count == 0) return;
        TryCommitEditDelta(PointLabelDelta.ToggleConstant(indices.ToArray(), DeletedLabelBit, 17, 1), "削除点の復元");
    }

    public void ClearAnnotationRedo()
    {
        editHistory.ClearRedo();
    }

    public bool AnnotationUndo()
    {
        if (brushStrokeActive)
        {
            bool hadChanges = brushStrokeChangedIndices.Count > 0;
            bool recorded = FinishBrushStroke();
            if (hadChanges && !recorded) return false;
        }
        return ApplyEditHistory(false);
    }

    public bool AnnotationRedo()
    {
        if (brushStrokeActive)
        {
            bool hadChanges = brushStrokeChangedIndices.Count > 0;
            FinishBrushStroke();
            if (hadChanges) return false;
        }
        return ApplyEditHistory(true);
    }

    public void ResetPointLabelHistory()
    {
        if (brushStrokeActive) FinishBrushStroke();
        editHistory.Clear();
        brushStrokeChangedIndices.Clear();
        editHistoryGeneration = targetRenderer != null ? targetRenderer.DatasetGeneration : -1;
    }

    public void ResetAnnotationHistory()
    {
        if (brushStrokeActive) FinishBrushStroke();
        editHistory.ClearDomain(PointLabelHistoryDomain.Annotation);
        brushStrokeChangedIndices.Clear();
        editHistoryGeneration = targetRenderer != null ? targetRenderer.DatasetGeneration : -1;
    }

    public void AssignLabelToSelected()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;
        int classVal = activeLabelClass & 0xFF;
        List<int> indices = CollectMatchingIndices(points,
            index => (points[index].label & SelectedLabelBit) != 0);
        if (indices.Count == 0) return;
        byte[] beforeClasses = new byte[indices.Count];
        for (int i = 0; i < indices.Count; i++)
            beforeClasses[i] = (byte)(points[indices[i]].label & 0xff);
        TryCommitEditDelta(PointLabelDelta.ClassAssignment(indices.ToArray(), beforeClasses, (byte)classVal),
            "選択点の分類");
    }

    private bool IsEditHistoryForCurrentCloud()
    {
        if (targetRenderer == null) return false;
        long generation = targetRenderer.DatasetGeneration;
        if (editHistoryGeneration < 0) editHistoryGeneration = generation;
        return editHistoryGeneration == generation;
    }

    private bool EnsureEditHistoryBound()
    {
        if (targetRenderer == null) return false;
        long generation = targetRenderer.DatasetGeneration;
        if (editHistoryGeneration == generation) return true;
        editHistory.Clear();
        brushStrokeChangedIndices.Clear();
        brushStrokeActive = false;
        editHistoryGeneration = generation;
        return true;
    }

    private List<int> CollectMatchingIndices(PointData[] points, Func<int, bool> predicate)
    {
        var matches = new List<int>();
        object gate = new object();
        Parallel.For(0, points.Length, () => new List<int>(), (index, _, local) =>
        {
            if (predicate(index)) local.Add(index);
            return local;
        }, local =>
        {
            lock (gate) matches.AddRange(local);
        });
        return matches;
    }

    private static bool HasValidUniqueIndices(int[] indices, int pointCount)
    {
        for (int i = 0; i < indices.Length; i++)
            if ((uint)indices[i] >= (uint)pointCount) return false;
        if (indices.Length < 2) return true;
        int[] sorted = (int[])indices.Clone();
        Array.Sort(sorted);
        for (int i = 1; i < sorted.Length; i++)
            if (sorted[i] == sorted[i - 1]) return false;
        return true;
    }

    private List<int> CollectSelectionChanges(PointData[] points, IList<int> candidates,
        bool selecting, bool onlyUnclassified, Func<int, bool> predicate)
    {
        int count = candidates != null ? candidates.Count : points.Length;
        var changed = new List<int>();
        object gate = new object();
        Parallel.For(0, count, () => new List<int>(), (offset, _, local) =>
        {
            int index = candidates != null ? candidates[offset] : offset;
            if ((uint)index >= (uint)points.Length) return local;
            if (predicate != null && !predicate(index)) return local;
            int label = points[index].label;
            if ((label & DeletedLabelBit) != 0 ||
                (selecting && onlyUnclassified && (label & 0xff) != 0)) return local;
            if (((label & SelectedLabelBit) != 0) == selecting) return local;
            local.Add(index);
            return local;
        }, local =>
        {
            lock (gate) changed.AddRange(local);
        });
        return changed;
    }

    private bool ApplySelectionMutation(string title, bool selecting, bool onlyUnclassified,
        IList<int> candidates, Func<int, bool> predicate, List<int> strokeTarget = null,
        PointCloudOperation allowedOperation = null)
    {
        if (!EnsureEditHistoryBound()) return false;
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return false;
        List<int> changed = CollectSelectionChanges(points, candidates, selecting, onlyUnclassified, predicate);
        if (changed.Count == 0) return false;
        if (strokeTarget != null)
            return TryApplyBrushStrokeChanges(title, points, changed, strokeTarget);
        byte beforeValue = (byte)(selecting ? 0 : 1);
        PointLabelDelta delta = PointLabelDelta.ToggleConstant(changed.ToArray(), SelectedLabelBit, 16, beforeValue);
        return TryCommitEditDelta(delta, title, allowedOperation: allowedOperation);
    }

    private bool TryApplyBrushStrokeChanges(string title, PointData[] points,
        List<int> changedIndices, List<int> strokeIndices)
    {
        if (PointCloudProgressManager.Instance.IsRunning || HasPendingRecovery || activeSelectionOperation != null)
            return false;

        long resultingBytes = 32L + ((long)strokeIndices.Count + changedIndices.Count) * sizeof(int);
        if (resultingBytes > editHistory.MaxBytesPerStack)
        {
            LastOperationFailure = $"Undo履歴の上限（{editHistory.MaxBytesPerStack / (1024 * 1024)} MiB/操作）を超えるため、変更しませんでした。";
            PointCloudProgressManager.Instance.ShowError(title, LastOperationFailure);
            return false;
        }

        for (int i = 0; i < changedIndices.Count; i++)
        {
            int index = changedIndices[i];
            PointData point = points[index];
            point.label ^= SelectedLabelBit;
            points[index] = point;
        }

        try
        {
            if (!targetRenderer.TryUpdatePointBuffer())
                throw new InvalidOperationException("GPU点群バッファを更新できません。");
        }
        catch (Exception applyException)
        {
            for (int i = 0; i < changedIndices.Count; i++)
            {
                int index = changedIndices[i];
                PointData point = points[index];
                point.label ^= SelectedLabelBit;
                points[index] = point;
            }
            bool restored = false;
            try { restored = targetRenderer.TryUpdatePointBuffer(); }
            catch (Exception rollbackException)
            {
                Debug.LogError($"[RecoverableOperationError] {title}: GPU失敗後の点群再同期に失敗しました。\n{applyException}\n{rollbackException}");
            }
            LastOperationFailure = restored
                ? "GPU更新失敗後、点群を変更前に戻しました。"
                : "GPU更新失敗後に点群表示を同期できませんでした。再読み込みしてください。";
            PointCloudProgressManager.Instance.ShowError(title, LastOperationFailure);
            return false;
        }

        strokeIndices.AddRange(changedIndices);
        statsDirty = true;
        LastOperationFailure = string.Empty;
        return true;
    }

    private bool TryCommitEditDelta(PointLabelDelta delta, string title,
        PointCloudOperation allowedOperation = null)
    {
        if (brushStrokeActive) FinishBrushStroke();
        LastOperationFailure = string.Empty;
        if (!EnsureEditHistoryBound() || delta == null || targetRenderer == null) return false;
        bool ownsRunningOperation = allowedOperation != null &&
            ReferenceEquals(activeSelectionOperation, allowedOperation) && allowedOperation.IsCurrent;
        if ((activeSelectionOperation != null && !ownsRunningOperation) ||
            (PointCloudProgressManager.Instance.IsRunning && !ownsRunningOperation) || HasPendingRecovery)
        {
            LastOperationFailure = "別の処理中、または復旧確認中のため編集を適用しませんでした。";
            return false;
        }

        if (!editHistory.CanRecord(delta))
        {
            LastOperationFailure = $"Undo履歴の上限（{editHistory.MaxBytesPerStack / (1024 * 1024)} MiB/操作）を超えるため、変更しませんでした。";
            PointCloudProgressManager.Instance.ShowError(title, LastOperationFailure);
            return false;
        }

        PointData[] points = targetRenderer.GetPointData();
        if (points == null || !delta.MatchesExpected(points, true))
        {
            LastOperationFailure = "対象ラベルが変更されているため、古い状態を上書きしませんでした。";
            return false;
        }
        try
        {
            delta.Apply(points, true);
            if (!targetRenderer.TryUpdatePointBuffer())
                throw new InvalidOperationException("GPU点群バッファを更新できません。");
        }
        catch (Exception applyException)
        {
            bool restored = false;
            try
            {
                delta.Apply(points, false);
                restored = targetRenderer.TryUpdatePointBuffer();
            }
            catch (Exception rollbackException)
            {
                Debug.LogError($"[RecoverableOperationError] {title}: CPUラベル復元またはGPU再同期に失敗しました。\n{applyException}\n{rollbackException}");
            }
            PointCloudProgressManager.Instance.ShowError(title, restored
                ? "点群を変更前に戻しました。操作を再試行できます。"
                : "点群状態の復元に失敗しました。表示とデータを確認してから再読み込みしてください。");
            LastOperationFailure = restored
                ? "GPUバッファ更新に失敗したため、CPU点群と表示を変更前に戻しました。"
                : "GPU更新失敗後に点群表示を同期できませんでした。";
            Debug.LogWarning($"[RecoverableOperationError] {title}: {applyException}");
            return false;
        }

        if (!editHistory.Record(delta))
        {
            bool restored = false;
            try
            {
                delta.Apply(points, false);
                restored = targetRenderer.TryUpdatePointBuffer();
            }
            catch (Exception rollbackException)
            {
                Debug.LogError($"[RecoverableOperationError] {title}: 履歴保存失敗後の復元に失敗しました。{rollbackException}");
            }
            LastOperationFailure = restored
                ? "Undo履歴を保存できなかったため、操作を取り消しました。"
                : "Undo履歴保存後の復元に失敗しました。点群を再読み込みしてください。";
            PointCloudProgressManager.Instance.ShowError(title, LastOperationFailure);
            return false;
        }

        statsDirty = true;
        LastOperationFailure = string.Empty;
        return true;
    }

    private bool ApplyEditHistory(bool forward)
    {
        if (!(forward ? CanAnnotationRedo : CanAnnotationUndo) || targetRenderer == null) return false;
        PointData[] points = targetRenderer.GetPointData();
        PointLabelDelta delta = forward ? editHistory.PeekRedo() : editHistory.PeekUndo();
        PointLabelHistoryDomain domain = forward ? editHistory.RedoDomain : editHistory.UndoDomain;
        if (points == null || delta == null || !delta.MatchesExpected(points, forward))
        {
            PointCloudProgressManager.Instance.ShowError(forward ? "やり直し" : "元に戻す",
                "対象ラベルが履歴と一致しないため適用しませんでした。新しい操作を優先して履歴を保持しました。");
            return false;
        }

        try
        {
            delta.Apply(points, forward);
            if (!targetRenderer.TryUpdatePointBuffer())
                throw new InvalidOperationException("GPU点群バッファを更新できません。");
        }
        catch (Exception applyException)
        {
            bool restored = false;
            try
            {
                delta.Apply(points, !forward);
                restored = targetRenderer.TryUpdatePointBuffer();
            }
            catch (Exception rollbackException)
            {
                Debug.LogError($"[RecoverableOperationError] 履歴適用後の復元に失敗しました。\n{applyException}\n{rollbackException}");
            }
            PointCloudProgressManager.Instance.ShowError(forward ? "やり直し" : "元に戻す",
                restored
                    ? "変更を適用できず、点群を変更前に戻しました。操作を再試行できます。"
                    : "変更適用に失敗し、描画との再同期も確認できません。点群を再読み込みしてください。");
            return false;
        }

        bool moved = forward ? editHistory.CompleteRedo() : editHistory.CompleteUndo();
        if (!moved)
        {
            Debug.LogError("[PointCloudEditor] 履歴スタックの移動に失敗しました。");
            return false;
        }
        if (domain == PointLabelHistoryDomain.Noise)
            NoiseFilterManager.Instance.NotifySharedHistoryApplied(targetRenderer, points);
        statsDirty = true;
        return true;
    }

    // Recalculate statistics for labels
    void RecalculateStats()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null) return;

        labelCountsMap.Clear();
        noiseDeletedCount = 0;
        visiblePointCount = 0;
        selectedPointCount = 0;

        // Initialize active classes to ensure 0 counts are shown
        if (annotationUI != null && annotationUI.GetActivePreset() != null)
        {
            foreach (var cls in annotationUI.GetActivePreset().classes)
            {
                labelCountsMap[cls.id] = 0;
            }
        }
        else
        {
            for (int i = 0; i <= 5; i++) labelCountsMap[i] = 0;
        }

        for (int i = 0; i < points.Length; i++)
        {
            int labelVal = points[i].label;
            bool isDeleted = (labelVal & 0x20000) != 0;
            bool isNoiseHidden = (labelVal & NoiseFilterManager.NOISE_HIDDEN_BIT) != 0;
            if (!isDeleted && !isNoiseHidden) visiblePointCount++;
            if (isDeleted)
            {
                noiseDeletedCount++;
            }
            else
            {
                int classId = labelVal & 0xFF; // 下位8ビット
                if (labelCountsMap.ContainsKey(classId))
                {
                    labelCountsMap[classId]++;
                }
                else
                {
                    labelCountsMap[classId] = 1;
                }
            }

            // 選択状態の点をカウント
            if ((labelVal & 0x10000) != 0)
            {
                selectedPointCount++;
            }
        }

        // Keep legacy array filled for fallback
        System.Array.Clear(labelCounts, 0, labelCounts.Length);
        for (int i = 0; i < 6; i++)
        {
            if (labelCountsMap.ContainsKey(i))
            {
                labelCounts[i] = labelCountsMap[i];
            }
        }
        labelCounts[6] = noiseDeletedCount;

        statsDirty = false;
    }

    // Exporter in background thread to prevent freezing
    public async Task ExportLabeledPointsAsync(string exportPath, bool asBinary = false, CancellationToken token = default,
        PointCloudOperation operation = null)
    {
        PointData[] points = targetRenderer.GetPointData();
        bool calibrated = !string.IsNullOrEmpty(GetCalibrationPlyMetadata());
        var request = new PlyExportRequest(points, exportPath, asBinary, calibrated, ExportPointMode.AllVisible);
        await Task.Run(() => PlyExportService.Write(request, token,
            (progress, message) => operation?.Update(progress, message)), token);
    }

    public void ExportLabeledPoints(bool asBinary = false)
    {
        _ = RunPointCloudExportAsync("_labeled", ExportPointMode.AllVisible, asBinary,
            "PLYファイル書き出し", null);
    }

    public void ExportCleanedPoints()
    {
        string outputRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend/output"));
        string reportSource = string.Empty;
        try
        {
            reportSource = Path.Combine(OutputGenerationStore.ResolveCurrentGeneration(outputRoot), "removal_report.json");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecoverableOperationError] 前回のノイズ解析レポートを解決できません: {ex.Message}");
        }
        _ = RunPointCloudExportAsync("_cleaned", ExportPointMode.CleanedVisible, false,
            "クリーンアップ済PLYエクスポート", reportSource);
    }

    public async Task ExportSelectedPointsAsync(string exportPath, bool asBinary = false, CancellationToken token = default,
        PointCloudOperation operation = null)
    {
        PointData[] points = targetRenderer.GetPointData();
        bool calibrated = !string.IsNullOrEmpty(GetCalibrationPlyMetadata());
        var request = new PlyExportRequest(points, exportPath, asBinary, calibrated, ExportPointMode.SelectedVisible);
        await Task.Run(() => PlyExportService.Write(request, token,
            (progress, message) => operation?.Update(progress, message)), token);
    }

    public void ExportSelectedPoints(bool asBinary = false)
    {
        _ = RunPointCloudExportAsync("_selected", ExportPointMode.SelectedVisible, asBinary,
            "選択点PLYファイル書き出し", null);
    }

    private async Task RunPointCloudExportAsync(string suffix, ExportPointMode mode, bool asBinary,
        string operationTitle, string removalReportSource)
    {
        PointCloudOperation operation = PointCloudProgressManager.Instance.TryStart(operationTitle, "書き出しデータ準備中...");
        if (operation == null) return;

        try
        {
            string inputPath = GetLoadedPointCloudPath();
            if (string.IsNullOrWhiteSpace(inputPath)) throw new IOException("ロード中の点群ファイルパスがありません。");
            string directory = Path.GetDirectoryName(Path.GetFullPath(inputPath));
            string outputPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(inputPath) + suffix + ".ply");
            PointData[] points = targetRenderer != null ? targetRenderer.GetPointData() : null;
            bool calibrated = !string.IsNullOrEmpty(GetCalibrationPlyMetadata());
            MeasurementDocument measurementSnapshot = CreateMeasurementSnapshotForExport();
            var request = new PlyExportRequest(points, outputPath, asBinary, calibrated, mode);
            CancellationToken token = operation.CancellationToken;

            PlyExportResult result = await Task.Run(
                () => PlyExportService.Write(request, token, (progress, message) => operation.Update(progress, message)), token);

            var warnings = new StringBuilder();
            var warningDetails = new StringBuilder();
            if (measurementSnapshot != null)
            {
                try
                {
                    await SaveDerivedMeasurementSidecarAsync(result.OutputPath, measurementSnapshot);
                }
                catch (Exception ex)
                {
                    warnings.AppendLine("計測JSONを保存できませんでした。");
                    warningDetails.AppendLine(ex.ToString());
                }
            }

            if (!string.IsNullOrEmpty(removalReportSource))
            {
                try
                {
                    string reportDestination = Path.Combine(directory, Path.GetFileNameWithoutExtension(inputPath) + "_removal_report.json");
                    await Task.Run(() =>
                    {
                        if (File.Exists(removalReportSource)) File.Copy(removalReportSource, reportDestination, true);
                    });
                }
                catch (Exception ex)
                {
                    warnings.AppendLine("除去レポートをコピーできませんでした。");
                    warningDetails.AppendLine(ex.ToString());
                }
            }

            if (warnings.Length > 0)
            {
                string message = "PLY本体は保存済みです。" + Environment.NewLine + warnings.ToString().Trim();
                string detail = warningDetails.ToString();
                operation.CompleteWithWarning(message, detail);
                Debug.LogWarning($"[RecoverableOperationError] {operationTitle}: {message}{Environment.NewLine}{detail}");
                return;
            }

            operation.Complete();
            Debug.Log($"[{operationTitle}] {result.VertexCount:N0} 点を保存しました: {result.OutputPath}");
        }
        catch (OperationCanceledException)
        {
            operation.CompleteCancelled();
            Debug.LogWarning($"[{operationTitle}] キャンセルされました。");
        }
        catch (Exception ex)
        {
            operation.Fail(operationTitle, "PLYを書き出せませんでした。詳細を確認してください。", ex.ToString());
            Debug.LogWarning($"[RecoverableOperationError] {operationTitle}: {ex}");
        }
    }

    private static async Task SaveDerivedMeasurementSidecarAsync(string outputPath, MeasurementDocument snapshot)
    {
        MeasurementDocument derived = MeasurementDocumentStore.CreateDerivedDocument(snapshot, outputPath);
        derived.sourceSha256 = await Task.Run(() => MeasurementDocumentStore.ComputeSha256(outputPath));
        string json = MeasurementDocumentStore.Serialize(derived);
        string sidecarPath = MeasurementDocumentStore.GetSidecarPath(outputPath);
        await Task.Run(() => MeasurementDocumentStore.WriteSerializedAtomic(sidecarPath, json));
    }

    // --- ADVANCED SELECTION IMPLEMENTATIONS ---

    void HandleLassoTool()
    {
        bool keyboardAvailable = GUIUtility.keyboardControl == 0 &&
            (editorUI == null || !editorUI.HasKeyboardInputFocus);
        if (keyboardAvailable && lassoPoints.Count > 0 && Input.GetKeyDown(KeyCode.Backspace))
        {
            lassoPoints.RemoveAt(lassoPoints.Count - 1);
            return;
        }
        if (keyboardAvailable && Input.GetKeyDown(KeyCode.Escape) && lassoPoints.Count > 0)
        {
            lassoPoints.Clear();
            return;
        }

        if (editorUI != null && editorUI.IsMouseOverUI() && lassoPoints.Count == 0)
        {
            if (brushVisual != null) brushVisual.SetActive(false);
            return;
        }

        if (brushVisual != null) brushVisual.SetActive(false);

        // Add a vertex with the middle button or Ctrl+left click for trackpads.
        if (IsSelectionPointerPressed())
        {
            if (editorUI == null || !editorUI.IsMouseOverUI())
            {
                lassoPoints.Add(Input.mousePosition);
            }
        }

        // Close and apply on Return key or Space key (Right-click removed to avoid camera rotation conflict)
        // テキスト入力フィールドにフォーカスがある場合はキー入力を無視する（IMEやBackspaceの競合を回避）
        bool pointerOverUi = editorUI != null && editorUI.IsMouseOverUI();
        if (!pointerOverUi && keyboardAvailable &&
            (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space)))
        {
            if (lassoPoints.Count >= 3)
            {
                ApplyLassoSelection();
            }
            lassoPoints.Clear();
        }
    }

    void ApplyLassoSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        List<Vector2> normalizedPolygon = new List<Vector2>(lassoPoints.Count);
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;

        foreach (Vector2 p in lassoPoints)
        {
            Vector2 norm = new Vector2(p.x / Screen.width, p.y / Screen.height);
            normalizedPolygon.Add(norm);
            minX = Mathf.Min(minX, norm.x);
            maxX = Mathf.Max(maxX, norm.x);
            minY = Mathf.Min(minY, norm.y);
            maxY = Mathf.Max(maxY, norm.y);
        }

        Rect polygonScreenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);

        Camera camera = Camera.main;
        if (camera == null) return;
        Matrix4x4 localToScreen = camera.projectionMatrix * camera.worldToCameraMatrix * targetRenderer.DisplayTransform.localToWorldMatrix;
        bool selecting = brushSelectMode;

        var octree = targetRenderer.Octree;
        bool useOctree = octree != null && targetRenderer.IsOctreeReady;

        IList<int> candidates = null;
        if (useOctree)
        {
            searchCandidates.Clear();
            TraverseMarquee(octree.root, localToScreen, polygonScreenRect, searchCandidates);
            candidates = searchCandidates;
        }
        ApplySelectionMutation("なげなわ選択", selecting, selectOnlyUnclassified, candidates, index =>
        {
            Vector3 position = points[index].position;
            Vector4 clipPos = localToScreen * new Vector4(position.x, position.y, position.z, 1f);
            if (clipPos.w <= 0.0001f) return false;
            Vector2 screenPos = new Vector2(clipPos.x / clipPos.w * 0.5f + 0.5f,
                clipPos.y / clipPos.w * 0.5f + 0.5f);
            return IsPointInPolygon(screenPos, normalizedPolygon);
        });
    }

    private bool IsPointInPolygon(Vector2 p, List<Vector2> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            if (((polygon[i].y > p.y) != (polygon[j].y > p.y)) &&
                (p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    void HandleConnectTool()
    {
        if (editorUI != null && editorUI.IsMouseOverUI())
        {
            if (brushVisual != null) brushVisual.SetActive(false);
            return;
        }

        if (brushVisual != null) brushVisual.SetActive(false);

        if (IsSelectionPointerPressed())
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            Vector3 hitPoint;
            int hitIndex;

            if (FindClosestPointIndexOnRay(ray, out hitIndex, out hitPoint))
            {
                ApplyConnectionSelection(hitIndex);
            }
        }
    }

    bool FindClosestPointIndexOnRay(Ray worldRay, out int hitIndex, out Vector3 hitWorldPoint)
    {
        hitIndex = -1;
        hitWorldPoint = Vector3.zero;
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return false;
        Vector3[] positions = targetRenderer.GetPositions();

        Matrix4x4 worldToLocal = targetRenderer.DisplayTransform.worldToLocalMatrix;
        Vector3 localOrigin = worldToLocal.MultiplyPoint(worldRay.origin);
        Vector3 localDir = worldToLocal.MultiplyVector(worldRay.direction).normalized;
        Ray localRay = new Ray(localOrigin, localDir);

        float localConeAngle = 0.01f; 
        float localCylinderRadius = 10f / targetRenderer.DisplayTransform.lossyScale.x;

        if (Camera.main != null)
        {
            if (Camera.main.orthographic)
            {
                float orthoSize = Camera.main.orthographicSize;
                localCylinderRadius = (orthoSize / Mathf.Max(1f, Screen.height * 0.5f)) * 10f;
                localCylinderRadius /= targetRenderer.DisplayTransform.lossyScale.x;
                localConeAngle = 0f;
            }
            else
            {
                localConeAngle = Mathf.Tan(Camera.main.fieldOfView * 0.5f * Mathf.Deg2Rad) * (10f / Mathf.Max(1f, Screen.height * 0.5f));
                localCylinderRadius = 0f;
            }
        }

        if (!pickDensityEnabled)
        {
            // Search for the nearest point to camera within picking cone (CC-compatible)
            float minProj = float.MaxValue;
            bool found = false;
            int bestIndex = -1;

            var octree = targetRenderer.Octree;
            bool useOctree = octree != null && targetRenderer.IsOctreeReady;

            if (useOctree)
            {
                TraverseRayIndex(octree.root, localRay, localConeAngle, localCylinderRadius, ref minProj, ref bestIndex, ref found, points);
            }
            else
            {
                for (int i = 0; i < points.Length; i++)
                {
                    if ((points[i].label & 0x20000) != 0) continue;

                    Vector3 p = points[i].position;
                    Vector3 v = p - localRay.origin;
                    float proj = Vector3.Dot(v, localRay.direction);
                    if (proj < 0 || proj >= minProj) continue;

                    float currentRadius = localCylinderRadius + proj * localConeAngle;
                    float threshSq = currentRadius * currentRadius;

                    Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
                    float distSq = (p - closestPointOnRay).sqrMagnitude;
                    if (distSq < threshSq)
                    {
                        minProj = proj;
                        bestIndex = i;
                        found = true;
                    }
                }
            }

            if (found)
            {
                hitIndex = bestIndex;
                hitWorldPoint = targetRenderer.DisplayTransform.TransformPoint(points[bestIndex].position);
                return true;
            }
            return false;
        }

        // Density-based point picking
        pickCandidates.Clear();

        var octree2 = targetRenderer.Octree;
        bool useOctree2 = octree2 != null && targetRenderer.IsOctreeReady;

        if (useOctree2)
        {
            TraverseRayCandidates(octree2.root, localRay, localConeAngle, localCylinderRadius, points);
        }
        else
        {
            for (int i = 0; i < points.Length; i++)
            {
                if ((points[i].label & 0x20000) != 0) continue;

                Vector3 p = points[i].position;
                Vector3 v = p - localRay.origin;
                float proj = Vector3.Dot(v, localRay.direction);
                if (proj < 0) continue;

                float currentRadius = localCylinderRadius + proj * localConeAngle;
                float threshSq = currentRadius * currentRadius;

                Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
                float distSq = (p - closestPointOnRay).sqrMagnitude;
                if (distSq < threshSq)
                {
                    AddPickCandidate(i, p, proj);
                }
            }
        }

        if (pickCandidates.Count == 0) return false;

        // Sort candidates by distance from camera (proj)
        pickCandidates.Sort((a, b) => a.proj.CompareTo(b.proj));

        float pickRadiusLocal = targetRenderer.MillimetersToDataLength(PickDensityNeighborRadiusMillimeters);

        for (int i = 0; i < pickCandidates.Count; i++)
        {
            var cand = pickCandidates[i];
            int neighbors = CountNeighborsInRadius(cand.index, cand.position, pickRadiusLocal, points, positions);
            if (neighbors >= pickDensityMinCount)
            {
                hitIndex = cand.index;
                hitWorldPoint = targetRenderer.DisplayTransform.TransformPoint(cand.position);
                return true;
            }
        }

        // Fallback: pick the frontmost one
        hitIndex = pickCandidates[0].index;
        hitWorldPoint = targetRenderer.DisplayTransform.TransformPoint(pickCandidates[0].position);
        return true;
    }

    private void TraverseRayIndex(PointCloudOctree.Node node, Ray localRay, float localConeAngle, float localCylinderRadius, ref float minProj, ref int bestIndex, ref bool found, PointData[] points)
    {
        if (node == null) return;

        float distanceProjAtCenter = Vector3.Dot(node.center - localRay.origin, localRay.direction);
        float currentRadiusAtNode = localCylinderRadius + Mathf.Max(0f, distanceProjAtCenter) * localConeAngle;
        float expandedRadius = node.radius + currentRadiusAtNode;

        float distanceProj;
        if (!RaySphereIntersect(localRay, node.center, expandedRadius, out distanceProj))
        {
            return;
        }

        // Pruning: if the closest possible point of the sphere along the ray is further than minProj, skip
        float minPossibleProj = distanceProj - expandedRadius;
        if (minPossibleProj >= minProj)
        {
            return;
        }

        foreach (int idx in node.pointIndices)
        {
            if ((points[idx].label & 0x20000) != 0) continue;

            Vector3 p = points[idx].position;
            Vector3 v = p - localRay.origin;
            float proj = Vector3.Dot(v, localRay.direction);
            if (proj < 0 || proj >= minProj) continue;

            float currentRadius = localCylinderRadius + proj * localConeAngle;
            float threshSq = currentRadius * currentRadius;

            Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
            float distSq = (p - closestPointOnRay).sqrMagnitude;
            if (distSq < threshSq)
            {
                minProj = proj;
                bestIndex = idx;
                found = true;
            }
        }

        if (!node.isLeaf)
        {
            for (int i = 0; i < 8; i++)
            {
                if (node.children[i] != null)
                {
                    TraverseRayIndex(node.children[i], localRay, localConeAngle, localCylinderRadius, ref minProj, ref bestIndex, ref found, points);
                }
            }
        }
    }

    void ApplyConnectionSelection(int startIdx)
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0 || startIdx < 0 || startIdx >= points.Length || maxConnectionPoints <= 0) return;

        float localRadius = targetRenderer.MillimetersToDataLength(connectionRadius);
        if (float.IsNaN(localRadius) || float.IsInfinity(localRadius) || localRadius <= 0f) return;
        Vector3[] positions = targetRenderer.GetPositions();
        if (positions == null || positions.Length != points.Length) return;

        bool selecting = brushSelectMode;
        bool onlyUnclassified = selectOnlyUnclassified;
        int maxLimit = maxConnectionPoints;
        PointCloudOperation operation = PointCloudProgressManager.Instance.TryStart("空間近接接続探索", "探索開始...");
        if (operation == null) return;
        activeSelectionOperation = operation;
        CancellationToken token = operation.CancellationToken;
        long datasetGeneration = targetRenderer.DatasetGeneration;

        Task.Run(() =>
        {
            int[] resultIndices = null;
            Exception error = null;
            try
            {
                int numPoints = points.Length;
                int numBuckets = numPoints;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                operation.Update(0f, "セル接続グリッド構築中...");

                float invCellSize = 1f / localRadius;
                float radiusSquared = localRadius * localRadius;
                lock (this)
                {
                    if (connQueue == null || connQueue.Length < numPoints) connQueue = new int[numPoints];
                    if (connCellBucketHead == null || connCellBucketHead.Length < numBuckets) connCellBucketHead = new int[numBuckets];
                    if (connCellNext == null || connCellNext.Length < numPoints ||
                        connCellX == null || connCellY == null || connCellZ == null ||
                        connCellPointHead == null || connPointNextInCell == null ||
                        connPointVisited == null || connPointVisited.Length < numPoints)
                    {
                        connCellNext = new int[numPoints];
                        connCellX = new int[numPoints];
                        connCellY = new int[numPoints];
                        connCellZ = new int[numPoints];
                        connCellPointHead = new int[numPoints];
                        connPointNextInCell = new int[numPoints];
                        connPointVisited = new bool[numPoints];
                    }
                }

                Array.Fill(connCellBucketHead, -1, 0, numBuckets);
                Array.Fill(connCellNext, -1, 0, numPoints);
                Array.Fill(connCellPointHead, -1, 0, numPoints);
                Array.Fill(connPointNextInCell, -1, 0, numPoints);
                Array.Clear(connPointVisited, 0, numPoints);

                int cellCount = 0;
                int startCell = -1;
                for (int i = 0; i < numPoints; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    if ((points[i].label & 0x20000) != 0) continue;
                    Vector3 pos = positions[i];
                    int vx = (int)Math.Floor(pos.x * invCellSize);
                    int vy = (int)Math.Floor(pos.y * invCellSize);
                    int vz = (int)Math.Floor(pos.z * invCellSize);
                    int hash = GetVoxelHash(vx, vy, vz, numBuckets);
                    int cell = connCellBucketHead[hash];
                    while (cell != -1 && (connCellX[cell] != vx || connCellY[cell] != vy || connCellZ[cell] != vz))
                        cell = connCellNext[cell];
                    if (cell == -1)
                    {
                        cell = cellCount++;
                        connCellX[cell] = vx; connCellY[cell] = vy; connCellZ[cell] = vz;
                        connCellNext[cell] = connCellBucketHead[hash];
                        connCellBucketHead[hash] = cell;
                    }
                    connPointNextInCell[i] = connCellPointHead[cell];
                    connCellPointHead[cell] = i;
                    if (i == startIdx) startCell = cell;
                }

                token.ThrowIfCancellationRequested();
                if (startCell >= 0)
                {
                    operation.Update(0.1f, "セル接続探索中...");
                    int queueHead = 0;
                    int qTail = 0;
                    long lastProgressUpdate = 0;
                    connQueue[qTail++] = startIdx;
                    connPointVisited[startIdx] = true;
                    while (queueHead < qTail && qTail < maxLimit)
                    {
                        token.ThrowIfCancellationRequested();
                        int currentIdx = connQueue[queueHead++];
                        Vector3 current = positions[currentIdx];
                        int cx = (int)Math.Floor(current.x * invCellSize);
                        int cy = (int)Math.Floor(current.y * invCellSize);
                        int cz = (int)Math.Floor(current.z * invCellSize);
                        for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            int hash = GetVoxelHash(cx + dx, cy + dy, cz + dz, numBuckets);
                            int neighborCell = connCellBucketHead[hash];
                            while (neighborCell != -1)
                            {
                                if (connCellX[neighborCell] == cx + dx && connCellY[neighborCell] == cy + dy && connCellZ[neighborCell] == cz + dz)
                                {
                                    for (int candidate = connCellPointHead[neighborCell]; candidate != -1 && qTail < maxLimit;
                                         candidate = connPointNextInCell[candidate])
                                    {
                                        if (connPointVisited[candidate]) continue;
                                        if ((positions[candidate] - current).sqrMagnitude > radiusSquared) continue;
                                        connPointVisited[candidate] = true;
                                        connQueue[qTail++] = candidate;
                                    }
                                }
                                neighborCell = connCellNext[neighborCell];
                            }
                        }
                        long elapsed = sw.ElapsedMilliseconds;
                        if (elapsed - lastProgressUpdate > 100)
                        {
                            lastProgressUpdate = elapsed;
                            operation.Update(0.1f + 0.8f * ((float)qTail / maxLimit),
                                $"実距離による接続探索中... 対象点: {qTail:N0} / {maxLimit:N0} 点");
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    resultIndices = new int[qTail];
                    Array.Copy(connQueue, resultIndices, qTail);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { error = ex; }
            finally
            {
                backgroundSelectionResults.Enqueue(new BackgroundSelectionResult
                {
                    Indices = resultIndices,
                    SourcePoints = points,
                    Selecting = selecting,
                    SelectOnlyUnclassified = onlyUnclassified,
                    Cancelled = token.IsCancellationRequested,
                    Error = error,
                    OperationTitle = "空間近接接続探索",
                    Operation = operation,
                    SourceGeneration = datasetGeneration
                });
            }
        });
    }

    private static int GetVoxelHash(int x, int y, int z, int numBuckets)
    {
        long hash = ((long)x * 73856093) ^ ((long)y * 19349663) ^ ((long)z * 83492791);
        return (int)((hash & 0x7FFFFFFFFFFFFFFF) % numBuckets);
    }

    public void ApplyRansacSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        Vector3[] positions = targetRenderer.GetPositions();
        if (positions == null || positions.Length == 0) return;

        Transform trans = targetRenderer.transform;
        float localTolerance = targetRenderer.MillimetersToDataLength(ransacTolerance);
        float fallbackRadiusLocal = targetRenderer.MillimetersToDataLength(10f);
        float minimumRadiusLocal = targetRenderer.MillimetersToDataLength(0.1f);
        float colorTol = ransacColorTolerance;
        RansacType type = ransacType;
        bool selecting = brushSelectMode;

        // カメラから見た上・右・前方向ベクトルをローカル空間に変換 (スレッドセーフ化のためメインスレッドで事前取得)
        Camera mainCam = Camera.main;
        Vector3 camUpWorld = mainCam != null ? mainCam.transform.up : Vector3.up;
        Vector3 camRightWorld = mainCam != null ? mainCam.transform.right : Vector3.right;
        Vector3 camForwardWorld = mainCam != null ? mainCam.transform.forward : Vector3.forward;

        Vector3 localUp = trans.InverseTransformDirection(camUpWorld).normalized;
        Vector3 localRight = trans.InverseTransformDirection(camRightWorld).normalized;
        Vector3 localForward = trans.InverseTransformDirection(camForwardWorld).normalized;

        PointCloudOperation operation = PointCloudProgressManager.Instance.TryStart($"RANSAC検出 ({type})", "点群データを解析中...");
        if (operation == null) return;
        activeSelectionOperation = operation;
        CancellationToken token = operation.CancellationToken;
        long datasetGeneration = targetRenderer.DatasetGeneration;
        bool[] resultMask = null;
        Exception operationError = null;

        Task.Run(() =>
        {
            try
            {
                List<int> activeIndices = new List<int>(points.Length / 2);
                List<int> selectedIndices = new List<int>(points.Length / 100);

                for (int i = 0; i < points.Length; i++)
                {
                    if (token.IsCancellationRequested) return;
                    int label = points[i].label;
                    if ((label & 0x20000) == 0)
                    {
                        if (selecting && selectOnlyUnclassified && (label & 0xFF) != 0) continue;
                        activeIndices.Add(i);
                        if ((label & 0x10000) != 0)
                        {
                            selectedIndices.Add(i);
                        }
                    }
                }

                if (activeIndices.Count < 3)
                {
                    throw new InvalidOperationException("RANSACには3点以上の有効な点が必要です。");
                }

                bool isSelectedPointFit = (selectedIndices.Count >= 3);
                List<int> fitSourceIndices = isSelectedPointFit ? selectedIndices : activeIndices;

                // PCA (主成分分析) で支柱の軸 (pcaUp) を決定
                Vector3 pcaUp = localUp;
                Color32 targetAvgColor = new Color32(0, 0, 0, 0);
                bool hasColorConstraint = false;
                float rEst = fallbackRadiusLocal;

                if (isSelectedPointFit)
                {
                    // 1. 重心および平均色の計算
                    Vector3 mean = Vector3.zero;
                    long totalR = 0, totalG = 0, totalB = 0;
                    for (int i = 0; i < selectedIndices.Count; i++)
                    {
                        int idx = selectedIndices[i];
                        mean += positions[idx];
                        Color32 col = PointData.UnpackColor(points[idx].originalColor);
                        totalR += col.r;
                        totalG += col.g;
                        totalB += col.b;
                    }
                    mean /= selectedIndices.Count;
                    targetAvgColor = new Color32(
                        (byte)(totalR / selectedIndices.Count),
                        (byte)(totalG / selectedIndices.Count),
                        (byte)(totalB / selectedIndices.Count),
                        255
                    );
                    hasColorConstraint = true;

                    // 2. 分散共分散行列 (3x3) の計算
                    float cxx = 0, cxy = 0, cxz = 0;
                    float cyy = 0, cyz = 0, czz = 0;
                    for (int i = 0; i < selectedIndices.Count; i++)
                    {
                        Vector3 diff = positions[selectedIndices[i]] - mean;
                        cxx += diff.x * diff.x;
                        cxy += diff.x * diff.y;
                        cxz += diff.x * diff.z;
                        cyy += diff.y * diff.y;
                        cyz += diff.y * diff.z;
                        czz += diff.z * diff.z;
                    }
                    int count = selectedIndices.Count;
                    cxx /= count; cxy /= count; cxz /= count;
                    cyy /= count; cyz /= count; czz /= count;

                    // 3. べき乗法 (Power Iteration) による第一主成分（主要な伸びの軸）の算出
                    Vector3 v = localUp;
                    if (v.sqrMagnitude < 0.01f) v = Vector3.up;
                    for (int iter = 0; iter < 15; iter++)
                    {
                        float nx = cxx * v.x + cxy * v.y + cxz * v.z;
                        float ny = cxy * v.x + cyy * v.y + cyz * v.z;
                        float nz = cxz * v.x + cyz * v.y + czz * v.z;
                        Vector3 nextV = new Vector3(nx, ny, nz);
                        float mag = nextV.magnitude;
                        if (mag < 0.0001f) break;
                        v = nextV / mag;
                    }
                    pcaUp = v.normalized;

                    // 4. 選択された点群からPCA軸までの平均距離 (rEst) を算出（座標系のスケールを自動抽出）
                    Vector3 rAxis = Vector3.Cross(pcaUp, Math.Abs(pcaUp.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
                    Vector3 fAxis = Vector3.Cross(rAxis, pcaUp).normalized;
                    float totalDist = 0f;
                    Vector2 meanProj = new Vector2(Vector3.Dot(mean, rAxis), Vector3.Dot(mean, fAxis));
                    for (int i = 0; i < selectedIndices.Count; i++)
                    {
                        Vector3 p = positions[selectedIndices[i]];
                        Vector2 proj = new Vector2(Vector3.Dot(p, rAxis), Vector3.Dot(p, fAxis));
                        totalDist += Vector2.Distance(proj, meanProj);
                    }
                    rEst = totalDist / selectedIndices.Count;
                    if (rEst < minimumRadiusLocal) rEst = fallbackRadiusLocal;
                }

                object locker = new object();
                object progressLocker = new object();
                int bestInlierCount = 0;
                
                // Keep track of best model parameters instead of huge inlier list to avoid GC allocation spikes
                Vector4 bestPlaneEq = Vector4.zero;
                Vector2 bestCylinderCenter = Vector2.zero;
                float bestCylinderRadius = 0f;
                Vector3 bestCylinderUp = pcaUp;
                Vector3 bestCylinderRight = Vector3.Cross(pcaUp, Math.Abs(pcaUp.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
                Vector3 bestCylinderForward = Vector3.Cross(bestCylinderRight, pcaUp).normalized;

                int iterations = 250;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                long lastProgressUpdate = 0;

                if (type == RansacType.Plane)
                {
                    Parallel.For(0, iterations, (iter, state) =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            state.Stop();
                            return;
                        }

                        // Throttle progress updates to avoid lock contention and string allocation bottleneck
                        long elapsed = sw.ElapsedMilliseconds;
                        if (elapsed - lastProgressUpdate > 50)
                        {
                            lock (progressLocker)
                            {
                                if (sw.ElapsedMilliseconds - lastProgressUpdate > 50)
                                {
                                    lastProgressUpdate = sw.ElapsedMilliseconds;
                                    operation.Update((float)iter / iterations, $"平面フィッティング中... (イテレーション {iter}/{iterations})");
                                }
                            }
                        }

                        // Thread-local random source using unique seeds
                        var rand = new System.Random(System.Guid.NewGuid().GetHashCode() + iter);

                        int idx1 = activeIndices[rand.Next(0, activeIndices.Count)];
                        int idx2 = activeIndices[rand.Next(0, activeIndices.Count)];
                        int idx3 = activeIndices[rand.Next(0, activeIndices.Count)];

                        if (idx1 == idx2 || idx2 == idx3 || idx1 == idx3) return;

                        Vector3 p1 = positions[idx1];
                        Vector3 p2 = positions[idx2];
                        Vector3 p3 = positions[idx3];

                        Vector3 normal = Vector3.Cross(p2 - p1, p3 - p1).normalized;
                        if (normal.sqrMagnitude < 0.001f) return;

                        float d = -Vector3.Dot(normal, p1);
                        Vector4 planeEq = new Vector4(normal.x, normal.y, normal.z, d);

                        // Allocation-free inlier counting, cache-friendly vector lookup
                        int currentInlierCount = 0;
                        for (int i = 0; i < activeIndices.Count; i++)
                        {
                            int idx = activeIndices[i];
                            Vector3 p = positions[idx];
                            float dist = (float)Math.Abs(planeEq.x * p.x + planeEq.y * p.y + planeEq.z * p.z + planeEq.w);
                            if (dist < localTolerance)
                            {
                                currentInlierCount++;
                            }
                        }

                        lock (locker)
                        {
                            if (currentInlierCount > bestInlierCount)
                            {
                                bestInlierCount = currentInlierCount;
                                bestPlaneEq = planeEq;
                            }
                        }
                    });
                }
                else
                {
                    // PCA軸に直交する基底を作る
                    Vector3 uAxis = Vector3.Cross(pcaUp, Math.Abs(pcaUp.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
                    Vector3 vAxis = Vector3.Cross(pcaUp, uAxis).normalized;

                    Parallel.For(0, iterations, (iter, state) =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            state.Stop();
                            return;
                        }

                        long elapsed = sw.ElapsedMilliseconds;
                        if (elapsed - lastProgressUpdate > 50)
                        {
                            lock (progressLocker)
                            {
                                if (sw.ElapsedMilliseconds - lastProgressUpdate > 50)
                                {
                                    lastProgressUpdate = sw.ElapsedMilliseconds;
                                    string msg = isSelectedPointFit ? "選択点から支柱フィッティング中..." : "鉛直円柱フィッティング中...";
                                    operation.Update((float)iter / iterations, $"{msg} (イテレーション {iter}/{iterations})");
                                }
                            }
                        }

                        // Thread-local random source using unique seeds
                        var rand = new System.Random(System.Guid.NewGuid().GetHashCode() + iter);

                        // PCA軸からの揺らぎ（最大5度）を考慮した localUp_iter の生成
                        float maxAngleRad = (float)(5.0 * Math.PI / 180.0);
                        float theta = (float)(rand.NextDouble() * maxAngleRad);
                        float phi = (float)(rand.NextDouble() * 2.0 * Math.PI);

                        // 傾斜した軸ベクトル
                        Vector3 localUp_iter = (pcaUp * (float)Math.Cos(theta) + (uAxis * (float)Math.Cos(phi) + vAxis * (float)Math.Sin(phi)) * (float)Math.Sin(theta)).normalized;

                        // localUp_iter に直交する直交座標系 (localRight_iter, localForward_iter) を構築
                        Vector3 localRight_iter = Vector3.Cross(localUp_iter, localForward).normalized;
                        if (localRight_iter.sqrMagnitude < 0.001f)
                        {
                            localRight_iter = Vector3.Cross(localUp_iter, localRight).normalized;
                        }
                        Vector3 localForward_iter = Vector3.Cross(localRight_iter, localUp_iter).normalized;

                        // 母集団 fitSourceIndices から3点をランダムに選択
                        int idx1 = fitSourceIndices[rand.Next(0, fitSourceIndices.Count)];
                        int idx2 = fitSourceIndices[rand.Next(0, fitSourceIndices.Count)];
                        int idx3 = fitSourceIndices[rand.Next(0, fitSourceIndices.Count)];

                        if (idx1 == idx2 || idx2 == idx3 || idx1 == idx3) return;

                        Vector3 p1 = positions[idx1];
                        Vector3 p2 = positions[idx2];
                        Vector3 p3 = positions[idx3];

                        Vector2 a = new Vector2(Vector3.Dot(p1, localRight_iter), Vector3.Dot(p1, localForward_iter));
                        Vector2 b = new Vector2(Vector3.Dot(p2, localRight_iter), Vector3.Dot(p2, localForward_iter));
                        Vector2 c = new Vector2(Vector3.Dot(p3, localRight_iter), Vector3.Dot(p3, localForward_iter));

                        // 投影点の相互距離が 0.2 * rEst 未満の場合は数値的安定性のために即時棄却 (一直線上サンプリング防止)
                        float minDistSqr = (rEst * 0.2f) * (rEst * 0.2f);
                        if (Vector2.SqrMagnitude(a - b) < minDistSqr ||
                            Vector2.SqrMagnitude(b - c) < minDistSqr ||
                            Vector2.SqrMagnitude(c - a) < minDistSqr) return;

                        float dVal = 2f * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y));
                        if (Math.Abs(dVal) < 0.0001f) return;

                        float xc = ((a.x * a.x + a.y * a.y) * (b.y - c.y) + (b.x * b.x + b.y * b.y) * (c.y - a.y) + (c.x * c.x + c.y * c.y) * (a.y - b.y)) / dVal;
                        float zc = ((a.x * a.x + a.y * a.y) * (c.x - b.x) + (b.x * b.x + b.y * b.y) * (a.x - c.x) + (c.x * c.x + c.y * c.y) * (b.x - a.x)) / dVal;

                        Vector2 center = new Vector2(xc, zc);
                        float radius = Vector2.Distance(a, center);

                        // 基準半径 rEst に基づく相対半径制限（0.4倍〜2.0倍）により、植物巻き込みを防止
                        if (radius < rEst * 0.4f || radius > rEst * 2.0f) return;

                        // インライア判定の誤差許容度（REstの0.5倍以下に動的制限し、植物侵入を排除）
                        float toleranceWithSlack = Math.Min(localTolerance * 1.5f, rEst * 0.5f);

                        // 母集団 fitSourceIndices に対してインライア数を数える
                        int currentInlierCount = 0;
                        for (int i = 0; i < fitSourceIndices.Count; i++)
                        {
                            int idx = fitSourceIndices[i];
                            Vector3 p = positions[idx];
                            Vector2 projP = new Vector2(Vector3.Dot(p, localRight_iter), Vector3.Dot(p, localForward_iter));
                            float distFromCenter = Vector2.Distance(projP, center);
                            float error = (float)Math.Abs(distFromCenter - radius);

                            if (error < toleranceWithSlack)
                            {
                                // 色情報によるフィルタリング（ハサミや葉っぱの混入防止）
                                if (hasColorConstraint)
                                {
                                    Color32 col = PointData.UnpackColor(points[idx].originalColor);
                                    float rDiff = (float)col.r - targetAvgColor.r;
                                    float gDiff = (float)col.g - targetAvgColor.g;
                                    float bDiff = (float)col.b - targetAvgColor.b;
                                    float colorDist = (float)Math.Sqrt(rDiff * rDiff + gDiff * gDiff + bDiff * bDiff);
                                    if (colorDist > colorTol) continue;
                                }
                                currentInlierCount++;
                            }
                        }

                        lock (locker)
                        {
                            if (currentInlierCount > bestInlierCount)
                            {
                                bestInlierCount = currentInlierCount;
                                bestCylinderCenter = center;
                                bestCylinderRadius = radius;
                                bestCylinderUp = localUp_iter;
                                bestCylinderRight = localRight_iter;
                                bestCylinderForward = localForward_iter;
                            }
                        }
                    });
                }

                if (!token.IsCancellationRequested && bestInlierCount > 0)
                {
                    operation.Update(0.95f, "適合データを点群に適用中...");
                    
                    // 円柱時の許容誤差を rEst * 0.5f に自動的に引き締め
                    float toleranceWithSlack = (type == RansacType.Plane) ? localTolerance * 1.5f : Math.Min(localTolerance * 1.5f, rEst * 0.5f);

                    resultMask = new bool[points.Length];
                    Parallel.For(0, activeIndices.Count, i =>
                    {
                        int idx = activeIndices[i];
                        Vector3 p = positions[idx];
                        float dist = 0f;

                        if (type == RansacType.Plane)
                        {
                            dist = (float)Math.Abs(bestPlaneEq.x * p.x + bestPlaneEq.y * p.y + bestPlaneEq.z * p.z + bestPlaneEq.w);
                        }
                        else
                        {
                            Vector2 projP = new Vector2(Vector3.Dot(p, bestCylinderRight), Vector3.Dot(p, bestCylinderForward));
                            float distFromCenter = Vector2.Distance(projP, bestCylinderCenter);
                            dist = (float)Math.Abs(distFromCenter - bestCylinderRadius);
                        }

                        float allowedTolerance = (type == RansacType.Plane) ? localTolerance : toleranceWithSlack;

                        if (dist < allowedTolerance)
                        {
                            // 円柱検出かつ色制約ありの場合、最終適用でも色をチェックしてハサミや葉っぱを除外する
                            if (type == RansacType.Cylinder && hasColorConstraint)
                            {
                                Color32 col = PointData.UnpackColor(points[idx].originalColor);
                                float rDiff = (float)col.r - targetAvgColor.r;
                                float gDiff = (float)col.g - targetAvgColor.g;
                                float bDiff = (float)col.b - targetAvgColor.b;
                                float colorDist = (float)Math.Sqrt(rDiff * rDiff + gDiff * gDiff + bDiff * bDiff);
                                if (colorDist > colorTol) return;
                            }

                            resultMask[idx] = true;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                operationError = ex;
            }
            finally
            {
                backgroundSelectionResults.Enqueue(new BackgroundSelectionResult
                {
                    Mask = resultMask,
                    SourcePoints = points,
                    Selecting = selecting,
                    Cancelled = token.IsCancellationRequested,
                    Error = operationError,
                    OperationTitle = $"RANSAC検出 ({type})",
                    Operation = operation,
                    SourceGeneration = datasetGeneration
                });
            }
        });
    }

    public void ApplySupportCylinderFromSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        List<int> selectedIndices = new List<int>(4096);
        for (int i = 0; i < points.Length; i++)
        {
            int label = points[i].label;
            if ((label & 0x20000) == 0 && (label & 0x10000) != 0) selectedIndices.Add(i);
        }
        if (selectedIndices.Count < 12)
        {
            PointCloudProgressManager.Instance.ShowError("支柱抽出", "先に支柱の一部を12点以上選択してください。");
            return;
        }

        string inputPath = GetLoadedPointCloudPath();
        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
        {
            var error = new FileNotFoundException("現在ロード中のPLYファイルが見つかりません。", inputPath);
            PointCloudProgressManager.Instance.ShowError("支柱抽出", error.Message);
            Debug.LogWarning($"[RecoverableOperationError] 支柱抽出: {error}");
            return;
        }

        bool selecting = brushSelectMode;
        float tubeMultiplier = supportTubeMultiplier;
        float colorTolerance = supportColorTolerance;
        float heightBinMultiplier = supportHeightBinMultiplier;
        float coordinateScaleToMm = targetRenderer.DisplayScale;
        int maxEmptyBins = supportMaxEmptyBins;
        string backendDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend"));
        string outputDir = Path.Combine(backendDir, "output_support");
        string seedPath = Path.Combine(outputDir, "support_seed_indices.bin");
        string maskPath = Path.Combine(outputDir, "support_mask.bin");
        string scriptPath = Path.Combine(backendDir, "run_support_cylinder.py");
        string pythonPath = PythonBridge.GetPythonPath();

        PointCloudOperation operation = PointCloudProgressManager.Instance.TryStart("支柱抽出 Python", "Pythonバックエンドを起動中...");
        if (operation == null) return;
        activeSelectionOperation = operation;
        CancellationToken token = operation.CancellationToken;
        long datasetGeneration = targetRenderer.DatasetGeneration;
        _ = RunSupportCylinderAsync(points, selectedIndices.ToArray(), inputPath, seedPath, maskPath,
            outputDir, scriptPath, pythonPath, coordinateScaleToMm, tubeMultiplier, colorTolerance,
            heightBinMultiplier, maxEmptyBins, selecting, operation, datasetGeneration);
    }

    private async Task RunSupportCylinderAsync(PointData[] points, int[] seedIndices, string inputPath,
        string seedPath, string maskPath, string outputDir, string scriptPath, string pythonPath,
        float coordinateScaleToMm, float tubeMultiplier, float colorTolerance, float heightBinMultiplier,
        int maxEmptyBins, bool selecting, PointCloudOperation operation, long datasetGeneration)
    {
        CancellationToken token = operation.CancellationToken;
        try
        {
            await PythonBridge.EnsureEnvironmentReadyAsync(token, operation);
            pythonPath = PythonBridge.GetPythonPath();
            byte[] mask = await Task.Run(() =>
            {
                Directory.CreateDirectory(outputDir);
                using (BinaryWriter writer = new BinaryWriter(File.Open(seedPath, FileMode.Create, FileAccess.Write)))
                    for (int i = 0; i < seedIndices.Length; i++) writer.Write(seedIndices[i]);

                string args = $"-u \"{scriptPath}\"" +
                              $" --input \"{inputPath}\"" +
                              $" --seed_indices \"{seedPath}\"" +
                              $" --output_dir \"{outputDir}\"" +
                              $" --coordinate-scale-to-mm {coordinateScaleToMm.ToString(CultureInfo.InvariantCulture)}" +
                              $" --tube_multiplier {tubeMultiplier.ToString(CultureInfo.InvariantCulture)}" +
                              $" --color_tolerance {colorTolerance.ToString(CultureInfo.InvariantCulture)}" +
                              $" --height_bin_multiplier {heightBinMultiplier.ToString(CultureInfo.InvariantCulture)}" +
                              $" --max_empty_bins {maxEmptyBins}";
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                object logLock = new object();
                using (var process = new System.Diagnostics.Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        lock (logLock) stdout.AppendLine(e.Data);
                        if (e.Data.StartsWith("[Progress]", StringComparison.Ordinal))
                        {
                            string rest = e.Data.Substring(10).Trim();
                            int split = rest.IndexOf(' ');
                            if (split > 0 && float.TryParse(rest.Substring(0, split), NumberStyles.Any,
                                CultureInfo.InvariantCulture, out float percent))
                                operation.Update(0.05f + 0.85f * (percent / 100f), rest.Substring(split + 1));
                        }
                    };
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data != null) lock (logLock) stderr.AppendLine(e.Data);
                    };
                    if (!process.Start()) throw new InvalidOperationException("Python支柱抽出プロセスを開始できませんでした。");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    try
                    {
                        while (!process.WaitForExit(100)) token.ThrowIfCancellationRequested();
                        process.WaitForExit();
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        process.WaitForExit();
                        throw;
                    }

                    if (process.ExitCode != 0)
                    {
                        string outText;
                        string errorText;
                        lock (logLock) { outText = stdout.ToString(); errorText = stderr.ToString(); }
                        throw new InvalidOperationException(
                            $"Python支柱抽出がエラーで終了しました (ExitCode: {process.ExitCode})\n[stderr]\n{errorText}\n[stdout]\n{outText}");
                    }
                }

                token.ThrowIfCancellationRequested();
                if (!File.Exists(maskPath)) throw new FileNotFoundException("support_mask.bin が見つかりません。", maskPath);
                byte[] result = File.ReadAllBytes(maskPath);
                if (result.Length != points.Length)
                    throw new InvalidDataException($"支柱マスクの点数が一致しません。mask={result.Length}, points={points.Length}");
                return result;
            }, token);

            ApplyBackgroundSelectionResult(new BackgroundSelectionResult
            {
                Mask = Array.ConvertAll(mask, value => value != 0),
                SourcePoints = points,
                Selecting = selecting,
                OperationTitle = "支柱抽出",
                Operation = operation,
                SourceGeneration = datasetGeneration
            });
        }
        catch (OperationCanceledException)
        {
            operation.CompleteCancelled("支柱抽出をキャンセルしました。選択状態は変更していません。");
            ClearActiveSelectionOperation(operation);
        }
        catch (Exception ex)
        {
            operation.Fail("支柱抽出", "Pythonによる支柱抽出に失敗しました。選択状態は変更していません。", ex.ToString());
            ClearActiveSelectionOperation(operation);
            Debug.LogWarning($"[RecoverableOperationError] 支柱抽出: {ex}");
        }
    }

    public void ApplyAttributeFilterSelection()
    {
        PointData[] points = targetRenderer.GetPointData();
        if (points == null || points.Length == 0) return;

        bool selecting = brushSelectMode;
        float localFilterMin = filterMin;
        float localFilterMax = filterMax;
        if (filterType == FilterType.Height)
        {
            localFilterMin = targetRenderer.MillimetersToDataLength(filterMin);
            localFilterMax = targetRenderer.MillimetersToDataLength(filterMax);
        }
        FilterType selectedFilter = filterType;
        float requestedMin = filterMin;
        float requestedMax = filterMax;
        ApplySelectionMutation("属性フィルタ選択", selecting, selectOnlyUnclassified, null, i =>
        {
            float value;
            if (selectedFilter == FilterType.Height) value = points[i].position.y;
            else if (selectedFilter == FilterType.Distance) value = points[i].distance;
            else
            {
                Color32 color = PointData.UnpackColor(points[i].originalColor);
                value = selectedFilter == FilterType.Redness
                    ? (float)color.r / Math.Max(1f, (float)color.g + color.b)
                    : (float)color.g / Math.Max(1f, (float)color.r + color.b);
            }
            float min = selectedFilter == FilterType.Height || selectedFilter == FilterType.Distance ? localFilterMin : requestedMin;
            float max = selectedFilter == FilterType.Height || selectedFilter == FilterType.Distance ? localFilterMax : requestedMax;
            return value >= min && value <= max;
        });
    }

    void OnDestroy()
    {
        if (activeSelectionOperation != null)
        {
            activeSelectionOperation.Cancel();
            activeSelectionOperation.CompleteCancelled("選択処理の所有者が破棄されたため中止しました。");
            activeSelectionOperation = null;
        }
        if (brushVisual != null) Destroy(brushVisual);
        if (brushMaterial != null) Destroy(brushMaterial);
        foreach (MeasurementVisual visual in measurementVisuals.Values) DestroyMeasurementVisual(visual);
        measurementVisuals.Clear();
        DestroyMeasurementVisual(draftVisual);
    }

    private bool HandlePointCloudChanging(string path)
    {
        if (measurementDraftActive)
        {
            if (measurementPath.Points.Count >= 2) FinishMeasurement();
            else CancelMeasurementDraft();
        }
        return SaveMeasurementDocument();
    }

    private void HandlePointCloudLoaded(string path)
    {
        if (pointCloudLoader != null) handledPointCloudRevision = pointCloudLoader.SuccessfulLoadRevision;
        if (targetRenderer != null) NoiseFilterManager.Instance.ResetForPointCloud(targetRenderer);
        string fullPath = Path.GetFullPath(path);
        PlayerPrefs.SetString("PointCloudVR.LastOpenedPath", fullPath);
        PlayerPrefs.Save();
        recoverySourcePath = fullPath;
        recoveryCheckpointPath = PointCloudSessionRecoveryStore.GetCheckpointPath(Application.persistentDataPath, fullPath);
        recoveryDatasetGeneration = targetRenderer != null ? targetRenderer.DatasetGeneration : -1;
        recoverySourceHash = string.Empty;
        recoveryHashTask = null;
        recoveryLoadTask = null;
        recoveryRestoreAttempted = false;
        pendingRecoveryLabels = null;
        pendingRecoverySavedAtLocalText = string.Empty;
        pendingRecoveryGeneration = -1;
        pendingRecoveryRevision = -1;
        recoveryDecisionStatus = string.Empty;
        recoveryObservedRevision = targetRenderer != null ? targetRenderer.ContentRevision : -1;
        recoveryLastSavedRevision = -1;
        recoverySnapshotRevision = -1;
        recoveryLastEditUtc = DateTime.UtcNow;
        recoveryLabelSnapshot = null;
        recoverySnapshotCursor = 0;
        lassoPoints.Clear();
        isDrawingMarquee = false;
        trackpadSelectionActive = false;
        editHistory.Clear();
        editHistoryGeneration = targetRenderer != null ? targetRenderer.DatasetGeneration : -1;
        brushStrokeActive = false;
        brushStrokeChangedIndices.Clear();
        ClearMeasurementVisuals();
        measurementCloudPath = Path.GetFullPath(path);
        measurementDocumentPath = MeasurementDocumentStore.GetSidecarPath(measurementCloudPath);
        measurementExpectedHash = "";
        measurementActualHash = "";
        measurementFingerprintMismatch = false;
        measurementDocumentReady = false;
        measurementDocumentDirty = false;
        measurementSidecarExisted = false;
        measurementUndoStack.Clear();
        selectedMeasurementId = "";
        measurementDraftActive = false;
        editingMeasurementId = "";
        measurementPath.Clear();
        measurementVisualsDirty = true;

        try
        {
            measurementDocument = MeasurementDocumentStore.LoadOrCreate(measurementCloudPath, out measurementSidecarExisted);
            measurementDocumentDirty = MeasurementDocumentStore.NormalizeLegacyCoordinates(measurementDocument);
            measurementExpectedHash = measurementDocument.sourceSha256 ?? "";
            measurementFingerprintPending = true;
            measurementStatus = "点群ファイルを照合中...";
            string pathSnapshot = measurementCloudPath;
            measurementFingerprintTask = Task.Run(() => MeasurementDocumentStore.ComputeSha256(pathSnapshot));
            recoveryHashTask = Task.Run(() => PointCloudSessionRecoveryStore.ComputeFileSha256(pathSnapshot));
        }
        catch (System.Exception ex)
        {
            measurementDocument = null;
            measurementFingerprintPending = false;
            measurementFingerprintTask = null;
            recoveryHashTask = Task.Run(() => PointCloudSessionRecoveryStore.ComputeFileSha256(measurementCloudPath));
            measurementStatus = $"計測JSONを読み込めません: {ex.Message}";
            PointCloudProgressManager.Instance.ShowError("計測JSON", measurementStatus);
            Debug.LogWarning($"[RecoverableOperationError] {measurementStatus}\n{ex}");
        }
        UpdateMeasureVisuals();
    }

    private void PollMeasurementFingerprint()
    {
        if (!measurementFingerprintPending || measurementFingerprintTask == null || !measurementFingerprintTask.IsCompleted) return;

        Task<string> completed = measurementFingerprintTask;
        measurementFingerprintTask = null;
        measurementFingerprintPending = false;
        if (completed.IsFaulted)
        {
            measurementStatus = "点群ファイルの照合に失敗しました。";
            PointCloudProgressManager.Instance.ShowError("計測データ照合", measurementStatus);
            Debug.LogWarning($"[RecoverableOperationError] {measurementStatus}\n{completed.Exception}");
            return;
        }

        string actualHash = completed.Result;
        measurementActualHash = actualHash;
        if (measurementSidecarExisted && !string.IsNullOrEmpty(measurementExpectedHash) &&
            !string.Equals(actualHash, measurementExpectedHash, System.StringComparison.OrdinalIgnoreCase))
        {
            measurementFingerprintMismatch = true;
            measurementStatus = "PLYの内容が保存時から変わっています。計測線を表示する前に関連付けを確認してください。";
            UpdateMeasureVisuals();
            return;
        }

        if (measurementDocument == null) return;
        measurementDocument.sourceSha256 = actualHash;
        measurementDocument.sourceFileName = Path.GetFileName(measurementCloudPath);
        measurementDocumentReady = true;
        measurementStatus = "計測データを読み込みました。";
        measurementVisualsDirty = true;
        if (measurementDocumentDirty || (measurementSidecarExisted && string.IsNullOrEmpty(measurementExpectedHash) &&
            measurementDocument.measurements.Count > 0))
        {
            measurementDocumentDirty = true;
            SaveMeasurementDocument();
        }
        UpdateMeasureVisuals();
    }

    private void UpdateSessionRecovery()
    {
        PointData[] points = targetRenderer != null ? targetRenderer.GetPointData() : null;
        if (points == null || recoveryDatasetGeneration != targetRenderer.DatasetGeneration) return;

        if (recoveryHashTask != null && recoveryHashTask.IsCompleted)
        {
            Task<string> completedHash = recoveryHashTask;
            recoveryHashTask = null;
            if (completedHash.IsFaulted)
            {
                Debug.LogWarning($"[Recovery] 元PLYのfingerprintを計算できません。編集復旧は利用できません。\n{completedHash.Exception}");
            }
            else if (completedHash.IsCanceled)
            {
                Debug.LogWarning("[Recovery] 元PLYのfingerprint計算がキャンセルされました。");
            }
            else
            {
                recoverySourceHash = completedHash.Result;
            }
        }

        if (recoveryWasUnclean && !recoveryRestoreAttempted && recoveryWriteTask == null &&
            !string.IsNullOrEmpty(recoverySourceHash))
        {
            recoveryRestoreAttempted = true;
            string checkpoint = recoveryCheckpointPath;
            string fingerprint = recoverySourceHash;
            int pointCount = points.Length;
            recoveryLoadTask = Task.Run(() =>
            {
                var loaded = new RecoveryLoadResult();
                loaded.Success = PointCloudSessionRecoveryStore.TryRead(checkpoint, fingerprint,
                    pointCount, out loaded.Labels, out loaded.Revision, out loaded.FailureReason);
                return loaded;
            });
        }

        if (recoveryLoadTask != null && recoveryLoadTask.IsCompleted)
        {
            Task<RecoveryLoadResult> completedLoad = recoveryLoadTask;
            recoveryLoadTask = null;
            try
            {
                RecoveryLoadResult recovered = completedLoad.Result;
                if (recovered.Success && recoveryWasUnclean && targetRenderer.DatasetGeneration == recoveryDatasetGeneration &&
                    ReferenceEquals(points, targetRenderer.GetPointData()) && recovered.Labels.Length == points.Length)
                {
                    pendingRecoveryLabels = recovered.Labels;
                    try
                    {
                        pendingRecoverySavedAtLocalText = File.GetLastWriteTime(recoveryCheckpointPath)
                            .ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.CurrentCulture);
                    }
                    catch (Exception timestampException)
                    {
                        pendingRecoverySavedAtLocalText = "取得できません";
                        Debug.LogWarning("[Recovery] 復旧データの保存日時を取得できませんでした。\n" + timestampException);
                    }
                    pendingRecoveryGeneration = recoveryDatasetGeneration;
                    pendingRecoveryRevision = recovered.Revision;
                    recoveryDecisionStatus = string.Empty;
                    Debug.Log($"[Recovery] 復元候補を検証しました。ユーザーの選択を待っています。 points={points.Length:N0}, checkpoint_revision={recovered.Revision}");
                }
                else if (!recovered.Success)
                {
                    Debug.LogWarning($"[Recovery] 復旧データは適用しませんでした: {recovered.FailureReason}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Recovery] 復旧スナップショットの読み込みに失敗しました。現在の点群は保持しました。\n{ex}");
            }
        }

        // Keep a validated recovery candidate stable until the user decides to apply or discard it.
        if (HasPendingRecovery) return;

        if (recoveryWriteTask != null && recoveryWriteTask.IsCompleted)
        {
            Task<bool> completedWrite = recoveryWriteTask;
            recoveryWriteTask = null;
            try
            {
                if (completedWrite.Result && recoveryWriteCheckpointPath == recoveryCheckpointPath)
                    recoveryLastSavedRevision = recoveryWriteRevision;
            }
            catch (Exception ex)
            {
                recoveryRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
                Debug.LogWarning($"[Recovery] 自動保存に失敗しました。後で再試行します。\n{ex}");
            }
        }

        long currentRevision = targetRenderer.ContentRevision;
        if (currentRevision != recoveryObservedRevision)
        {
            recoveryObservedRevision = currentRevision;
            recoveryLastEditUtc = DateTime.UtcNow;
            recoveryLabelSnapshot = null;
            recoverySnapshotCursor = 0;
        }

        if (PointCloudProgressManager.Instance.IsRunning || recoveryLoadTask != null ||
            (recoveryWasUnclean && !recoveryRestoreAttempted) || string.IsNullOrEmpty(recoverySourceHash) ||
            string.IsNullOrEmpty(recoveryCheckpointPath) || DateTime.UtcNow < recoveryRetryAfterUtc ||
            recoveryWriteTask != null || recoveryLastSavedRevision == currentRevision ||
            DateTime.UtcNow.Subtract(recoveryLastEditUtc).TotalSeconds < RecoveryQuietSeconds)
            return;

        if (recoveryLabelSnapshot == null)
        {
            recoveryLabelSnapshot = new int[points.Length];
            recoverySnapshotCursor = 0;
            recoverySnapshotRevision = currentRevision;
        }
        if (recoverySnapshotRevision != currentRevision || points.Length != recoveryLabelSnapshot.Length)
        {
            recoveryLabelSnapshot = null;
            recoverySnapshotCursor = 0;
            return;
        }

        int copyEnd = Math.Min(points.Length, recoverySnapshotCursor + RecoveryPointsPerFrame);
        for (; recoverySnapshotCursor < copyEnd; recoverySnapshotCursor++)
            recoveryLabelSnapshot[recoverySnapshotCursor] = points[recoverySnapshotCursor].label;
        if (recoverySnapshotCursor != points.Length) return;

        int[] snapshot = recoveryLabelSnapshot;
        long snapshotRevision = recoverySnapshotRevision;
        string checkpointPath = recoveryCheckpointPath;
        string sourceHash = recoverySourceHash;
        recoveryLabelSnapshot = null;
        recoverySnapshotCursor = 0;
        recoveryWriteCheckpointPath = checkpointPath;
        recoveryWriteRevision = snapshotRevision;
        recoveryWriteTask = Task.Run(() =>
        {
            PointCloudSessionRecoveryStore.WriteAtomic(checkpointPath, sourceHash, snapshot, snapshotRevision);
            return true;
        });
    }

    public bool ApplyPendingRecovery()
    {
        PointData[] points = targetRenderer != null ? targetRenderer.GetPointData() : null;
        if (!HasPendingRecovery || points == null || targetRenderer.DatasetGeneration != pendingRecoveryGeneration ||
            points.Length != pendingRecoveryLabels.Length || PointCloudProgressManager.Instance.IsRunning)
        {
            recoveryDecisionStatus = "復旧対象の点群が変わったか、処理中のため復元できません。現在の点群は変更していません。";
            return false;
        }

        int[] previousLabels = new int[points.Length];
        for (int i = 0; i < points.Length; i++) previousLabels[i] = points[i].label;
        try
        {
            for (int i = 0; i < points.Length; i++) points[i].label = pendingRecoveryLabels[i];
            if (!targetRenderer.TryUpdatePointBuffer())
                throw new InvalidOperationException("GPU点群バッファを更新できません。");

            pendingRecoveryLabels = null;
            pendingRecoverySavedAtLocalText = string.Empty;
            pendingRecoveryGeneration = -1;
            recoveryDecisionStatus = string.Empty;
            statsDirty = true;
            recoveryObservedRevision = targetRenderer.ContentRevision;
            recoveryLastSavedRevision = recoveryObservedRevision;
            recoveryLastEditUtc = DateTime.UtcNow;
            Debug.Log($"[Recovery] ユーザー確認後に編集ラベルを復元しました。points={points.Length:N0}, checkpoint_revision={pendingRecoveryRevision}");
            pendingRecoveryRevision = -1;
            ResetPointLabelHistory();
            return true;
        }
        catch (Exception applyException)
        {
            for (int i = 0; i < points.Length; i++) points[i].label = previousLabels[i];
            bool rendererRestored = false;
            try { rendererRestored = targetRenderer.TryUpdatePointBuffer(); }
            catch (Exception rollbackException)
            {
                recoveryDecisionStatus = "復元を適用できませんでした。CPU上の点ラベルは戻しましたが、描画同期にも失敗しました。再読み込みしてください。";
                Debug.LogError($"[Recovery] {recoveryDecisionStatus}\n{applyException}\n{rollbackException}");
            }
            if (!rendererRestored && string.IsNullOrEmpty(recoveryDecisionStatus))
                recoveryDecisionStatus = "復元を適用できませんでした。CPU上の点ラベルは戻しましたが、描画同期を確認できません。再読み込みしてください。";
            else if (rendererRestored)
                recoveryDecisionStatus = $"復元を適用できませんでした。点群を変更前に戻しました。\n{applyException.Message}";
            recoveryObservedRevision = targetRenderer.ContentRevision;
            Debug.LogWarning($"[Recovery] {recoveryDecisionStatus}\n{applyException}");
            return false;
        }
    }

    public bool DiscardPendingRecovery()
    {
        if (!HasPendingRecovery || PointCloudProgressManager.Instance.IsRunning) return false;
        try
        {
            if (File.Exists(recoveryCheckpointPath)) File.Delete(recoveryCheckpointPath);
            pendingRecoveryLabels = null;
            pendingRecoverySavedAtLocalText = string.Empty;
            pendingRecoveryGeneration = -1;
            pendingRecoveryRevision = -1;
            recoveryDecisionStatus = string.Empty;
            recoveryLastSavedRevision = targetRenderer != null ? targetRenderer.ContentRevision : -1;
            recoveryObservedRevision = recoveryLastSavedRevision;
            recoveryLastEditUtc = DateTime.UtcNow;
            Debug.LogWarning("[Recovery] ユーザー選択により編集復旧スナップショットを破棄しました。元PLYは変更していません。");
            return true;
        }
        catch (Exception ex)
        {
            recoveryDecisionStatus = $"復旧スナップショットを削除できませんでした。削除されるまで通常操作へ進めません。\n{ex.Message}";
            Debug.LogWarning($"[Recovery] {recoveryDecisionStatus}\n{ex}");
            return false;
        }
    }

    public bool AcceptMeasurementFingerprintMismatch()
    {
        if (!measurementFingerprintMismatch || measurementDocument == null || measurementFingerprintTask != null) return false;
        try
        {
            measurementDocument.sourceSha256 = measurementActualHash;
            measurementFingerprintMismatch = false;
            measurementDocumentReady = true;
            measurementStatus = "計測データをこのPLYに関連付けました。";
            measurementDocumentDirty = true;
            SaveMeasurementDocument();
            measurementVisualsDirty = true;
            UpdateMeasureVisuals();
            return true;
        }
        catch (System.Exception ex)
        {
            measurementStatus = $"関連付けを保存できません: {ex.Message}";
            return false;
        }
    }

    private void ClearMeasurementVisuals()
    {
        foreach (MeasurementVisual visual in measurementVisuals.Values) DestroyMeasurementVisual(visual);
        measurementVisuals.Clear();
        DestroyMeasurementVisual(draftVisual);
        draftVisual = null;
    }

    private MeasurementRecord FindMeasurement(string id)
    {
        if (measurementDocument == null || measurementDocument.measurements == null || string.IsNullOrEmpty(id)) return null;
        for (int i = 0; i < measurementDocument.measurements.Count; i++)
        {
            MeasurementRecord record = measurementDocument.measurements[i];
            if (record != null && record.id == id) return record;
        }
        return null;
    }

    private MeasurementPath CreatePath(MeasurementRecord record)
    {
        var path = new MeasurementPath();
        MeasurementMode mode = record != null && System.Enum.IsDefined(typeof(MeasurementMode), record.mode)
            ? (MeasurementMode)record.mode
            : MeasurementMode.TwoPoint;
        path.SetMode(mode);
        if (record != null && record.points != null)
        {
            for (int i = 0; i < record.points.Count; i++) path.AddPoint(record.points[i]);
        }
        return path;
    }

    public void SelectMeasurement(string id)
    {
        if (!measurementDocumentReady || measurementDraftActive || FindMeasurement(id) == null) return;
        selectedMeasurementId = id;
        measurementVisualsDirty = true;
        SyncLegacyMeasureFields();
        UpdateMeasureVisuals();
    }

    public bool BeginNewMeasurement(MeasurementMode mode)
    {
        if (!measurementDocumentReady || measurementDraftActive || measurementDocument == null) return false;
        toolBeforeMeasurement = activeTool == EditTool.Measure ? EditTool.None : activeTool;
        measurementDraftActive = true;
        editingMeasurementId = "";
        replaceMeasurementPointIndex = -1;
        measurementPath.Clear();
        measurementMode = mode;
        measurementPath.SetMode(mode);
        activeTool = EditTool.Measure;
        SyncLegacyMeasureFields();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
        return true;
    }

    public bool BeginEditingSelectedMeasurement()
    {
        MeasurementRecord record = SelectedMeasurement;
        if (!measurementDocumentReady || measurementDraftActive || record == null) return false;
        toolBeforeMeasurement = activeTool == EditTool.Measure ? EditTool.None : activeTool;
        measurementDraftActive = true;
        editingMeasurementId = record.id;
        replaceMeasurementPointIndex = -1;
        measurementMode = System.Enum.IsDefined(typeof(MeasurementMode), record.mode)
            ? (MeasurementMode)record.mode
            : MeasurementMode.TwoPoint;
        measurementPath.Clear();
        measurementPath.SetMode(measurementMode);
        for (int i = 0; i < record.points.Count; i++) measurementPath.AddPoint(record.points[i]);
        activeTool = EditTool.Measure;
        SyncLegacyMeasureFields();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
        return true;
    }

    public bool FinishMeasurement()
    {
        if (!measurementDraftActive || measurementPath.Points.Count < 2 || measurementDocument == null) return false;

        MeasurementRecord record = FindMeasurement(editingMeasurementId);
        PushMeasurementUndo();
        bool isNew = record == null;
        if (isNew)
        {
            record = new MeasurementRecord
            {
                id = System.Guid.NewGuid().ToString("N"),
                name = $"計測 {measurementDocument.measurements.Count + 1}",
                color = MeasurementPalette[measurementDocument.measurements.Count % MeasurementPalette.Length],
                createdUtc = System.DateTime.UtcNow.ToString("o")
            };
            measurementDocument.measurements.Add(record);
        }

        record.mode = (int)measurementMode;
        record.interpolation = MeasurementPath.CurveAlgorithmId;
        record.points = new List<Vector3>(measurementPath.Points);
        record.modifiedUtc = System.DateTime.UtcNow.ToString("o");
        selectedMeasurementId = record.id;
        measurementDocumentDirty = true;
        EndMeasurementDraft();
        SaveMeasurementDocument();
        measurementVisualsDirty = true;
        SyncLegacyMeasureFields();
        UpdateMeasureVisuals();
        return true;
    }

    public void CancelMeasurementDraft()
    {
        if (!measurementDraftActive) return;
        EndMeasurementDraft();
        measurementVisualsDirty = true;
        SyncLegacyMeasureFields();
        UpdateMeasureVisuals();
    }

    private void EndMeasurementDraft()
    {
        measurementDraftActive = false;
        editingMeasurementId = "";
        replaceMeasurementPointIndex = -1;
        measurementPath.Clear();
        activeTool = toolBeforeMeasurement;
    }

    public void SetMeasurementMode(MeasurementMode mode)
    {
        measurementMode = mode;
        if (measurementDraftActive)
        {
            measurementPath.SetMode(mode);
            if (mode == MeasurementMode.TwoPoint)
            {
                while (measurementPath.Points.Count > 2) measurementPath.RemoveLastPoint();
            }
        }
        SyncLegacyMeasureFields();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
    }

    public void ResetMeasurement()
    {
        CancelMeasurementDraft();
    }

    public void RemoveLastMeasurementPoint()
    {
        if (!measurementDraftActive) return;
        if (replaceMeasurementPointIndex >= 0) replaceMeasurementPointIndex = -1;
        measurementPath.RemoveLastPoint();
        SyncLegacyMeasureFields();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
    }

    public void RemoveMeasurementPointAt(int index)
    {
        if (!measurementDraftActive || index < 0 || index >= measurementPath.Points.Count) return;
        measurementPath.Points.RemoveAt(index);
        replaceMeasurementPointIndex = -1;
        SyncLegacyMeasureFields();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
    }

    public void ArmMeasurementPointReplacement(int index)
    {
        if (!measurementDraftActive || index < 0 || index >= measurementPath.Points.Count) return;
        replaceMeasurementPointIndex = index;
        measurementStatus = $"点{index + 1}の置換先を中クリック、またはCtrl+左クリックしてください。";
    }

    public int ReplacingMeasurementPointIndex => replaceMeasurementPointIndex;

    public void ToggleSelectedMeasurementVisibility()
    {
        MeasurementRecord record = SelectedMeasurement;
        if (!measurementDocumentReady || measurementDraftActive || record == null) return;
        PushMeasurementUndo();
        record.visible = !record.visible;
        record.modifiedUtc = System.DateTime.UtcNow.ToString("o");
        measurementDocumentDirty = true;
        SaveMeasurementDocument();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
    }

    public bool RenameSelectedMeasurement(string name)
    {
        MeasurementRecord record = SelectedMeasurement;
        if (!measurementDocumentReady || measurementDraftActive || record == null || string.IsNullOrWhiteSpace(name)) return false;
        string trimmed = name.Trim();
        if (record.name == trimmed) return true;
        PushMeasurementUndo();
        record.name = trimmed;
        record.modifiedUtc = System.DateTime.UtcNow.ToString("o");
        measurementDocumentDirty = true;
        SaveMeasurementDocument();
        return true;
    }

    public void CycleSelectedMeasurementColor()
    {
        MeasurementRecord record = SelectedMeasurement;
        if (!measurementDocumentReady || measurementDraftActive || record == null) return;
        PushMeasurementUndo();
        int nearest = 0;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < MeasurementPalette.Length; i++)
        {
            Color delta = record.color - MeasurementPalette[i];
            float distance = delta.r * delta.r + delta.g * delta.g + delta.b * delta.b + delta.a * delta.a;
            if (distance < nearestDistance) { nearestDistance = distance; nearest = i; }
        }
        record.color = MeasurementPalette[(nearest + 1) % MeasurementPalette.Length];
        record.modifiedUtc = System.DateTime.UtcNow.ToString("o");
        measurementDocumentDirty = true;
        SaveMeasurementDocument();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
    }

    public void DeleteSelectedMeasurement()
    {
        MeasurementRecord record = SelectedMeasurement;
        if (!measurementDocumentReady || measurementDraftActive || record == null) return;
        PushMeasurementUndo();
        int removedIndex = measurementDocument.measurements.IndexOf(record);
        measurementDocument.measurements.Remove(record);
        selectedMeasurementId = measurementDocument.measurements.Count > 0
            ? measurementDocument.measurements[Mathf.Clamp(removedIndex, 0, measurementDocument.measurements.Count - 1)].id
            : "";
        measurementDocumentDirty = true;
        SaveMeasurementDocument();
        measurementVisualsDirty = true;
        SyncLegacyMeasureFields();
        UpdateMeasureVisuals();
    }

    public bool UndoMeasurement()
    {
        if (!CanMeasurementUndo || measurementDraftActive || measurementDocument == null) return false;
        string previous = measurementUndoStack.Pop();
        measurementDocument = JsonUtility.FromJson<MeasurementDocument>(previous);
        if (FindMeasurement(selectedMeasurementId) == null)
        {
            selectedMeasurementId = measurementDocument.measurements.Count > 0 ? measurementDocument.measurements[0].id : "";
        }
        measurementDocumentDirty = true;
        SaveMeasurementDocument();
        measurementVisualsDirty = true;
        SyncLegacyMeasureFields();
        UpdateMeasureVisuals();
        return true;
    }

    private void PushMeasurementUndo()
    {
        if (measurementDocument == null) return;
        if (measurementUndoStack.Count >= MaxMeasurementUndo)
        {
            string[] items = measurementUndoStack.ToArray();
            measurementUndoStack.Clear();
            for (int i = items.Length - 2; i >= 0; i--) measurementUndoStack.Push(items[i]);
        }
        measurementUndoStack.Push(JsonUtility.ToJson(measurementDocument));
    }

    private bool SaveMeasurementDocument()
    {
        if (!measurementDocumentReady || measurementDocument == null || string.IsNullOrEmpty(measurementCloudPath)) return true;
        if (!measurementDocumentDirty && File.Exists(measurementDocumentPath)) return true;
        if (!measurementDocumentDirty && measurementDocument.measurements.Count == 0) return true;
        try
        {
            MeasurementDocumentStore.Save(measurementCloudPath, measurementDocument);
            measurementDocumentDirty = false;
            measurementStatus = "自動保存済み";
            return true;
        }
        catch (System.Exception ex)
        {
            measurementDocumentDirty = true;
            measurementStatus = $"保存に失敗しました: {ex.Message}";
            PointCloudProgressManager.Instance.ShowError("計測データ保存", measurementStatus);
            Debug.LogWarning($"[RecoverableOperationError] {measurementStatus}\n{ex}");
            return false;
        }
    }

    public void RetrySaveMeasurementDocument()
    {
        SaveMeasurementDocument();
    }

    private static readonly Color[] MeasurementPalette =
    {
        Color.yellow,
        new Color(0.1f, 0.85f, 1f),
        new Color(1f, 0.35f, 0.8f),
        new Color(1f, 0.45f, 0.15f),
        new Color(0.35f, 1f, 0.35f),
        Color.white
    };

    private void SyncLegacyMeasureFields()
    {
        List<Vector3> points = measurementDraftActive
            ? measurementPath.Points
            : (SelectedMeasurement != null ? SelectedMeasurement.points : null);
        hasMeasurePoint1 = points != null && points.Count >= 1;
        hasMeasurePoint2 = points != null && points.Count >= 2;
        measurePoint1 = hasMeasurePoint1 ? points[0] : Vector3.zero;
        measurePoint2 = hasMeasurePoint2 ? points[1] : Vector3.zero;
    }

    public float GetMeasurementLengthDataSpace()
    {
        if (measurementDraftActive) return measurementPath.GetLength();
        MeasurementRecord record = SelectedMeasurement;
        return record != null ? CreatePath(record).GetLength() : 0f;
    }

    public float GetMeasurementLengthMm()
    {
        return targetRenderer != null
            ? targetRenderer.DataLengthToMillimeters(GetMeasurementLengthDataSpace())
            : 0f;
    }

    public float GetMeasurementChordLengthDataSpace()
    {
        List<Vector3> points = measurementDraftActive
            ? measurementPath.Points
            : (SelectedMeasurement != null ? SelectedMeasurement.points : null);
        if (points == null || points.Count < 2) return 0f;
        return Vector3.Distance(points[0], points[points.Count - 1]);
    }

    public float GetMeasurementChordLengthMm()
    {
        return targetRenderer != null
            ? targetRenderer.DataLengthToMillimeters(GetMeasurementChordLengthDataSpace())
            : 0f;
    }

    public bool TryGetSelectedMeasurementChordLengthDataSpace(out float chordLength)
    {
        chordLength = 0f;
        MeasurementRecord record = SelectedMeasurement;
        if (measurementDraftActive || record == null || record.points == null || record.points.Count < 2)
            return false;

        float distance = Vector3.Distance(record.points[0], record.points[record.points.Count - 1]);
        if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0f) return false;
        chordLength = distance;
        return true;
    }

    public bool TryGetSelectedMeasurementChordLengthMm(out float chordLengthMm)
    {
        chordLengthMm = 0f;
        if (!TryGetSelectedMeasurementChordLengthDataSpace(out float chordLengthDataSpace) || targetRenderer == null)
            return false;

        chordLengthMm = targetRenderer.DataLengthToMillimeters(chordLengthDataSpace);
        return !float.IsNaN(chordLengthMm) && !float.IsInfinity(chordLengthMm) && chordLengthMm > 0f;
    }

    public MeasurementResult GetMeasurementResult(MeasurementRecord record)
    {
        if (record == null || targetRenderer == null || record.points == null || record.points.Count < 2) return null;

        MeasurementPath path = CreatePath(record);
        Vector3 first = record.points[0];
        Vector3 last = record.points[record.points.Count - 1];
        return new MeasurementResult
        {
            id = record.id,
            name = record.name,
            mode = GetMeasurementModeId(record.mode),
            length_mm = targetRenderer.DataLengthToMillimeters(path.GetLength()),
            chord_length_mm = targetRenderer.DataLengthToMillimeters(Vector3.Distance(first, last)),
            point_count = record.points.Count
        };
    }

    public List<MeasurementResult> GetMeasurementResults()
    {
        List<MeasurementResult> results = new List<MeasurementResult>();
        if (measurementDocument == null) return results;
        for (int i = 0; i < measurementDocument.measurements.Count; i++)
        {
            MeasurementResult result = GetMeasurementResult(measurementDocument.measurements[i]);
            if (result != null) results.Add(result);
        }
        return results;
    }

    public string ExportMeasurementResultsCsv()
    {
        if (!measurementDocumentReady || measurementFingerprintMismatch)
            throw new System.InvalidOperationException("点群と計測JSONの照合が完了していません。");
        if (string.IsNullOrEmpty(measurementCloudPath))
            throw new System.InvalidOperationException("現在の点群ファイルパスを取得できません。");

        List<MeasurementResult> results = GetMeasurementResults();
        if (results.Count == 0) throw new System.InvalidOperationException("CSVに出力できる計測結果がありません。");

        string directory = Path.GetDirectoryName(measurementCloudPath);
        string baseName = Path.GetFileNameWithoutExtension(measurementCloudPath);
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string outputPath = Path.Combine(directory, $"{baseName}_measurements_{timestamp}.csv");
        for (int suffix = 2; File.Exists(outputPath); suffix++)
            outputPath = Path.Combine(directory, $"{baseName}_measurements_{timestamp}_{suffix}.csv");

        MeasurementResultCsv.Write(outputPath, results);
        return outputPath;
    }

    private static string GetMeasurementModeId(int mode)
    {
        if (mode == (int)MeasurementMode.Polyline) return "polyline";
        if (mode == (int)MeasurementMode.SmoothCurve) return "smooth_curve";
        return "two_point";
    }

    public void UpdateMeasureVisuals()
    {
        if (targetRenderer == null) return;
        SyncLegacyMeasureFields();

        if (measurementVisualsDirty)
        {
            RefreshSavedMeasurementVisuals();
            RefreshDraftVisual();
            measurementVisualsDirty = false;
        }
        UpdateVisualScreenSizes();
    }

    private bool measurementVisualsDirty = true;

    private void RefreshSavedMeasurementVisuals()
    {
        if (!measurementDocumentReady || measurementDocument == null)
        {
            foreach (MeasurementVisual visual in measurementVisuals.Values) visual.root.SetActive(false);
            return;
        }

        var liveIds = new HashSet<string>();
        for (int i = 0; i < measurementDocument.measurements.Count; i++)
        {
            MeasurementRecord record = measurementDocument.measurements[i];
            if (record == null || string.IsNullOrEmpty(record.id)) continue;
            liveIds.Add(record.id);
            if (!measurementVisuals.TryGetValue(record.id, out MeasurementVisual visual))
            {
                visual = CreateMeasurementVisual(record.id, record.color);
                measurementVisuals.Add(record.id, visual);
            }
            visual.material.color = record.color;
            ConfigureMeasurementVisual(visual, record.points, (MeasurementMode)record.mode,
                record.visible && record.id != editingMeasurementId);
        }

        var removed = new List<string>();
        foreach (string id in measurementVisuals.Keys)
        {
            if (!liveIds.Contains(id)) removed.Add(id);
        }
        for (int i = 0; i < removed.Count; i++)
        {
            DestroyMeasurementVisual(measurementVisuals[removed[i]]);
            measurementVisuals.Remove(removed[i]);
        }
    }

    private void RefreshDraftVisual()
    {
        if (measurementDraftActive && measurementDocumentReady)
        {
            if (draftVisual == null) draftVisual = CreateMeasurementVisual("draft", Color.yellow);
            draftVisual.material.color = Color.yellow;
            ConfigureMeasurementVisual(draftVisual, measurementPath.Points, measurementMode, true);
        }
        else if (draftVisual != null)
        {
            draftVisual.root.SetActive(false);
        }
    }

    private MeasurementVisual CreateMeasurementVisual(string id, Color color)
    {
        var visual = new MeasurementVisual { sourceId = id };
        visual.root = new GameObject("Measurement_" + id);
        visual.root.transform.SetParent(targetRenderer.DisplayTransform, false);
        var lineObject = new GameObject("Line");
        lineObject.transform.SetParent(visual.root.transform, false);
        visual.line = lineObject.AddComponent<LineRenderer>();
        visual.line.useWorldSpace = false;
        visual.line.numCapVertices = 0;
        visual.line.numCornerVertices = 0;
        Shader overlay = Shader.Find("PointCloudWorkbench/OverlayColor");
        if (overlay == null) overlay = Shader.Find("Sprites/Default");
        visual.material = new Material(overlay) { color = color };
        visual.line.sharedMaterial = visual.material;
        visual.line.startColor = color;
        visual.line.endColor = color;
        visual.root.SetActive(false);
        return visual;
    }

    private void ConfigureMeasurementVisual(MeasurementVisual visual, List<Vector3> points, MeasurementMode mode, bool visible)
    {
        if (visual == null || visual.root == null) return;
        int pointCount = points != null ? points.Count : 0;
        visual.root.SetActive(visible && pointCount > 0);
        if (!visible || pointCount == 0)
        {
            visual.line.positionCount = 0;
            for (int i = 0; i < visual.markers.Count; i++) visual.markers[i].SetActive(false);
            return;
        }

        var path = new MeasurementPath();
        path.SetMode(mode);
        for (int i = 0; i < pointCount; i++) path.AddPoint(points[i]);
        List<Vector3> linePoints = path.BuildLocalLinePoints();
        visual.line.positionCount = linePoints.Count;
        for (int i = 0; i < linePoints.Count; i++) visual.line.SetPosition(i, linePoints[i]);

        while (visual.markers.Count < pointCount)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(marker.GetComponent<SphereCollider>());
            marker.name = "ControlPoint_" + (visual.markers.Count + 1);
            marker.transform.SetParent(visual.root.transform, false);
            marker.GetComponent<MeshRenderer>().sharedMaterial = visual.material;
            visual.markers.Add(marker);
        }
        for (int i = 0; i < visual.markers.Count; i++)
        {
            bool active = i < pointCount;
            visual.markers[i].SetActive(active);
            if (active) visual.markers[i].transform.localPosition = points[i];
        }
    }

    private void UpdateVisualScreenSizes()
    {
        const float markerScreenPx = 6f;
        const float lineScreenPx = 1.5f;
        float lossyScale = Mathf.Max(targetRenderer.DisplayTransform.lossyScale.x, 0.0001f);
        foreach (MeasurementVisual visual in measurementVisuals.Values) UpdateOneVisualSize(visual, markerScreenPx, lineScreenPx, lossyScale);
        if (draftVisual != null) UpdateOneVisualSize(draftVisual, markerScreenPx, lineScreenPx, lossyScale);
    }

    private void UpdateOneVisualSize(MeasurementVisual visual, float markerScreenPx, float lineScreenPx, float lossyScale)
    {
        if (visual == null || visual.root == null || !visual.root.activeSelf) return;
        for (int i = 0; i < visual.markers.Count; i++)
        {
            GameObject marker = visual.markers[i];
            if (!marker.activeSelf) continue;
            float worldSize = CalcConstantScreenSizeWorld(marker.transform.position, markerScreenPx);
            marker.transform.localScale = Vector3.one * (worldSize / lossyScale);
        }
        if (visual.line.positionCount > 0)
        {
            int mid = visual.line.positionCount / 2;
            Vector3 worldMid = targetRenderer.DisplayTransform.TransformPoint(visual.line.GetPosition(mid));
            float width = CalcConstantScreenSizeWorld(worldMid, lineScreenPx) / lossyScale;
            bool selected = visual.sourceId == selectedMeasurementId;
            visual.line.startWidth = width * (selected ? 1.35f : 1f);
            visual.line.endWidth = visual.line.startWidth;
        }
    }

    private float CalcConstantScreenSizeWorld(Vector3 worldPos, float screenSizePx)
    {
        Camera cam = Camera.main;
        if (cam == null) return 0.01f;
        float dist = Vector3.Distance(cam.transform.position, worldPos);
        if (cam.orthographic) return cam.orthographicSize * 2f * screenSizePx / Screen.height;
        float halfFovRad = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
        return 2f * dist * Mathf.Tan(halfFovRad) / Screen.height * screenSizePx;
    }

    private void DestroyMeasurementVisual(MeasurementVisual visual)
    {
        if (visual == null) return;
        if (visual.root != null) Destroy(visual.root);
        if (visual.material != null) Destroy(visual.material);
    }

    void HandleMeasureTool()
    {
        if (!measurementDraftActive || !measurementDocumentReady) return;
        if (editorUI != null && editorUI.IsMouseOverUI()) return;
        DistanceMeasurementUI measureUI = GetComponent<DistanceMeasurementUI>();
        if (measureUI != null && measureUI.IsMouseOverPanel()) return;
        if (!IsSelectionPointerPressed()) return;

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (!FindClosestPointOnRay(ray, out Vector3 hitPoint)) return;
        Vector3 localHit = targetRenderer.DisplayTransform.InverseTransformPoint(hitPoint);
        if (replaceMeasurementPointIndex >= 0 && replaceMeasurementPointIndex < measurementPath.Points.Count)
        {
            measurementPath.Points[replaceMeasurementPointIndex] = localHit;
            replaceMeasurementPointIndex = -1;
        }
        else
        {
            if (measurementMode == MeasurementMode.TwoPoint && measurementPath.Points.Count >= 2) return;
            measurementPath.AddPoint(localHit);
        }

        SyncLegacyMeasureFields();
        measurementVisualsDirty = true;
        UpdateMeasureVisuals();
        Vector3 displayedPointMm = targetRenderer.DataPointToMillimeters(localHit);
        string lengthText = $"{GetMeasurementLengthMm():F1} mm";
        string pointText = $"座標 ({displayedPointMm.x:F1}, {displayedPointMm.y:F1}, {displayedPointMm.z:F1}) mm";
        Debug.Log($"[PointCloudEditor] 計測点{measurementPath.Points.Count}を設定: {pointText}, 線長: {lengthText}");
        if (measurementMode == MeasurementMode.TwoPoint && measurementPath.Points.Count == 2 && string.IsNullOrEmpty(editingMeasurementId))
        {
            FinishMeasurement();
        }
    }
}
