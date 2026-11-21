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
        public const int MaxNations = 16; // CapitalCover — ushort, бит на нацию

        public int NationCount { get; private set; }
        public MapGenRules MapGen { get; private set; }
        public FrontRules Front { get; private set; }
        public EconomyRules Economy { get; private set; }
        public TerrainRules Terrain { get; private set; }
        public UnitRules[] Units { get; private set; }
        /// <summary>Типы дивизий (спека 01c §3.1): индекс = тип.</summary>
        public DivisionRules[] Divisions { get; internal set; }
        public ArmyRules Army { get; private set; }
        public EndRules End { get; private set; }
        /// <summary>Война и мир (спека 02b §3.5).</summary>
        public WarRules War { get; private set; }
        public AiFile Ai { get; private set; }

        readonly string[] _profileNameByNation;
        readonly AiProfile[] _profileByNation;

        Rules(int nationCount, RulesFile file, AiFile ai)
        {
            NationCount = nationCount;
            MapGen = file.MapGen;
            Front = file.Front;
            Economy = file.Economy;
            Terrain = file.Terrain;
            Units = file.Units;
            Divisions = file.Divisions;
            Army = file.Army;
            End = file.End;
            War = file.War;
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
            RebuildDivisionTables();
        }

        // ---------- таблицы дивизий: считаются один раз при загрузке правил (01c §4.1) ----------

        int[] _divCost = new int[0];
        int[] _divUpkeep = new int[0];
        int[] _divAtk100 = new int[0];
        int[] _divDef100 = new int[0];

        /// <summary>
        /// Цена, содержание и atk100/def100 по типу и рельефу из состава и rules.units. Тесты, которые меняют units
        /// или divisions после загрузки, вызывают это сами.
        /// </summary>
        internal void RebuildDivisionTables()
        {
            int types = Divisions.Length, kinds = World.TerrainKinds;
            _divCost = new int[types];
            _divUpkeep = new int[types];
            _divAtk100 = new int[types * kinds];
            _divDef100 = new int[types * kinds];
            for (int t = 0; t < types; t++)
            {
                var comp = Divisions[t].Composition;
                long cost = 0, upkeep = 0;
                for (int u = 0; u < Units.Length; u++)
                {
                    cost += (long)comp[u] * Units[u].Cost_c;
                    upkeep += (long)comp[u] * Units[u].Upkeep_c;
                    for (int tr = 0; tr < kinds; tr++)
                    {
                        // По батальонам, деление на каждом шаге.
                        _divAtk100[t * kinds + tr] += comp[u] * (int)((long)Units[u].Attack * 100 * Units[u].AttackTerrain_bp[tr] / 10000);
                        _divDef100[t * kinds + tr] += comp[u] * (int)((long)Units[u].Defense * 100 * Units[u].DefenseTerrain_bp[tr] / 10000);
                    }
                }
                _divCost[t] = (int)cost;
                _divUpkeep[t] = (int)upkeep;
            }
        }

        /// <summary>Цена дивизии в сотых: Σ composition[u] × units[u].cost_c.</summary>
        public int DivisionCost(int type) => _divCost[type];

        /// <summary>Содержание дивизии в сотых за секунду: Σ composition[u] × units[u].upkeep_c.</summary>
        public int DivisionUpkeep(int type) => _divUpkeep[type];

        /// <summary>Атака дивизии типа на рельефе tr, очки ×100, при численности 100%.</summary>
        public int DivisionAtk100(int type, int tr) => _divAtk100[type * World.TerrainKinds + tr];

        /// <summary>Оборона дивизии типа на рельефе tr, очки ×100, при численности и организации 100%.</summary>
        public int DivisionDef100(int type, int tr) => _divDef100[type * World.TerrainKinds + tr];

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
            var file = new RulesFile { MapGen = MapGen, Front = Front, Economy = Economy, Terrain = Terrain, Units = Units, Divisions = Divisions, Army = Army, End = End, War = War };
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
        /// <summary>Форма государства и падежи имени (спека 02a §4.2): читает только игра.</summary>
        [JsonProperty("form")] public string Form = "";
        [JsonProperty("cases")] public Dictionary<string, string> Cases;
    }

    public sealed class RulesFile
    {
        /// <summary>4 — война по объявлению (спека 02b): раздел war. 3 — дивизии (01c). 2 — соединения 01b.</summary>
        public const int SupportedFormat = 4;
        public const int MaxUnitTypes = 8;
        public const int MaxDivisionTypes = 8;

        [JsonProperty("format")] public int Format = SupportedFormat;
        [JsonProperty("mapgen")] public MapGenRules MapGen = new MapGenRules();
        [JsonProperty("front")] public FrontRules Front = new FrontRules();
        [JsonProperty("economy")] public EconomyRules Economy = new EconomyRules();
        [JsonProperty("terrain")] public TerrainRules Terrain = new TerrainRules();
        [JsonProperty("units")] public UnitRules[] Units = UnitRules.CreateDefault();
        [JsonProperty("divisions")] public DivisionRules[] Divisions = DivisionRules.CreateDefault();
        [JsonProperty("army")] public ArmyRules Army = new ArmyRules();
        [JsonProperty("end")] public EndRules End = new EndRules();
        [JsonProperty("war")] public WarRules War = new WarRules();

        public void Validate()
        {
            Rules.Require(Format == SupportedFormat, "rules", $"format {Format} is not supported, expected {SupportedFormat}");
            Rules.Require(MapGen != null && Front != null && Economy != null && Terrain != null && Units != null && Divisions != null && Army != null && End != null
                          && War != null, "rules", "all sections are required");
            War.Validate();
            MapGen.Validate();
            Front.Validate();
            Economy.Validate();
            Terrain.Validate();
            Rules.Require(Units.Length >= 1 && Units.Length <= MaxUnitTypes, "rules.units", $"units must have 1..{MaxUnitTypes} types");
            for (int i = 0; i < Units.Length; i++)
            {
                Rules.Require(Units[i] != null, "rules.units", $"unit {i} is empty");
                Units[i].Validate(i);
            }
            Rules.Require(Divisions.Length >= 1 && Divisions.Length <= MaxDivisionTypes, "rules.divisions", $"divisions must have 1..{MaxDivisionTypes} types");
            for (int i = 0; i < Divisions.Length; i++)
            {
                Rules.Require(Divisions[i] != null, "rules.divisions", $"division {i} is empty");
                Divisions[i].Validate(i, Units.Length);
            }
            Army.Validate();
            End.Validate();
            Rules.Require(Army.StartDivisions.Length == Divisions.Length, "rules.army", "startDivisions must have one count per division type");
            int start = 0;
            foreach (var c in Army.StartDivisions) start += c;
            Rules.Require(start <= Army.MaxDivisionsPerNation, "rules.army", "startDivisions must fit maxDivisionsPerNation");
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
        /// <summary>Доля суши под холмами и горами (спека 01b §5.7): горы — самые высокие, холмы — следующие.</summary>
        [JsonProperty("hillsShare_bp")] public int HillsShare_bp = 2500;
        [JsonProperty("mountainsShare_bp")] public int MountainsShare_bp = 1000;
        /// <summary>Биомы для картинки (спека 02a §4.1). Меняют хеш, но не баланс: на фронт не влияют.</summary>
        [JsonProperty("biomes")] public BiomeRules Biomes = new BiomeRules();

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
            Rules.Require(HillsShare_bp >= 0 && MountainsShare_bp >= 0 && HillsShare_bp + MountainsShare_bp <= 9000,
                "rules.mapgen", "hillsShare_bp + mountainsShare_bp must be in 0..9000 (plains are needed for capitals)");
            Rules.Require(Biomes != null, "rules.mapgen", "biomes is required");
            Biomes.Validate();
        }
    }

    /// <summary>Пороги биомов (спека 02a §4.1), все _bp в 0..10 000.</summary>
    public sealed class BiomeRules
    {
        [JsonProperty("moistureCells")] public int[] MoistureCells = { 64, 32, 16, 8 };
        [JsonProperty("forestMoist_bp")] public int ForestMoist_bp = 5800;
        [JsonProperty("steppeMoist_bp")] public int SteppeMoist_bp = 4200;
        [JsonProperty("desertMoist_bp")] public int DesertMoist_bp = 3300;
        [JsonProperty("desertTemp_bp")] public int DesertTemp_bp = 5500;
        [JsonProperty("tundraTemp_bp")] public int TundraTemp_bp = 2000;
        [JsonProperty("snowShareOfMountains_bp")] public int SnowShareOfMountains_bp = 3500;
        [JsonProperty("hillsForestMoist_bp")] public int HillsForestMoist_bp = 6200;
        [JsonProperty("beachMaxDist")] public int BeachMaxDist = 1;

        internal void Validate()
        {
            const string f = "rules.mapgen.biomes";
            Rules.Require(MoistureCells != null && MoistureCells.Length >= 1 && MoistureCells.Length <= 8, f, "moistureCells must have 1..8 octaves");
            foreach (var c in MoistureCells) Rules.Require(c >= 1, f, "moistureCells must be >= 1");
            foreach (var v in new[] { ForestMoist_bp, SteppeMoist_bp, DesertMoist_bp, DesertTemp_bp, TundraTemp_bp, SnowShareOfMountains_bp, HillsForestMoist_bp })
                Rules.Require(v >= 0 && v <= 10000, f, "_bp values must be in 0..10000");
            Rules.Require(BeachMaxDist >= 0, f, "beachMaxDist must be >= 0");
        }
    }

    public sealed class FrontRules
    {
        [JsonProperty("expandChance_bp")] public int ExpandChance_bp = 70;
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
        [JsonProperty("flatIncome_c")] public int FlatIncome_c = 200;
        [JsonProperty("pixelsPerGold")] public int PixelsPerGold = 2000;

        internal void Validate()
        {
            Rules.Require(StartGold_c >= 0, "rules.economy", "startGold_c must be >= 0");
            Rules.Require(FlatIncome_c >= 0, "rules.economy", "flatIncome_c must be >= 0");
            Rules.Require(PixelsPerGold >= 1, "rules.economy", "pixelsPerGold must be >= 1");
        }
    }

    /// <summary>Рельеф (спека 01b §9): множитель обороны по рельефу пикселя-цели, [равнина, холмы, горы].</summary>
    public sealed class TerrainRules
    {
        [JsonProperty("terrainDefense_bp")] public int[] TerrainDefense_bp = { 10000, 12500, 15000 };

        internal void Validate()
        {
            Rules.Require(TerrainDefense_bp != null && TerrainDefense_bp.Length == 3, "rules.terrain", "terrainDefense_bp must have 3 values");
            foreach (var m in TerrainDefense_bp) Rules.Require(m >= 1, "rules.terrain", "terrainDefense_bp must be >= 1");
        }
    }

    /// <summary>Тип батальона (спека 01b §4.2). Индекс в массиве rules.units = тип.</summary>
    public sealed class UnitRules
    {
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("cost_c")] public int Cost_c;
        /// <summary>Содержание в сотых за секунду.</summary>
        [JsonProperty("upkeep_c")] public int Upkeep_c;
        [JsonProperty("attack")] public int Attack;
        [JsonProperty("defense")] public int Defense;
        [JsonProperty("attackTerrain_bp")] public int[] AttackTerrain_bp = { 10000, 10000, 10000 };
        [JsonProperty("defenseTerrain_bp")] public int[] DefenseTerrain_bp = { 10000, 10000, 10000 };

        public static UnitRules[] CreateDefault() => new[]
        {
            new UnitRules
            {
                Id = "infantry", Cost_c = 15000, Upkeep_c = 10, Attack = 10, Defense = 14,
                AttackTerrain_bp = new[] { 10000, 10000, 9000 }, DefenseTerrain_bp = new[] { 10000, 10000, 10000 },
            },
            new UnitRules
            {
                Id = "armor", Cost_c = 40000, Upkeep_c = 30, Attack = 26, Defense = 14,
                AttackTerrain_bp = new[] { 13000, 8000, 4000 }, DefenseTerrain_bp = new[] { 10000, 8000, 5000 },
            },
        };

        internal void Validate(int index)
        {
            string f = "rules.units[" + index + "]";
            Rules.Require(!string.IsNullOrEmpty(Id), f, "id is required");
            Rules.Require(Cost_c >= 0 && Upkeep_c >= 0, f, "costs must be >= 0");
            Rules.Require(Attack >= 0 && Attack <= 10000 && Defense >= 0 && Defense <= 10000, f, "attack and defense must be in 0..10000");
            Rules.Require(AttackTerrain_bp != null && AttackTerrain_bp.Length == 3, f, "attackTerrain_bp must have 3 values");
            Rules.Require(DefenseTerrain_bp != null && DefenseTerrain_bp.Length == 3, f, "defenseTerrain_bp must have 3 values");
            for (int i = 0; i < 3; i++)
                Rules.Require(AttackTerrain_bp[i] >= 0 && DefenseTerrain_bp[i] >= 0, f, "terrain multipliers must be >= 0");
        }
    }

    /// <summary>Тип дивизии (спека 01c §3.1): сколько батальонов каждого типа rules.units. Цена и содержание — из батальонов.</summary>
    public sealed class DivisionRules
    {
        [JsonProperty("id")] public string Id = "";
        [JsonProperty("composition")] public int[] Composition = new int[0];
        [JsonProperty("trainTicks")] public int TrainTicks = 300;

        public static DivisionRules[] CreateDefault() => new[]
        {
            new DivisionRules { Id = "infantry_div", Composition = new[] { 3, 0 }, TrainTicks = 300 },
            new DivisionRules { Id = "armor_div", Composition = new[] { 1, 2 }, TrainTicks = 300 },
        };

        internal void Validate(int index, int unitTypes)
        {
            string f = "rules.divisions[" + index + "]";
            Rules.Require(!string.IsNullOrEmpty(Id), f, "id is required");
            Rules.Require(Composition != null && Composition.Length == unitTypes, f, "composition must have one count per unit type");
            int sum = 0;
            foreach (var c in Composition)
            {
                Rules.Require(c >= 0 && c <= 64, f, "composition counts must be in 0..64");
                sum += c;
            }
            Rules.Require(sum > 0, f, "composition must not be empty");
            Rules.Require(TrainTicks >= 0, f, "trainTicks must be >= 0");
        }
    }

    /// <summary>Армия (спека 01c §8): переброска, потери, пополнение, организация.</summary>
    public sealed class ArmyRules
    {
        [JsonProperty("maxDivisionsPerNation")] public int MaxDivisionsPerNation = 32;
        [JsonProperty("transferTicks")] public int TransferTicks = 300;
        [JsonProperty("attackingDefense_bp")] public int AttackingDefense_bp = 7000;
        [JsonProperty("attackLoss_bp")] public int AttackLoss_bp = 50;
        [JsonProperty("defendLoss_bp")] public int DefendLoss_bp = 20;
        [JsonProperty("lossRatioMin_bp")] public int LossRatioMin_bp = 5000;
        [JsonProperty("lossRatioMax_bp")] public int LossRatioMax_bp = 20000;
        [JsonProperty("reinforceFront_bp")] public int ReinforceFront_bp = 30;
        [JsonProperty("reinforceRear_bp")] public int ReinforceRear_bp = 150;
        [JsonProperty("reinforceCostMult_bp")] public int ReinforceCostMult_bp = 2000;
        [JsonProperty("attackOrgLoss_bp")] public int AttackOrgLoss_bp = 150;
        [JsonProperty("defendOrgLoss_bp")] public int DefendOrgLoss_bp = 100;
        [JsonProperty("orgRecoverFront_bp")] public int OrgRecoverFront_bp = 100;
        [JsonProperty("orgRecoverRear_bp")] public int OrgRecoverRear_bp = 250;
        [JsonProperty("haltOrg_bp")] public int HaltOrg_bp = 1500;
        [JsonProperty("resumeOrg_bp")] public int ResumeOrg_bp = 6000;
        [JsonProperty("orgDefenseMin_bp")] public int OrgDefenseMin_bp = 5000;
        [JsonProperty("desertLoss_bp")] public int DesertLoss_bp = 2000;
        /// <summary>Стартовые дивизии по типам: готовы, в тылу, численность и организация 100%.</summary>
        [JsonProperty("startDivisions")] public int[] StartDivisions = { 1, 0 };

        internal void Validate()
        {
            const string f = "rules.army";
            Rules.Require(MaxDivisionsPerNation >= 1 && MaxDivisionsPerNation <= 255, f, "maxDivisionsPerNation must be in 1..255");
            Rules.Require(TransferTicks >= 0, f, "transferTicks must be >= 0");
            Rules.Require(AttackingDefense_bp >= 0, f, "attackingDefense_bp must be >= 0");
            Rules.Require(AttackLoss_bp >= 0 && DefendLoss_bp >= 0, f, "losses must be >= 0");
            Rules.Require(LossRatioMin_bp >= 0 && LossRatioMax_bp >= LossRatioMin_bp, f, "lossRatioMin_bp <= lossRatioMax_bp");
            Rules.Require(ReinforceFront_bp >= 0 && ReinforceRear_bp >= 0 && ReinforceFront_bp <= 10000 && ReinforceRear_bp <= 10000,
                f, "reinforce rates must be in 0..10000");
            Rules.Require(ReinforceCostMult_bp >= 0, f, "reinforceCostMult_bp must be >= 0");
            // Потери за секунду не больше численности дивизии: Strength_bp — short.
            Rules.Require((long)AttackLoss_bp * LossRatioMax_bp / 10000 + (long)DefendLoss_bp * LossRatioMax_bp / 10000 <= 10000,
                f, "losses per second must be <= 10000 bp");
            Rules.Require(AttackOrgLoss_bp >= 0 && DefendOrgLoss_bp >= 0 && (long)AttackOrgLoss_bp * LossRatioMax_bp / 10000 <= 10000
                          && (long)DefendOrgLoss_bp * LossRatioMax_bp / 10000 <= 10000, f, "org losses per second must be in 0..10000 bp");
            Rules.Require(OrgRecoverFront_bp >= 0 && OrgRecoverRear_bp >= 0 && OrgRecoverFront_bp <= 10000 && OrgRecoverRear_bp <= 10000,
                f, "org recovery must be in 0..10000");
            Rules.Require(HaltOrg_bp >= 0 && ResumeOrg_bp >= HaltOrg_bp && ResumeOrg_bp <= 10000, f, "0 <= haltOrg_bp <= resumeOrg_bp <= 10000");
            Rules.Require(OrgDefenseMin_bp >= 0 && OrgDefenseMin_bp <= 10000, f, "orgDefenseMin_bp must be in 0..10000");
            Rules.Require(DesertLoss_bp >= 1 && DesertLoss_bp <= 10000, f, "desertLoss_bp must be in 1..10000");
            Rules.Require(StartDivisions != null, f, "startDivisions is required");
            foreach (var c in StartDivisions) Rules.Require(c >= 0, f, "startDivisions must be >= 0");
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

    /// <summary>Война и мир (спека 02b §3.5). Время — в тиках, доли — в bp.</summary>
    public sealed class WarRules
    {
        [JsonProperty("claimRadius")] public int ClaimRadius = 18;
        [JsonProperty("claimMaxDepth")] public int ClaimMaxDepth = 30;
        [JsonProperty("claimMinPixels")] public int ClaimMinPixels = 300;
        [JsonProperty("claimHold_bp")] public int ClaimHold_bp = 7000;
        [JsonProperty("repOccupy_bp")] public int RepOccupy_bp = 1000;
        [JsonProperty("defenseWin_bp")] public int DefenseWin_bp = 1000;
        [JsonProperty("goalHoldSeconds")] public int GoalHoldSeconds = 20;
        [JsonProperty("tribute_bp")] public int Tribute_bp = 5000;
        [JsonProperty("tributeTicks")] public int TributeTicks = 3000;
        [JsonProperty("truceTicks")] public int TruceTicks = 3000;
        [JsonProperty("maxWarTicks")] public int MaxWarTicks = 4800;
        [JsonProperty("offerTicks")] public int OfferTicks = 100;
        [JsonProperty("offerCooldownTicks")] public int OfferCooldownTicks = 600;

        internal void Validate()
        {
            const string f = "rules.war";
            Rules.Require(ClaimRadius >= 1 && ClaimRadius <= 64, f, "claimRadius must be in 1..64");
            Rules.Require(ClaimMaxDepth >= 0, f, "claimMaxDepth must be >= 0");
            Rules.Require(ClaimMinPixels >= 1, f, "claimMinPixels must be >= 1");
            foreach (var v in new[] { ClaimHold_bp, RepOccupy_bp, DefenseWin_bp, Tribute_bp })
                Rules.Require(v >= 1 && v <= 10000, f, "_bp values must be in 1..10000");
            Rules.Require(GoalHoldSeconds >= 1 && GoalHoldSeconds <= 32000, f, "goalHoldSeconds must be in 1..32000");
            Rules.Require(TributeTicks >= 0 && TruceTicks >= 0 && MaxWarTicks >= 1 && OfferTicks >= 1 && OfferCooldownTicks >= 0,
                f, "durations must be >= 0 (maxWarTicks and offerTicks >= 1)");
        }
    }

    public sealed class AiFile
    {
        /// <summary>4 — война по объявлению (02b §8): declareRatio, idleWarTicks и др. 3 — профили дивизий (01c §4.8).</summary>
        public const int SupportedFormat = 4;

        [JsonProperty("format")] public int Format = SupportedFormat;
        [JsonProperty("default")] public string Default = "base";
        /// <summary>id нации (строкой) → имя профиля.</summary>
        [JsonProperty("assign")] public Dictionary<string, string> Assign = new Dictionary<string, string>();
        [JsonProperty("profiles")] public Dictionary<string, AiProfile> Profiles = new Dictionary<string, AiProfile>();

        public static AiFile CreateDefault()
        {
            var f = new AiFile();
            f.Profiles["base"] = new AiProfile();
            f.Profiles["spread"] = new AiProfile { Spread = true };
            f.Profiles["no_armor"] = new AiProfile { ArmorPlainShare_bp = 1000000 };
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

    /// <summary>Профиль ИИ (спека 01c §4.8). Полный, без наследования.</summary>
    public sealed class AiProfile
    {
        [JsonProperty("thinkIntervalTicks")] public int ThinkIntervalTicks = 10;
        /// <summary>Абляция «ровный слой»: правила 2–5 заменены на «из тыла — на самый слабый фронт, все приказы — наступление».</summary>
        [JsonProperty("spread")] public bool Spread;
        [JsonProperty("rearStrength_bp")] public int RearStrength_bp = 3000;
        [JsonProperty("deployStrength_bp")] public int DeployStrength_bp = 7000;
        [JsonProperty("attackMinRatio_bp")] public int AttackMinRatio_bp = 11000;
        [JsonProperty("threatRatio_bp")] public int ThreatRatio_bp = 12000;
        [JsonProperty("armorPlainShare_bp")] public int ArmorPlainShare_bp = 5500;
        [JsonProperty("armorMaxShare_bp")] public int ArmorMaxShare_bp = 5000;
        [JsonProperty("recruitReserve_c")] public int RecruitReserve_c = 0;
        [JsonProperty("leaderShare_bp")] public int LeaderShare_bp = 2500;
        [JsonProperty("leaderBonus_bp")] public int LeaderBonus_bp = 4000;
        [JsonProperty("noise_bp")] public int Noise_bp = 800;
        /// <summary>Война (02b §8): объявляет при армии сильнее в declareRatio раз.</summary>
        [JsonProperty("declareRatio_bp")] public int DeclareRatio_bp = 12000;
        /// <summary>Столько тиков без своих войн — объявляет всё равно.</summary>
        [JsonProperty("idleWarTicks")] public int IdleWarTicks = 1800;
        /// <summary>Доля готовых дивизий у границы перед объявлением.</summary>
        [JsonProperty("stageShare_bp")] public int StageShare_bp = 6000;
        [JsonProperty("maxOwnWars")] public int MaxOwnWars = 1;
        /// <summary>Вероятность выбрать претензии, а не репарации.</summary>
        [JsonProperty("claimsChance_bp")] public int ClaimsChance_bp = 6000;
        /// <summary>Белый мир не раньше стольких тиков войны.</summary>
        [JsonProperty("peaceAfterTicks")] public int PeaceAfterTicks = 1200;

        internal void Validate(string name)
        {
            Rules.Require(DeclareRatio_bp >= 0 && IdleWarTicks >= 0 && StageShare_bp >= 0 && StageShare_bp <= 10000 && MaxOwnWars >= 0
                          && ClaimsChance_bp >= 0 && ClaimsChance_bp <= 10000 && PeaceAfterTicks >= 0,
                "ai.profiles." + name, "war values out of range");
            string f = "ai.profiles." + name;
            Rules.Require(ThinkIntervalTicks >= 1, f, "thinkIntervalTicks must be >= 1");
            Rules.Require(RearStrength_bp >= 0 && DeployStrength_bp >= 0,
                f, "strength thresholds must be >= 0");
            Rules.Require(AttackMinRatio_bp >= 0 && ThreatRatio_bp >= 0, f, "ratios must be >= 0");
            Rules.Require(ArmorPlainShare_bp >= 0 && ArmorMaxShare_bp >= 0 && RecruitReserve_c >= 0, f, "recruit values must be >= 0");
            Rules.Require(LeaderShare_bp >= 0 && LeaderBonus_bp >= 0, f, "leader values must be >= 0");
            Rules.Require(Noise_bp >= 0 && Noise_bp < int.MaxValue, f, "noise_bp must be >= 0");
        }
    }
}
