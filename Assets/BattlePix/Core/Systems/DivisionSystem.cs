namespace BattlePix.Core
{
    /// <summary>
    /// Дивизии (спека 01c §4): сила сторон на фронте (FillPow, до фронта), закрытие фронтов (после фронта),
    /// раз в секунду после экономики — организация, потери, пополнение. Обход строго по возрастанию слота; случайности нет.
    /// Пайплайн вызывает части через DivisionPowStep, DivisionCloseStep и DivisionSecondStep.
    /// </summary>
    public sealed class DivisionSystem
    {
        long[] _att100 = new long[0];
        long[] _def100 = new long[0];
        long[] _weightedAtt = new long[0];
        long[] _weightedDef = new long[0];

        // ---------- 4.1. Сила сторон ----------

        /// <summary>Множитель обороны по организации: orgDefenseMin + (10000 − orgDefenseMin) × Org / 10000.</summary>
        public static int OrgMult(ArmyRules army, int org_bp) => army.OrgDefenseMin_bp + (10000 - army.OrgDefenseMin_bp) * org_bp / 10000;

        /// <summary>Вклад дивизии в оборону на рельефе tr, очки ×100 (до деления на длину фронта).</summary>
        public static long Defense100(World w, in Division v, int tr, bool attacking)
        {
            var army = w.Rules.Army;
            long x = (long)w.Rules.DivisionDef100(v.Type, tr) * v.Strength_bp / 10000;
            x = x * OrgMult(army, v.Org_bp) / 10000;
            return x * (attacking ? army.AttackingDefense_bp : 10000) / 10000;
        }

        /// <summary>Вклад дивизии в атаку на рельефе tr, очки ×100 (до деления на длину фронта).</summary>
        public static long Attack100(World w, in Division v, int tr) => (long)w.Rules.DivisionAtk100(v.Type, tr) * v.Strength_bp / 10000;

        /// <summary>SidePow для каждой живой нации a и каждого d с FrontLen[a, d] > 0 по рельефу.</summary>
        public void FillPow(World w)
        {
            var rules = w.Rules;
            int n = w.NationCount, stride = n + 1, kinds = World.TerrainKinds;
            int size = stride * stride * kinds;
            if (_att100.Length != size)
            {
                _att100 = new long[size];
                _def100 = new long[size];
            }
            System.Array.Clear(_att100, 0, size);
            System.Array.Clear(_def100, 0, size);

            var divisions = w.DivisionsRW;
            for (int slot = 0; slot < divisions.Length; slot++)
            {
                var v = divisions[slot];
                if (!w.OnFront(v)) continue;
                int a = v.Owner, d = v.Front;
                if (w.FrontLength(a, d) <= 0) continue;
                int baseIdx = (a * stride + d) * kinds;
                bool attacking = w.Attacking(v);
                for (int tr = 0; tr < kinds; tr++)
                {
                    if (attacking) _att100[baseIdx + tr] += Attack100(w, v, tr);
                    _def100[baseIdx + tr] += Defense100(w, v, tr, attacking);
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

        // ---------- 4.5. Закрытие фронта ----------

        /// <summary>Дивизии на фронте без общей границы или против мёртвой нации уходят в тыл (и те, что в пути или готовятся).</summary>
        public static void CloseFronts(World w)
        {
            var divisions = w.DivisionsRW;
            for (int slot = 0; slot < divisions.Length; slot++)
            {
                var v = divisions[slot];
                if (v.Alive == 0 || v.Front == 0) continue;
                if (w.FrontLength(v.Owner, v.Front) > 0 && w.NationsRW[v.Front - 1].Alive != 0) continue;
                v.Front = 0;
                v.ArriveTick = 0;
                divisions[slot] = v;
            }
        }

        // ---------- 4.2–4.3. Организация, потери, пополнение ----------

        /// <summary>Раз в секунду (последний тик секунды), после экономики: организация, потери и гибель, пополнение.</summary>
        public void PerSecond(World w, int tps)
        {
            if (w.Tick % tps != tps - 1) return;
            var rules = w.Rules;
            var army = rules.Army;
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

            var divisions = w.DivisionsRW;

            // Организация.
            for (int slot = 0; slot < divisions.Length; slot++)
            {
                var v = divisions[slot];
                if (v.Alive == 0) continue;
                int org = v.Org_bp;
                bool onFront = w.OnFront(v);
                long attAD = 0, defDA = 0, attDA = 0, defAD = 0;
                if (onFront)
                {
                    int a = v.Owner, d = v.Front;
                    attAD = _weightedAtt[a * stride + d];
                    defDA = _weightedDef[d * stride + a];
                    attDA = _weightedAtt[d * stride + a];
                    defAD = _weightedDef[a * stride + d];
                }
                if (w.Attacking(v) && attAD > 0)
                    org -= (int)(army.AttackOrgLoss_bp * LossRatio(defDA * 10000 / attAD, army) / 10000);
                else if (onFront && attDA > 0)
                    org -= (int)(army.DefendOrgLoss_bp * LossRatio(attDA * 10000 / (defAD > 0 ? defAD : 1), army) / 10000);
                else
                    org += onFront ? army.OrgRecoverFront_bp : army.OrgRecoverRear_bp;
                if (org < 0) org = 0;
                if (org > 10000) org = 10000;
                v.Org_bp = (short)org;
                if (org < army.HaltOrg_bp) v.Exhausted = 1;
                if (v.Exhausted != 0 && org >= army.ResumeOrg_bp) v.Exhausted = 0;
                divisions[slot] = v;
            }

            // Потери.
            for (int slot = 0; slot < divisions.Length; slot++)
            {
                var v = divisions[slot];
                if (!w.OnFront(v)) continue;
                int a = v.Owner, d = v.Front;
                long attAD = _weightedAtt[a * stride + d], defDA = _weightedDef[d * stride + a];
                long attDA = _weightedAtt[d * stride + a], defAD = _weightedDef[a * stride + d];
                long loss = 0;
                if (w.Attacking(v) && attAD > 0)
                    loss = army.AttackLoss_bp * LossRatio(defDA * 10000 / attAD, army) / 10000;
                if (attDA > 0)
                    loss += army.DefendLoss_bp * LossRatio(attDA * 10000 / (defAD > 0 ? defAD : 1), army) / 10000;
                if (loss <= 0) continue;
                int str = v.Strength_bp - (int)loss;
                if (str <= 0)
                {
                    Kill(w, slot);
                    continue;
                }
                v.Strength_bp = (short)str;
                divisions[slot] = v;
            }

            // Пополнение: по нациям по id, по слотам дивизий.
            var nations = w.NationsRW;
            for (int id = 1; id <= n; id++)
            {
                var nat = nations[id - 1];
                if (nat.Alive == 0) continue;
                for (int k = 0; k < w.MaxDivisionsPerNation; k++)
                {
                    int slot = w.DivisionSlot((byte)id, k);
                    var v = divisions[slot];
                    if (v.Alive == 0) continue;
                    int add = 10000 - v.Strength_bp;
                    int rate = ReinforceRate(w, v);
                    if (add > rate) add = rate;
                    if (add <= 0) continue;
                    long price = ReinforcePrice(rules.DivisionCost(v.Type), add, army.ReinforceCostMult_bp);
                    if (nat.Gold < price) continue;
                    nat.Gold -= (int)price;
                    v.Strength_bp = (short)(v.Strength_bp + add);
                    divisions[slot] = v;
                }
                nations[id - 1] = nat;
            }
        }

        /// <summary>Скорость пополнения: на месте на фронте — reinforceFront, в тылу, в пути и в подготовке — reinforceRear.</summary>
        static int ReinforceRate(World w, in Division v) => w.OnFront(v) ? w.Rules.Army.ReinforceFront_bp : w.Rules.Army.ReinforceRear_bp;

        static long LossRatio(long ratio, ArmyRules army)
        {
            if (ratio < army.LossRatioMin_bp) return army.LossRatioMin_bp;
            if (ratio > army.LossRatioMax_bp) return army.LossRatioMax_bp;
            return ratio;
        }

        /// <summary>price = cost × add / 10000 × reinforceCostMult / 10000.</summary>
        public static long ReinforcePrice(int cost_c, int add_bp, int mult_bp) => (long)cost_c * add_bp / 10000 * mult_bp / 10000;

        // ---------- общие операции ----------

        /// <summary>Дивизия погибла: потери или дезертирство.</summary>
        internal static void Kill(World w, int slot)
        {
            var divisions = w.DivisionsRW;
            var v = divisions[slot];
            if (v.Alive == 0) return;
            divisions[slot] = new Division { Owner = v.Owner };
            var nations = w.NationsRW;
            var nat = nations[v.Owner - 1];
            nat.Divisions -= 1;
            nat.DivisionsLost += 1;
            nations[v.Owner - 1] = nat;
        }

        /// <summary>Первый свободный слот дивизии у нации, −1 — нет.</summary>
        public static int FreeSlot(World w, byte owner)
        {
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
                if (w.DivisionsRW[w.DivisionSlot(owner, k)].Alive == 0) return k;
            return -1;
        }

        /// <summary>Новая дивизия в слоте k: в тылу, численность и организация 100%, готова с readyTick. Счётчики набора ведёт вызывающий.</summary>
        internal static void Create(World w, byte owner, int k, byte type, int readyTick)
        {
            w.DivisionsRW[w.DivisionSlot(owner, k)] = new Division
            {
                Owner = owner, Alive = 1, Type = type, Front = 0, ReadyTick = readyTick, ArriveTick = 0,
                Strength_bp = 10000, Org_bp = 10000, Exhausted = 0,
            };
            var nations = w.NationsRW;
            var nat = nations[owner - 1];
            nat.Divisions += 1;
            nations[owner - 1] = nat;
        }

        /// <summary>Гибель нации: все её дивизии сняты (не считаются потерями), её строка и столбец FrontOrder обнулены.</summary>
        internal static void RemoveAll(World w, byte owner)
        {
            var divisions = w.DivisionsRW;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
                divisions[w.DivisionSlot(owner, k)] = new Division { Owner = owner };
            int n = w.NationCount;
            var order = w.FrontOrderRW;
            for (int i = 0; i < n; i++)
            {
                order[(owner - 1) * n + i] = 0;
                order[i * n + (owner - 1)] = 0;
            }
        }

        /// <summary>
        /// Дезертирство (01c §4.4): дивизия нации с наименьшей численностью (при равенстве — с наибольшим слотом)
        /// теряет desertLoss_bp, на нуле гибнет. Возвращает false, если дивизий нет.
        /// </summary>
        internal static bool Desert(World w, byte owner)
        {
            int best = -1, bestStr = int.MaxValue;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                int slot = w.DivisionSlot(owner, k);
                var v = w.DivisionsRW[slot];
                if (v.Alive == 0) continue;
                if (v.Strength_bp <= bestStr)
                {
                    bestStr = v.Strength_bp;
                    best = slot;
                }
            }
            if (best < 0) return false;
            var d = w.DivisionsRW[best];
            int str = d.Strength_bp - w.Rules.Army.DesertLoss_bp;
            if (str <= 0)
            {
                Kill(w, best);
                return true;
            }
            d.Strength_bp = (short)str;
            w.DivisionsRW[best] = d;
            return true;
        }

        /// <summary>Содержание нации в сотых за секунду: живые дивизии (включая готовящиеся) по типам.</summary>
        public static long Upkeep(World w, byte owner)
        {
            long sum = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionsRW[w.DivisionSlot(owner, k)];
                if (v.Alive != 0) sum += w.Rules.DivisionUpkeep(v.Type);
            }
            return sum;
        }

        /// <summary>Сколько стоит пополнение нации в эту секунду, если хватит денег (для UI), в сотых.</summary>
        public static long ReinforceDemand(World w, byte owner)
        {
            var army = w.Rules.Army;
            long sum = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionsRW[w.DivisionSlot(owner, k)];
                if (v.Alive == 0) continue;
                int add = 10000 - v.Strength_bp;
                int rate = ReinforceRate(w, v);
                if (add > rate) add = rate;
                if (add > 0) sum += ReinforcePrice(w.Rules.DivisionCost(v.Type), add, army.ReinforceCostMult_bp);
            }
            return sum;
        }

        /// <summary>Сколько живых дивизий нации типа type (−1 — всех).</summary>
        public static int Count(World w, byte owner, int type = -1)
        {
            int c = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionsRW[w.DivisionSlot(owner, k)];
                if (v.Alive != 0 && (type < 0 || v.Type == type)) c++;
            }
            return c;
        }
    }

    /// <summary>Шаг 2 тика: сила сторон до фронта.</summary>
    public sealed class DivisionPowStep : ISimSystem
    {
        readonly DivisionSystem _s;
        public DivisionPowStep(DivisionSystem s) { _s = s; }
        public void Tick(World world, CommandContext ctx) => _s.FillPow(world);
    }

    /// <summary>Шаг 4 тика: закрытие фронтов после фронта.</summary>
    public sealed class DivisionCloseStep : ISimSystem
    {
        public void Tick(World world, CommandContext ctx) => DivisionSystem.CloseFronts(world);
    }

    /// <summary>Шаг 5 тика (после экономики): организация, потери и пополнение раз в секунду.</summary>
    public sealed class DivisionSecondStep : ISimSystem
    {
        readonly DivisionSystem _s;
        public DivisionSecondStep(DivisionSystem s) { _s = s; }
        public void Tick(World world, CommandContext ctx) => _s.PerSecond(world, ctx.Config.TicksPerSecond);
    }
}
