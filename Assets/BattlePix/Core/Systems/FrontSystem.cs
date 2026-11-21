using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>
    /// Соседи по 4-соседству. Порядок k фиксирован: 0 — N (y + 1), 1 — E (x + 1), 2 — S (y − 1), 3 — W (x − 1).
    /// Строка 0 карты внизу (как в текстуре), поэтому N — вверх.
    /// </summary>
    public static class Grid
    {
        /// <summary>Биты существующих соседей пикселя (x, y): 1 — N, 2 — E, 4 — S, 8 — W.</summary>
        public static byte EdgeMask(int x, int y, int w, int h)
        {
            int m = 0;
            if (y + 1 < h) m |= 1;
            if (x + 1 < w) m |= 2;
            if (y > 0) m |= 4;
            if (x > 0) m |= 8;
            return (byte)m;
        }

        public static bool Neighbour(int t, int k, int w, int h, out int q) => Neighbour(t, EdgeMask(t % w, t / w, w, h), k, w, out q);

        /// <summary>Сосед k по маске краёв: без деления на ширину в горячих циклах.</summary>
        public static bool Neighbour(int t, byte edge, int k, int w, out int q)
        {
            switch (k)
            {
                case 0: q = t + w; break;
                case 1: q = t + 1; break;
                case 2: q = t - w; break;
                default: q = t - 1; break;
            }
            return (edge & (1 << k)) != 0;
        }

        /// <summary>Пограничный: суша, и хотя бы один сосед — суша с другим владельцем (включая ничью землю).</summary>
        public static bool IsFrontier(int p, NativeArray<byte> owner, NativeArray<byte> terrain, NativeArray<byte> edgeMask, int w)
        {
            if (terrain[p] == (byte)Terrain.Water) return false;
            byte o = owner[p], edge = edgeMask[p];
            for (int k = 0; k < 4; k++)
                if (Neighbour(p, edge, k, w, out int q) && terrain[q] != (byte)Terrain.Water && owner[q] != o)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Фронт (спека 01 §5.3–5.5, 01b §5.2): соль тика, фаза A (параллельный Burst-джоб, только чтение), FlipHistory,
    /// фаза B (последовательно), падение столиц, уплотнение Frontier, FrontLen и FrontTerrainLen.
    /// </summary>
    public sealed class FrontSystem : ISimSystem, IDisposable
    {
        /// <summary>Только для тестов: фаза A одним потоком (Run) вместо параллельного Schedule.</summary>
        public bool SingleThreaded;

        const int BatchSize = 256;
        /// <summary>Записей шанса на пару: 2 флага столицы × 3 рельефа.</summary>
        internal const int ChanceSlots = 2 * World.TerrainKinds;

        NativeArray<byte> _decision;
        /// <summary>Шанс захвата a → d в bp: [((a × (N + 1) + d) × 2 + столица d рядом) × 3 + рельеф пикселя-цели].</summary>
        NativeArray<int> _chance;
        NativeArray<byte> _alive;
        NativeArray<byte> _captured;
        NativeList<int> _added;
        NativeArray<int> _dirty;
        int _n = -1;

        void Ensure(int nations, int frontier)
        {
            if (_n != nations)
            {
                DisposeArrays();
                _n = nations;
                int nn = (nations + 1) * (nations + 1);
                _chance = new NativeArray<int>(nn * ChanceSlots, Allocator.Persistent);
                _alive = new NativeArray<byte>(nations + 1, Allocator.Persistent);
                _captured = new NativeArray<byte>(nations + 1, Allocator.Persistent);
                _added = new NativeList<int>(1024, Allocator.Persistent);
                _dirty = new NativeArray<int>(2, Allocator.Persistent);
            }
            if (!_decision.IsCreated || _decision.Length < frontier)
            {
                if (_decision.IsCreated) _decision.Dispose();
                _decision = new NativeArray<byte>(Math.Max(1024, frontier * 2), Allocator.Persistent);
            }
        }

        public void Tick(World world, CommandContext ctx)
        {
            int n = world.NationCount, w = world.Width, h = world.Height, tick = world.Tick;
            int tps = ctx.Config.TicksPerSecond;

            // Соль тика: один Next() из потока Front.
            uint salt = world.Rng.Get(RngStream.Front).Next();

            // Frontier уже уплотнён в конце прошлого тика (вместе с FrontLen), а между тиками владельцы не меняются.
            int count = PhaseA(world, salt);

            // Новая секунда — её ячейка FlipHistory обнуляется до записи.
            int sec = (tick / tps) % World.FlipSeconds;
            int nn = n * n;
            var flips = world.FlipHistoryRW;
            if (tick % tps == 0)
                for (int i = 0; i < nn; i++) flips[sec * nn + i] = 0;

            // Фаза B: последовательно применить решения.
            for (int i = 0; i <= n; i++) _captured[i] = 0;
            _added.Clear();
            _dirty[0] = int.MaxValue;
            _dirty[1] = -1;
            new ApplyFrontJob
            {
                Decision = _decision,
                Count = count,
                Frontier = world.FrontierRW.AsArray(),
                Added = _added,
                InFrontier = world.InFrontierRW,
                Owner = world.OwnerRW,
                Terrain = world.TerrainRW,
                EdgeMask = world.EdgeMaskRW,
                Nations = world.NationsRW,
                Flips = flips,
                FlipBase = sec * nn,
                N = n,
                W = w,
                Captured = _captured,
                Dirty = _dirty,
            }.Run();
            if (_added.Length > 0) world.FrontierRW.AddRange(_added.AsArray());
            if (_dirty[1] >= 0)
            {
                world.MarkDirtyRow(_dirty[0]);
                world.MarkDirtyRow(_dirty[1]);
            }

            // Павшие столицы: после всех переходов тика, по возрастанию id.
            bool anyDied = false;
            for (int d = 1; d <= n; d++)
            {
                if (_captured[d] == 0) continue;
                KillNation(world, (byte)d);
                anyDied = true;
            }
            if (anyDied) RebuildFrontier(world);

            RecountFrontLen(world);
        }

        /// <summary>Фаза A: решения по текущему Frontier в _decision. Возвращает число пограничных пикселей.</summary>
        int PhaseA(World world, uint salt)
        {
            int n = world.NationCount;
            int count = world.FrontierRW.Length;
            Ensure(n, count);
            FillTables(world, world.Tick);

            var job = new FrontJob
            {
                Frontier = world.FrontierRW.AsArray(),
                Owner = world.OwnerRW,
                Terrain = world.TerrainRW,
                CapitalCover = world.CapitalCoverRW,
                EdgeMask = world.EdgeMaskRW,
                Chance = _chance,
                Alive = _alive,
                W = world.Width,
                Stride = n + 1,
                Salt = salt,
                SurroundMult_bp = Surround(world.Rules.Front),
                Decision = _decision,
            };
            if (SingleThreaded) job.Run(count);
            else job.Schedule(count, BatchSize).Complete();
            return count;
        }

        static FixedList32Bytes<int> Surround(FrontRules fr)
        {
            var list = new FixedList32Bytes<int>();
            for (int i = 0; i < 4; i++) list.Add(fr.SurroundMult_bp[i]);
            return list;
        }

        /// <summary>Только для тестов: решения фазы A с заданной солью, без применения.</summary>
        internal byte[] PhaseAForTest(World world, uint salt)
        {
            int count = PhaseA(world, salt);
            var r = new byte[count];
            for (int i = 0; i < count; i++) r[i] = _decision[i];
            return r;
        }

        /// <summary>
        /// Таблицы для фазы A (01b §5.2): живые нации и шанс захвата для каждой пары, флага «столица d рядом» и рельефа
        /// пикселя-цели. Сила сторон уже в SidePow (DivisionSystem.FillPow этого тика). att = 0 — шанс 0.
        /// Ничья земля: expandChance от любого соседа-нации, рельеф не влияет.
        /// </summary>
        void FillTables(World world, int tick)
        {
            var rules = world.Rules;
            var fr = rules.Front;
            int n = world.NationCount, stride = n + 1, kinds = World.TerrainKinds;
            for (int a = 0; a <= n; a++)
                _alive[a] = a > 0 && world.NationsRW[a - 1].Alive != 0 ? (byte)1 : (byte)0;
            for (int a = 0; a <= n; a++)
            for (int d = 0; d <= n; d++)
            {
                int baseIdx = (a * stride + d) * ChanceSlots;
                for (int f = 0; f < ChanceSlots; f++) _chance[baseIdx + f] = 0;
                if (a == 0 || a == d || _alive[a] == 0) continue;
                if (d == 0)
                {
                    for (int f = 0; f < ChanceSlots; f++) _chance[baseIdx + f] = fr.ExpandChance_bp;
                    continue;
                }
                if (world.FrontLength(a, d) <= 0) continue;
                for (int cap = 0; cap < 2; cap++)
                {
                    int defMult = cap != 0 ? fr.CapitalMult_bp : 10000;
                    if (defMult > fr.DefenseMultCap_bp) defMult = fr.DefenseMultCap_bp;
                    for (int tr = 0; tr < kinds; tr++)
                    {
                        int att = world.AttPow(a, d, tr);
                        int def = FrontMath.MulBp(world.DefPow(d, a, tr), defMult);
                        _chance[baseIdx + cap * kinds + tr] = att <= 0 ? 0
                            : FrontMath.Chance(att, def, fr.WarChance_bp, fr.StallRatio_bp, fr.MaxChance_bp, out _);
                    }
                }
            }
        }

        /// <summary>Только для тестов: шанс из таблицы фазы A после FillTables.</summary>
        internal int ChanceForTest(World world, int a, int d, int cap, int tr)
        {
            Ensure(world.NationCount, world.FrontierRW.Length);
            FillTables(world, world.Tick);
            return _chance[(a * (world.NationCount + 1) + d) * ChanceSlots + cap * World.TerrainKinds + tr];
        }

        /// <summary>Гибель нации: земля ничья, соединения сняты, армия и золото обнулены.</summary>
        internal static void KillNation(World world, byte d)
        {
            new KillNationJob
            {
                Owner = world.OwnerRW,
                CapitalCover = world.CapitalCoverRW,
                Nation = d,
            }.Run();
            world.MarkAllDirty();

            DivisionSystem.RemoveAll(world, d);

            var nations = world.NationsRW;
            var s = nations[d - 1];
            s.Alive = 0;
            s.Capital = -1;
            s.Pixels = 0;
            s.Gold = 0;
            s.Divisions = 0;
            s.DiedAtTick = world.Tick;
            nations[d - 1] = s;
        }

        /// <summary>CapitalCover: круги capitalRadius вокруг столиц живых наций. После генерации карты.</summary>
        internal static void StampCapitals(World world)
        {
            var cover = world.CapitalCoverRW;
            int w = world.Width, h = world.Height, r = world.Rules.Front.CapitalRadius;
            for (int k = 0; k < world.NationCount; k++)
            {
                var nat = world.NationsRW[k];
                if (nat.Alive == 0 || nat.Capital < 0) continue;
                int cx = nat.Capital % w, cy = nat.Capital / w;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h || dx * dx + dy * dy > r * r) continue;
                    int i = y * w + x;
                    cover[i] = (ushort)(cover[i] | (1 << k));
                }
            }
        }

        /// <summary>Полный проход: Frontier заново. После генерации и после гибели нации.</summary>
        internal static void RebuildFrontier(World world)
        {
            new RebuildFrontierJob
            {
                Frontier = world.FrontierRW,
                InFrontier = world.InFrontierRW,
                Owner = world.OwnerRW,
                Terrain = world.TerrainRW,
                EdgeMask = world.EdgeMaskRW,
                W = world.Width,
            }.Run();
        }

        /// <summary>
        /// Уплотнение Frontier (ленивое удаление: выкинуть записи, которые больше не пограничные) и FrontLen по нему
        /// одним проходом. Делается в конце тика: до фазы A следующего тика владельцы не меняются,
        /// поэтому это то же самое, что уплотнять в начале фазы A.
        /// </summary>
        internal static void RecountFrontLen(World world)
        {
            var count = new NativeArray<int>(1, Allocator.TempJob);
            new FrontLenJob
            {
                Frontier = world.FrontierRW.AsArray(),
                Written = count,
                InFrontier = world.InFrontierRW,
                Owner = world.OwnerRW,
                Terrain = world.TerrainRW,
                FrontLen = world.FrontLenRW,
                FrontTerrainLen = world.FrontTerrainLenRW,
                EdgeMask = world.EdgeMaskRW,
                W = world.Width,
                Stride = world.NationCount + 1,
            }.Run();
            world.FrontierRW.ResizeUninitialized(count[0]);
            count.Dispose();
        }

        void DisposeArrays()
        {
            if (_chance.IsCreated) _chance.Dispose();
            if (_alive.IsCreated) _alive.Dispose();
            if (_captured.IsCreated) _captured.Dispose();
            if (_added.IsCreated) _added.Dispose();
            if (_dirty.IsCreated) _dirty.Dispose();
        }

        public void Dispose()
        {
            DisposeArrays();
            if (_decision.IsCreated) _decision.Dispose();
            _n = -1;
        }
    }

    /// <summary>Фаза A: решение по каждому пограничному пикселю. Пишет только в Decision[i].</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct FrontJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> Frontier;
        [ReadOnly] public NativeArray<byte> Owner;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<ushort> CapitalCover;
        [ReadOnly] public NativeArray<byte> EdgeMask;
        [ReadOnly] public NativeArray<int> Chance;
        [ReadOnly] public NativeArray<byte> Alive;
        public int W, Stride;
        public uint Salt;
        [WriteOnly] public NativeArray<byte> Decision;

        /// <summary>Множитель шанса войны по числу соседей атакующего у пикселя (1..4), bp.</summary>
        public Unity.Collections.FixedList32Bytes<int> SurroundMult_bp;

        public void Execute(int i)
        {
            Decision[i] = Decide(Frontier[i]);
        }

        int Land(int t, byte edge, int k)
        {
            if (!Grid.Neighbour(t, edge, k, W, out int q) || Terrain[q] == (byte)Core.Terrain.Water) return -1;
            return Owner[q];
        }

        byte Decide(int t)
        {
            if (Terrain[t] == (byte)Core.Terrain.Water) return 0;
            int d = Owner[t];
            // Индекс в таблице пары: (столица d рядом) × 3 + рельеф пикселя-цели (tr = terrain − 1).
            int sub = Terrain[t] - 1;
            if (d != 0 && (CapitalCover[t] & (1 << (d - 1))) != 0) sub += World.TerrainKinds;
            byte edge = EdgeMask[t];
            // Соседи на суше: владелец или −1. Сколько соседей у каждого атакующего — для множителя окружения.
            int o0 = Land(t, edge, 0), o1 = Land(t, edge, 1), o2 = Land(t, edge, 2), o3 = Land(t, edge, 3);
            uint seen = 0;
            for (int k = 0; k < 4; k++)
            {
                int a = k == 0 ? o0 : k == 1 ? o1 : k == 2 ? o2 : o3;
                if (a <= 0 || a == d || Alive[a] == 0) continue;
                uint abit = 1u << a;
                if ((seen & abit) != 0) continue; // один сосед бросает один раз
                seen |= abit;
                int chance = Chance[(a * Stride + d) * FrontSystem.ChanceSlots + sub];
                if (d != 0 && chance > 0)
                {
                    int around = (o0 == a ? 1 : 0) + (o1 == a ? 1 : 0) + (o2 == a ? 1 : 0) + (o3 == a ? 1 : 0);
                    long c = (long)chance * SurroundMult_bp[around - 1] / 10000;
                    chance = c > 10000 ? 10000 : (int)c;
                }
                if (chance > 0 && Pcg32.FrontRoll(Salt, t, k) < chance) return (byte)a;
            }
            return 0;
        }
    }

    /// <summary>Фаза B: последовательно применить решения в порядке Frontier.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct ApplyFrontJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Decision;
        public int Count;
        /// <summary>Frontier на начало тика (только чтение). Новые пограничные пиксели — в Added.</summary>
        [ReadOnly] public NativeArray<int> Frontier;
        public NativeList<int> Added;
        public NativeArray<byte> InFrontier;
        public NativeArray<byte> Owner;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<byte> EdgeMask;
        public NativeArray<NationState> Nations;
        public NativeArray<short> Flips;
        public int FlipBase, N, W;
        public NativeArray<byte> Captured;
        public NativeArray<int> Dirty;

        public void Execute()
        {
            for (int i = 0; i < Count; i++)
            {
                byte a = Decision[i];
                if (a == 0) continue;
                int t = Frontier[i];
                byte d = Owner[t];
                Owner[t] = a;

                var na = Nations[a - 1];
                na.Pixels++;
                Nations[a - 1] = na;

                if (d != 0)
                {
                    var nd = Nations[d - 1];
                    nd.Pixels--;
                    Flips[FlipBase + (a - 1) * N + (d - 1)]++;
                    if (nd.Capital == t) Captured[d] = 1;
                    Nations[d - 1] = nd;
                }

                int row = t / W;
                if (row < Dirty[0]) Dirty[0] = row;
                if (row > Dirty[1]) Dirty[1] = row;

                TryAdd(t);
                byte edge = EdgeMask[t];
                for (int k = 0; k < 4; k++)
                    if (Grid.Neighbour(t, edge, k, W, out int q)) TryAdd(q);
            }
        }

        void TryAdd(int p)
        {
            if (InFrontier[p] != 0 || !Grid.IsFrontier(p, Owner, Terrain, EdgeMask, W)) return;
            InFrontier[p] = 1;
            Added.Add(p);
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct RebuildFrontierJob : IJob
    {
        public NativeList<int> Frontier;
        public NativeArray<byte> InFrontier;
        [ReadOnly] public NativeArray<byte> Owner;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<byte> EdgeMask;
        public int W;

        public void Execute()
        {
            Frontier.Clear();
            for (int p = 0; p < Owner.Length; p++)
            {
                bool f = Grid.IsFrontier(p, Owner, Terrain, EdgeMask, W);
                InFrontier[p] = f ? (byte)1 : (byte)0;
                if (f) Frontier.Add(p);
            }
        }
    }

    /// <summary>
    /// Уплотнение Frontier и FrontLen: для каждого пограничного пикселя рёбра к соседям E и S с другим владельцем на суше.
    /// Оба конца такого ребра пограничные, поэтому каждое ребро посчитано ровно один раз.
    /// FrontTerrainLen[a, b, tr] — те же рёбра по рельефу пикселя b (01b §4.3).
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct FrontLenJob : IJob
    {
        /// <summary>Frontier как массив: уплотняется на месте, новая длина — в Written[0].</summary>
        public NativeArray<int> Frontier;
        [WriteOnly] public NativeArray<int> Written;
        public NativeArray<byte> InFrontier;
        [ReadOnly] public NativeArray<byte> Owner;
        [ReadOnly] public NativeArray<byte> Terrain;
        public NativeArray<int> FrontLen;
        public NativeArray<int> FrontTerrainLen;
        [ReadOnly] public NativeArray<byte> EdgeMask;
        public int W, Stride;

        public void Execute()
        {
            for (int i = 0; i < FrontLen.Length; i++) FrontLen[i] = 0;
            for (int i = 0; i < FrontTerrainLen.Length; i++) FrontTerrainLen[i] = 0;
            int write = 0;
            for (int i = 0; i < Frontier.Length; i++)
            {
                int p = Frontier[i];
                if (!Grid.IsFrontier(p, Owner, Terrain, EdgeMask, W))
                {
                    InFrontier[p] = 0;
                    continue;
                }
                Frontier[write++] = p;
                int a = Owner[p];
                byte edge = EdgeMask[p];
                if ((edge & 2) != 0) Edge(a, p, p + 1); // E
                if ((edge & 4) != 0) Edge(a, p, p - W); // S
            }
            Written[0] = write;
        }

        void Edge(int a, int p, int q)
        {
            if (Terrain[q] == (byte)Core.Terrain.Water) return;
            int b = Owner[q];
            if (b == a) return;
            FrontLen[a * Stride + b]++;
            FrontLen[b * Stride + a]++;
            FrontTerrainLen[(a * Stride + b) * World.TerrainKinds + Terrain[q] - 1]++;
            FrontTerrainLen[(b * Stride + a) * World.TerrainKinds + Terrain[p] - 1]++;
        }
    }

    /// <summary>Гибель нации: все её пиксели ничьи, её бит CapitalCover снят.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct KillNationJob : IJob
    {
        public NativeArray<byte> Owner;
        public NativeArray<ushort> CapitalCover;
        public byte Nation;

        public void Execute()
        {
            ushort mask = (ushort)~(1 << (Nation - 1));
            for (int p = 0; p < Owner.Length; p++)
            {
                if (Owner[p] == Nation) Owner[p] = 0;
                CapitalCover[p] = (ushort)(CapitalCover[p] & mask);
            }
        }
    }
}
