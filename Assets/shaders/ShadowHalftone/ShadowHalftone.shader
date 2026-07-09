Shader "Hidden/XYXS/ShadowHalftone"
{
    Properties
    {
        _SourceTexture("Source Texture", 2D) = "black" {}
        _PatternColor("Pattern Color", Color) = (0, 0, 0, 1)
        _HalftoneParams("Halftone Params", Vector) = (0.45, 0.08, 1, 0.261799)
        _PatternParams("Pattern Params", Vector) = (18, 0.35, 0.04, 0)
        _DebugView("Debug View", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "ShadowHalftone"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_SourceTexture);
            float4 _SourceTexture_TexelSize;

            float4 _PatternColor;
            float4 _HalftoneParams;
            float4 _PatternParams;
            float _DebugView;
            float _UseWorldSpacePattern;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float GetLuminance(float3 color)
            {
                return dot(color, float3(0.2126, 0.7152, 0.0722));
            }

            float2 RotatePoint(float2 position, float radians)
            {
                float s = sin(radians);
                float c = cos(radians);
                return float2(
                    position.x * c - position.y * s,
                    position.x * s + position.y * c);
            }

            float2 GetScreenPatternPosition(float2 uv)
            {
                float2 pixelCenter = _SourceTexture_TexelSize.zw * 0.5;
                float2 pixelPos = uv * _SourceTexture_TexelSize.zw;
                return RotatePoint(pixelPos - pixelCenter, _HalftoneParams.w) + pixelCenter;
            }

            float2 GetWorldPatternPosition(float2 uv)
            {
                float deviceDepth = SampleSceneDepth(uv);
            #if UNITY_REVERSED_Z
                if (deviceDepth <= 0.000001)
                    return GetScreenPatternPosition(uv);
            #else
                if (deviceDepth >= 0.999999)
                    return GetScreenPatternPosition(uv);
            #endif
            #if !UNITY_REVERSED_Z
                deviceDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, deviceDepth);
            #endif
                float3 positionWS = ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);
                return RotatePoint(positionWS.xz, _HalftoneParams.w);
            }

            float GetDotPattern(float2 patternPosition, float luminance)
            {
                float cellSize = max(_PatternParams.x, 0.001);
                float baseRadius = saturate(_PatternParams.y);
                float patternSoftness = saturate(_PatternParams.z);
                float gridLineWidth = saturate(_PatternParams.w);

                float2 cellUv = frac(patternPosition / cellSize);
                float2 centeredCell = cellUv - 0.5;

                float shadowStrength = saturate((_HalftoneParams.x - luminance) / max(_HalftoneParams.x, 0.001));
                float radius = baseRadius * lerp(0.55, 1.35, shadowStrength);
                float distanceToCenter = length(centeredCell);
                float dotMask = 1.0 - smoothstep(radius, radius + max(patternSoftness, 0.001), distanceToCenter);

                float2 distanceToEdge = min(cellUv, 1.0 - cellUv);
                float lineDistance = min(distanceToEdge.x, distanceToEdge.y);
                float gridMask = 1.0 - smoothstep(gridLineWidth, gridLineWidth + max(patternSoftness, 0.001), lineDistance);
                gridMask *= step(0.0001, gridLineWidth);

                return saturate(max(dotMask, gridMask));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;
                half4 sourceColor = SAMPLE_TEXTURE2D_LOD(_SourceTexture, sampler_LinearClamp, uv, 0.0);
                float luminance = GetLuminance(sourceColor.rgb);

                if (_DebugView > 3.5)
                    return sourceColor;

                if (_DebugView > 0.5 && _DebugView < 1.5)
                    return half4(luminance.xxx, 1.0);

                float threshold = saturate(_HalftoneParams.x);
                float softness = max(_HalftoneParams.y, 0.001);
                float shadowMask = 1.0 - smoothstep(threshold - softness, threshold + softness, luminance);

                if (_DebugView > 1.5 && _DebugView < 2.5)
                    return half4(shadowMask.xxx, 1.0);

                float2 patternPosition = _UseWorldSpacePattern > 0.5
                    ? GetWorldPatternPosition(uv)
                    : GetScreenPatternPosition(uv);
                float pattern = GetDotPattern(patternPosition, luminance);

                if (_DebugView > 2.5 && _DebugView < 3.5)
                    return half4(pattern.xxx, 1.0);

                float finalMask = saturate(shadowMask * pattern * _HalftoneParams.z);
                sourceColor.rgb = lerp(sourceColor.rgb, _PatternColor.rgb, finalMask * _PatternColor.a);
                return sourceColor;
            }
            ENDHLSL
        }
    }
}
