using System;
using System.Collections.Generic;
using BattlePix.Core;
using Newtonsoft.Json;
using UnityEngine;
using Terrain = BattlePix.Core.Terrain;

namespace BattlePix.Game
{
    /// <summary>
    /// palette.json (спека 02a §4.3): всё, что читает только рендер. В DataHash не входит.
    /// Цвета — строки "#RRGGBB", доли — базисные пункты (10 000 = 100%).
    /// </summary>
    public sealed class PaletteData
    {
        [JsonProperty("cellPx")] public int CellPx = 6;
        [JsonProperty("terraceOffsets")] public int[] TerraceOffsets = { 0, 5, 10, 16, 23, 32, 44 };
        [JsonProperty("bandLevels")] public BandLevels Bands = new BandLevels();
        [JsonProperty("water")] public WaterColors Water = new WaterColors();
        [JsonProperty("biomeTop")] public Dictionary<string, string> BiomeTop = new Dictionary<string, string>();
        [JsonProperty("biomeWall")] public Dictionary<string, string> BiomeWall = new Dictionary<string, string>();
        [JsonProperty("topNoise_bp")] public int TopNoise_bp = 450;
        [JsonProperty("levelLight_bp")] public int LevelLight_bp = 350;
        [JsonProperty("rimLight_bp")] public int RimLight_bp = 1300;
        [JsonProperty("aoDark_bp")] public int AoDark_bp = 2000;
        [JsonProperty("wallDarkToBottom_bp")] public int WallDarkToBottom_bp = 3500;
        [JsonProperty("strataEvery")] public int StrataEvery = 4;
        [JsonProperty("strataDark_bp")] public int StrataDark_bp = 1400;
        [JsonProperty("decorDensity_bp")] public Dictionary<string, int> DecorDensity_bp = new Dictionary<string, int>();
        [JsonProperty("tint_bp")] public ZoomPair Tint_bp = new ZoomPair();
        [JsonProperty("tintKindMult_bp")] public KindMult TintKindMult_bp = new KindMult();
        [JsonProperty("border")] public BorderStyle Border = new BorderStyle();
        [JsonProperty("fx")] public FxStyle Fx = new FxStyle();
        /// <summary>Война на карте (спека 02b §9): штриховка занятой земли, превью района.</summary>
        [JsonProperty("war")] public WarStyle War = new WarStyle();
        [JsonProperty("ocean")] public string Ocean = "#265496";
        [JsonProperty("sky")] public string Sky = "#265496";

        public sealed class BandLevels
        {
            [JsonProperty("plains")] public int[] Plains = { 1, 1 };
            [JsonProperty("hills")] public int[] Hills = { 2, 3 };
            [JsonProperty("mountains")] public int[] Mountains = { 4, 6 };
        }

        public sealed class WaterColors
        {
            [JsonProperty("tones")] public string[] Tones = { "#60ACD6", "#468CC8", "#346EB2", "#265496" };
            [JsonProperty("toneDist")] public int[] ToneDist = { 1, 3, 7 };
            [JsonProperty("foam")] public string Foam = "#96CEE8";
        }

        /// <summary>По зуму ½ / 1 / 2: [чужая, своя].</summary>
        public sealed class ZoomPair
        {
            [JsonProperty("half")] public int[] Half = { 4200, 5000 };
            [JsonProperty("one")] public int[] One = { 3600, 4200 };
            [JsonProperty("two")] public int[] Two = { 3000, 3600 };
        }

        public sealed class KindMult
        {
            [JsonProperty("face")] public int Face = 10000;
            [JsonProperty("wall")] public int Wall = 6000;
            [JsonProperty("decor")] public int Decor = 3500;
        }

        public sealed class ZoomInts
        {
            [JsonProperty("half")] public int Half = 2;
            [JsonProperty("one")] public int One = 2;
            [JsonProperty("two")] public int Two = 3;
        }

        public sealed class BorderStyle
        {
            [JsonProperty("linePx")] public int LinePx = 1;
            [JsonProperty("fringePx")] public ZoomInts FringePx = new ZoomInts();
            [JsonProperty("line")] public string Line = "#14121A";
            [JsonProperty("ownFringe")] public string OwnFringe = "#FFFFFF";
            [JsonProperty("otherFringeLight_bp")] public int OtherFringeLight_bp = 2500;
            [JsonProperty("peaceLine")] public string PeaceLine = "#C8CCD4";
            [JsonProperty("peaceAlpha_bp")] public int PeaceAlpha_bp = 6000;
            [JsonProperty("truceDash")] public int TruceDash = 4;
        }

        public sealed class WarStyle
        {
            [JsonProperty("hatchStep")] public int HatchStep = 4;
            [JsonProperty("hatchAlpha_bp")] public int HatchAlpha_bp = 3500;
            [JsonProperty("claimOk")] public string ClaimOk = "#5AD25A";
            [JsonProperty("claimBad")] public string ClaimBad = "#E04848";
        }

        public sealed class FxStyle
        {
            [JsonProperty("gainLight_bp")] public int GainLight_bp = 4500;
            [JsonProperty("lossDark_bp")] public int LossDark_bp = 4500;
            [JsonProperty("seconds")] public float Seconds = 1f;
        }

        static readonly JsonSerializerSettings Strict = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };

        public static PaletteData FromJson(string json)
        {
            var p = JsonConvert.DeserializeObject<PaletteData>(json, Strict) ?? throw new FormatException("palette: empty");
            p.Validate();
            return p;
        }

        public int MaxOffset
        {
            get
            {
                int m = 0;
                foreach (var o in TerraceOffsets) if (o > m) m = o;
                return m;
            }
        }

        void Validate()
        {
            if (CellPx < 2 || CellPx > 16) throw new FormatException("palette: cellPx must be in 2..16");
            if (TerraceOffsets == null || TerraceOffsets.Length < 7) throw new FormatException("palette: terraceOffsets needs 7 levels");
            for (int i = 1; i < TerraceOffsets.Length; i++)
                if (TerraceOffsets[i] < TerraceOffsets[i - 1]) throw new FormatException("palette: terraceOffsets must not decrease");
            foreach (var band in new[] { Bands.Plains, Bands.Hills, Bands.Mountains })
                if (band == null || band.Length != 2 || band[0] < 1 || band[1] < band[0] || band[1] >= TerraceOffsets.Length)
                    throw new FormatException("palette: bandLevels must be [a, b] with 1 <= a <= b < levels");
            if (Water.Tones == null || Water.Tones.Length != 4 || Water.ToneDist == null || Water.ToneDist.Length != 3)
                throw new FormatException("palette: water needs 4 tones and 3 distances");
            if (StrataEvery < 1) throw new FormatException("palette: strataEvery must be >= 1");
            for (int b = 1; b <= 9; b++)
            {
                string name = ((Biome)b).ToString();
                if (!BiomeTop.ContainsKey(name) || !BiomeWall.ContainsKey(name)) throw new FormatException("palette: no color for biome " + name);
            }
        }

        public static Color32 Hex(string s)
        {
            if (!ColorUtility.TryParseHtmlString(s, out var c)) throw new FormatException("palette: bad color " + s);
            return c;
        }

        public int Density(Biome b) => DecorDensity_bp.TryGetValue(b.ToString(), out var v) ? v : 0;

        /// <summary>Уровни 0..6 по полосе Terrain: [a, b].</summary>
        public int[] Band(Terrain t) =>
            t == Terrain.Hills ? Bands.Hills : t == Terrain.Mountains ? Bands.Mountains : Bands.Plains;
    }
}
