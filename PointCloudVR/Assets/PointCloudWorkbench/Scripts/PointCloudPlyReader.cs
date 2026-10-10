using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PointCloudWorkbench
{
    public struct PlyVertex
    {
        public float X;
        public float Y;
        public float Z;
        public byte Red;
        public byte Green;
        public byte Blue;
        public int Label;

        public PlyVertex(float x, float y, float z, byte red, byte green, byte blue, int label)
        {
            X = x;
            Y = y;
            Z = z;
            Red = red;
            Green = green;
            Blue = blue;
            Label = label;
        }
    }

    public sealed class PointCloudPlyData
    {
        public PlyVertex[] Vertices { get; private set; }
        public bool ScaleCalibrated { get; private set; }

        internal PointCloudPlyData(PlyVertex[] vertices, bool scaleCalibrated)
        {
            Vertices = vertices;
            ScaleCalibrated = scaleCalibrated;
        }
    }

    public static class PointCloudPlyReader
    {
        private sealed class Property
        {
            public string Name;
            public string Type;
            public int Size;
            public int Offset;
            public int Index;
        }

        private enum EncodingKind
        {
            Ascii,
            BinaryLittleEndian,
            BinaryBigEndian
        }

        public static int Validate(string path, CancellationToken cancellationToken = default(CancellationToken))
        {
            byte[] validation = ReadConverted(path, int.MaxValue, vertex =>
            {
                if (!IsFinite(vertex.X) || !IsFinite(vertex.Y) || !IsFinite(vertex.Z))
                    throw new InvalidDataException("PLY vertex coordinates contain NaN or infinity.");
                return (byte)0;
            }, out _, cancellationToken);
            return validation.Length;
        }

        public static bool HasScaleCalibrationMarker(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                List<string> header = ReadHeader(stream);
                if (header.Count == 0 || !string.Equals(header[0], "ply", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("PLY header must start with 'ply'.");
                ParseHeader(header, out _, out _, out _, out bool calibrated);
                return calibrated;
            }
        }

        public static PointCloudPlyData Read(string path, int maxPointsToLoad, CancellationToken cancellationToken = default(CancellationToken))
        {
            bool scaleCalibrated;
            PlyVertex[] vertices = ReadConverted(path, maxPointsToLoad, vertex => vertex,
                out scaleCalibrated, cancellationToken);
            return new PointCloudPlyData(vertices, scaleCalibrated);
        }

        public static T[] ReadConverted<T>(string path, int maxPointsToLoad, Func<PlyVertex, T> convert,
            out bool scaleCalibrated, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A PLY path is required.", nameof(path));
            if (maxPointsToLoad < 1) throw new ArgumentOutOfRangeException(nameof(maxPointsToLoad));
            if (convert == null) throw new ArgumentNullException(nameof(convert));

            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                List<string> header = ReadHeader(stream);
                if (header.Count == 0 || !string.Equals(header[0], "ply", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("PLY header must start with 'ply'.");

                EncodingKind encoding = ParseHeader(header, out int vertexCount, out int stride,
                    out List<Property> properties, out scaleCalibrated);
                int countToLoad = Math.Min(vertexCount, maxPointsToLoad);
                T[] vertices = new T[countToLoad];

                if (encoding == EncodingKind.Ascii)
                    ReadAsciiVertices(stream, countToLoad, properties, vertices, convert, cancellationToken);
                else
                    ReadBinaryVertices(stream, countToLoad, stride, properties, vertices,
                        encoding == EncodingKind.BinaryLittleEndian, convert, cancellationToken);

                return vertices;
            }
        }

        private static List<string> ReadHeader(FileStream stream)
        {
            const int maxHeaderBytes = 4 * 1024 * 1024;
            List<string> lines = new List<string>();
            List<byte> currentLine = new List<byte>();
            int totalBytes = 0;
            int value;
            while ((value = stream.ReadByte()) != -1)
            {
                if (++totalBytes > maxHeaderBytes) throw new InvalidDataException("PLY header is unexpectedly large.");
                if (value == '\n')
                {
                    string line = Encoding.ASCII.GetString(currentLine.ToArray()).Trim();
                    lines.Add(line);
                    currentLine.Clear();
                    if (string.Equals(line, "end_header", StringComparison.OrdinalIgnoreCase)) return lines;
                }
                else if (value != '\r')
                {
                    currentLine.Add((byte)value);
                }
            }
            throw new InvalidDataException("PLY header is truncated or missing end_header.");
        }

        private static EncodingKind ParseHeader(List<string> lines, out int vertexCount, out int stride,
            out List<Property> properties, out bool scaleCalibrated)
        {
            vertexCount = -1;
            stride = 0;
            properties = new List<Property>();
            scaleCalibrated = false;
            EncodingKind encoding = EncodingKind.Ascii;
            bool formatSeen = false;
            bool vertexSeen = false;
            bool currentElementIsVertex = false;
            bool priorNonVertexHasRecords = false;

            for (int i = 1; i < lines.Count; i++)
            {
                string[] tokens = lines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                if (tokens[0].Equals("comment", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokens.Length >= 3 && tokens[1].Equals("pcwb_scale_calibrated", StringComparison.OrdinalIgnoreCase) &&
                        tokens[2].Equals("true", StringComparison.OrdinalIgnoreCase))
                        scaleCalibrated = true;
                    continue;
                }
                if (tokens[0].Equals("format", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokens.Length != 3 || !tokens[2].Equals("1.0", StringComparison.Ordinal))
                        throw new InvalidDataException("Only PLY format version 1.0 is supported.");
                    if (tokens[1].Equals("ascii", StringComparison.OrdinalIgnoreCase)) encoding = EncodingKind.Ascii;
                    else if (tokens[1].Equals("binary_little_endian", StringComparison.OrdinalIgnoreCase)) encoding = EncodingKind.BinaryLittleEndian;
                    else if (tokens[1].Equals("binary_big_endian", StringComparison.OrdinalIgnoreCase)) encoding = EncodingKind.BinaryBigEndian;
                    else throw new InvalidDataException("Unsupported PLY encoding: " + tokens[1]);
                    formatSeen = true;
                    continue;
                }
                if (tokens[0].Equals("element", StringComparison.OrdinalIgnoreCase))
                {
                    if (tokens.Length != 3 || !int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out int elementCount) || elementCount < 0)
                        throw new InvalidDataException("PLY element declaration has an invalid count.");
                    currentElementIsVertex = tokens[1].Equals("vertex", StringComparison.OrdinalIgnoreCase);
                    if (currentElementIsVertex)
                    {
                        if (vertexSeen) throw new InvalidDataException("PLY header contains multiple vertex elements.");
                        vertexSeen = true;
                        vertexCount = elementCount;
                        if (priorNonVertexHasRecords)
                            throw new InvalidDataException("PLY elements with records before vertex are not supported.");
                    }
                    else if (!vertexSeen && elementCount > 0)
                    {
                        priorNonVertexHasRecords = true;
                    }
                    continue;
                }
                if (tokens[0].Equals("property", StringComparison.OrdinalIgnoreCase) && currentElementIsVertex)
                {
                    if (tokens.Length == 5 && tokens[1].Equals("list", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("List properties in the PLY vertex element are not supported.");
                    if (tokens.Length != 3) throw new InvalidDataException("PLY vertex properties must be scalar values.");
                    string type = tokens[1].ToLowerInvariant();
                    int size = GetTypeSize(type);
                    if (size == 0) throw new InvalidDataException("Unsupported PLY vertex property type: " + tokens[1]);
                    properties.Add(new Property
                    {
                        Name = tokens[2].ToLowerInvariant(), Type = type, Size = size,
                        Offset = stride, Index = properties.Count
                    });
                    stride = checked(stride + size);
                }
            }

            if (!formatSeen) throw new InvalidDataException("PLY header has no supported format declaration.");
            if (!vertexSeen || vertexCount < 0) throw new InvalidDataException("PLY header has no valid vertex element.");
            if (stride <= 0) throw new InvalidDataException("PLY vertex element has no scalar properties.");
            if (FindProperty(properties, "x") == null || FindProperty(properties, "y") == null || FindProperty(properties, "z") == null)
                throw new InvalidDataException("PLY vertex element must contain x, y, and z properties.");
            return encoding;
        }

        private static void ReadAsciiVertices<T>(Stream stream, int count, List<Property> properties, T[] vertices,
            Func<PlyVertex, T> convert, CancellationToken cancellationToken)
        {
            Property x = FindProperty(properties, "x");
            Property y = FindProperty(properties, "y");
            Property z = FindProperty(properties, "z");
            Property red = FindColorProperty(properties, "red", "r", "diffuse_red");
            Property green = FindColorProperty(properties, "green", "g", "diffuse_green");
            Property blue = FindColorProperty(properties, "blue", "b", "diffuse_blue");
            Property label = FindColorProperty(properties, "label", "class", "scalar_label");
            using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 65536, true))
            {
                for (int i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string line = reader.ReadLine();
                    if (line == null) throw new InvalidDataException("PLY ASCII vertex payload is truncated at vertex " + i + ".");
                    string[] values = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (values.Length < properties.Count)
                        throw new InvalidDataException("PLY ASCII vertex " + i + " has fewer values than declared properties.");
                    vertices[i] = convert(CreateVertex(values, i, x, y, z, red, green, blue, label));
                }
            }
        }

        private static PlyVertex CreateVertex(string[] values, int index, Property xProperty, Property yProperty,
            Property zProperty, Property red, Property green, Property blue, Property label)
        {
            double x = ParseAsciiScalar(values, xProperty, index);
            double y = ParseAsciiScalar(values, yProperty, index);
            double z = ParseAsciiScalar(values, zProperty, index);
            return new PlyVertex(ToFiniteFloat(x, "x", index), ToFiniteFloat(y, "y", index), ToFiniteFloat(z, "z", index),
                red == null ? (byte)255 : ToColor(ParseAsciiScalar(values, red, index), red.Type),
                green == null ? (byte)255 : ToColor(ParseAsciiScalar(values, green, index), green.Type),
                blue == null ? (byte)255 : ToColor(ParseAsciiScalar(values, blue, index), blue.Type),
                label == null ? 0 : ToLabel(ParseAsciiScalar(values, label, index), index));
        }

        private static double ParseAsciiScalar(string[] values, Property property, int index)
        {
            int propertyIndex = property.Index;
            if (propertyIndex < 0 || !double.TryParse(values[propertyIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ||
                double.IsNaN(result) || double.IsInfinity(result))
                throw new InvalidDataException("PLY ASCII vertex " + index + " contains an invalid " + property.Name + " value.");
            return result;
        }

        private static void ReadBinaryVertices<T>(Stream stream, int count, int stride, List<Property> properties,
            T[] vertices, bool payloadIsLittleEndian, Func<PlyVertex, T> convert, CancellationToken cancellationToken)
        {
            const int targetBufferBytes = 1024 * 1024;
            int recordsPerChunk = Math.Max(1, targetBufferBytes / stride);
            byte[] buffer = new byte[checked(Math.Min(count, recordsPerChunk) * stride)];
            bool reverse = payloadIsLittleEndian != BitConverter.IsLittleEndian;
            Property x = FindProperty(properties, "x");
            Property y = FindProperty(properties, "y");
            Property z = FindProperty(properties, "z");
            Property red = FindColorProperty(properties, "red", "r", "diffuse_red");
            Property green = FindColorProperty(properties, "green", "g", "diffuse_green");
            Property blue = FindColorProperty(properties, "blue", "b", "diffuse_blue");
            Property label = FindColorProperty(properties, "label", "class", "scalar_label");

            for (int batchStart = 0; batchStart < count; batchStart += recordsPerChunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int batchCount = Math.Min(recordsPerChunk, count - batchStart);
                int batchByteCount = checked(batchCount * stride);
                ReadExactly(stream, buffer, batchByteCount, batchStart);
                for (int localIndex = 0; localIndex < batchCount; localIndex++)
                {
                    int i = batchStart + localIndex;
                    if ((localIndex & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int recordOffset = localIndex * stride;
                    if (reverse)
                    {
                        for (int p = 0; p < properties.Count; p++)
                        {
                            Property property = properties[p];
                            if (property.Size > 1) Array.Reverse(buffer, recordOffset + property.Offset, property.Size);
                        }
                    }
                    double xValue = ReadScalar(buffer, recordOffset, x);
                    double yValue = ReadScalar(buffer, recordOffset, y);
                    double zValue = ReadScalar(buffer, recordOffset, z);
                    vertices[i] = convert(new PlyVertex(ToFiniteFloat(xValue, "x", i), ToFiniteFloat(yValue, "y", i), ToFiniteFloat(zValue, "z", i),
                        red == null ? (byte)255 : ToColor(ReadScalar(buffer, recordOffset, red), red.Type),
                        green == null ? (byte)255 : ToColor(ReadScalar(buffer, recordOffset, green), green.Type),
                        blue == null ? (byte)255 : ToColor(ReadScalar(buffer, recordOffset, blue), blue.Type),
                        label == null ? 0 : ToLabel(ReadScalar(buffer, recordOffset, label), i)));
                }
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int byteCount, int firstVertexIndex)
        {
            int read = 0;
            while (read < byteCount)
            {
                int amount = stream.Read(buffer, read, byteCount - read);
                if (amount <= 0) throw new InvalidDataException("PLY binary vertex payload is truncated at vertex " + firstVertexIndex + ".");
                read += amount;
            }
        }

        private static double ReadScalar(byte[] data, int recordOffset, Property property)
        {
            int offset = recordOffset + property.Offset;
            switch (property.Type)
            {
                case "char": case "int8": return unchecked((sbyte)data[offset]);
                case "uchar": case "uint8": return data[offset];
                case "short": case "int16": return BitConverter.ToInt16(data, offset);
                case "ushort": case "uint16": return BitConverter.ToUInt16(data, offset);
                case "int": case "int32": return BitConverter.ToInt32(data, offset);
                case "uint": case "uint32": return BitConverter.ToUInt32(data, offset);
                case "float": case "float32": return BitConverter.ToSingle(data, offset);
                case "double": case "float64": return BitConverter.ToDouble(data, offset);
                default: throw new InvalidDataException("Unsupported PLY scalar type: " + property.Type);
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static int GetTypeSize(string type)
        {
            switch (type)
            {
                case "char": case "int8": case "uchar": case "uint8": return 1;
                case "short": case "int16": case "ushort": case "uint16": return 2;
                case "int": case "int32": case "uint": case "uint32": case "float": case "float32": return 4;
                case "double": case "float64": return 8;
                default: return 0;
            }
        }

        private static Property FindProperty(List<Property> properties, string name)
        {
            for (int i = 0; i < properties.Count; i++)
                if (properties[i].Name == name) return properties[i];
            return null;
        }

        private static Property FindColorProperty(List<Property> properties, string first, string second, string third)
        {
            return FindProperty(properties, first) ?? FindProperty(properties, second) ?? FindProperty(properties, third);
        }

        private static float ToFiniteFloat(double value, string name, int index)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < -float.MaxValue || value > float.MaxValue)
                throw new InvalidDataException("PLY vertex " + index + " has an out-of-range " + name + " coordinate.");
            float converted = (float)value;
            if (float.IsNaN(converted) || float.IsInfinity(converted))
                throw new InvalidDataException("PLY vertex " + index + " has a non-finite " + name + " coordinate.");
            return converted;
        }

        private static byte ToColor(double value, string type)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidDataException("PLY color value must be finite.");
            if ((type == "float" || type == "float32" || type == "double" || type == "float64") && value <= 1.0)
                value *= 255.0;
            if (value < 0.0) return 0;
            if (value > 255.0) return 255;
            return (byte)value;
        }

        private static int ToLabel(double value, int index)
        {
            if (value < int.MinValue || value > int.MaxValue || Math.Truncate(value) != value)
                throw new InvalidDataException("PLY vertex " + index + " has an invalid integer label.");
            return (int)value;
        }
    }
}
