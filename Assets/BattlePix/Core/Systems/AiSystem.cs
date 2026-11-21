namespace BattlePix.Core
{
    /// <summary>
    /// ИИ (спека 01b §5.8). ИИ — не отдельная логика, а профиль весов из ai.json, который выбирает из тех же команд.
    /// Каждая ИИ-нация думает раз в thinkIntervalTicks со сдвигом (id − 1) % interval и отдаёт не больше одной команды
    /// на Tick + 1 через ту же очередь. Читает только World (силы сторон из SidePow этого тика) и свои веса.
    /// Правила сверху вниз, первое сработавшее отдаёт команду: отвести обескровленное, закрыть прорыв, ударный кулак,
    /// наступать обязательно, набор. Профиль spread заменяет правила 2–4 «ровным слоем».
    /// </summary>
    public sealed class AiSystem : ISimSystem
    {
        long[] _threat = new long[0];
        long[] _opp = new long[0];
        long[] _potential = new long[0];

        public void Tick(World world, CommandContext ctx)
        {
            int n = world.NationCount;
            for (int id = 1; id <= n; id++)
            {
                if (id == ctx.HumanNation || world.Nation((byte)id).Alive == 0) continue;
                var p = world.Rules.Profile((byte)id);
                if (world.Tick % p.ThinkIntervalTicks != (id - 1) % p.ThinkIntervalTicks) continue;
                if (Think(world, (byte)id, p, out var cmd)) ctx.IssueInternal(world, cmd);
            }
        }

        bool Think(World w, byte self, AiProfile p, out Command cmd)
        {
            cmd = default;
            int n = w.NationCount, maxF = w.MaxFormationsPerNation;
            if (_threat.Length != n + 1)
            {
                _threat = new long[n + 1];
                _opp = new long[n + 1];
                _potential = new long[World.TerrainKinds];
            }
            ref var rng = ref w.Rng.Get(RngStream.Ai);

            // Оценки фронтов: threat, opp и цель (max opp, при равенстве меньший id). Шум — по фронтам в порядке id.
            int target = 0;
            long bestOpp = long.MinValue;
            for (int d = 1; d <= n; d++)
            {
                _threat[d] = 0;
                _opp[d] = long.MinValue;
                if (!IsFront(w, self, d)) continue;
                _threat[d] = w.WeightedAttack(d, self) * 10000 / Max1(w.WeightedDefense(self, d));
                long opp = PotentialAttack(w, self, d) * 10000 / Max1(w.WeightedDefense(d, self));
                if ((long)w.Nation((byte)d).Pixels * 10000 >= (long)p.LeaderShare_bp * w.TotalLand) opp += p.LeaderBonus_bp;
                opp += rng.NextInt(p.Noise_bp + 1);
                _opp[d] = opp;
                if (opp > bestOpp)
                {
                    bestOpp = opp;
                    target = d;
                }
            }

            // 1. Отвести обескровленное.
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(self, k);
                var f = w.FormationAt(slot);
                if (f.Alive == 0 || f.Front == 0) continue;
                int avg = FormationSystem.AverageStrength(w, slot);
                if (f.Order == Orders.Attack && avg < p.RetreatStrength_bp)
                    return Issue(self, CommandType.SetOrder, k, Orders.Defend, out cmd);
                if (f.Order == Orders.Defend && avg < p.RearStrength_bp && _threat[f.Front] < p.ThreatRatio_bp)
                    return Issue(self, CommandType.AssignFormation, k, 0, out cmd);
            }

            if (p.Spread)
            {
                if (Spread(w, self, p, out cmd)) return true;
            }
            else if (target != 0)
            {
                // 2. Закрыть прорыв: фронт под давлением без своих соединений (на месте или в пути).
                for (int d = 1; d <= n; d++)
                {
                    if (!IsFront(w, self, d) || _threat[d] < p.ThreatRatio_bp || HasFormationOn(w, self, d)) continue;
                    int k = RearFormation(w, self, p.DeployStrength_bp);
                    if (k < 0) k = FromWeakestThreatFront(w, self, target, d);
                    if (k < 0) continue;
                    var f = w.FormationAt(w.FormationSlot(self, k));
                    if (f.Order == Orders.Attack) return Issue(self, CommandType.SetOrder, k, Orders.Defend, out cmd);
                    return Issue(self, CommandType.AssignFormation, k, d, out cmd);
                }

                // 3. Ударный кулак: из тыла — на цель.
                int rear = RearFormation(w, self, p.DeployStrength_bp);
                if (rear >= 0) return Issue(self, CommandType.AssignFormation, rear, target, out cmd);

                // 4. Наступать обязательно, если никто не наступает и на цели есть кем.
                if (!AnyAttacking(w, self) && _opp[target] >= p.AttackMinRatio_bp)
                    for (int k = 0; k < maxF; k++)
                    {
                        int slot = w.FormationSlot(self, k);
                        var f = w.FormationAt(slot);
                        if (!w.OnFront(f) || f.Front != target || f.Order == Orders.Attack) continue;
                        if (FormationSystem.AverageStrength(w, slot) < p.AttackStrength_bp) continue;
                        return Issue(self, CommandType.SetOrder, k, Orders.Attack, out cmd);
                    }
            }

            // 5. Набор: броня под равнину цели, пока её доля в армии ниже потолка; иначе пехота.
            return Recruit(w, self, p, target, out cmd);
        }

        /// <summary>Абляция spread: из тыла — на фронт, где своей обороны на ребро меньше всего; на месте — наступать.</summary>
        bool Spread(World w, byte self, AiProfile p, out Command cmd)
        {
            cmd = default;
            int n = w.NationCount, maxF = w.MaxFormationsPerNation;
            int rear = RearFormation(w, self, p.DeployStrength_bp);
            if (rear >= 0)
            {
                int best = 0;
                long bestDef = long.MaxValue;
                for (int d = 1; d <= n; d++)
                {
                    if (!IsFront(w, self, d)) continue;
                    long def = OwnDefense100(w, self, d) / w.FrontLength(self, d);
                    if (def < bestDef)
                    {
                        bestDef = def;
                        best = d;
                    }
                }
                if (best != 0) return Issue(self, CommandType.AssignFormation, rear, best, out cmd);
            }
            for (int k = 0; k < maxF; k++)
            {
                var f = w.FormationAt(w.FormationSlot(self, k));
                if (!w.OnFront(f) || f.Order == Orders.Attack) continue;
                if (FormationSystem.AverageStrength(w, w.FormationSlot(self, k)) < p.AttackStrength_bp) continue;
                return Issue(self, CommandType.SetOrder, k, Orders.Attack, out cmd);
            }
            return false;
        }

        bool Recruit(World w, byte self, AiProfile p, int target, out Command cmd)
        {
            cmd = default;
            var rules = w.Rules;
            var me = w.Nation(self);
            byte type = UnitTypes.Infantry;
            if (target != 0 && PlainShare(w, self, target) >= p.ArmorPlainShare_bp && ArmorShare(w, self) < p.ArmorMaxShare_bp)
                type = UnitTypes.Armor;
            if ((long)me.Gold < (long)rules.Units[type].Cost_c + p.RecruitReserve_c) return false;

            int maxF = w.MaxFormationsPerNation;
            int into = -1;
            if (target != 0)
                for (int k = 0; k < maxF && into < 0; k++)
                {
                    var f = w.FormationAt(w.FormationSlot(self, k));
                    if (f.Alive != 0 && f.Front == target && GameCommands.ValidateRecruit(w, self, k, type) == RejectReason.None) into = k;
                }
            for (int k = 0; k < maxF && into < 0; k++)
                if (GameCommands.OwnFormation(w, self, k) && GameCommands.ValidateRecruit(w, self, k, type) == RejectReason.None) into = k;
            if (into < 0)
            {
                if ((long)me.Gold < (long)GameCommands.RecruitCost(w, -1, type) + p.RecruitReserve_c) return false;
                if (GameCommands.ValidateRecruit(w, self, -1, type) != RejectReason.None) return false;
            }
            return Issue(self, CommandType.RecruitUnit, into, type, out cmd);
        }

        static bool Issue(byte self, CommandType type, int a, int b, out Command cmd)
        {
            cmd = new Command { Nation = self, Type = type, A = a, B = b };
            return true;
        }

        static long Max1(long v) => v > 0 ? v : 1;

        static bool IsFront(World w, byte self, int d) => d != self && w.FrontLength(self, d) > 0 && w.Nation((byte)d).Alive != 0;

        /// <summary>Атака всех своих соединений на d, как если бы все наступали там: Σ_tr (att100 / FrontLen) × FrontTerrainLen.</summary>
        long PotentialAttack(World w, byte self, int d)
        {
            var units = w.Rules.Units;
            int maxF = w.MaxFormationsPerNation, maxB = w.MaxBattalions, kinds = World.TerrainKinds;
            for (int tr = 0; tr < kinds; tr++) _potential[tr] = 0;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(self, k);
                if (w.FormationAt(slot).Alive == 0) continue;
                for (int j = 0; j < maxB; j++)
                {
                    var b = w.BattalionAt(slot * maxB + j);
                    if (b.Alive == 0) continue;
                    var u = units[b.Type];
                    for (int tr = 0; tr < kinds; tr++)
                        _potential[tr] += (long)u.Attack * 100 * u.AttackTerrain_bp[tr] / 10000 * b.Strength_bp / 10000;
                }
            }
            int len = w.FrontLength(self, d);
            long sum = 0;
            for (int tr = 0; tr < kinds; tr++) sum += _potential[tr] / len * w.FrontTerrainLength(self, d, tr);
            return sum;
        }

        /// <summary>Оборона своих соединений с Front == d (на месте и в пути), очки ×100, по равнине.</summary>
        static long OwnDefense100(World w, byte self, int d)
        {
            var units = w.Rules.Units;
            int maxF = w.MaxFormationsPerNation, maxB = w.MaxBattalions;
            long sum = 0;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(self, k);
                var f = w.FormationAt(slot);
                if (f.Alive == 0 || f.Front != d) continue;
                for (int j = 0; j < maxB; j++)
                {
                    var b = w.BattalionAt(slot * maxB + j);
                    if (b.Alive != 0) sum += (long)units[b.Type].Defense * 100 * b.Strength_bp / 10000;
                }
            }
            return sum;
        }

        public static int PlainShare(World w, byte self, int d)
        {
            int len = w.FrontLength(self, d);
            return len > 0 ? (int)((long)w.FrontTerrainLength(self, d, 0) * 10000 / len) : 0;
        }

        /// <summary>Доля брони среди живых батальонов, bp.</summary>
        static int ArmorShare(World w, byte self)
        {
            int maxF = w.MaxFormationsPerNation, maxB = w.MaxBattalions, all = 0, armor = 0;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(self, k);
                if (w.FormationAt(slot).Alive == 0) continue;
                for (int j = 0; j < maxB; j++)
                {
                    var b = w.BattalionAt(slot * maxB + j);
                    if (b.Alive == 0) continue;
                    all++;
                    if (b.Type == UnitTypes.Armor) armor++;
                }
            }
            return all > 0 ? armor * 10000 / all : 0;
        }

        static bool HasFormationOn(World w, byte self, int d)
        {
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
            {
                var f = w.FormationAt(w.FormationSlot(self, k));
                if (f.Alive != 0 && f.Front == d) return true;
            }
            return false;
        }

        static bool AnyAttacking(World w, byte self)
        {
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
            {
                var f = w.FormationAt(w.FormationSlot(self, k));
                if (f.Alive != 0 && f.Front != 0 && f.Order == Orders.Attack) return true;
            }
            return false;
        }

        /// <summary>Первое соединение в тылу с avgStr ≥ minStrength, −1 — нет.</summary>
        static int RearFormation(World w, byte self, int minStrength)
        {
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
            {
                int slot = w.FormationSlot(self, k);
                var f = w.FormationAt(slot);
                if (f.Alive == 0 || f.Front != 0) continue;
                if (FormationSystem.BattalionCount(w, slot) > 0 && FormationSystem.AverageStrength(w, slot) >= minStrength) return k;
            }
            return -1;
        }

        /// <summary>Соединение с фронта с наименьшим threat (кроме цели и самого прорыва), −1 — нет.</summary>
        int FromWeakestThreatFront(World w, byte self, int target, int breach)
        {
            int best = -1;
            long bestThreat = long.MaxValue;
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
            {
                var f = w.FormationAt(w.FormationSlot(self, k));
                if (f.Alive == 0 || f.Front == 0 || f.Front == target || f.Front == breach) continue;
                if (_threat[f.Front] < bestThreat)
                {
                    bestThreat = _threat[f.Front];
                    best = k;
                }
            }
            return best;
        }
    }

    /// <summary>Конец партии (спека 5.7): проверки по порядку, первая сработавшая заканчивает партию.</summary>
    public sealed class EndSystem : ISimSystem
    {
        public void Tick(World world, CommandContext ctx)
        {
            var end = world.Rules.End;
            int n = world.NationCount;
            byte human = ctx.HumanNation;

            if (human != 0 && world.Nation(human).Alive == 0)
            {
                Finish(world, EndReason.HumanDefeated, 0);
                return;
            }

            byte leader = 0, largest = 0;
            int leaderPx = -1, largestPx = -1, alive = 0;
            byte lastAlive = 0;
            for (int id = 1; id <= n; id++)
            {
                var s = world.Nation((byte)id);
                if (s.Alive == 0) continue;
                alive++;
                lastAlive = (byte)id;
                if (s.Pixels > largestPx)
                {
                    largestPx = s.Pixels;
                    largest = (byte)id;
                }
                if ((long)s.Pixels * 10000 >= (long)end.VictoryShare_bp * world.TotalLand && s.Pixels > leaderPx)
                {
                    leaderPx = s.Pixels;
                    leader = (byte)id;
                }
            }
            if (leader != 0)
            {
                Finish(world, EndReason.Conquest, leader);
                return;
            }
            if (alive == 1)
            {
                Finish(world, EndReason.Conquest, lastAlive);
                return;
            }
            if (world.Tick + 1 >= end.TimeoutTicks)
                Finish(world, EndReason.Timeout, largest);
        }

        static void Finish(World w, EndReason reason, byte winner)
        {
            w.IsOver = true;
            w.EndReason = reason;
            w.Winner = winner;
        }
    }
}
