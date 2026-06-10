using UnityEngine;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using PointCloudWorkbench;

public class PointCloudLoader : MonoBehaviour
{
    [Header("References")]
    public PointCloudRenderer targetRenderer;

    [Header("Settings")]
    [Tooltip("Path to the point cloud file. Can be absolute or relative to StreamingAssets.")]
    public string fileName = "point_cloud - Cloud.segmented.remaining.segmented.ply";
    
    [Tooltip("If checked, reads from externalFolderPath instead of StreamingAssets. If empty or invalid, defaults to project_root/../PointCloudData.")]
    public bool useExternalPath = true;
    public string externalFolderPath = "";
    public string CurrentFilePath { get; private set; } = "";

    [Header("Import Controls")]
    [Tooltip("Maximum points to load to prevent memory issues")]
    public int maxPointsToLoad = 20000000; // Updated default to 20 million

    private struct PLYProperty
    {
        public string name;
        public string type;
        public int size;
        public int offset;
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

    public void LoadPointCloud(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"[PointCloudLoader] File not found at: {filePath}");
            return;
        }

        System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();

        Debug.Log($"[PointCloudLoader] Loading file: {filePath}");
        CurrentFilePath = filePath;
        string extension = Path.GetExtension(filePath).ToLower();

        PointData[] loadedPoints = null;

        if (extension == ".ply")
        {
            loadedPoints = ParsePLY(filePath);
        }
        else
        {
            loadedPoints = ParseTXT(filePath);
        }

        stopwatch.Stop();

        if (loadedPoints != null && loadedPoints.Length > 0)
        {
            Debug.Log($"[PointCloudLoader] Loaded {loadedPoints.Length} points in {stopwatch.ElapsedMilliseconds} ms.");
            if (targetRenderer != null)
            {
                targetRenderer.SetPointCloudData(loadedPoints);
            }
            else
            {
                Debug.LogError("[PointCloudLoader] Target PointCloudRenderer is not set!");
            }
        }
        else
        {
            Debug.LogWarning("[PointCloudLoader] No points loaded from file.");
        }
    }

    private PointData[] ParsePLY(string path)
    {
        List<string> headerLines = new List<string>();
        long dataOffset = 0;

        // 1. Read header byte-by-byte to find exact start of binary data
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            List<byte> lineBytes = new List<byte>();
            int b;
            while ((b = fs.ReadByte()) != -1)
            {
                if (b == '\n')
                {
                    string line = System.Text.Encoding.ASCII.GetString(lineBytes.ToArray()).Trim();
                    headerLines.Add(line);
                    lineBytes.Clear();
                    if (line == "end_header")
                    {
                        dataOffset = fs.Position;
                        break;
                    }
                }
                else if (b != '\r')
                {
                    lineBytes.Add((byte)b);
                }
            }
        }

        // 2. Parse header properties
        bool isBinary = false;
        int vertexCount = 0;
        List<PLYProperty> properties = new List<PLYProperty>();
        int stride = 0;

        int xOffset = -1, yOffset = -1, zOffset = -1;
        int rOffset = -1, gOffset = -1, bOffset = -1;
        int labelOffset = -1;

        string xType = "", yType = "", zType = "";
        string rType = "", gType = "", bType = "";
        string labelType = "";

        foreach (var line in headerLines)
        {
            string[] tokens = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            if (tokens[0] == "format")
            {
                if (tokens[1].StartsWith("binary"))
                {
                    isBinary = true;
                }
            }
            else if (tokens[0] == "element" && tokens.Length >= 3)
            {
                if (tokens[1] == "vertex")
                {
                    int.TryParse(tokens[2], out vertexCount);
                }
            }
            else if (tokens[0] == "property" && tokens.Length >= 3)
            {
                string typeStr = tokens[1].ToLower();
                string propName = tokens[2].ToLower();
                int propSize = GetTypeSize(typeStr);

                PLYProperty prop = new PLYProperty
                {
                    name = propName,
                    type = typeStr,
                    size = propSize,
                    offset = stride
                };
                properties.Add(prop);

                if (propName == "x") { xOffset = prop.offset; xType = typeStr; }
                else if (propName == "y") { yOffset = prop.offset; yType = typeStr; }
                else if (propName == "z") { zOffset = prop.offset; zType = typeStr; }
                else if (propName == "red" || propName == "r" || propName == "diffuse_red") { rOffset = prop.offset; rType = typeStr; }
                else if (propName == "green" || propName == "g" || propName == "diffuse_green") { gOffset = prop.offset; gType = typeStr; }
                else if (propName == "blue" || propName == "b" || propName == "diffuse_blue") { bOffset = prop.offset; bType = typeStr; }
                else if (propName == "label" || propName == "class" || propName == "scalar_label") { labelOffset = prop.offset; labelType = typeStr; }

                stride += propSize;
            }
        }

        if (vertexCount <= 0)
        {
            Debug.LogError("[PointCloudLoader] No vertices found in PLY header.");
            return null;
        }

        int countToLoad = Mathf.Min(vertexCount, maxPointsToLoad);
        PointData[] points = new PointData[countToLoad];

        if (isBinary)
        {
            // --- HIGH PERFORMANCE BINARY PARSING ---
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                fs.Seek(dataOffset, SeekOrigin.Begin);
                byte[] buffer = new byte[countToLoad * stride];
                int bytesRead = fs.Read(buffer, 0, buffer.Length);
                int loadedCount = bytesRead / stride;
                
                if (loadedCount < countToLoad)
                {
                    countToLoad = loadedCount;
                    System.Array.Resize(ref points, countToLoad);
                }

                // Parse byte buffer
                for (int i = 0; i < countToLoad; i++)
                {
                    int elementOffset = i * stride;

                    // Read Positions (X, Y, Z)
                    float x = ReadFloat(buffer, elementOffset + xOffset, xType);
                    float y = ReadFloat(buffer, elementOffset + yOffset, yType);
                    float z = ReadFloat(buffer, elementOffset + zOffset, zType);

                    // Read Colors (R, G, B)
                    byte r = rOffset >= 0 ? ReadByte(buffer, elementOffset + rOffset, rType) : (byte)255;
                    byte g = gOffset >= 0 ? ReadByte(buffer, elementOffset + gOffset, gType) : (byte)255;
                    byte b = bOffset >= 0 ? ReadByte(buffer, elementOffset + bOffset, bType) : (byte)255;

                    // Read Label (optional)
                    int label = labelOffset >= 0 ? ReadInt(buffer, elementOffset + labelOffset, labelType) : 0;

                    points[i] = new PointData(new Vector3(x, y, z), new Color32(r, g, b, 255), label, 0f);
                }
            }
        }
        else
        {
            // --- OPTIMIZED ASCII PARSING ---
            using (StreamReader reader = new StreamReader(path))
            {
                // Skip header again
                for (int i = 0; i < headerLines.Count; i++) reader.ReadLine();

                string line;
                for (int i = 0; i < countToLoad; i++)
                {
                    if ((line = reader.ReadLine()) == null)
                    {
                        System.Array.Resize(ref points, i);
                        break;
                    }

                    string[] tokens = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < properties.Count) continue;

                    float x = xOffset >= 0 ? float.Parse(tokens[properties.FindIndex(p => p.name == "x")], CultureInfo.InvariantCulture) : 0;
                    float y = yOffset >= 0 ? float.Parse(tokens[properties.FindIndex(p => p.name == "y")], CultureInfo.InvariantCulture) : 0;
                    float z = zOffset >= 0 ? float.Parse(tokens[properties.FindIndex(p => p.name == "z")], CultureInfo.InvariantCulture) : 0;

                    byte r = 255, g = 255, b = 255;
                    if (rOffset >= 0)
                    {
                        float rVal = float.Parse(tokens[properties.FindIndex(p => p.name == "red" || p.name == "r" || p.name == "diffuse_red")], CultureInfo.InvariantCulture);
                        r = rVal > 1.0f ? (byte)rVal : (byte)(rVal * 255f);
                    }
                    if (gOffset >= 0)
                    {
                        float gVal = float.Parse(tokens[properties.FindIndex(p => p.name == "green" || p.name == "g" || p.name == "diffuse_green")], CultureInfo.InvariantCulture);
                        g = gVal > 1.0f ? (byte)gVal : (byte)(gVal * 255f);
                    }
                    if (bOffset >= 0)
                    {
                        float bVal = float.Parse(tokens[properties.FindIndex(p => p.name == "blue" || p.name == "b" || p.name == "diffuse_blue")], CultureInfo.InvariantCulture);
                        b = bVal > 1.0f ? (byte)bVal : (byte)(bVal * 255f);
                    }

                    int label = 0;
                    if (labelOffset >= 0)
                    {
                        label = int.Parse(tokens[properties.FindIndex(p => p.name == "label" || p.name == "class" || p.name == "scalar_label")]);
                    }

                    points[i] = new PointData(new Vector3(x, y, z), new Color32(r, g, b, 255), label, 0f);
                }
            }
        }

        return points;
    }

    private PointData[] ParseTXT(string path)
    {
        List<PointData> list = new List<PointData>();
        using (StreamReader reader = new StreamReader(path))
        {
            string line;
            int loaded = 0;

            while ((line = reader.ReadLine()) != null && loaded < maxPointsToLoad)
            {
                line = line.Trim();
                if (line.StartsWith("#") || string.IsNullOrEmpty(line)) continue;

                string[] tokens = line.Split(new char[] { ',', ' ', '\t', ';' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 3) continue;

                float x = 0, y = 0, z = 0;
                float r = 255, g = 255, b = 255;

                float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z);

                if (tokens.Length >= 6)
                {
                    float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
                    float.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out g);
                    float.TryParse(tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
                }

                byte rNorm = r > 1.0f ? (byte)r : (byte)(r * 255f);
                byte gNorm = g > 1.0f ? (byte)g : (byte)(g * 255f);
                byte bNorm = b > 1.0f ? (byte)b : (byte)(b * 255f);

                list.Add(new PointData(new Vector3(x, y, z), new Color32(rNorm, gNorm, bNorm, 255), 0, 0f));
                loaded++;
            }
        }
        return list.ToArray();
    }

    private int GetTypeSize(string type)
    {
        type = type.ToLower();
        if (type == "float" || type == "float32" || type == "int" || type == "int32" || type == "uint" || type == "uint32") return 4;
        if (type == "double" || type == "float64") return 8;
        if (type == "short" || type == "int16" || type == "ushort" || type == "uint16") return 2;
        if (type == "char" || type == "uchar" || type == "int8" || type == "uint8") return 1;
        return 4; // default fallback
    }

    private float ReadFloat(byte[] data, int offset, string type)
    {
        if (type == "float" || type == "float32")
            return System.BitConverter.ToSingle(data, offset);
        if (type == "double" || type == "float64")
            return (float)System.BitConverter.ToDouble(data, offset);
        return 0f;
    }

    private byte ReadByte(byte[] data, int offset, string type)
    {
        if (type == "uchar" || type == "uint8")
            return data[offset];
        if (type == "char" || type == "int8")
            return (byte)data[offset];
        if (type == "float" || type == "float32")
            return (byte)Mathf.Clamp(System.BitConverter.ToSingle(data, offset) * 255.0f, 0, 255);
        return 255;
    }

    private int ReadInt(byte[] data, int offset, string type)
    {
        if (type == "int" || type == "int32")
            return System.BitConverter.ToInt32(data, offset);
        if (type == "uint" || type == "uint32")
            return (int)System.BitConverter.ToUInt32(data, offset);
        if (type == "short" || type == "int16")
            return System.BitConverter.ToInt16(data, offset);
        if (type == "ushort" || type == "uint16")
            return System.BitConverter.ToUInt16(data, offset);
        if (type == "uchar" || type == "uint8" || type == "char" || type == "int8")
            return data[offset];
        return 0;
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
