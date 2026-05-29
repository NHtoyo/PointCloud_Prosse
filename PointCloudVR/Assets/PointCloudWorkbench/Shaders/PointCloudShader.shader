Shader "PointCloudWorkbench/PointCloudShader"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 2.0
        _ColorMode ("Color Mode (0:RGB, 1:Height, 2:Label, 3:Distance)", Int) = 0
        _MinHeight ("Min Height (for HeightMap)", Float) = -2.0
        _MaxHeight ("Max Height (for HeightMap)", Float) = 2.0
        _MaxDistanceThreshold ("Max Distance Threshold (for C2C)", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0
            #include "UnityCG.cginc"

            struct PointData
            {
                float3 position;
                uint originalColor;  // Packed Color32 (RGBA)
                int label;           // lower 16 bits = classId, bit 16 = selected, bit 17 = deleted
                float distance;
            };

            // Compute Buffer
            StructuredBuffer<PointData> _PointBuffer;
            StructuredBuffer<int> _Indices;

            // Shader variables
            float _PointSize;
            int _ColorMode;
            float _MinHeight;
            float _MaxHeight;
            float _MaxDistanceThreshold;
            float4x4 _LocalToWorld; // Passed manually from C# for transform tracking

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
            };

            // Unpack Color32 (RGBA) from uint
            float4 UnpackColor(uint packedColor)
            {
                float r = (packedColor & 0xFF) / 255.0;
                float g = ((packedColor >> 8) & 0xFF) / 255.0;
                float b = ((packedColor >> 16) & 0xFF) / 255.0;
                float a = ((packedColor >> 24) & 0xFF) / 255.0;
                return float4(r, g, b, a);
            }

            // Simple Height Map color helper
            float4 GetHeightColor(float height)
            {
                float t = saturate((height - _MinHeight) / (_MaxHeight - _MinHeight));
                float3 cold = float3(0.0, 0.5, 1.0); // Blue
                float3 warm = float3(1.0, 0.2, 0.0); // Red
                float3 mid = float3(0.0, 1.0, 0.5);  // Green-ish
                
                float3 col;
                if (t < 0.5)
                {
                    col = lerp(cold, mid, t * 2.0);
                }
                else
                {
                    col = lerp(mid, warm, (t - 0.5) * 2.0);
                }
                return float4(col, 1.0);
            }

            // Get color by Label ID (classId)
            float4 GetLabelColor(int label)
            {
                if (label == 0) return float4(0.7, 0.7, 0.7, 1.0); // Unclassified
                if (label == 1) return float4(0.55, 0.35, 0.15, 1.0); // Stem (Brown)
                if (label == 2) return float4(0.1, 0.7, 0.2, 1.0); // Leaf (Green)
                if (label == 3) return float4(1.0, 0.1, 0.1, 1.0); // Fruit (Red)
                if (label == 4) return float4(1.0, 0.9, 0.0, 1.0); // Flower (Yellow)
                if (label == 5) return float4(0.0, 0.6, 0.9, 1.0); // Support (Cyan/Blue)
                if (label == 6) return float4(0.9, 0.0, 0.9, 1.0); // Noise (Magenta)
                
                return float4(0.5, 0.5, 0.5, 1.0);
            }

            // CloudCompare style: Blue (close) -> Green -> Yellow -> Red (far)
            float4 GetDistanceColor(float dist)
            {
                float t = saturate(dist / _MaxDistanceThreshold);
                float3 col;
                if (t < 0.33)
                {
                    col = lerp(float3(0, 0, 1), float3(0, 1, 0), t / 0.33);
                }
                else if (t < 0.66)
                {
                    col = lerp(float3(0, 1, 0), float3(1, 1, 0), (t - 0.33) / 0.33);
                }
                else
                {
                    col = lerp(float3(1, 1, 0), float3(1, 0, 0), (t - 0.66) / 0.34);
                }
                return float4(col, 1.0);
            }

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                
                // One point is rendered as a quad (6 vertices)
                uint pointIndex = _Indices[id / 6];
                uint vertexIndex = id % 6;

                PointData pt = _PointBuffer[pointIndex];
                
                // Check labels using bitwise mask
                int labelVal = pt.label;
                int classId = labelVal & 0xFFFF;
                bool isSelected = (labelVal & 0x10000) != 0;
                bool isDeleted = (labelVal & 0x20000) != 0;

                // Discard deleted points immediately by setting size to 0
                if (isDeleted)
                {
                    o.pos = float4(0, 0, 0, 0);
                    o.color = float4(0, 0, 0, 0);
                    return o;
                }

                float2 offsets[6] = {
                    float2(-0.5, -0.5),
                    float2(-0.5,  0.5),
                    float2( 0.5,  0.5),
                    float2(-0.5, -0.5),
                    float2( 0.5,  0.5),
                    float2( 0.5, -0.5)
                };

                // Transform center point to World Space manually using passed Matrix
                float4 worldPos = mul(_LocalToWorld, float4(pt.position, 1.0));

                // Transform to View Space
                float4 viewPos = mul(UNITY_MATRIX_V, worldPos);

                // Transform to Projection Space (Clip Space)
                o.pos = mul(UNITY_MATRIX_P, viewPos);

                // Add offset in Screen Space (Pixel units)
                // _PointSize is interpreted as screen pixel size (e.g. 1.0, 2.0, 3.0...)
                // Screen size is _ScreenParams.xy
                // To keep pixel size constant regardless of distance, scale offset by o.pos.w
                // We use max(pointSize, 2.0) to ensure it's at least visible if user accidentally set it to 0.005
                float actualPointSize = max(_PointSize, 2.0);
                float2 offset = offsets[vertexIndex] * (actualPointSize * 2.0 / _ScreenParams.xy) * o.pos.w;
                o.pos.xy += offset;

                // Set color according to mode (Override if selected)
                if (isSelected)
                {
                    o.color = float4(1.0, 0.85, 0.0, 1.0); // Bright Yellow for selection highlight
                }
                else
                {
                    if (_ColorMode == 0)
                    {
                        o.color = UnpackColor(pt.originalColor);
                        // Fallback if color is 0 (fully transparent)
                        if (o.color.a < 0.01) o.color = float4(1.0, 1.0, 1.0, 1.0);
                    }
                    else if (_ColorMode == 1)
                    {
                        o.color = GetHeightColor(pt.position.y);
                    }
                    else if (_ColorMode == 2)
                    {
                        o.color = GetLabelColor(classId); // Use unpacked classId
                    }
                    else // _ColorMode == 3
                    {
                        o.color = GetDistanceColor(pt.distance);
                    }
                }

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Discard invisible pixel if alpha is 0 (deleted point fallback)
                if (i.color.a < 0.01) discard;
                return i.color;
            }
            ENDCG
        }
    }
}
