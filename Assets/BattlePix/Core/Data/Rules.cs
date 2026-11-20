using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BattlePix.Core
{
    /// <summary>
    /// Правила партии: rules.json + ai.json + число наций из nations.json.
    /// Все числа баланса живут в данных. Значения по умолчанию в DTO совпадают с файлами в Data/.
    /// </summary>
    public sealed class Rules
    {
        public const int MaxNations = 16; // FortCover — ushort, бит на нацию

        public int NationCount { get; private set; }
        public MapGenRules MapGen { get; private set; }
        public FrontRules Front { get; private set; }
        public EconomyRules Economy { get; private set; }
        public FortRules Forts { get; private set; }
        public StrikeRules Strikes { get; private set; }
        public EndRules End { get; private set; }
        public AiFile Ai { get; private set; }

        readonly string[] _profileNameByNation;
        readonly AiProfile[] _profileByNation;

        Rules(int nationCount, RulesFile file, AiFile ai)
        {
            NationCount = nationCount;
            MapGen = file.MapGen;
            Front = file.Front;
            Economy = file.Economy;
            Forts = file.Forts;
            Strikes = file.Strikes;
            End = file.End;
            Ai = ai;
            _profileNameByNation = new string[nationCount];
            _profileByNation = new AiProfile[nationCount];
            for (int id = 1; id <= nationCount; id++)
            {
                string name = ai.Default;
                if (ai.Assign.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var assigned))
                    name = assigned;
                AssignProfile(id, name);
            }
        }

        static readonly JsonSerializerSettings Strict = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
        };

        public static Rules FromJson(string nationsJson, string rulesJson, string aiJson)
        {
            var nations = Parse<List<NationEntry>>(nationsJson, "nations");
            int count = ValidateNations(nations);
            var rules = Parse<RulesFile>(rulesJson, "rules");
            rules.Validate();
            var ai = Parse<AiFile>(aiJson, "ai");
            ai.Validate();
            return new Rules(count, rules, ai);
        }

        /// <summary>Строгий разбор: неизвестное поле — ошибка данных, а не тихий ноль.</summary>
        static T Parse<T>(string json, string file) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json, Strict) ?? throw new FormatException(file + ": empty");
            }
            catch (JsonException e)
            {
                throw new FormatException(file + ": " + e.Message, e);
            }
        }

        /// <summary>Правила со значениями по умолчанию (как в Data/) для тестов.</summary>
        public static Rules Default(int nationCount)
        {
            var file = new RulesFile();
            file.Validate();
            var ai = AiFile.CreateDefault();
            ai.Validate();
            if (nationCount < 2 || nationCount > MaxNations)
                throw new ArgumentOutOfRangeException(nameof(nationCount));
            return new Rules(nationCount, file, ai);
        }

        /// <summary>Глубокая копия через JSON: тесты и абляции меняют копию, не общий объект.</summary>
        public Rules Clone()
        {
            var file = new RulesFile { MapGen = MapGen, Front = Front, Economy = Economy, Forts = Forts, Strikes = Strikes, End = End };
            var fileCopy = JsonConvert.DeserializeObject<RulesFile>(JsonConvert.SerializeObject(file), Strict);
            var aiCopy = JsonConvert.DeserializeObject<AiFile>(JsonConvert.SerializeObject(Ai), Strict);
            var r = new Rules(NationCount, fileCopy, aiCopy);
            for (int id = 1; id <= NationCount; id++) r.AssignProfile(id, ProfileName((byte)id));
            return r;
        }

        static int ValidateNations(List<NationEntry> nations)
        {
            // Запись id 0 — ничья земля, дальше нации подряд с 1.
            var seen = new bool[256];
            int max = 0;
            foreach (var n in nations)
            {
                if (n.Id < 0 || n.Id > 255) throw new FormatException($"nations: id {n.Id} out of range");
                if (seen[n.Id]) throw new FormatException($"nations: duplicate id {n.Id}");
                seen[n.Id] = true;
                if (n.Id > max) max = n.Id;
            }
            if (!seen[0]) throw new FormatException("nations: entry with id 0 (no man's land) is required");
            for (int i = 1; i <= max; i++)
                if (!seen[i]) throw new FormatException($"nations: ids must be contiguous, missing {i}");
            if (max < 2 || max > MaxNations) throw new FormatException($"nations: count {max} must be in 2..{MaxNations}");
            return max;
        }

        /// <summary>Назначить нации профиль ИИ (флаг -profile раннера перекрывает assign из ai.json).</summary>
        public void AssignProfile(int nationId, string profileName)
        {
            if (nationId < 1 || nationId > NationCount) throw new ArgumentOutOfRangeException(nameof(nationId));
            if (!Ai.Profiles.TryGetValue(profileName, out var p))
                throw new FormatException($"ai: unknown profile '{profileName}'");
            _profileNameByNation[nationId - 1] = profileName;
            _profileByNation[nationId - 1] = p;
        }

        public AiProfile Profile(byte nationId) => _profileByNation[nationId - 1];
        public string ProfileName(byte nationId) => _profileNameByNation[nationId - 1];

        /// <summary>Цена следующей крепости при fortsAlive стоящих: таблица, после её конца ×3/2 за шаг.</summary>
        public int FortCost(int fortsAlive)
        {
            var t = Forts.FortCosts_c;
            if (fortsAlive < t.Length) return t[fortsAlive];
            long c = t[t.Length - 1];
            for (int i = t.Length - 1; i < fortsAlive; i++)
            {
                c = c * 3 / 2;
                if (c > int.MaxValue) return int.MaxValue;
            }
            return (int)c;
        }

        internal static void Require(bool ok, string file, string message)
        {
            if (!ok) throw new FormatException(file + ": " + message);
        }
    }

    /// <summary>Запись nations.json. Ядро читает только id, имя и цвет нужны игре.</summary>
    public sealed class NationEntry
    {
        [JsonProperty("id")] public int Id;
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("color")] public string Color = "";
    }

    public sealed class RulesFile
    {
        public const int SupportedFormat = 1;

        [JsonProperty("format")] public int Format = SupportedFormat;
        [JsonProperty("mapgen")] public MapGenRules MapGen = new MapGenRules();
        [JsonProperty("front")] public FrontRules Front = new FrontRules();
        [JsonProperty("economy")] public EconomyRules Economy = new EconomyRules();
        [JsonProperty("forts")] public FortRules Forts = new FortRules();
        [JsonProperty("strikes")] public StrikeRules Strikes = new StrikeRules();
        [JsonProperty("end")] public EndRules End = new EndRules();

        public void Validate()
        {
            Rules.Require(Format == SupportedFormat, "rules", $"format {Format} is not supported, expected {SupportedFormat}");
            Rules.Require(MapGen != null && Front != null && Economy != null && Forts != null && Strikes != null && End != null,
                "rules", "all sections are required");
            MapGen.Validate();
            Front.Validate();
            Economy.Validate();
            Forts.Validate();
            Strikes.Validate();
            End.Validate();
        }
    }

    public sealed class MapGenRules
    {
        [JsonProperty("octaves")] public int Octaves = 4;
        [JsonProperty("baseCellPx")] public int BaseCellPx = 64;
        [JsonProperty("persistence_bp")] public int Persistence_bp = 5000;
        [JsonProperty("edgeFalloffPx")] public int EdgeFalloffPx = 24;
        [JsonProperty("edgePenaltyPerPx")] public int EdgePenaltyPerPx = 4000;
        [JsonProperty("landShare_bp")] public int LandShare_bp = 4700;
        /// <summary>Окно доли суши континента (после удаления островов). Не попали — новая карта.</summary>
        [JsonProperty("minLandShare_bp")] public int MinLandShare_bp = 4500;
        [JsonProperty("maxLandShare_bp")] public int MaxLandShare_bp = 4900;
        [JsonProperty("capitalCoastDist")] public int CapitalCoastDist = 6;
        [JsonProperty("startFairnessMax_bp")] public int StartFairnessMax_bp = 25000;
        [JsonProperty("startRadius")] public int StartRadius = 8;
        [JsonProperty("mapRetries")] public int MapRetries = 8;
        [JsonProperty("capitalRetries")] public int CapitalRetries = 16;

        internal void Validate()
        {
            Rules.Require(Octaves >= 1 && Octaves <= 8, "rules.mapgen", "octaves must be in 1..8");
            Rules.Require(BaseCellPx >= 1 && (BaseCellPx >> (Octaves - 1)) >= 1, "rules.mapgen", "baseCellPx too small for octaves");
            Rules.Require(Persistence_bp >= 0 && Persistence_bp <= 10000, "rules.mapgen", "persistence_bp must be in 0..10000");
            Rules.Require(EdgeFalloffPx >= 0 && EdgePenaltyPerPx >= 0, "rules.mapgen", "edge falloff must be >= 0");
            Rules.Require(LandShare_bp > 0 && LandShare_bp < 10000, "rules.mapgen", "landShare_bp must be in 1..9999");
            Rules.Require(MinLandShare_bp >= 0 && MinLandShare_bp <= LandShare_bp, "rules.mapgen", "minLandShare_bp must be in 0..landShare_bp");
            Rules.Require(MaxLandShare_bp >= LandShare_bp && MaxLandShare_bp <= 10000, "rules.mapgen", "maxLandShare_bp must be in landShare_bp..10000");
            Rules.Require(CapitalCoastDist >= 0, "rules.mapgen", "capitalCoastDist must be >= 0");
            Rules.Require(StartFairnessMax_bp >= 10000, "rules.mapgen", "startFairnessMax_bp must be >= 10000");
            Rules.Require(StartRadius >= 0, "rules.mapgen", "startRadius must be >= 0");
            Rules.Require(MapRetries >= 1 && CapitalRetries >= 1, "rules.mapgen", "retries must be >= 1");
        }
    }

    public sealed class FrontRules
    {
        [JsonProperty("expandChance_bp")] public int ExpandChance_bp = 120;
        [JsonProperty("warChance_bp")] public int WarChance_bp = 180;
        [JsonProperty("stallRatio_bp")] public int StallRatio_bp = 8000;
        [JsonProperty("maxChance_bp")] public int MaxChance_bp = 800;
        [JsonProperty("garrisonPerEdge")] public int GarrisonPerEdge = 40;
        [JsonProperty("capitalRadius")] public int CapitalRadius = 28;
        [JsonProperty("capitalMult_bp")] public int CapitalMult_bp = 30000;
        [JsonProperty("defenseMultCap_bp")] public int DefenseMultCap_bp = 60000;
        /// <summary>
        /// Множитель шанса войны по числу соседей атакующего у пикселя (1, 2, 3, 4), bp. Даёт границе «поверхностное
        /// натяжение»: анклав в окружении съедается, выступ в один пиксель почти не растёт. [10000 ×4] — как без множителя.
        /// </summary>
        [JsonProperty("surroundMult_bp")] public int[] SurroundMult_bp = { 5000, 10000, 20000, 40000 };

        internal void Validate()
        {
            Rules.Require(ExpandChance_bp >= 0 && ExpandChance_bp <= 10000, "rules.front", "expandChance_bp must be in 0..10000");
            Rules.Require(WarChance_bp >= 0, "rules.front", "warChance_bp must be >= 0");
            Rules.Require(StallRatio_bp >= 0, "rules.front", "stallRatio_bp must be >= 0");
            Rules.Require(MaxChance_bp >= 0 && MaxChance_bp <= 10000, "rules.front", "maxChance_bp must be in 0..10000");
            Rules.Require(GarrisonPerEdge >= 1, "rules.front", "garrisonPerEdge must be >= 1 (no division by zero)");
            Rules.Require(CapitalRadius >= 0, "rules.front", "capitalRadius must be >= 0");
            Rules.Require(CapitalMult_bp >= 1 && DefenseMultCap_bp >= 1, "rules.front", "multipliers must be >= 1");
            Rules.Require(SurroundMult_bp != null && SurroundMult_bp.Length == 4, "rules.front", "surroundMult_bp must have 4 values");
            foreach (var m in SurroundMult_bp) Rules.Require(m >= 0, "rules.front", "surroundMult_bp must be >= 0");
        }
    }

    public sealed class EconomyRules
    {
        [JsonProperty("startGold_c")] public int StartGold_c = 30000;
        [JsonProperty("startBattalions")] public int StartBattalions = 3;
        [JsonProperty("flatIncome_c")] public int FlatIncome_c = 200;
        [JsonProperty("pixelsPerGold")] public int PixelsPerGold = 2000;
        [JsonProperty("battalionCost_c")] public int BattalionCost_c = 20000;
        [JsonProperty("upkeep_c_per_battalion")] public int Upkeep_c_per_battalion = 25;
        [JsonProperty("battalionCap")] public int BattalionCap = 60;

        internal void Validate()
        {
            Rules.Require(StartGold_c >= 0 && StartBattalions >= 0, "rules.economy", "start values must be >= 0");
            Rules.Require(FlatIncome_c >= 0, "rules.economy", "flatIncome_c must be >= 0");
            Rules.Require(PixelsPerGold >= 1, "rules.economy", "pixelsPerGold must be >= 1");
            Rules.Require(BattalionCost_c >= 0 && Upkeep_c_per_battalion >= 0, "rules.economy", "costs must be >= 0");
            Rules.Require(BattalionCap >= 1 && BattalionCap <= 1000000, "rules.economy", "battalionCap must be in 1..1000000");
            Rules.Require(StartBattalions <= BattalionCap, "rules.economy", "startBattalions must be <= battalionCap");
        }
    }

    public sealed class FortRules
    {
        [JsonProperty("fortCosts_c")] public int[] FortCosts_c = { 40000, 60000, 90000, 135000, 202500 };
        [JsonProperty("maxFortsPerNation")] public int MaxFortsPerNation = 8;
        [JsonProperty("fortRadius")] public int FortRadius = 16;
        [JsonProperty("fortMinSpacing")] public int FortMinSpacing = 16;
        [JsonProperty("fortMult_bp")] public int FortMult_bp = 30000;

        internal void Validate()
        {
            Rules.Require(FortCosts_c != null && FortCosts_c.Length >= 1, "rules.forts", "fortCosts_c must have at least one value");
            foreach (var c in FortCosts_c) Rules.Require(c >= 0, "rules.forts", "fortCosts_c must be >= 0");
            Rules.Require(MaxFortsPerNation >= 0 && MaxFortsPerNation <= 255, "rules.forts", "maxFortsPerNation must be in 0..255");
            Rules.Require(FortRadius >= 0 && FortRadius <= 255, "rules.forts", "fortRadius must be in 0..255");
            Rules.Require(FortMinSpacing >= 0, "rules.forts", "fortMinSpacing must be >= 0");
            Rules.Require(FortMult_bp >= 1, "rules.forts", "fortMult_bp must be >= 1");
        }
    }

    public sealed class StrikeRules
    {
        [JsonProperty("strikeMult_bp")] public int StrikeMult_bp = 30000;
        [JsonProperty("strikeDurationTicks")] public int StrikeDurationTicks = 200;

        internal void Validate()
        {
            Rules.Require(StrikeMult_bp >= 1, "rules.strikes", "strikeMult_bp must be >= 1");
            Rules.Require(StrikeDurationTicks >= 1, "rules.strikes", "strikeDurationTicks must be >= 1");
        }
    }

    public sealed class EndRules
    {
        [JsonProperty("victoryShare_bp")] public int VictoryShare_bp = 4000;
        [JsonProperty("timeoutTicks")] public int TimeoutTicks = 27000;

        internal void Validate()
        {
            Rules.Require(VictoryShare_bp >= 1 && VictoryShare_bp <= 10000, "rules.end", "victoryShare_bp must be in 1..10000");
            Rules.Require(TimeoutTicks >= 1, "rules.end", "timeoutTicks must be >= 1");
        }
    }

    public sealed class AiFile
    {
        public const int SupportedFormat = 1;

        [JsonProperty("format")] public int Format = SupportedFormat;
        [JsonProperty("default")] public string Default = "base";
        /// <summary>id нации (строкой) → имя профиля.</summary>
        [JsonProperty("assign")] public Dictionary<string, string> Assign = new Dictionary<string, string>();
        [JsonProperty("profiles")] public Dictionary<string, AiProfile> Profiles = new Dictionary<string, AiProfile>();

        public static AiFile CreateDefault()
        {
            var f = new AiFile();
            f.Profiles["base"] = new AiProfile();
            f.Profiles["no_forts"] = new AiProfile { FortLossThresholdPx = 1000000 };
            f.Profiles["no_strikes"] = new AiProfile { StrikeMinBattalions = 1000000 };
            return f;
        }

        internal void Validate()
        {
            Rules.Require(Format == SupportedFormat, "ai", $"format {Format} is not supported, expected {SupportedFormat}");
            Rules.Require(Profiles != null && Profiles.Count > 0, "ai", "profiles are required");
            Rules.Require(Assign != null, "ai", "assign is required");
            Rules.Require(Default != null && Profiles.ContainsKey(Default), "ai", $"default profile '{Default}' not found");
            foreach (var kv in Profiles)
            {
                Rules.Require(kv.Value != null, "ai", $"profile '{kv.Key}' is empty");
                kv.Value.Validate(kv.Key);
            }
            foreach (var kv in Assign)
            {
                Rules.Require(int.TryParse(kv.Key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out _), "ai", $"assign key '{kv.Key}' is not a nation id");
                Rules.Require(Profiles.ContainsKey(kv.Value), "ai", $"assign: unknown profile '{kv.Value}'");
            }
        }
    }

    public sealed class AiProfile
    {
        [JsonProperty("thinkIntervalTicks")] public int ThinkIntervalTicks = 10;
        [JsonProperty("fortLossThresholdPx")] public int FortLossThresholdPx = 6;
        [JsonProperty("fortDepthPx")] public int FortDepthPx = 6;
        [JsonProperty("targetDensity_milli")] public int TargetDensity_milli = 4000;
        [JsonProperty("minBattalions")] public int MinBattalions = 3;
        [JsonProperty("recruitReserve_c")] public int RecruitReserve_c = 0;
        [JsonProperty("strikeMinBattalions")] public int StrikeMinBattalions = 5;
        [JsonProperty("strikeMinScore_bp")] public int StrikeMinScore_bp = 11000;
        [JsonProperty("leaderShare_bp")] public int LeaderShare_bp = 2500;
        [JsonProperty("leaderBonus_bp")] public int LeaderBonus_bp = 4000;
        [JsonProperty("noise_bp")] public int Noise_bp = 1500;

        internal void Validate(string name)
        {
            string f = "ai.profiles." + name;
            Rules.Require(ThinkIntervalTicks >= 1, f, "thinkIntervalTicks must be >= 1");
            Rules.Require(FortLossThresholdPx >= 0 && FortDepthPx >= 0, f, "fort values must be >= 0");
            Rules.Require(TargetDensity_milli >= 0 && MinBattalions >= 0 && RecruitReserve_c >= 0, f, "recruit values must be >= 0");
            Rules.Require(StrikeMinBattalions >= 1, f, "strikeMinBattalions must be >= 1");
            Rules.Require(StrikeMinScore_bp >= 0 && LeaderShare_bp >= 0 && LeaderBonus_bp >= 0, f, "strike values must be >= 0");
            Rules.Require(Noise_bp >= 0 && Noise_bp < int.MaxValue, f, "noise_bp must be >= 0");
        }
    }
}
