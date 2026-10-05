using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using PointCloudWorkbench;
using UnityEngine;

public sealed class StemDiameterUI : MonoBehaviour
{
    private const int WindowId = 39174;
    private readonly ConcurrentQueue<OutputLine> outputQueue = new ConcurrentQueue<OutputLine>();
    private readonly StringBuilder errorOutput = new StringBuilder();
    private Rect windowRect = new Rect(12f, 12f, 390f, 470f);
    private Process process;
    private volatile bool stdoutEnded;
    private volatile bool stderrEnded;
    private bool collapsed;
    private bool windowPlaced;
    private bool showOverlay = true;
    private string status = "点群とスケール校正を確認してください。";
    private string outputDirectory = "";
    private string inputPath = "";
    private float scaleMmPerUnit;
    private int metricMode;
    private int selectedIndex = -1;
    private PointCloudLoader loader;
    private PointCloudRenderer targetRenderer;
    private PointCloudEditor editor;
    private StemDiameterVisualizer visualizer;
    private StemDiameterResult result;

    private struct OutputLine
    {
        public bool IsError;
        public string Text;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AttachToWorkbench()
    {
        PointCloudEditorUI workbench = FindAnyObjectByType<PointCloudEditorUI>();
        if (workbench == null || workbench.GetComponent<StemDiameterUI>() != null) return;
        workbench.gameObject.AddComponent<StemDiameterUI>();
    }

    private void Awake()
    {
        loader = GetComponent<PointCloudLoader>();
        targetRenderer = GetComponent<PointCloudRenderer>();
        editor = GetComponent<PointCloudEditor>();
        visualizer = GetComponent<StemDiameterVisualizer>();
        if (visualizer == null) visualizer = gameObject.AddComponent<StemDiameterVisualizer>();
        if (loader != null) loader.PointCloudLoaded += OnPointCloudLoaded;
    }

    private void OnDestroy()
    {
        if (loader != null) loader.PointCloudLoaded -= OnPointCloudLoaded;
        StopProcess();
    }

    private void Update()
    {
        if (process != null && PointCloudProgressManager.Instance.CancellationToken.IsCancellationRequested)
        {
            StopProcess();
            return;
        }

        while (outputQueue.TryDequeue(out OutputLine line))
        {
            if (line.IsError)
            {
                if (errorOutput.Length > 0) errorOutput.AppendLine();
                errorOutput.Append(line.Text);
                continue;
            }
            ProcessOutputLine(line.Text);
        }

        if (process == null || !process.HasExited || !stdoutEnded || !stderrEnded) return;
        int exitCode = process.ExitCode;
        process.Dispose();
        process = null;
        if (exitCode == 0)
        {
            try
            {
                string jsonPath = Path.Combine(outputDirectory, "stem_diameter.json");
                result = JsonUtility.FromJson<StemDiameterResult>(File.ReadAllText(jsonPath));
                if (result == null || result.schema_version != 1 || result.sections == null)
                    throw new InvalidDataException("JSONの形式またはschema_versionが不正です。");
                selectedIndex = result.sections.Length > 0 ? 0 : -1;
                visualizer.SetResult(targetRenderer, result);
                if (selectedIndex >= 0) visualizer.SelectSection(selectedIndex);
                visualizer.SetVisible(showOverlay);
                status = $"完了: {result.sections.Length}断面 / {result.centerline_length_mm:F1} mm";
                PointCloudProgressManager.Instance.Complete();
                UnityEngine.Debug.Log($"[StemDiameterUI] 解析完了: {jsonPath}");
            }
            catch (Exception ex)
            {
                Fail($"解析結果を読み込めません: {ex.Message}");
            }
        }
        else
        {
            string detail = errorOutput.ToString();
            Fail($"Python解析に失敗しました (ExitCode: {exitCode})\n{detail}");
        }
    }

    private void OnPointCloudLoaded(string path)
    {
        result = null;
        selectedIndex = -1;
        if (visualizer != null) visualizer.Clear();
        inputPath = path;
        status = "点群が変わりました。茎径解析を再実行してください。";
    }

    private void OnGUI()
    {
        if (!windowPlaced)
        {
            windowRect.x = Mathf.Max(12f, Screen.width - windowRect.width - 12f);
            windowPlaced = true;
        }
        if (windowRect.width != 390f) windowRect.width = 390f;
        windowRect.height = Mathf.Min(windowRect.height, Screen.height - 20f);
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, Screen.width - windowRect.width));
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Screen.height - windowRect.height));
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "茎径プロファイル");

        Event current = Event.current;
        if (current != null && current.type == EventType.ScrollWheel && windowRect.Contains(current.mousePosition))
            current.Use();
    }

    private void DrawWindow(int id)
    {
        RefreshSourceInfo();
        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.Label(string.IsNullOrEmpty(inputPath) ? "現在の点群を対象にします" : Path.GetFileName(inputPath),
            GUILayout.ExpandWidth(true));
        if (GUILayout.Button(collapsed ? "開く" : "最小化", GUILayout.Width(58f), GUILayout.Height(26f)))
            collapsed = !collapsed;
        GUILayout.EndHorizontal();

        if (!collapsed)
        {
            GUILayout.Label($"スケール: {(scaleMmPerUnit > 0f ? $"{scaleMmPerUnit:G5} mm/unit" : "未校正")}");
            GUILayout.Label("間隔 10 mm   中心線支持 5 mm   局所軸半径 15 mm   断面厚 3 / 5 / 7 mm");
            GUILayout.Label("主茎を抽出した点群を入力してください。節・葉柄等は品質指標で確認します。");
            GUILayout.BeginHorizontal();
            bool priorEnabled = GUI.enabled;
            GUI.enabled = process == null && !PointCloudProgressManager.Instance.IsRunning;
            if (GUILayout.Button("茎径解析を実行", GUILayout.Height(32f))) StartAnalysis();
            GUI.enabled = process != null;
            if (GUILayout.Button("キャンセル", GUILayout.Width(90f), GUILayout.Height(32f))) StopProcess();
            GUI.enabled = priorEnabled;
            GUILayout.EndHorizontal();
            GUILayout.Label(status, GUILayout.MinHeight(28f));

            if (result != null && result.sections != null && result.sections.Length > 0)
            {
                DrawMetricButtons();
                Rect chart = GUILayoutUtility.GetRect(340f, 155f, GUILayout.ExpandWidth(true));
                DrawGraph(chart);
                DrawSelectedDetails();
                GUILayout.BeginHorizontal();
                showOverlay = GUILayout.Toggle(showOverlay, "中心線・選択断面を3D表示", "Button", GUILayout.Height(26f));
                if (visualizer != null) visualizer.SetVisible(showOverlay);
                GUILayout.EndHorizontal();
                GUILayout.Label(outputDirectory, GUI.skin.label);
            }
        }
        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 24f));
    }

    private void DrawMetricButtons()
    {
        string[] labels = { "径 3/5/7 mm", "周方向 coverage", "最大欠損角", "局所軸角", "円形度", "軸比" };
        for (int row = 0; row < 2; row++)
        {
            GUILayout.BeginHorizontal();
            for (int column = 0; column < 3; column++)
            {
                int i = row * 3 + column;
                if (GUILayout.Toggle(metricMode == i, labels[i], "Button", GUILayout.Height(27f))) metricMode = i;
            }
            GUILayout.EndHorizontal();
        }
    }

    private void DrawGraph(Rect rect)
    {
        Color oldColor = GUI.color;
        GUI.color = new Color(0.05f, 0.07f, 0.09f, 1f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = oldColor;

        Rect plot = new Rect(rect.x + 38f, rect.y + 8f, rect.width - 48f, rect.height - 28f);
        if (plot.width <= 0f || plot.height <= 0f) return;
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < result.sections.Length; i++)
        {
            float value = MetricValue(result.sections[i], metricMode);
            if (IsValid(value)) { min = Mathf.Min(min, value); max = Mathf.Max(max, value); }
        }
        if (!IsValid(min) || !IsValid(max))
        {
            GUI.Label(plot, "有効な断面値がありません。");
            return;
        }
        if (Mathf.Abs(max - min) < 0.0001f) { min -= 0.5f; max += 0.5f; }
        float padding = (max - min) * 0.08f;
        min -= padding;
        max += padding;

        for (int i = 0; i <= 4; i++)
        {
            float y = plot.y + plot.height * i / 4f;
            DrawLine(new Vector2(plot.x, y), new Vector2(plot.xMax, y), new Color(0.35f, 0.39f, 0.43f), 1f);
        }
        GUI.Label(new Rect(rect.x + 1f, plot.y, 35f, 20f), max.ToString("G3", CultureInfo.InvariantCulture));
        GUI.Label(new Rect(rect.x + 1f, plot.yMax - 17f, 35f, 20f), min.ToString("G3", CultureInfo.InvariantCulture));

        if (metricMode == 0)
        {
            DrawDiameterSeries(plot, min, max, "diameter_3mm", new Color(0.2f, 0.8f, 0.5f));
            DrawDiameterSeries(plot, min, max, "diameter_5mm", new Color(1f, 0.66f, 0.15f));
            DrawDiameterSeries(plot, min, max, "diameter_7mm", new Color(0.3f, 0.65f, 1f));
        }
        else
        {
            Vector2? previous = null;
            for (int i = 0; i < result.sections.Length; i++)
            {
                float value = MetricValue(result.sections[i], metricMode);
                if (!IsValid(value)) { previous = null; continue; }
                Vector2 point = GraphPoint(plot, i, value, min, max);
                if (previous.HasValue) DrawLine(previous.Value, point, new Color(1f, 0.66f, 0.15f), 2f);
                previous = point;
            }
        }

        if (selectedIndex >= 0 && selectedIndex < result.sections.Length)
        {
            float x = plot.x + (result.sections.Length <= 1 ? 0f : (float)selectedIndex / (result.sections.Length - 1)) * plot.width;
            DrawLine(new Vector2(x, plot.y), new Vector2(x, plot.yMax), Color.white, 1.5f);
        }
        GUI.Label(new Rect(plot.x, plot.yMax + 2f, plot.width, 18f), "上端  ← 距離に沿った断面 →  下端");

        Event e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
        {
            float normalized = Mathf.Clamp01((e.mousePosition.x - plot.x) / plot.width);
            selectedIndex = Mathf.RoundToInt(normalized * (result.sections.Length - 1));
            if (visualizer != null) visualizer.SelectSection(selectedIndex);
            e.Use();
        }
    }

    private void DrawDiameterSeries(Rect plot, float min, float max, string field, Color color)
    {
        Vector2? previous = null;
        for (int i = 0; i < result.sections.Length; i++)
        {
            StemDiameterSection section = result.sections[i];
            float value = field == "diameter_3mm" ? section.diameter_3mm :
                field == "diameter_5mm" ? section.diameter_5mm : section.diameter_7mm;
            if (!IsValid(value)) { previous = null; continue; }
            Vector2 point = GraphPoint(plot, i, value, min, max);
            if (previous.HasValue) DrawLine(previous.Value, point, color, 2f);
            previous = point;
        }
    }

    private Vector2 GraphPoint(Rect plot, int index, float value, float min, float max)
    {
        float x = result.sections.Length <= 1 ? plot.x : plot.x + (float)index / (result.sections.Length - 1) * plot.width;
        float y = plot.yMax - Mathf.InverseLerp(min, max, value) * plot.height;
        return new Vector2(x, y);
    }

    private static float MetricValue(StemDiameterSection section, int mode)
    {
        if (section == null) return float.NaN;
        if (mode == 0) return section.calculation_status == "ok" ? section.equivalent_diameter_mm : float.NaN;
        if (mode == 3) return section.local_axis_xyz != null ? section.local_axis_angle_deg : float.NaN;
        StemDiameterSlice primary = GetPrimarySlice(section);
        if (primary == null) return float.NaN;
        switch (mode)
        {
            case 1: return primary.angular_coverage;
            case 2: return primary.max_gap_deg;
            case 4: return primary.circularity;
            case 5: return primary.shape_axis_ratio;
            default: return float.NaN;
        }
    }

    private void DrawSelectedDetails()
    {
        if (selectedIndex < 0 || selectedIndex >= result.sections.Length) return;
        StemDiameterSection section = result.sections[selectedIndex];
        StemDiameterSlice primary = GetPrimarySlice(section);
        GUILayout.Label($"断面 {section.index + 1} / {result.sections.Length}    上端から {section.position_mm:F1} mm    状態: {section.calculation_status}");
        string diameter3 = section.calculation_status == "ok" ? Format(section.diameter_3mm) : "--";
        string diameter5 = section.calculation_status == "ok" ? Format(section.diameter_5mm) : "--";
        string diameter7 = section.calculation_status == "ok" ? Format(section.diameter_7mm) : "--";
        GUILayout.Label($"径 3/5/7 mm: {diameter3} / {diameter5} / {diameter7} mm");
        if (primary != null)
        {
            GUILayout.Label($"面積 {Format(primary.area_mm2)} mm²   coverage {Format(primary.angular_coverage)}   最大欠損角 {Format(primary.max_gap_deg)}°");
            GUILayout.Label($"点数 raw/used {primary.raw_point_count}/{primary.used_point_count}   外れ点率 {Format(primary.outlier_fraction)}   円形度 {Format(primary.circularity)}   軸比 {Format(primary.shape_axis_ratio)}");
        }
        string axisAngle = section.local_axis_xyz != null ? Format(section.local_axis_angle_deg) : "--";
        GUILayout.Label($"局所軸角 {axisAngle}°   PCA線状性 {Format(section.local_pca_linearity)}");
    }

    private static StemDiameterSlice GetPrimarySlice(StemDiameterSection section)
    {
        if (section == null || section.slice_results == null) return null;
        for (int i = 0; i < section.slice_results.Length; i++)
            if (Mathf.Abs(section.slice_results[i].thickness_mm - 5f) < 0.001f &&
                section.slice_results[i].calculation_status == "ok") return section.slice_results[i];
        return null;
    }

    private static string Format(float value)
    {
        return IsValid(value) ? value.ToString("F3", CultureInfo.InvariantCulture) : "--";
    }

    private static bool IsValid(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void DrawLine(Vector2 start, Vector2 end, Color color, float width)
    {
        Vector2 delta = end - start;
        if (delta.sqrMagnitude < 0.001f) return;
        Matrix4x4 previous = GUI.matrix;
        Color previousColor = GUI.color;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
        GUI.color = color;
        GUI.DrawTexture(new Rect(start.x, start.y - width * 0.5f, delta.magnitude, width), Texture2D.whiteTexture);
        GUI.color = previousColor;
        GUI.matrix = previous;
    }

    private void StartAnalysis()
    {
        try
        {
            RefreshSourceInfo();
            if (loader == null || targetRenderer == null) throw new InvalidOperationException("PointCloudLoader / PointCloudRenderer が見つかりません。");
            if (targetRenderer.GetPointData() == null || targetRenderer.GetPointData().Length == 0)
                throw new InvalidOperationException("点群がまだ読み込まれていません。");
            if (!File.Exists(inputPath)) throw new FileNotFoundException("現在の点群PLYが見つかりません。", inputPath);

            if (!IsValid(scaleMmPerUnit) || scaleMmPerUnit <= 0f)
                throw new InvalidOperationException("点群の実スケールが未校正です。先にスケール校正を行ってください。");

            string backend = Path.GetFullPath(Path.Combine(Application.dataPath, "../python_backend"));
            string script = Path.Combine(backend, "run_stem_diameter.py");
            if (!File.Exists(script)) throw new FileNotFoundException("茎径解析CLIが見つかりません。", script);
            string pythonVenv = Path.Combine(backend, ".venv", "Scripts", "python.exe");
            string python = File.Exists(pythonVenv) ? pythonVenv : "python";
            outputDirectory = Path.Combine(loader.GetPointCloudDataDirectory(),
                Path.GetFileNameWithoutExtension(inputPath) + "_stem_diameter");
            Directory.CreateDirectory(outputDirectory);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = python,
                Arguments = string.Join(" ", new[]
                {
                    Quote(script), "--input", Quote(inputPath), "--output_dir", Quote(outputDirectory),
                    "--scale-mm-per-unit", scaleMmPerUnit.ToString("R", CultureInfo.InvariantCulture),
                    "--query-workers", "-1"
                }),
                WorkingDirectory = backend,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            stdoutEnded = false;
            stderrEnded = false;
            errorOutput.Length = 0;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) stdoutEnded = true;
                else outputQueue.Enqueue(new OutputLine { IsError = false, Text = e.Data });
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) stderrEnded = true;
                else outputQueue.Enqueue(new OutputLine { IsError = true, Text = e.Data });
            };
            if (!process.Start()) throw new InvalidOperationException("Pythonプロセスを開始できませんでした。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            result = null;
            selectedIndex = -1;
            if (visualizer != null) visualizer.Clear();
            status = "中心線と局所断面を解析中...";
            PointCloudProgressManager.Instance.Start("茎径プロファイル", "Pythonを起動中...");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void RefreshSourceInfo()
    {
        if (loader == null) loader = GetComponent<PointCloudLoader>();
        if (targetRenderer == null) targetRenderer = GetComponent<PointCloudRenderer>();
        if (editor == null) editor = GetComponent<PointCloudEditor>();
        if (loader != null)
            inputPath = !string.IsNullOrEmpty(loader.CurrentFilePath) ? loader.CurrentFilePath : loader.GetFilePath();
        scaleMmPerUnit = editor != null && editor.HasScaleCalibration
            ? editor.ScaleMetersPerSourceUnit * 1000f
            : targetRenderer != null && targetRenderer.CoordinateScaleIsKnown
                ? targetRenderer.CoordinateScaleMetersPerSourceUnit * 1000f
                : 0f;
    }

    private void ProcessOutputLine(string line)
    {
        if (line.StartsWith("[Progress]", StringComparison.Ordinal))
        {
            string[] fields = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 3 && float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
            {
                status = fields[2];
                PointCloudProgressManager.Instance.Update(percent / 100f, status);
            }
        }
        else if (!string.IsNullOrWhiteSpace(line))
        {
            UnityEngine.Debug.Log($"[StemDiameter] {line}");
        }
    }

    private void StopProcess()
    {
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) { UnityEngine.Debug.LogWarning($"[StemDiameterUI] Python停止時の警告: {ex.Message}"); }
        status = "解析をキャンセルしました。";
        process.Dispose();
        process = null;
        PointCloudProgressManager.Instance.Complete();
    }

    private void Fail(string message)
    {
        status = message;
        UnityEngine.Debug.LogError($"[StemDiameterUI] {message}");
        PointCloudProgressManager.Instance.ShowError("茎径解析エラー", message);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
