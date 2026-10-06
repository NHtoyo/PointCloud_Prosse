using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PointCloudWorkbench;
using UnityEngine;

public sealed class StemDiameterUI : MonoBehaviour
{
    private readonly ConcurrentQueue<OutputLine> outputQueue = new ConcurrentQueue<OutputLine>();
    private readonly StringBuilder errorOutput = new StringBuilder();
    private Rect panelRect;
    private Vector2 contentScroll;
    private Texture2D panelBackgroundTexture;
    private GUIStyle panelBackgroundStyle;
    private Process process;
    private volatile bool stdoutEnded;
    private volatile bool stderrEnded;
    private bool showOverlay = true;
    private string status = "茎径解析の準備ができています。";
    private string outputDirectory = "";
    private string inputPath = "";
    private int metricMode;
    private int selectedIndex = -1;
    private PointCloudLoader loader;
    private PointCloudRenderer targetRenderer;
    private PointCloudEditorUI editorUI;
    private StemDiameterVisualizer visualizer;
    private StemDiameterResult result;
    private bool isLoadingResult;
    private bool ownsOperation;
    private bool isStopping;
    private int processGeneration;

    private struct OutputLine
    {
        public bool IsError;
        public string Text;
    }

    private void Awake()
    {
        loader = GetComponent<PointCloudLoader>();
        targetRenderer = GetComponent<PointCloudRenderer>();
        editorUI = GetComponent<PointCloudEditorUI>();
        visualizer = GetComponent<StemDiameterVisualizer>();
        if (visualizer == null) visualizer = gameObject.AddComponent<StemDiameterVisualizer>();
        if (loader != null) loader.PointCloudLoaded += OnPointCloudLoaded;
    }

    private void OnDestroy()
    {
        if (loader != null) loader.PointCloudLoaded -= OnPointCloudLoaded;
        if (ownsOperation)
        {
            PointCloudProgressManager.Instance.Cancel();
            _ = StopProcessAsync();
        }
        if (panelBackgroundTexture != null) Destroy(panelBackgroundTexture);
    }

    private void Update()
    {
        if (isStopping) return;
        if (ownsOperation && PointCloudProgressManager.Instance.IsRunning && PointCloudProgressManager.Instance.CancellationToken.IsCancellationRequested)
        {
            _ = StopProcessAsync();
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
            _ = LoadResultAsync(Path.Combine(outputDirectory, "stem_diameter.json"), PointCloudProgressManager.Instance.CancellationToken);
        }
        else
        {
            string detail = errorOutput.ToString();
            Fail($"Python解析に失敗しました (ExitCode: {exitCode})", detail);
        }
    }

    private async Task LoadResultAsync(string jsonPath, CancellationToken cancellationToken)
    {
        isLoadingResult = true;
        try
        {
            string json = await Task.Run(() => File.ReadAllText(jsonPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StemDiameterResult parsed = JsonUtility.FromJson<StemDiameterResult>(json);
            if (parsed == null || parsed.schema_version != 2 || parsed.sections == null)
                throw new InvalidDataException("JSONの形式またはschema_versionが不正です。");

            result = parsed;
            selectedIndex = result.sections.Length > 0 ? 0 : -1;
            visualizer.SetResult(targetRenderer, result);
            if (selectedIndex >= 0) visualizer.SelectSection(selectedIndex);
            visualizer.SetVisible(showOverlay);
            status = $"完了: {result.sections.Length}断面 / {result.centerline_length_mm:F1} mm";
            PointCloudProgressManager.Instance.Complete();
            ownsOperation = false;
            UnityEngine.Debug.Log($"[StemDiameterUI] 解析完了: {jsonPath}");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested) return;
            Fail($"解析結果を読み込めません: {ex.Message}", ex.ToString());
        }
        finally
        {
            isLoadingResult = false;
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

    public bool IsMouseOverPanel()
    {
        if (editorUI != null && !editorUI.showStemDiameterUI) return false;
        Vector3 mouse = Input.mousePosition;
        mouse.y = Screen.height - mouse.y;
        return panelRect.Contains(mouse);
    }

    public void DrawGUI(ref float currentY)
    {
        if (editorUI == null) editorUI = GetComponent<PointCloudEditorUI>();
        if (editorUI == null || !editorUI.showStemDiameterUI) return;

        RefreshSourceInfo();
        float barX = Mathf.Min(460f, Screen.width * 0.25f) + 30f;
        float rightWidth = Mathf.Min(480f, Screen.width * 0.25f) + 30f;
        float availableWidth = Screen.width - barX - rightWidth - 30f;
        float barWidth = Mathf.Min(Screen.width - 30f, Mathf.Max(430f, availableWidth));
        barWidth = Mathf.Max(280f, barWidth);
        barX = Mathf.Clamp(barX + (availableWidth - barWidth) * 0.5f, 15f, Screen.width - barWidth - 15f);
        bool hasResult = result != null && result.sections != null && result.sections.Length > 0;
        float preferredHeight = hasResult ? 460f : 220f;
        float availableHeight = Mathf.Max(160f, Screen.height - currentY - 18f);
        float barHeight = Mathf.Min(preferredHeight, availableHeight);
        panelRect = new Rect(barX, currentY, barWidth, barHeight);
        EnsurePanelBackgroundStyle();
        GUI.Box(panelRect, GUIContent.none, panelBackgroundStyle);

        Rect contentRect = new Rect(panelRect.x + 10f, panelRect.y + 7f, panelRect.width - 20f, panelRect.height - 14f);
        GUILayout.BeginArea(contentRect);
        contentScroll = GUILayout.BeginScrollView(contentScroll, false, true);
        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.Label("茎径プロファイル", GUI.skin.label, GUILayout.ExpandWidth(true));
        GUILayout.Label(string.IsNullOrEmpty(inputPath) ? "現在の点群を対象にします" : Path.GetFileName(inputPath),
            GUILayout.ExpandWidth(true));
        if (GUILayout.Button("閉じる", GUILayout.Width(68f), GUILayout.Height(28f)))
            editorUI.SetStemDiameterPanelVisible(false);
        GUILayout.EndHorizontal();

        GUILayout.Label("間隔 10 mm   中心線支持 5 mm   局所軸半径 15 mm   断面厚 3 / 5 / 7 mm");
        GUILayout.Label("主茎を抽出した点群を入力してください。節・葉柄等は品質指標で確認します。");
        GUILayout.BeginHorizontal();
        bool priorEnabled = GUI.enabled;
        GUI.enabled = process == null && !isLoadingResult && !PointCloudProgressManager.Instance.IsRunning;
        if (GUILayout.Button("茎径解析を実行", GUILayout.Height(32f))) StartAnalysis();
        GUI.enabled = ownsOperation && PointCloudProgressManager.Instance.IsRunning;
        if (GUILayout.Button("キャンセル", GUILayout.Width(90f), GUILayout.Height(32f))) _ = StopProcessAsync();
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
        GUILayout.EndVertical();
        GUILayout.EndScrollView();
        GUILayout.EndArea();
        currentY += panelRect.height + 10f;

        Event current = Event.current;
        if (current != null && current.type == EventType.ScrollWheel && panelRect.Contains(current.mousePosition))
            current.Use();
    }

    private void EnsurePanelBackgroundStyle()
    {
        if (panelBackgroundStyle != null) return;

        panelBackgroundTexture = new Texture2D(1, 1);
        panelBackgroundTexture.SetPixel(0, 0, new Color(0.09f, 0.11f, 0.15f, 0.85f));
        panelBackgroundTexture.Apply();

        panelBackgroundStyle = new GUIStyle(GUI.skin.box);
        panelBackgroundStyle.normal.background = panelBackgroundTexture;
        panelBackgroundStyle.border = new RectOffset(1, 1, 1, 1);
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
        string graphUnit = metricMode == 0 ? "mm" : metricMode == 2 || metricMode == 3 ? "°" : "";
        GUI.Label(new Rect(rect.x + 1f, plot.y, 35f, 20f), graphUnit);
        GUI.Label(new Rect(rect.x + 1f, plot.y + 18f, 35f, 20f), max.ToString("G3", CultureInfo.InvariantCulture));
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
        GUI.Label(new Rect(plot.x, plot.yMax + 2f, plot.width, 18f), "上端からの距離 (mm) → 下端");

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
        PointCloudProgressManager progress = PointCloudProgressManager.Instance;
        if (!progress.Start("茎径プロファイル", "Pythonを起動中...")) return;
        ownsOperation = true;
        isStopping = false;
        try
        {
            RefreshSourceInfo();
            if (loader == null || targetRenderer == null) throw new InvalidOperationException("PointCloudLoader / PointCloudRenderer が見つかりません。");
            if (targetRenderer.GetPointData() == null || targetRenderer.GetPointData().Length == 0)
                throw new InvalidOperationException("点群がまだ読み込まれていません。");
            if (!File.Exists(inputPath)) throw new FileNotFoundException("現在の点群PLYが見つかりません。", inputPath);

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
                    "--coordinate-scale-to-mm", targetRenderer.DisplayScale.ToString("R", CultureInfo.InvariantCulture),
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
            while (outputQueue.TryDequeue(out _)) { }
            int generation = ++processGeneration;
            process.OutputDataReceived += (_, e) =>
            {
                if (generation != Volatile.Read(ref processGeneration)) return;
                if (e.Data == null) stdoutEnded = true;
                else outputQueue.Enqueue(new OutputLine { IsError = false, Text = e.Data });
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (generation != Volatile.Read(ref processGeneration)) return;
                if (e.Data == null) stderrEnded = true;
                else outputQueue.Enqueue(new OutputLine { IsError = true, Text = e.Data });
            };
            if (!process.Start()) throw new InvalidOperationException("Pythonプロセスを開始できませんでした。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            isLoadingResult = false;
            result = null;
            selectedIndex = -1;
            if (visualizer != null) visualizer.Clear();
            status = "中心線と局所断面を解析中...";
        }
        catch (Exception ex)
        {
            Fail(ex.Message, ex.ToString());
        }
    }

    private void RefreshSourceInfo()
    {
        if (loader == null) loader = GetComponent<PointCloudLoader>();
        if (targetRenderer == null) targetRenderer = GetComponent<PointCloudRenderer>();
        if (editorUI == null) editorUI = GetComponent<PointCloudEditorUI>();
        if (loader != null)
            inputPath = !string.IsNullOrEmpty(loader.CurrentFilePath) ? loader.CurrentFilePath : loader.GetFilePath();
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

    private async Task StopProcessAsync()
    {
        if (!ownsOperation || isStopping) return;
        isStopping = true;
        Interlocked.Increment(ref processGeneration);
        Process stoppingProcess = process;
        try
        {
            if (stoppingProcess != null)
            {
                try { if (!stoppingProcess.HasExited) stoppingProcess.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    if (!stoppingProcess.HasExited)
                        UnityEngine.Debug.LogWarning($"[StemDiameterUI] Python停止時の警告: {ex.Message}");
                }
                await Task.Run(() => stoppingProcess.WaitForExit());
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[StemDiameterUI] Python停止時の警告: {ex}");
        }
        finally
        {
            if (ReferenceEquals(process, stoppingProcess))
            {
                stoppingProcess?.Dispose();
                process = null;
            }
            isLoadingResult = false;
            status = "解析をキャンセルしました。";
            if (PointCloudProgressManager.Instance.IsRunning)
                PointCloudProgressManager.Instance.CompleteCancelled(status);
            ownsOperation = false;
            isStopping = false;
        }
    }

    private void Fail(string message, string detail = null)
    {
        status = message;
        Interlocked.Increment(ref processGeneration);
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception stopException) { UnityEngine.Debug.LogWarning($"[StemDiameterUI] Python停止時の警告: {stopException.Message}"); }
            process.Dispose();
            process = null;
        }
        isLoadingResult = false;
        PointCloudProgressManager.Instance.Fail("茎径プロファイル", message, detail ?? message);
        ownsOperation = false;
        UnityEngine.Debug.LogWarning($"[RecoverableOperationError] {message}\n{detail ?? message}");
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
