Shader "Unlit/SDFBase"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [Enum(Circle,0,Square,1,Rectangle,2,Triangle,3)] _Shape("Shape", Float) = 0
        _Radius("Radius",float) = 0.2
        _RectangleSize("Rectangle Size", Vector) = (0.4, 0.2, 0, 0)
        _Offset("Offset",Range(-1,1)) = 0.1
        _feather("_feather",Range(0,1)) = 0.1
        _DistortStrength("Distort Strength", Range(0, 0.2)) = 0.03
        _DistortScale("Distort Scale", Range(1, 50)) = 12
        _DistortSpeed("Distort Speed", Range(0, 5)) = 1
    }
    SubShader
    {
        Tags
        {
            "RenderType"="TransparentCutout"
            "Queue"="AlphaTest"
            "RenderPipeline"="UniversalPipeline"
        }
        LOD 100

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }
            //Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/shaders/HLSL/BaseToolFunction.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float fogCoord : TEXCOORD1;
                float4 positionHCS : SV_POSITION;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _RectangleSize;
                float _Shape;
                float _Radius;
                float _Offset;
                float _feather;
                float _DistortStrength;
                float _DistortScale;
                float _DistortSpeed;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.fogCoord = ComputeFogFactor(output.positionHCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 realUv = input.uv * 2.0 - 1.0;
                float2 rectangleHalfSize = _RectangleSize.xy * 0.5;
                float shape01 = drawShape(realUv, _Shape, _Radius, rectangleHalfSize);
                float shape02 = drawShape(float2(realUv.x, realUv.y + _Offset), _Shape, _Radius, rectangleHalfSize);
                float d = smoothUnion(shape01, shape02, _feather);
                
                float n = noise(float2(realUv.x * _DistortScale + _Time.y *_DistortSpeed*0.01,realUv.y * _DistortScale + _Time.y * _DistortSpeed));
                d += (n - 0.5) * _DistortStrength;

                float circle = 1.0 - smoothstep(0.0, fwidth(d), d);
                clip(circle - 0.01);
                float2 n1;
                n1.x = noise(realUv * _DistortScale + _Time.y * _DistortSpeed);
                n1.y = noise(realUv * _DistortScale + 17.37 + _Time.y * _DistortSpeed);
                n1 = n1 - 0.5;
                float2 distortedUv = lerp(input.uv, input.uv +(n1-0.5)*0.01 , 0.75);
                half4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, distortedUv);
                col *= half4(circle, circle, circle, 1.0);
                col.rgb = MixFog(col.rgb, input.fogCoord);

                return col;
            }
            ENDHLSL
        }
    }
}
