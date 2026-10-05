using UnityEngine;
using PointCloudWorkbench;

public class CloudCompareCameraController : MonoBehaviour
{
    [Header("Navigation Settings")]
    public float rotateSpeed = 1.0f; 
    public float panSpeed = 1.0f;
    public float zoomSpeed = 2f;
    public float doubleClickTime = 0.3f;
    public float pickingRadius = 80f;

    [Header("Target Pivot")]
    public Vector3 pivotPoint = new Vector3(0, 1000, 0);
    public float distanceToPivot = 3000f;

    [Header("Visual Indicator")]
    public Color indicatorColor = new Color(0f, 1f, 0f, 0.8f);
    
    // 内部状態
    private Vector3 lastMousePos;
    private float lastClickTime = 0f;
    [HideInInspector] public bool hasCenteredOnCloud = false;

    // 分離したコンポーネントへの参照
    private PivotIndicator pivotIndicator;
    private CameraRotationGuide rotationGuide;
    private PointCloudPicker pointCloudPicker;
    private PointCloudEditorUI editorUI;
    private Camera mainCamera;
    private float minimumOrbitDistance = 0.000001f;

    void Start()
    {
        mainCamera = GetComponent<Camera>();
        if (mainCamera == null) mainCamera = Camera.main;
        editorUI = Object.FindAnyObjectByType<PointCloudEditorUI>();

        // PCモード: TrackedPoseDriver を無効化してマウス操作を有効にする
        var trackedPoseDriver = GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
        if (trackedPoseDriver != null) trackedPoseDriver.enabled = false;

        var legacyTrackedPoseDriver = GetComponent<UnityEngine.SpatialTracking.TrackedPoseDriver>();
        if (legacyTrackedPoseDriver != null) legacyTrackedPoseDriver.enabled = false;

        if (transform.parent != null && transform.parent.name == "Camera Offset")
        {
            transform.SetParent(null);
            Debug.Log("[CC_Camera] Unparented Main Camera from XR Offset to allow absolute positioning.");
        }

        // 依存コンポーネントの自動アタッチと取得、インスペクター設定の同期
        pivotIndicator = GetComponent<PivotIndicator>();
        if (pivotIndicator == null) pivotIndicator = gameObject.AddComponent<PivotIndicator>();
        pivotIndicator.indicatorColor = indicatorColor;

        rotationGuide = GetComponent<CameraRotationGuide>();
        if (rotationGuide == null) rotationGuide = gameObject.AddComponent<CameraRotationGuide>();

        pointCloudPicker = GetComponent<PointCloudPicker>();
        if (pointCloudPicker == null) pointCloudPicker = gameObject.AddComponent<PointCloudPicker>();
        pointCloudPicker.pickingRadius = pickingRadius;

        // デフォルトのピボットをカメラ前方に設定
        pivotPoint = transform.position + transform.forward * distanceToPivot;

        // 点群の自動フォーカス試行
        TryCenterOnPointCloud();

        distanceToPivot = Vector3.Distance(transform.position, pivotPoint);

        lastMousePos = Input.mousePosition;
    }

    public void CenterOnRenderer(PointCloudRenderer renderer)
    {
        if (renderer == null) return;
        var positions = renderer.GetPositions();
        if (positions == null || positions.Length == 0) return;

        // Source PLYs may use different coordinate scales, so fit from actual bounds without unit-based fallbacks.
        Vector3 localMin = positions[0];
        Vector3 localMax = positions[0];
        for (int i = 1; i < positions.Length; i++)
        {
            localMin = Vector3.Min(localMin, positions[i]);
            localMax = Vector3.Max(localMax, positions[i]);
        }

        Vector3 localCenter = (localMin + localMax) * 0.5f;
        Vector3 worldCenter = renderer.transform.TransformPoint(localCenter);
        float cloudRadius = 0f;
        for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
        {
            Vector3 localCorner = new Vector3(
                (cornerIndex & 1) == 0 ? localMin.x : localMax.x,
                (cornerIndex & 2) == 0 ? localMin.y : localMax.y,
                (cornerIndex & 4) == 0 ? localMin.z : localMax.z);
            Vector3 worldCorner = renderer.transform.TransformPoint(localCorner);
            cloudRadius = Mathf.Max(cloudRadius, Vector3.Distance(worldCenter, worldCorner));
        }
        cloudRadius = Mathf.Max(cloudRadius, 0.000001f);

        pivotPoint = worldCenter;
        float halfFovRadians = (mainCamera != null ? mainCamera.fieldOfView : 60f) * 0.5f * Mathf.Deg2Rad;
        distanceToPivot = cloudRadius / Mathf.Max(Mathf.Sin(halfFovRadians), 0.01f) * 1.1f;
        minimumOrbitDistance = Mathf.Max(cloudRadius * 0.005f, 0.000001f);
        pickingRadius = Mathf.Max(cloudRadius * 0.08f, 0.000001f);
        transform.position = pivotPoint - transform.forward * distanceToPivot + Vector3.up * cloudRadius * 0.3f;
        transform.LookAt(pivotPoint);
        if (mainCamera != null)
        {
            mainCamera.nearClipPlane = Mathf.Min(mainCamera.nearClipPlane, Mathf.Max(cloudRadius * 0.05f, 0.000001f));
            float requiredFarClip = Vector3.Distance(transform.position, pivotPoint) + cloudRadius * 1.1f;
            mainCamera.farClipPlane = Mathf.Max(mainCamera.farClipPlane, requiredFarClip);
        }

        hasCenteredOnCloud = true;
        Debug.Log("[CC_Camera] Auto-centered on point cloud.");
    }

    private void TryCenterOnPointCloud()
    {
        var renderer = Object.FindAnyObjectByType<PointCloudRenderer>();
        if (renderer == null) return;
        var positions = renderer.GetPositions();
        if (positions == null || positions.Length == 0) return;
        CenterOnRenderer(renderer);
    }

    private bool IsMouseOverUI()
    {
        if (editorUI != null)
        {
            return editorUI.IsMouseOverUI();
        }
        float mouseX = Input.mousePosition.x;
        float mouseY = Input.mousePosition.y;
        bool overLeftUI = (mouseX >= 10f && mouseX <= 410f && mouseY >= (Screen.height - 850f) && mouseY <= Screen.height);
        bool overRightUI = (mouseX >= Screen.width - 430f && mouseX <= Screen.width - 10f && mouseY >= (Screen.height - 770f) && mouseY <= Screen.height);
        return overLeftUI || overRightUI;
    }

    Vector2 GetScreenPivotCenter()
    {
        float width = Screen.width;
        float height = Screen.height;
        Vector2 Q2D = new Vector2(width / 2.0f, height / 2.0f);

        if (mainCamera != null)
        {
            Vector3 screenPivot = mainCamera.WorldToScreenPoint(pivotPoint);
            if (screenPivot.z > 0)
            {
                Q2D.x = screenPivot.x;
                Q2D.y = screenPivot.y;
            }
        }
        return Q2D;
    }

    void Update()
    {
        if (!hasCenteredOnCloud)
        {
            TryCenterOnPointCloud();
        }

        Vector3 currentMousePos = Input.mousePosition;
        Vector3 mouseDelta = currentMousePos - lastMousePos;
        lastMousePos = currentMousePos;

        bool overUI = IsMouseOverUI();

        // 編集ツールの競合確認（変数の前方宣言）
        bool isEditing = false;
        var editor = Object.FindAnyObjectByType<PointCloudEditor>();
        if (editor != null && editor.activeTool != PointCloudEditor.EditTool.None)
        {
            isEditing = true;
        }

        // UIを操作中はカメラ／点サイズのキーボード操作も受け取らない。
        if (!overUI && GUIUtility.keyboardControl == 0)
        {
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.LeftBracket))
            {
                var r = Object.FindAnyObjectByType<PointCloudRenderer>();
                if (r != null) r.pointSize = Mathf.Max(1.0f, Mathf.Round(r.pointSize) - 1.0f);
            }
            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.RightBracket))
            {
                var r = Object.FindAnyObjectByType<PointCloudRenderer>();
                if (r != null) r.pointSize = Mathf.Min(20.0f, Mathf.Round(r.pointSize) + 1.0f);
            }
        }

        // ダブルクリックでピボット選択（編集ツール動作中は無効化）
        if (Input.GetMouseButtonDown(0) && !overUI && !isEditing)
        {
            float timeSinceLastClick = Time.time - lastClickTime;
            if (timeSinceLastClick < doubleClickTime)
            {
                TryPickPivotPoint();
            }
            lastClickTime = Time.time;
        }

        // 1. ズーム（マウスホイール & ドラッグ）
        if (!overUI)
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
            {
                distanceToPivot -= scroll * zoomSpeed * distanceToPivot;
                distanceToPivot = Mathf.Max(minimumOrbitDistance, distanceToPivot);
                if (pivotIndicator != null) pivotIndicator.Show(pivotPoint, distanceToPivot);
            }

            // 中ボタンドラッグズームは通常時のみ有効（編集時は中ボタンを選択に使うため）
            if ((!isEditing && Input.GetMouseButton(2)) || (Input.GetMouseButton(1) && Input.GetKey(KeyCode.LeftAlt)))
            {
                float zoomDelta = mouseDelta.y * 0.005f * zoomSpeed * distanceToPivot;
                distanceToPivot += zoomDelta;
                distanceToPivot = Mathf.Max(minimumOrbitDistance, distanceToPivot);
                if (pivotIndicator != null) pivotIndicator.Show(pivotPoint, distanceToPivot);
            }
        }

        // 2. 回転（常に左ドラッグ）
        bool rotating = Input.GetMouseButton(0) && !overUI;

        if (rotating)
        {
            Vector2 screenCenter = GetScreenPivotCenter();
            float screenHeight = Screen.height;
            float fov = mainCamera != null ? mainCamera.fieldOfView : 60f;

            Vector3 prevOrientation = TrackballMath.ConvertMousePositionToOrientation(
                currentMousePos - mouseDelta, screenCenter, screenHeight, fov, distanceToPivot);
            Vector3 currOrientation = TrackballMath.ConvertMousePositionToOrientation(
                currentMousePos, screenCenter, screenHeight, fov, distanceToPivot);

            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                float dx = mouseDelta.x;
                float angle = -dx / Screen.width * 360f * rotateSpeed;
                transform.rotation = transform.rotation * Quaternion.Euler(0, 0, angle);
            }
            else
            {
                Quaternion rotCameraCS = Quaternion.FromToRotation(prevOrientation, currOrientation);
                rotCameraCS.z = -rotCameraCS.z; // Z軸反転補正
                transform.rotation = transform.rotation * rotCameraCS;
            }
            
            if (rotationGuide != null) rotationGuide.SetActive(true);
            if (pivotIndicator != null) pivotIndicator.Show(pivotPoint, distanceToPivot);
        }
        else
        {
            if (rotationGuide != null) rotationGuide.SetActive(false);
        }

        // 3. パン（常に右ドラッグ）
        bool panning = Input.GetMouseButton(1) && !overUI;

        if (panning)
        {
            float fov = mainCamera != null ? mainCamera.fieldOfView : 60f;
            float halfFovRad = fov * 0.5f * Mathf.Deg2Rad;
            float h = 2f * distanceToPivot * Mathf.Tan(halfFovRad);
            float pixelSize = h / Screen.height;

            float dx = -mouseDelta.x * pixelSize * panSpeed;
            float dy = -mouseDelta.y * pixelSize * panSpeed;

            Vector3 translation = transform.right * dx + transform.up * dy;
            pivotPoint += translation;
            if (pivotIndicator != null) pivotIndicator.Show(pivotPoint, distanceToPivot);
        }

        // カメラをピボット中心に周回させる（ここで位置と姿勢が確定する）
        transform.position = pivotPoint - (transform.rotation * Vector3.forward * distanceToPivot);

        // ガイドとインジケータの位置・向きをカメラ更新後に同期して振動を完全に防ぐ
        if (pivotIndicator != null)
        {
            pivotIndicator.UpdatePosition(pivotPoint, distanceToPivot);
        }
        if (rotationGuide != null && rotating)
        {
            rotationGuide.UpdatePositionAndRotation(pivotPoint, distanceToPivot, mainCamera);
        }
    }

    void TryPickPivotPoint()
    {
        if (pointCloudPicker == null || mainCamera == null) return;

        pointCloudPicker.pickingRadius = pickingRadius;
        if (pointCloudPicker.TryPickPoint(mainCamera, Input.mousePosition, pivotPoint, out Vector3 pickedPoint))
        {
            pivotPoint = pickedPoint;
            distanceToPivot = Vector3.Distance(transform.position, pivotPoint);
            if (pivotIndicator != null) pivotIndicator.Show(pivotPoint, distanceToPivot);
            Debug.Log($"[CC_Camera] Set new rotation pivot at clicked point: {pivotPoint}");
        }
    }
}
