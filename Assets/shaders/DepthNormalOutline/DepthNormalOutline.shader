Shader "Hidden/XYXS/DepthNormalOutline"
{
    Properties
    {
        _OutlineColor("Outline Color", Color) = (0, 0, 0, 1)
        _OutlineParams("Outline Params", Vector) = (1, 2, 1.5, 1)
        _OutlineThresholds("Outline Thresholds", Vector) = (0.03, 0.2, 0.08, 0)
        _SourceTexture("Source Texture", 2D) = "black" {}
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
            Name "DepthNormalOutline"

            HLSLPROGRAM
            #pragma vertex VertOutline
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            TEXTURE2D(_SourceTexture);
            float4 _SourceTexture_TexelSize;

            float4 _OutlineColor;
            float4 _OutlineParams;
            float4 _OutlineThresholds;
            float4 _DistanceFadeParams;
            float _DebugView;

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

            Varyings VertOutline(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float LinearEyeDepth01(float rawDepth)
            {
                return LinearEyeDepth(rawDepth, _ZBufferParams);
            }

            float DepthDifference(float centerDepth, float sampleDepth)
            {
                float centerEye = LinearEyeDepth01(centerDepth);
                float sampleEye = LinearEyeDepth01(sampleDepth);
                return abs(centerEye - sampleEye) / max(centerEye, 1.0);
            }

            float NormalDifference(float3 centerNormal, float3 sampleNormal)
            {
                centerNormal = normalize(centerNormal);
                sampleNormal = normalize(sampleNormal);
                return 1.0 - saturate(dot(centerNormal, sampleNormal));
            }

            float EdgeAtOffset(float2 uv, float2 offset, float centerDepth, float3 centerNormal)
            {
                float2 sampleUv = saturate(uv + offset);
                float sampleDepth = SampleSceneDepth(sampleUv);
                float3 sampleNormal = SampleSceneNormals(sampleUv);

                float depthEdge = DepthDifference(centerDepth, sampleDepth) * _OutlineParams.y;
                float normalEdge = NormalDifference(centerNormal, sampleNormal) * _OutlineParams.z;

                float depthMask = smoothstep(_OutlineThresholds.x, _OutlineThresholds.x + _OutlineThresholds.z, depthEdge);
                float normalMask = smoothstep(_OutlineThresholds.y, _OutlineThresholds.y + _OutlineThresholds.z, normalEdge);
                return max(depthMask, normalMask);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord.xy;
                half4 sceneColor = SAMPLE_TEXTURE2D_LOD(_SourceTexture, sampler_LinearClamp, uv, 0.0);

                if (_DebugView > 4.5)
                    return sceneColor;

                if (_DebugView > 0.5 && _DebugView < 1.5)
                    return half4(_OutlineColor.rgb, 1.0);

                float centerDepth = SampleSceneDepth(uv);
                float3 centerNormal = SampleSceneNormals(uv);
                float eyeDepth = LinearEyeDepth01(centerDepth);
                float distanceFade = 1.0 - smoothstep(_DistanceFadeParams.x, _DistanceFadeParams.y, eyeDepth);
                distanceFade = lerp(1.0, distanceFade, _OutlineThresholds.w);
                float2 texel = _SourceTexture_TexelSize.xy * max(_OutlineParams.x * distanceFade, 0.25);

                float edge = 0.0;
                edge = max(edge, EdgeAtOffset(uv, float2(texel.x, 0.0), centerDepth, centerNormal));
                edge = max(edge, EdgeAtOffset(uv, float2(-texel.x, 0.0), centerDepth, centerNormal));
                edge = max(edge, EdgeAtOffset(uv, float2(0.0, texel.y), centerDepth, centerNormal));
                edge = max(edge, EdgeAtOffset(uv, float2(0.0, -texel.y), centerDepth, centerNormal));

                float diagonalScale = 0.70710678;
                edge = max(edge, EdgeAtOffset(uv, float2(texel.x, texel.y) * diagonalScale, centerDepth, centerNormal));
                edge = max(edge, EdgeAtOffset(uv, float2(-texel.x, texel.y) * diagonalScale, centerDepth, centerNormal));
                edge = max(edge, EdgeAtOffset(uv, float2(texel.x, -texel.y) * diagonalScale, centerDepth, centerNormal));
                edge = max(edge, EdgeAtOffset(uv, float2(-texel.x, -texel.y) * diagonalScale, centerDepth, centerNormal));

                edge = saturate(edge * _OutlineParams.w * distanceFade);

                if (_DebugView > 3.5)
                    return half4(centerNormal * 0.5 + 0.5, 1.0);

                if (_DebugView > 2.5)
                {
                    float depth01 = saturate(1.0 - eyeDepth * 0.02);
                    return half4(depth01.xxx, 1.0);
                }

                if (_DebugView > 1.5)
                    return half4(edge.xxx, 1.0);

                sceneColor.rgb = lerp(sceneColor.rgb, _OutlineColor.rgb, edge);
                return sceneColor;
            }
            ENDHLSL
        }
    }
}
