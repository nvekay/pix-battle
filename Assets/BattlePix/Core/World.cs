using System;
using Unity.Collections;

namespace BattlePix.Core
{
    public enum EndReason : byte
    {
        None = 0,
        /// <summary>Страховочный лимит раннера. В партии с правилами это баг правил.</summary>
        TickLimit = 1,
        /// <summary>Кто-то взял порог суши или остался один.</summary>
        Conquest = 2,
        /// <summary>Таймер правил.</summary>
        Timeout = 3,
        /// <summary>Пала столица человека.</summary>
        HumanDefeated = 4,
        // Значения только дописываются.
    }

    /// <summary>Рельеф. Значения только дописываются.</summary>
    public enum Terrain : byte
    {
        Water = 0,
        /// <summary>Равнина. Суша — это всё, что не Water: проверять terrain != Water.</summary>
        Land = 1,
        Hills = 2,
        Mountains = 3,
    }

    public struct NationState
    {
        public byte Id;
        /// <summary>1 — жива; 0 — пала столица.</summary>
        public byte Alive;
        /// <summary>Индекс пикселя столицы; −1 у мёртвой.</summary>
        public int Capital;
        /// <summary>Сколько пикселей у нации, ведётся инкрементально.</summary>
        public int Pixels;
        /// <summary>Золото в сотых (100 = 1 золото), не меньше 0.</summary>
        public int Gold;
        /// <summary>Число живых батальонов во всех соединениях, ведётся инкрементально.</summary>
        public int Battalions;
        /// <summary>Число живых соединений (штабов).</summary>
        public byte Formations;
        /// <summary>−1, пока жива.</summary>
        public int DiedAtTick;
        /// <summary>Счётчики для отчёта: набрано пехоты и брони, погибло батальонов (потери и дезертирство).</summary>
        public int RecruitedInf;
        public int RecruitedArmor;
        public int BattalionsLost;
    }

    /// <summary>
    /// Соединение (спека 01b §4.3): штаб и до maxBattalions батальонов. Слот (owner − 1) × maxFormationsPerNation + k,
    /// k — номер соединения у нации (игрок видит «k + 1-е»).
    /// </summary>
    public struct Formation
    {
        public byte Owner;
        /// <summary>1 — есть штаб.</summary>
        public byte Alive;
        /// <summary>id нации-противника, 0 — тыл.</summary>
        public byte Front;
        /// <summary>0 — оборона, 1 — наступление.</summary>
        public byte Order;
        /// <summary>До этого тика (не включая) соединение в пути и не даёт силы; 0 — на месте.</summary>
        public int ArriveTick;
    }

    /// <summary>Батальон: слот formationSlot × maxBattalions + j.</summary>
    public struct Battalion
    {
        /// <summary>Индекс в rules.units: 0 — пехота, 1 — броня.</summary>
        public byte Type;
        public byte Alive;
        /// <summary>Численность 0..10 000 bp.</summary>
        public short Strength_bp;
    }

    public static class UnitTypes
    {
        public const byte Infantry = 0;
        public const byte Armor = 1;
    }

    public static class Orders
    {
        public const byte Defend = 0;
        public const byte Attack = 1;
    }

    /// <summary>Как прошла генерация карты. В хеш не входит: это производное от сида и правил.</summary>
    public struct MapGenStats
    {
        public int MapAttempts;
        public int CapitalAttempts;
        public bool MapRetryFail;
        public bool UnfairStart;
        /// <summary>Доля суши от карты после удаления островов, bp.</summary>
        public int LandShare_bp;
        /// <summary>max/min размеров стартовых ячеек, bp.</summary>
        public int Fairness_bp;
    }

    /// <summary>
    /// Состояние мира. Снаружи всё доступно только на чтение,
    /// пишут системы ядра и применение команд.
    /// </summary>
    public sealed class World : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public int PixelCount => Width * Height;

        NativeArray<byte> _owner;
        NativeArray<byte> _terrain;
        NativeArray<NationState> _nations;

        // Массивы правил. В мире Фундамента (без правил) у них нулевая длина.
        NativeArray<Formation> _formations;
        NativeArray<Battalion> _battalions;
        NativeArray<int> _frontLen;
        NativeArray<int> _frontTerrainLen;
        NativeArray<int> _sidePow;
        NativeArray<short> _flipHistory;
        NativeList<int> _frontier;
        NativeArray<byte> _inFrontier;
        NativeArray<byte> _edgeMask;
        NativeArray<ushort> _capitalCover;

        public int Tick { get; internal set; }
        public RngStreams Rng { get; }
        public bool IsOver { get; internal set; }
        public EndReason EndReason { get; internal set; }

        /// <summary>Правила партии. null — мир Фундамента.</summary>
        public Rules Rules { get; }
        public bool HasRules => Rules != null;
        public int MaxFormationsPerNation { get; }
        public int MaxBattalions { get; }
        /// <summary>Число пикселей суши после генерации.</summary>
        public int TotalLand { get; internal set; }
        /// <summary>Победитель, 0 — нет.</summary>
        public byte Winner { get; internal set; }
        public MapGenStats MapGen { get; internal set; }

        /// <summary>Длина кольца FlipHistory в секундах.</summary>
        public const int FlipSeconds = 10;

        /// <summary>Диапазон строк, изменённых с последнего чтения рендером. −1, если изменений нет.</summary>
        public int DirtyRowMin { get; private set; } = -1;
        public int DirtyRowMax { get; private set; } = -1;

        public bool IsDisposed { get; private set; }

        /// <summary>Мир Фундамента: без правил.</summary>
        public World(int width, int height, int nationCount, ulong seed) : this(width, height, nationCount, seed, null)
        {
        }

        /// <summary>Мир с правилами (rules != null): массивы фронта и соединений.</summary>
        public World(int width, int height, int nationCount, ulong seed, Rules rules)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (nationCount < 0 || nationCount > 255) throw new ArgumentOutOfRangeException(nameof(nationCount));
            if (rules != null && (nationCount != rules.NationCount || nationCount > Rules.MaxNations))
                throw new ArgumentOutOfRangeException(nameof(nationCount));
            Width = width;
            Height = height;
            Rules = rules;
            int n = width * height;
            _owner = new NativeArray<byte>(n, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _terrain = new NativeArray<byte>(n, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _nations = new NativeArray<NationState>(nationCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < nationCount; i++)
                _nations[i] = new NationState { Id = (byte)(i + 1), Alive = 1, Capital = -1, DiedAtTick = -1 };

            MaxFormationsPerNation = rules != null ? rules.Formations.MaxFormationsPerNation : 0;
            MaxBattalions = rules != null ? rules.Formations.MaxBattalions : 0;
            int nn = rules != null ? nationCount : 0;
            int pixels = rules != null ? n : 0;
            int pairs = rules != null ? (nn + 1) * (nn + 1) : 0;
            _formations = new NativeArray<Formation>(nn * MaxFormationsPerNation, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _battalions = new NativeArray<Battalion>(nn * MaxFormationsPerNation * MaxBattalions, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _frontLen = new NativeArray<int>(pairs, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _frontTerrainLen = new NativeArray<int>(pairs * TerrainKinds, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _sidePow = new NativeArray<int>(pairs * TerrainKinds * 2, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _flipHistory = new NativeArray<short>(FlipSeconds * nn * nn, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _frontier = new NativeList<int>(rules != null ? 16384 : 0, Allocator.Persistent);
            _inFrontier = new NativeArray<byte>(pixels, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _capitalCover = new NativeArray<ushort>(pixels, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            _edgeMask = new NativeArray<byte>(pixels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < pixels; i++) _edgeMask[i] = Grid.EdgeMask(i % width, i / width, width, height);

            Rng = new RngStreams(seed);
        }

        public NativeArray<byte>.ReadOnly Owner => _owner.AsReadOnly();
        public NativeArray<byte>.ReadOnly Terrain => _terrain.AsReadOnly();
        public NativeArray<NationState>.ReadOnly Nations => _nations.AsReadOnly();
        public NativeArray<Formation>.ReadOnly Formations => _formations.AsReadOnly();
        public NativeArray<Battalion>.ReadOnly Battalions => _battalions.AsReadOnly();
        public NativeArray<int>.ReadOnly FrontLen => _frontLen.AsReadOnly();
        /// <summary>Рёбра фронта [a, d] по рельефу пикселя d: [(a × (N + 1) + d) × 3 + tr]. Производное, считается с FrontLen.</summary>
        public NativeArray<int>.ReadOnly FrontTerrainLen => _frontTerrainLen.AsReadOnly();
        /// <summary>Сила на ребро (01b §5.1): [((a × (N + 1) + d) × 3 + tr) × 2 + 0 атака / 1 оборона]. Производное, каждый тик.</summary>
        public NativeArray<int>.ReadOnly SidePow => _sidePow.AsReadOnly();
        public NativeArray<short>.ReadOnly FlipHistory => _flipHistory.AsReadOnly();
        public NativeArray<int>.ReadOnly Frontier => _frontier.AsArray().AsReadOnly();
        public NativeArray<byte>.ReadOnly InFrontier => _inFrontier.AsReadOnly();
        /// <summary>Какие из 4 соседей есть на карте: бит k (0 — N, 1 — E, 2 — S, 3 — W). Не меняется.</summary>
        public NativeArray<byte>.ReadOnly EdgeMask => _edgeMask.AsReadOnly();
        /// <summary>Бит id − 1, если пиксель в круге capitalRadius от столицы живой нации. Производное от столиц.</summary>
        public NativeArray<ushort>.ReadOnly CapitalCover => _capitalCover.AsReadOnly();
        public int NationCount => _nations.Length;

        // Доступ на запись — только внутри ядра.
        internal ref NativeArray<byte> OwnerRW => ref _owner;
        internal ref NativeArray<byte> TerrainRW => ref _terrain;
        internal ref NativeArray<NationState> NationsRW => ref _nations;
        internal ref NativeArray<Formation> FormationsRW => ref _formations;
        internal ref NativeArray<Battalion> BattalionsRW => ref _battalions;
        internal ref NativeArray<int> FrontLenRW => ref _frontLen;
        internal ref NativeArray<int> FrontTerrainLenRW => ref _frontTerrainLen;
        internal ref NativeArray<int> SidePowRW => ref _sidePow;
        internal ref NativeArray<short> FlipHistoryRW => ref _flipHistory;
        internal ref NativeList<int> FrontierRW => ref _frontier;
        internal ref NativeArray<byte> InFrontierRW => ref _inFrontier;
        internal ref NativeArray<byte> EdgeMaskRW => ref _edgeMask;
        internal ref NativeArray<ushort> CapitalCoverRW => ref _capitalCover;

        public int Index(int x, int y) => y * Width + x;
        public bool InMap(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        public bool IsLand(int index) => _terrain[index] != (byte)Core.Terrain.Water;

        public NationState Nation(byte id) => _nations[id - 1];

        // ---------- фронт: открытые формулы, считаются из текущего состояния ----------

        /// <summary>Число рёбер между пикселями a и d по 4-соседству. a или d = 0 — ничья земля.</summary>
        public int FrontLength(int a, int d) => _frontLen[a * (NationCount + 1) + d];

        /// <summary>Рельефов в таблицах: tr = terrain − 1 (0 равнина, 1 холмы, 2 горы).</summary>
        public const int TerrainKinds = 3;

        /// <summary>Рёбра фронта [a, d], у которых пиксель d с рельефом tr.</summary>
        public int FrontTerrainLength(int a, int d, int tr) => _frontTerrainLen[(a * (NationCount + 1) + d) * TerrainKinds + tr];

        /// <summary>Атака a по d на ребро по рельефу tr (01b §5.1), посчитана в начале тика.</summary>
        public int AttPow(int a, int d, int tr) => _sidePow[((a * (NationCount + 1) + d) * TerrainKinds + tr) * 2];

        /// <summary>Оборона a против d на ребро по рельефу tr, с гарнизоном и множителем рельефа.</summary>
        public int DefPow(int a, int d, int tr) => _sidePow[((a * (NationCount + 1) + d) * TerrainKinds + tr) * 2 + 1];

        /// <summary>Взвешенная атака a по d за секунду (01b §5.3): Σ_tr AttPow(a, d, tr) × FrontTerrainLen[a, d, tr].</summary>
        public long WeightedAttack(int a, int d)
        {
            long sum = 0;
            for (int tr = 0; tr < TerrainKinds; tr++) sum += (long)AttPow(a, d, tr) * FrontTerrainLength(a, d, tr);
            return sum;
        }

        /// <summary>Взвешенная оборона d против a: Σ_tr DefPow(d, a, tr) × FrontTerrainLen[a, d, tr] (рельеф пикселей d).</summary>
        public long WeightedDefense(int d, int a)
        {
            long sum = 0;
            for (int tr = 0; tr < TerrainKinds; tr++) sum += (long)DefPow(d, a, tr) * FrontTerrainLength(a, d, tr);
            return sum;
        }

        public int FormationSlot(byte owner, int k) => (owner - 1) * MaxFormationsPerNation + k;
        public Formation FormationAt(int slot) => _formations[slot];
        public Battalion BattalionAt(int slot) => _battalions[slot];

        /// <summary>Соединение на месте на фронте (не в пути, не в тылу).</summary>
        public bool OnFront(in Formation f) => f.Alive != 0 && f.Front != 0 && f.ArriveTick <= Tick;

        /// <summary>Сколько пикселей d перешло к a минус обратно за последние 10 с. Положительное — a наступает.</summary>
        public int NetFlips10s(int a, int d)
        {
            if (a < 1 || a > NationCount || d < 1 || d > NationCount) return 0;
            int n = NationCount, nn = n * n, sum = 0;
            for (int s = 0; s < FlipSeconds; s++)
                sum += _flipHistory[s * nn + (a - 1) * n + (d - 1)] - _flipHistory[s * nn + (d - 1) * n + (a - 1)];
            return sum;
        }

        internal void SetOwner(int index, byte owner)
        {
            _owner[index] = owner;
            MarkDirtyRow(index / Width);
        }

        internal void MarkDirtyRow(int y)
        {
            if (DirtyRowMin < 0 || y < DirtyRowMin) DirtyRowMin = y;
            if (DirtyRowMax < 0 || y > DirtyRowMax) DirtyRowMax = y;
        }

        /// <summary>Рендер вызывает после того, как забрал изменённые строки. На хеш не влияет.</summary>
        public void ClearDirtyRows()
        {
            DirtyRowMin = -1;
            DirtyRowMax = -1;
        }

        /// <summary>Пометить всю карту изменённой (например, для первой загрузки текстуры).</summary>
        public void MarkAllDirty()
        {
            DirtyRowMin = 0;
            DirtyRowMax = Height - 1;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_owner.IsCreated) _owner.Dispose();
            if (_terrain.IsCreated) _terrain.Dispose();
            if (_nations.IsCreated) _nations.Dispose();
            if (_formations.IsCreated) _formations.Dispose();
            if (_battalions.IsCreated) _battalions.Dispose();
            if (_frontLen.IsCreated) _frontLen.Dispose();
            if (_frontTerrainLen.IsCreated) _frontTerrainLen.Dispose();
            if (_sidePow.IsCreated) _sidePow.Dispose();
            if (_flipHistory.IsCreated) _flipHistory.Dispose();
            if (_frontier.IsCreated) _frontier.Dispose();
            if (_inFrontier.IsCreated) _inFrontier.Dispose();
            if (_edgeMask.IsCreated) _edgeMask.Dispose();
            if (_capitalCover.IsCreated) _capitalCover.Dispose();
        }
    }

    /// <summary>Формулы фронта. Целые, деление к нулю. Используются и фронтом, и ИИ, и UI.</summary>
    public static class FrontMath
    {
        /// <summary>Шанс захвата в bp: clamp(warChance × (ratio − stall) / 10000, 0, max).</summary>
        public static int Chance(int att, int def, int warChance_bp, int stallRatio_bp, int maxChance_bp, out int ratio_bp)
        {
            ratio_bp = def > 0 ? (int)((long)att * 10000L / def) : int.MaxValue;
            long c = (long)warChance_bp * (ratio_bp - (long)stallRatio_bp) / 10000L;
            if (c < 0) c = 0;
            if (c > maxChance_bp) c = maxChance_bp;
            return (int)c;
        }

        /// <summary>x × m_bp / 10000.</summary>
        public static int MulBp(int x, int m_bp) => (int)((long)x * m_bp / 10000L);
    }
}
