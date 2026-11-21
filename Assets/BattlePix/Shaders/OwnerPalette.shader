// Карта: байт владельца из R8-текстуры → цвет из палитры 256×1. GPU только рисует, вся симуляция на CPU.
// Поверх: вода и тон рельефа (холмы, горы), тонкая светлая граница наций, вспышки своего фронта (FrontFx),
// пульс вдоль фронта удара человека, значки столиц и крепостей, кольцо крепости.
Shader "BattlePix/OwnerPalette"
{
    Properties
    {
        [NoScaleOffset] _OwnerTex ("Owner (R8)", 2D) = "black" {}
        [NoScaleOffset] _PaletteTex ("Palette (256x1)", 2D) = "white" {}
        [NoScaleOffset] _TerrainTex ("Terrain (R8)", 2D) = "white" {}
        [NoScaleOffset] _FrontFxTex ("Front fx (R8)", 2D) = "black" {}
        _WaterColor ("Water", Color) = (0.075, 0.13, 0.22, 1)
        _BorderColor ("Border", Color) = (1, 1, 1, 0.55)
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
            TEXTURE2D(_FrontFxTex);
            SAMPLER(bp_point_clamp_sampler); // inline-сэмплер: point + clamp по имени

            half4 _WaterColor;
            half4 _BorderColor;
            float4 _MapSize;      // x, y — ширина и высота карты в пикселях
            float _HumanId;       // нация человека
            float _StrikeMask;    // биты наций (id − 1), по которым идёт удар человека
            float _FocusNation;   // нация, фронт с которой подсвечен (режим удара), 0 — нет
            float4 _Markers[96];  // x, y — центр в пикселях карты; z — тип (1 столица, 2 крепость, 3 кольцо); w — владелец или радиус
            float _MarkerCount;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float OwnerAt(float2 px)
            {
                float2 uv = (floor(px) + 0.5) / _MapSize.xy;
                return floor(SAMPLE_TEXTURE2D_LOD(_OwnerTex, bp_point_clamp_sampler, uv, 0).r * 255.0 + 0.5);
            }

            float LandAt(float2 px)
            {
                if (px.x < 0 || px.y < 0 || px.x >= _MapSize.x || px.y >= _MapSize.y) return 0;
                float2 uv = (floor(px) + 0.5) / _MapSize.xy;
                return floor(SAMPLE_TEXTURE2D_LOD(_TerrainTex, bp_point_clamp_sampler, uv, 0).r * 255.0 + 0.5);
            }

            half3 Palette(float index)
            {
                return SAMPLE_TEXTURE2D_LOD(_PaletteTex, bp_point_clamp_sampler, float2((index + 0.5) / 256.0, 0.5), 0).rgb;
            }

            bool HasBit(float mask, float id)
            {
                return id >= 1 && fmod(floor(mask / exp2(id - 1)), 2.0) >= 1.0;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float2 p = i.uv * _MapSize.xy;     // координата в пикселях карты, дробная
                float2 cell = floor(p);
                float2 f = p - cell;
                float owner = OwnerAt(cell);
                float land = LandAt(cell);
                if (land < 0.5) return _WaterColor;

                half3 col = Palette(owner);

                // Рельеф (спека 01b §10): 1 равнина, 2 холмы темнее на 10%, 3 горы на 22% и редкая диагональная штриховка.
                // Ничья земля тонируется так же поверх нейтрального цвета палитры.
                if (land > 2.5)
                {
                    col *= 0.78;
                    if (fmod(cell.x + cell.y, 5.0) < 0.5) col *= 0.82;
                }
                else if (land > 1.5) col *= 0.90;

                // Вспышки своего фронта: 1..127 — недавно взятый пиксель (светлее), 129..255 — потерянный (темнее).
                float fx = floor(SAMPLE_TEXTURE2D_LOD(_FrontFxTex, bp_point_clamp_sampler, (cell + 0.5) / _MapSize.xy, 0).r * 255.0 + 0.5);
                if (fx >= 1 && fx <= 127) col = lerp(col, half3(1, 1, 1), 0.45 * fx / 127.0);
                else if (fx >= 129) col = lerp(col, half3(0, 0, 0), 0.45 * (fx - 128) / 127.0);

                // Соседи по 4-соседству: тонкая граница с той стороны, где владелец другой.
                float oL = OwnerAt(cell + float2(-1, 0)), oR = OwnerAt(cell + float2(1, 0));
                float oD = OwnerAt(cell + float2(0, -1)), oU = OwnerAt(cell + float2(0, 1));
                float lL = LandAt(cell + float2(-1, 0)), lR = LandAt(cell + float2(1, 0));
                float lD = LandAt(cell + float2(0, -1)), lU = LandAt(cell + float2(0, 1));
                const float w = 0.18;
                float edge = 0;
                if (lL > 0.5 && oL != owner && f.x < w) edge = 1;
                if (lR > 0.5 && oR != owner && f.x > 1 - w) edge = 1;
                if (lD > 0.5 && oD != owner && f.y < w) edge = 1;
                if (lU > 0.5 && oU != owner && f.y > 1 - w) edge = 1;
                col = lerp(col, _BorderColor.rgb, edge * _BorderColor.a);

                // Фронт с нацией x: пиксель человека рядом с x или пиксель x рядом с человеком.
                float h = _HumanId;
                bool nearHuman = (oL == h && lL > 0.5) || (oR == h && lR > 0.5) || (oD == h && lD > 0.5) || (oU == h && lU > 0.5);
                bool strikeFront = false, focusFront = false;
                if (owner == h)
                {
                    float4 o4 = float4(oL, oR, oD, oU);
                    float4 l4 = float4(lL, lR, lD, lU);
                    [unroll] for (int k = 0; k < 4; k++)
                    {
                        if (l4[k] < 0.5 || o4[k] == h || o4[k] == 0) continue;
                        if (HasBit(_StrikeMask, o4[k])) strikeFront = true;
                        if (o4[k] == _FocusNation) focusFront = true;
                    }
                }
                else if (owner != 0 && nearHuman)
                {
                    strikeFront = HasBit(_StrikeMask, owner);
                    focusFront = owner == _FocusNation;
                }
                if (strikeFront) col = lerp(col, half3(1, 0.95, 0.6), 0.35 + 0.3 * sin(_Time.y * 8.0));
                if (focusFront) col = lerp(col, half3(1, 1, 1), 0.55);

                // Значки и кольца.
                int count = (int)_MarkerCount;
                for (int m = 0; m < count; m++)
                {
                    float4 mk = _Markers[m];
                    float2 d = p - (mk.xy + 0.5);
                    if (mk.z < 1.5)
                    {
                        // Столица: ромб с тёмной обводкой.
                        float r = abs(d.x) + abs(d.y);
                        if (r < 3.2) col = r > 2.3 ? half3(0.05, 0.05, 0.05) : half3(1, 1, 1);
                    }
                    else if (mk.z < 2.5)
                    {
                        // Крепость: квадрат цвета владельца с тёмной рамкой.
                        float r = max(abs(d.x), abs(d.y));
                        if (r < 2.6) col = r > 1.7 ? half3(0.05, 0.05, 0.05) : lerp(Palette(mk.w), half3(1, 1, 1), 0.35);
                    }
                    else
                    {
                        // Кольцо радиуса mk.w.
                        float r = length(d);
                        if (abs(r - mk.w) < 0.45) col = lerp(col, half3(1, 1, 1), 0.85);
                    }
                }
                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
