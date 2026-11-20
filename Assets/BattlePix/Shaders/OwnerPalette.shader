// Карта владельцев: байт владельца из R8-текстуры → цвет из палитры 256×1.
// Рельеф из второй R8-текстуры: ничья вода красится цветом воды.
// GPU только рисует, вся симуляция на CPU.
Shader "BattlePix/OwnerPalette"
{
    Properties
    {
        [NoScaleOffset] _OwnerTex ("Owner (R8)", 2D) = "black" {}
        [NoScaleOffset] _PaletteTex ("Palette (256x1)", 2D) = "white" {}
        [NoScaleOffset] _TerrainTex ("Terrain (R8)", 2D) = "white" {}
        _WaterColor ("Water", Color) = (0.075, 0.13, 0.22, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Off
        ZWrite On

        Pass
        {
            Name "OwnerPalette"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_OwnerTex);
            TEXTURE2D(_PaletteTex);
            TEXTURE2D(_TerrainTex);
            half4 _WaterColor;
            SAMPLER(bp_point_clamp_sampler); // inline-сэмплер: point + clamp по имени

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                // R8 хранит байт как value/255. Округляем до индекса и берём центр тексела палитры.
                float owner = SAMPLE_TEXTURE2D(_OwnerTex, bp_point_clamp_sampler, i.uv).r;
                float index = floor(owner * 255.0 + 0.5);
                float terrain = floor(SAMPLE_TEXTURE2D(_TerrainTex, bp_point_clamp_sampler, i.uv).r * 255.0 + 0.5);
                if (index < 0.5 && terrain < 0.5) return _WaterColor; // Terrain.Water = 0
                float2 puv = float2((index + 0.5) / 256.0, 0.5);
                return SAMPLE_TEXTURE2D(_PaletteTex, bp_point_clamp_sampler, puv);
            }
            ENDHLSL
        }
    }
}
