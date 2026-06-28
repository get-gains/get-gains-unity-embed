Shader "FlutterEmbed/WireframeBackground"
{
    Properties
    {
        _GridColor ("Grid Color", Color) = (1, 0.45, 0, 0.15)
        _BackgroundColor ("Background Color", Color) = (0.04, 0.04, 0.04, 1)
        _GridScale ("Grid Scale", Float) = 4
        _LineThickness ("Line Thickness", Range(0.001, 0.1)) = 0.015
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            half4 _GridColor;
            half4 _BackgroundColor;
            float _GridScale;
            float _LineThickness;

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 p = input.positionWS * _GridScale;
                float3 distToEdge = min(frac(p), 1.0 - frac(p));
                float3 df = fwidth(p);
                float3 lines = 1.0 - smoothstep(_LineThickness - df, _LineThickness + df, distToEdge);
                float grid = max(lines.x, max(lines.y, lines.z));
                return lerp(_BackgroundColor, _GridColor, grid * _GridColor.a);
            }
            ENDHLSL
        }
    }
}
