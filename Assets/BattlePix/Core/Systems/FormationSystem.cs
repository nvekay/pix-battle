namespace BattlePix.Core
{
    /// <summary>
    /// Соединения (спека 01b §5): сила сторон на фронте (FillPow, до фронта), закрытие фронтов (после фронта),
    /// потери и пополнение (раз в секунду, после экономики). Обход строго по возрастанию слота; случайности нет.
    /// Пайплайн вызывает части через FormationPowStep, FormationCloseStep и FormationLossStep.
    /// </summary>
    public sealed class FormationSystem
    {
        long[] _att100 = new long[0];
        long[] _def100 = new long[0];
        long[] _weightedAtt = new long[0];
        long[] _weightedDef = new long[0];

        // ---------- 5.1. Сила сторон ----------

        /// <summary>SidePow для каждой живой нации a и каждого d с FrontLen[a, d] > 0 по рельефу.</summary>
        public void FillPow(World w)
        {
            var rules = w.Rules;
            var units = rules.Units;
            var fr = rules.Formations;
            int n = w.NationCount, stride = n + 1, kinds = World.TerrainKinds;
            int size = stride * stride * kinds;
            if (_att100.Length != size)
            {
                _att100 = new long[size];
                _def100 = new long[size];
            }
            System.Array.Clear(_att100, 0, size);
            System.Array.Clear(_def100, 0, size);

            var formations = w.FormationsRW;
            var battalions = w.BattalionsRW;
            int maxB = w.MaxBattalions;
            for (int slot = 0; slot < formations.Length; slot++)
            {
                var f = formations[slot];
                if (!w.OnFront(f)) continue;
                int a = f.Owner, d = f.Front;
                if (w.FrontLength(a, d) <= 0) continue;
                int baseIdx = (a * stride + d) * kinds;
                bool attacking = f.Order == Orders.Attack;
                for (int j = 0; j < maxB; j++)
                {
                    var b = battalions[slot * maxB + j];
                    if (b.Alive == 0) continue;
                    var u = units[b.Type];
                    for (int tr = 0; tr < kinds; tr++)
                    {
                        if (attacking)
                            _att100[baseIdx + tr] += (long)u.Attack * 100 * u.AttackTerrain_bp[tr] / 10000 * b.Strength_bp / 10000;
                        _def100[baseIdx + tr] += (long)u.Defense * 100 * u.DefenseTerrain_bp[tr] / 10000 * b.Strength_bp / 10000
                                                 * (attacking ? fr.AttackingDefense_bp : 10000) / 10000;
                    }
                }
            }

            var pow = w.SidePowRW;
            for (int i = 0; i < pow.Length; i++) pow[i] = 0;
            int garrison = rules.Front.GarrisonPerEdge;
            var terrainDef = rules.Terrain.TerrainDefense_bp;
            for (int a = 1; a <= n; a++)
            {
                if (w.NationsRW[a - 1].Alive == 0) continue;
                for (int d = 1; d <= n; d++)
                {
                    int len = w.FrontLength(a, d);
                    if (d == a || len <= 0) continue;
                    int baseIdx = (a * stride + d) * kinds;
                    for (int tr = 0; tr < kinds; tr++)
                    {
                        long att = _att100[baseIdx + tr] / len;
                        long def = (_def100[baseIdx + tr] / len + garrison) * terrainDef[tr] / 10000;
                        pow[(baseIdx + tr) * 2] = Clamp(att);
                        pow[(baseIdx + tr) * 2 + 1] = Clamp(def);
                    }
                }
            }
        }

        static int Clamp(long v) => v > int.MaxValue ? int.MaxValue : (int)v;

        // ---------- 5.5. Закрытие фронта ----------

        /// <summary>Соединения на фронте без общей границы или против мёртвой нации уходят в тыл.</summary>
        public static void CloseFronts(World w)
        {
            var formations = w.FormationsRW;
            for (int slot = 0; slot < formations.Length; slot++)
            {
                var f = formations[slot];
                if (f.Alive == 0 || f.Front == 0) continue;
                if (w.FrontLength(f.Owner, f.Front) > 0 && w.NationsRW[f.Front - 1].Alive != 0) continue;
                f.Front = 0;
                f.ArriveTick = 0;
                formations[slot] = f;
            }
        }

        // ---------- 5.3. Потери и пополнение ----------

        /// <summary>Раз в секунду (последний тик секунды), после экономики: потери, гибель, потом пополнение.</summary>
        public void LossesAndReinforcement(World w, int tps)
        {
            if (w.Tick % tps != tps - 1) return;
            var rules = w.Rules;
            var fr = rules.Formations;
            int n = w.NationCount, stride = n + 1;
            if (_weightedAtt.Length != stride * stride)
            {
                _weightedAtt = new long[stride * stride];
                _weightedDef = new long[stride * stride];
            }
            for (int a = 1; a <= n; a++)
            for (int d = 1; d <= n; d++)
            {
                _weightedAtt[a * stride + d] = a == d ? 0 : w.WeightedAttack(a, d);
                _weightedDef[a * stride + d] = a == d ? 0 : w.WeightedDefense(a, d); // D(a, d): оборона a против d
            }

            var formations = w.FormationsRW;
            var battalions = w.BattalionsRW;
            int maxB = w.MaxBattalions;
            for (int slot = 0; slot < formations.Length; slot++)
            {
                var f = formations[slot];
                if (!w.OnFront(f)) continue;
                int a = f.Owner, d = f.Front;
                long attAD = _weightedAtt[a * stride + d], defDA = _weightedDef[d * stride + a];
                long attDA = _weightedAtt[d * stride + a], defAD = _weightedDef[a * stride + d];
                long loss = 0;
                if (f.Order == Orders.Attack && attAD > 0)
                    loss = fr.AttackLoss_bp * LossRatio(defDA * 10000 / attAD, fr) / 10000;
                if (attDA > 0)
                    loss += fr.DefendLoss_bp * LossRatio(attDA * 10000 / (defAD > 0 ? defAD : 1), fr) / 10000;
                if (loss <= 0) continue;
                for (int j = 0; j < maxB; j++)
                {
                    int bs = slot * maxB + j;
                    var b = battalions[bs];
                    if (b.Alive == 0) continue;
                    int str = b.Strength_bp - (int)loss;
                    if (str <= 0)
                    {
                        KillBattalion(w, bs);
                        continue;
                    }
                    b.Strength_bp = (short)str;
                    battalions[bs] = b;
                }
            }

            // Пополнение: по нациям по id, по слотам соединений и батальонов.
            var nations = w.NationsRW;
            var units = rules.Units;
            for (int id = 1; id <= n; id++)
            {
                var nat = nations[id - 1];
                if (nat.Alive == 0) continue;
                int first = (id - 1) * w.MaxFormationsPerNation;
                for (int k = 0; k < w.MaxFormationsPerNation; k++)
                {
                    int slot = first + k;
                    var f = formations[slot];
                    if (f.Alive == 0) continue;
                    int rate = f.Front == 0 || f.ArriveTick > w.Tick ? fr.ReinforceRear_bp : fr.ReinforceFront_bp;
                    for (int j = 0; j < maxB; j++)
                    {
                        int bs = slot * maxB + j;
                        var b = battalions[bs];
                        if (b.Alive == 0) continue;
                        int add = 10000 - b.Strength_bp;
                        if (add > rate) add = rate;
                        if (add <= 0) continue;
                        long price = ReinforcePrice(units[b.Type].Cost_c, add, fr.ReinforceCostMult_bp);
                        if (nat.Gold < price) continue;
                        nat.Gold -= (int)price;
                        b.Strength_bp = (short)(b.Strength_bp + add);
                        battalions[bs] = b;
                    }
                }
                nations[id - 1] = nat;
            }
        }

        static long LossRatio(long ratio, FormationRules fr)
        {
            if (ratio < fr.LossRatioMin_bp) return fr.LossRatioMin_bp;
            if (ratio > fr.LossRatioMax_bp) return fr.LossRatioMax_bp;
            return ratio;
        }

        /// <summary>price = cost × add / 10000 × reinforceCostMult / 10000.</summary>
        public static long ReinforcePrice(int cost_c, int add_bp, int mult_bp) => (long)cost_c * add_bp / 10000 * mult_bp / 10000;

        // ---------- общие операции ----------

        /// <summary>Батальон погиб: потери или дезертирство.</summary>
        internal static void KillBattalion(World w, int battalionSlot)
        {
            var battalions = w.BattalionsRW;
            var b = battalions[battalionSlot];
            if (b.Alive == 0) return;
            b.Alive = 0;
            b.Strength_bp = 0;
            battalions[battalionSlot] = b;
            int owner = w.FormationsRW[battalionSlot / w.MaxBattalions].Owner;
            var nations = w.NationsRW;
            var nat = nations[owner - 1];
            nat.Battalions -= 1;
            nat.BattalionsLost += 1;
            nations[owner - 1] = nat;
        }

        /// <summary>Первый свободный слот соединения у нации, −1 — нет.</summary>
        public static int FreeFormation(World w, byte owner)
        {
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
                if (w.FormationsRW[w.FormationSlot(owner, k)].Alive == 0) return k;
            return -1;
        }

        /// <summary>Число живых батальонов в соединении.</summary>
        public static int BattalionCount(World w, int formationSlot)
        {
            int c = 0, maxB = w.MaxBattalions;
            for (int j = 0; j < maxB; j++)
                if (w.BattalionsRW[formationSlot * maxB + j].Alive != 0) c++;
            return c;
        }

        /// <summary>Средняя численность батальонов соединения, bp; 0 — пустое.</summary>
        public static int AverageStrength(World w, int formationSlot)
        {
            long sum = 0;
            int c = 0, maxB = w.MaxBattalions;
            for (int j = 0; j < maxB; j++)
            {
                var b = w.BattalionsRW[formationSlot * maxB + j];
                if (b.Alive == 0) continue;
                sum += b.Strength_bp;
                c++;
            }
            return c > 0 ? (int)(sum / c) : 0;
        }

        /// <summary>Новое соединение в слоте k: в тылу, оборона, без батальонов.</summary>
        internal static void CreateFormation(World w, byte owner, int k)
        {
            w.FormationsRW[w.FormationSlot(owner, k)] = new Formation { Owner = owner, Alive = 1, Front = 0, Order = Orders.Defend, ArriveTick = 0 };
            var nations = w.NationsRW;
            var nat = nations[owner - 1];
            nat.Formations += 1;
            nations[owner - 1] = nat;
        }

        /// <summary>Батальон типа type на 100% в первый свободный слот соединения. Счётчики набора ведёт вызывающий.</summary>
        internal static void AddBattalion(World w, int formationSlot, byte type)
        {
            int maxB = w.MaxBattalions;
            var battalions = w.BattalionsRW;
            for (int j = 0; j < maxB; j++)
            {
                int bs = formationSlot * maxB + j;
                if (battalions[bs].Alive != 0) continue;
                battalions[bs] = new Battalion { Type = type, Alive = 1, Strength_bp = 10000 };
                int owner = w.FormationsRW[formationSlot].Owner;
                var nations = w.NationsRW;
                var nat = nations[owner - 1];
                nat.Battalions += 1;
                nations[owner - 1] = nat;
                return;
            }
        }

        /// <summary>Гибель нации: все её соединения и батальоны сняты (не считаются потерями).</summary>
        internal static void RemoveAll(World w, byte owner)
        {
            int maxF = w.MaxFormationsPerNation, maxB = w.MaxBattalions;
            var formations = w.FormationsRW;
            var battalions = w.BattalionsRW;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(owner, k);
                formations[slot] = new Formation { Owner = owner };
                for (int j = 0; j < maxB; j++) battalions[slot * maxB + j] = default;
            }
        }

        /// <summary>
        /// Дезертирство (01b §5.4): гибнет батальон нации с наименьшей численностью, при равенстве — с наибольшим слотом.
        /// Возвращает false, если батальонов нет.
        /// </summary>
        internal static bool Desert(World w, byte owner)
        {
            int maxF = w.MaxFormationsPerNation, maxB = w.MaxBattalions;
            int best = -1, bestStr = int.MaxValue;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(owner, k);
                if (w.FormationsRW[slot].Alive == 0) continue;
                for (int j = 0; j < maxB; j++)
                {
                    int bs = slot * maxB + j;
                    var b = w.BattalionsRW[bs];
                    if (b.Alive == 0) continue;
                    if (b.Strength_bp <= bestStr)
                    {
                        bestStr = b.Strength_bp;
                        best = bs;
                    }
                }
            }
            if (best < 0) return false;
            KillBattalion(w, best);
            return true;
        }

        /// <summary>Содержание нации в сотых за секунду: батальоны по типам + штабы.</summary>
        public static long Upkeep(World w, byte owner)
        {
            var rules = w.Rules;
            int maxF = w.MaxFormationsPerNation, maxB = w.MaxBattalions;
            long sum = (long)w.NationsRW[owner - 1].Formations * rules.Formations.HqUpkeep_c;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(owner, k);
                if (w.FormationsRW[slot].Alive == 0) continue;
                for (int j = 0; j < maxB; j++)
                {
                    var b = w.BattalionsRW[slot * maxB + j];
                    if (b.Alive != 0) sum += rules.Units[b.Type].Upkeep_c;
                }
            }
            return sum;
        }

        /// <summary>Сколько стоит пополнение нации в эту секунду, если хватит денег (для UI), в сотых.</summary>
        public static long ReinforceDemand(World w, byte owner)
        {
            var rules = w.Rules;
            var fr = rules.Formations;
            int maxF = w.MaxFormationsPerNation, maxB = w.MaxBattalions;
            long sum = 0;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(owner, k);
                var f = w.FormationsRW[slot];
                if (f.Alive == 0) continue;
                int rate = f.Front == 0 || f.ArriveTick > w.Tick ? fr.ReinforceRear_bp : fr.ReinforceFront_bp;
                for (int j = 0; j < maxB; j++)
                {
                    var b = w.BattalionsRW[slot * maxB + j];
                    if (b.Alive == 0) continue;
                    int add = 10000 - b.Strength_bp;
                    if (add > rate) add = rate;
                    if (add > 0) sum += ReinforcePrice(rules.Units[b.Type].Cost_c, add, fr.ReinforceCostMult_bp);
                }
            }
            return sum;
        }
    }

    /// <summary>Шаг 2 тика: сила сторон до фронта.</summary>
    public sealed class FormationPowStep : ISimSystem
    {
        readonly FormationSystem _s;
        public FormationPowStep(FormationSystem s) { _s = s; }
        public void Tick(World world, CommandContext ctx) => _s.FillPow(world);
    }

    /// <summary>Шаг 4 тика: закрытие фронтов после фронта.</summary>
    public sealed class FormationCloseStep : ISimSystem
    {
        public void Tick(World world, CommandContext ctx) => FormationSystem.CloseFronts(world);
    }

    /// <summary>Шаг 5 тика (после экономики): потери и пополнение раз в секунду.</summary>
    public sealed class FormationLossStep : ISimSystem
    {
        readonly FormationSystem _s;
        public FormationLossStep(FormationSystem s) { _s = s; }
        public void Tick(World world, CommandContext ctx) => _s.LossesAndReinforcement(world, ctx.Config.TicksPerSecond);
    }
}
