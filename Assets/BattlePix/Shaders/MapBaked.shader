// Запечённая 2.5D-карта (спека 02a §5.4). Один квад. GPU только рисует: владельцы, вспышки фронта и статусы пар
// приходят из мира между тиками. Рельеф, вода и декор запечены при старте партии (TerrainBaker).
// Слои: запечённый цвет → тонировка владельцем по CellId → штриховка занятой земли (02b) → FrontFx → районы претензий →
// граница в экранном пространстве поверх всего.
Shader "BattlePix/MapBaked"
{
    Properties
    {
        [NoScaleOffset] _TerrainColor ("Terrain color (RGBA32, a = kind)", 2D) = "black" {}
        [NoScaleOffset] _CellId ("Cell id (RG32)", 2D) = "black" {}
        [NoScaleOffset] _OwnerTex ("Owner (R8)", 2D) = "black" {}
        [NoScaleOffset] _TerrainTex ("Terrain (R8)", 2D) = "black" {}
        [NoScaleOffset] _PaletteTex ("Palette (256x1)", 2D) = "white" {}
        [NoScaleOffset] _FrontFxTex ("Front fx (R8)", 2D) = "black" {}
        [NoScaleOffset] _PairStatus ("Pair status (R8 16x16)", 2D) = "black" {}
        [NoScaleOffset] _CoreTex ("Core owner (R8)", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Off
        ZWrite On

        Pass
        {
            Name "MapBaked"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_TerrainColor);
            TEXTURE2D(_CellId);
            TEXTURE2D(_OwnerTex);
            TEXTURE2D(_TerrainTex);
            TEXTURE2D(_PaletteTex);
            TEXTURE2D(_FrontFxTex);
            TEXTURE2D(_PairStatus);
            TEXTURE2D(_CoreTex);
            SAMPLER(bp_point_clamp_sampler);

            float4 _BakeSize;     // x, y — размер запечённой текстуры в px
            float4 _MapSize;      // x, y — размер карты в клетках
            float _HumanId;
            float _FocusNation;
            float _ZoomIndex;     // 0 — ½, 1 — 1:1, 2 — ×2
            float4 _Tint;         // x чужая, y своя (доли 0..1) для текущего зума
            float4 _KindMult;     // x верх, y стена, z декор
            float _LinePx;
            float _FringePx;
            float _OtherFringeLight;
            float _PeaceAlpha;
            float _TruceDash;
            float _GainLight;
            float _LossDark;
            half4 _SkyColor;      // sRGB
            half4 _LineColor;
            half4 _OwnFringe;
            half4 _PeaceLine;
            // 02b: занятая земля — штриховка цветом исконного владельца; районы претензий — пунктирные окружности.
            float _HatchStep;     // шаг штриховки в экранных px
            float _HatchAlpha;
            float4 _Claims[16];   // x, y — центр в клетках, z — радиус, w — id нападающего
            float _ClaimCount;
            float4 _ClaimPreview; // x, y, z как у _Claims; w: 0 — нет, 1 — можно, 2 — нельзя
            half4 _PreviewOk;
            half4 _PreviewBad;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            // Клетка под пикселем текстуры p (в px). x < 0 — над картой (небо) или за краем.
            float2 CellAt(float2 p)
            {
                if (p.x < 0 || p.y < 0 || p.x >= _BakeSize.x || p.y >= _BakeSize.y) return float2(-1, -1);
                float2 uv = (floor(p) + 0.5) / _BakeSize.xy;
                float2 c = floor(SAMPLE_TEXTURE2D_LOD(_CellId, bp_point_clamp_sampler, uv, 0).rg * 65535.0 + 0.5);
                if (c.x >= 65535.0) return float2(-1, -1);
                return c;
            }

            float2 CellUv(float2 c) { return (c + 0.5) / _MapSize.xy; }

            float OwnerOf(float2 c)
            {
                return floor(SAMPLE_TEXTURE2D_LOD(_OwnerTex, bp_point_clamp_sampler, CellUv(c), 0).r * 255.0 + 0.5);
            }

            bool LandOf(float2 c)
            {
                return SAMPLE_TEXTURE2D_LOD(_TerrainTex, bp_point_clamp_sampler, CellUv(c), 0).r > 0.5 / 255.0;
            }

            half3 NationColor(float id)
            {
                // Палитра sRGB-текстура: при чтении уже в линейном, переводим обратно для расчёта в sRGB.
                return LinearToSRGB(SAMPLE_TEXTURE2D_LOD(_PaletteTex, bp_point_clamp_sampler, float2((id + 0.5) / 256.0, 0.5), 0).rgb);
            }

            float PairStatus(float a, float b)
            {
                float2 uv = (float2(a, b) + 0.5) / 16.0;
                return floor(SAMPLE_TEXTURE2D_LOD(_PairStatus, bp_point_clamp_sampler, uv, 0).r * 255.0 + 0.5);
            }

            float Luma(half3 c) { return dot(c, half3(0.299, 0.587, 0.114)); }

            float CoreOf(float2 c)
            {
                return floor(SAMPLE_TEXTURE2D_LOD(_CoreTex, bp_point_clamp_sampler, CellUv(c), 0).r * 255.0 + 0.5);
            }

            // Окружность района: кольцо в одну клетку, пунктир по углу; внутри — лёгкая заливка. 0 — мимо, 1 — заливка, 2 — линия.
            float ClaimMark(float2 cell, float4 cl)
            {
                float2 dv = cell - cl.xy;
                float d = length(dv);
                if (d > cl.z + 0.5) return 0;
                if (d < cl.z - 0.5) return 1;
                float a = atan2(dv.y, dv.x) * cl.z; // длина дуги в клетках
                return fmod(floor(a / 2.0) + 1000.0, 2.0) < 0.5 ? 2 : 1;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float2 p = i.uv * _BakeSize.xy;
                float2 cell = CellAt(p);
                if (cell.x < 0) return half4(SRGBToLinear(_SkyColor.rgb), 1);

                float2 tuv = (floor(p) + 0.5) / _BakeSize.xy;
                float kind = floor(SAMPLE_TEXTURE2D_LOD(_TerrainColor, bp_point_clamp_sampler, tuv, 0).a * 255.0 + 0.5);
                // Зум ½ — мип 1 (усреднение 2×2), иначе мип 0. Расчёт цвета — в sRGB, как в мокапе.
                float mip = _ZoomIndex < 0.5 ? 1 : 0;
                float2 muv = mip > 0.5 ? (floor(p * 0.5) + 0.5) / (_BakeSize.xy * 0.5) : tuv;
                half3 col = LinearToSRGB(SAMPLE_TEXTURE2D_LOD(_TerrainColor, bp_point_clamp_sampler, muv, mip).rgb);

                bool land = LandOf(cell);
                float owner = land ? OwnerOf(cell) : 0;

                // Тонировка «Цвет»: оттенок нации, яркость рельефа.
                if (owner > 0)
                {
                    half3 nc = NationColor(owner);
                    half3 t = min(nc * (Luma(col) / max(Luma(nc), 0.01)), 1.0);
                    float km = kind > 2.5 ? _KindMult.z : kind > 1.5 ? _KindMult.y : _KindMult.x;
                    float s = (owner == _HumanId ? _Tint.y : _Tint.x) * km;
                    col = lerp(col, t, s);
                }

                // Занятая земля (Core ≠ Owner): держатель тонирует, поверх диагональная штриховка цветом исконного владельца.
                if (owner > 0)
                {
                    float core = CoreOf(cell);
                    if (core > 0 && core != owner)
                    {
                        float2 sp0 = i.positionCS.xy;
                        if (fmod(floor((sp0.x + sp0.y) / max(_HatchStep, 1)), 2.0) < 0.5)
                            col = lerp(col, NationColor(core), _HatchAlpha);
                    }
                }

                // FrontFx: взятая клетка светлее, потерянная темнее, остывает.
                float fx = floor(SAMPLE_TEXTURE2D_LOD(_FrontFxTex, bp_point_clamp_sampler, CellUv(cell), 0).r * 255.0 + 0.5);
                if (fx > 0.5 && kind < 2.5)
                {
                    if (fx < 128) col = lerp(col, 1.0, _GainLight * fx / 127.0);
                    else col *= 1.0 - _LossDark * (fx - 128) / 127.0;
                }

                // Районы претензий: заливка 10% и пунктир цветом нападающего; превью выбора — зелёным или красным.
                [loop] for (int ci = 0; ci < 16; ci++)
                {
                    if (ci >= _ClaimCount) break;
                    float m = ClaimMark(cell, _Claims[ci]);
                    half3 cc = NationColor(_Claims[ci].w);
                    if (m > 1.5) col = cc;
                    else if (m > 0.5) col = lerp(col, cc, 0.10);
                }
                if (_ClaimPreview.w > 0.5)
                {
                    float m = ClaimMark(cell, _ClaimPreview);
                    half3 pc = _ClaimPreview.w < 1.5 ? _PreviewOk.rgb : _PreviewBad.rgb;
                    if (m > 1.5) col = pc;
                    else if (m > 0.5) col = lerp(col, pc, 0.15);
                }

                // Граница в экранном пространстве: ближайший по экрану пиксель чужой суши на расстоянии d.
                if (land)
                {
                    float2 dx = ddx(p), dy = ddy(p);
                    float2 dirs[4] = { dx, -dx, dy, -dy };
                    float maxD = _LinePx + _FringePx;
                    float hitD = 0, hitO = 0;
                    [loop] for (float d = 1; d <= maxD && hitD == 0; d += 1)
                    {
                        [unroll] for (int k = 0; k < 4; k++)
                        {
                            float2 c2 = CellAt(p + dirs[k] * d);
                            if (c2.x < 0 || !LandOf(c2)) continue;
                            float o2 = OwnerOf(c2);
                            if (o2 == owner) continue;
                            hitD = d;
                            hitO = o2;
                            break;
                        }
                    }
                    if (hitD > 0)
                    {
                        bool isLine = hitD <= _LinePx;
                        float2 sp = i.positionCS.xy;
                        if (owner == 0 || hitO == 0)
                        {
                            // Граница с ничьей землёй: линия цветом нации, кайма только у нации.
                            float nation = owner == 0 ? hitO : owner;
                            half3 nc = NationColor(nation);
                            if (isLine) col = nc;
                            else if (owner != 0) col = owner == _HumanId ? _OwnFringe.rgb : lerp(nc, 1.0, _OtherFringeLight);
                        }
                        else
                        {
                            float st = PairStatus(owner, hitO);
                            bool focus = _FocusNation > 0 && ((owner == _HumanId && hitO == _FocusNation) || (owner == _FocusNation && hitO == _HumanId));
                            if (st < 0.5)
                            {
                                if (isLine) col = lerp(col, _PeaceLine.rgb, _PeaceAlpha);
                            }
                            else if (st > 1.5)
                            {
                                float on = fmod(floor((sp.x + sp.y) / max(_TruceDash, 1)), 2.0);
                                if (isLine && on < 0.5) col = _LineColor.rgb;
                            }
                            else if (isLine) col = _LineColor.rgb;
                            else
                            {
                                half3 fr = owner == _HumanId ? _OwnFringe.rgb : lerp(NationColor(owner), 1.0, _OtherFringeLight);
                                if (focus) fr = lerp(fr, 1.0, 0.5 + 0.5 * sin(_Time.y * 6.2831853 * 2.0));
                                col = fr;
                            }
                        }
                    }
                }
                return half4(SRGBToLinear(col), 1);
            }
            ENDHLSL
        }
    }
}
