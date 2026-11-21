namespace BattlePix.Core
{
    /// <summary>
    /// ИИ (спека 01c §4.8). ИИ — не отдельная логика, а профиль весов из ai.json, который выбирает из тех же команд.
    /// Каждая ИИ-нация думает раз в thinkIntervalTicks со сдвигом (id − 1) % interval и отдаёт не больше одной команды
    /// на Tick + 1 через ту же очередь. Читает только World (силы сторон из SidePow этого тика) и свои веса.
    /// Правила сверху вниз, первое сработавшее отдаёт команду: в тыл обескровленную, закрыть прорыв, ротация, кулак,
    /// приказы фронтам, набор. Профиль spread заменяет правила 2–5 «ровным слоем».
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
            int n = w.NationCount, maxK = w.MaxDivisionsPerNation;
            var army = w.Rules.Army;
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
            FillPotential(w, self);
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

            // 02b, правило 1. Забрать выполненное: своя цель в войне выполнена — мир на своих условиях.
            for (int d = 1; d <= n; d++)
                if (IsFront(w, self, d) && w.GoalFulfilled(self, d, self)
                    && WarSystem.ValidatePropose(w, self, d) == RejectReason.None)
                    return Issue(self, CommandType.ProposePeace, d, 0, out cmd);

            // 1. В тыл обескровленную: на фронте, численность ниже rearStrength, на её фронте нет угрозы.
            for (int k = 0; k < maxK; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (!w.OnFront(v)) continue;
                if (v.Strength_bp < p.RearStrength_bp && _threat[v.Front] < p.ThreatRatio_bp)
                    return Issue(self, CommandType.AssignDivision, k, 0, out cmd);
            }

            if (p.Spread)
            {
                if (Spread(w, self, p, out cmd)) return true;
            }
            else
            {
                if (target != 0)
                {
                    // 2. Закрыть прорыв: фронт под угрозой без своих дивизий на месте и в пути.
                    for (int d = 1; d <= n; d++)
                    {
                        if (!IsFront(w, self, d) || _threat[d] < p.ThreatRatio_bp || HasDivisionOn(w, self, d)) continue;
                        int k = FreshRear(w, self, p);
                        if (k < 0) k = FromWeakestThreatFront(w, self, target, d);
                        if (k < 0) continue;
                        return Issue(self, CommandType.AssignDivision, k, d, out cmd);
                    }

                    // 3. Ротация: на цели выдохшаяся, в тылу свежая — свежую на цель. Следующей мыслью — выдохшуюся в тыл,
                    //    если на цели нет угрозы и туда уже идёт (или стоит) невыдохшаяся.
                    int tired = ExhaustedOn(w, self, target);
                    if (tired >= 0)
                    {
                        int fresh = FreshRear(w, self, p);
                        if (fresh >= 0) return Issue(self, CommandType.AssignDivision, fresh, target, out cmd);
                        if (_threat[target] < p.ThreatRatio_bp && HasRelief(w, self, target))
                            return Issue(self, CommandType.AssignDivision, tired, 0, out cmd);
                    }

                    // 4. Кулак: свежая готовая дивизия из тыла — на цель.
                    int rear = FreshRear(w, self, p);
                    if (rear >= 0) return Issue(self, CommandType.AssignDivision, rear, target, out cmd);
                }

                // 5. Приказы: наступление на цели, если opp ≥ attackMinRatio, на остальных фронтах — оборона.
                for (int d = 1; d <= n; d++)
                {
                    if (d == self || w.Nation((byte)d).Alive == 0) continue;
                    byte want = d == target && _opp[d] >= p.AttackMinRatio_bp ? Orders.Attack : Orders.Defend;
                    if (w.FrontOrderOf(self, d) != want) return Issue(self, CommandType.SetFrontOrder, d, want, out cmd);
                }
            }

            // 02b, правило 4. Объявление, если своих войн меньше maxOwnWars (подготовка — PR 2).
            if (Declare(w, self, p, ref rng, out cmd)) return true;

            // 6. Набор: танковая под равнину цели, пока её доля среди живых ниже потолка; иначе пехотная.
            return Recruit(w, self, p, target, out cmd);
        }

        /// <summary>
        /// 02b §4.9, правило 4: кандидат — сосед в мире (не перемирие) с max ratio (при равенстве меньший id); объявлять,
        /// если ratio ≥ declareRatio или без своих войн прошло idleWarTicks. Цель: претензии с вероятностью claimsChance.
        /// </summary>
        bool Declare(World w, byte self, AiProfile p, ref Pcg32 rng, out Command cmd)
        {
            cmd = default;
            if (WarSystem.OwnWars(w, self) >= p.MaxOwnWars) return false;
            int c = 0;
            long best = long.MinValue;
            for (int d = 1; d <= w.NationCount; d++)
            {
                if (d == self || w.Nation((byte)d).Alive == 0 || w.FrontLength(self, d) <= 0) continue;
                if (w.WarOf(self, d).Status != WarStatus.Peace) continue;
                long r = DeclareRatio(w, self, d);
                if (r > best)
                {
                    best = r;
                    c = d;
                }
            }
            if (c == 0) return false;
            int since = w.Tick - w.Nation(self).OwnWarEndTick;
            if (best < p.DeclareRatio_bp && since < p.IdleWarTicks) return false;
            int b = 0;
            if (rng.NextInt(10000) < p.ClaimsChance_bp)
            {
                int center = WarSystem.SuggestClaimCenter(w, self, (byte)c);
                if (center >= 0) b = center + 1;
            }
            if (WarSystem.ValidateDeclare(w, self, c, b) != RejectReason.None) return false;
            return Issue(self, CommandType.DeclareWar, c, b, out cmd);
        }

        /// <summary>ratio(c) = Σ своих готовых atk100 по равнине × Strength / max(1, Σ их живых def100 по равнине × Strength), bp.</summary>
        public static long DeclareRatio(World w, byte self, int c)
        {
            long att = 0, def = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (v.Alive != 0 && v.ReadyTick <= w.Tick) att += DivisionSystem.Attack100(w, v, 0);
                var e = w.DivisionAt(w.DivisionSlot((byte)c, k));
                if (e.Alive != 0) def += (long)w.Rules.DivisionDef100(e.Type, 0) * e.Strength_bp / 10000;
            }
            return att * 10000 / Max1(def);
        }

        /// <summary>Абляция spread: готовую дивизию из тыла — на фронт, где своей обороны на ребро меньше всего; все приказы — наступление.</summary>
        bool Spread(World w, byte self, AiProfile p, out Command cmd)
        {
            cmd = default;
            int n = w.NationCount;
            int rear = FreshRear(w, self, p);
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
                if (best != 0) return Issue(self, CommandType.AssignDivision, rear, best, out cmd);
            }
            for (int d = 1; d <= n; d++)
            {
                if (!IsFront(w, self, d) || w.FrontOrderOf(self, d) == Orders.Attack) continue;
                return Issue(self, CommandType.SetFrontOrder, d, Orders.Attack, out cmd);
            }
            return false;
        }

        bool Recruit(World w, byte self, AiProfile p, int target, out Command cmd)
        {
            cmd = default;
            var rules = w.Rules;
            var me = w.Nation(self);
            byte type = DivisionTypes.Infantry;
            if (target != 0 && PlainShare(w, self, target) >= p.ArmorPlainShare_bp && ArmorShare(w, self) < p.ArmorMaxShare_bp)
                type = DivisionTypes.Armor;
            if (type >= rules.Divisions.Length) type = DivisionTypes.Infantry;
            if ((long)me.Gold < (long)rules.DivisionCost(type) + p.RecruitReserve_c) return false;
            if (GameCommands.ValidateRecruit(w, self, type) != RejectReason.None) return false;
            return Issue(self, CommandType.RecruitDivision, type, 0, out cmd);
        }

        static bool Issue(byte self, CommandType type, int a, int b, out Command cmd)
        {
            cmd = new Command { Nation = self, Type = type, A = a, B = b };
            return true;
        }

        static long Max1(long v) => v > 0 ? v : 1;

        /// <summary>Фронт войны (02b §4.9): общая граница, сосед жив, пара в войне.</summary>
        static bool IsFront(World w, byte self, int d) => d != self && w.FrontLength(self, d) > 0 && w.Nation((byte)d).Alive != 0 && w.AtWar(self, d);

        /// <summary>Атака всех своих готовых дивизий по рельефу, очки ×100, как если бы все наступали.</summary>
        void FillPotential(World w, byte self)
        {
            int kinds = World.TerrainKinds;
            for (int tr = 0; tr < kinds; tr++) _potential[tr] = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (v.Alive == 0 || v.ReadyTick > w.Tick) continue;
                for (int tr = 0; tr < kinds; tr++) _potential[tr] += DivisionSystem.Attack100(w, v, tr);
            }
        }

        /// <summary>Σ_tr (potential[tr] / FrontLen) × FrontTerrainLen[self, d, tr].</summary>
        long PotentialAttack(World w, byte self, int d)
        {
            int len = w.FrontLength(self, d);
            long sum = 0;
            for (int tr = 0; tr < World.TerrainKinds; tr++) sum += _potential[tr] / len * w.FrontTerrainLength(self, d, tr);
            return sum;
        }

        /// <summary>Оборона своих дивизий с Front == d (на месте и в пути), очки ×100, по равнине.</summary>
        static long OwnDefense100(World w, byte self, int d)
        {
            long sum = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (v.Alive == 0 || v.Front != d) continue;
                sum += (long)w.Rules.DivisionDef100(v.Type, 0) * v.Strength_bp / 10000;
            }
            return sum;
        }

        public static int PlainShare(World w, byte self, int d)
        {
            int len = w.FrontLength(self, d);
            return len > 0 ? (int)((long)w.FrontTerrainLength(self, d, 0) * 10000 / len) : 0;
        }

        /// <summary>Доля танковых среди живых дивизий, bp.</summary>
        static int ArmorShare(World w, byte self)
        {
            int all = 0, armor = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (v.Alive == 0) continue;
                all++;
                if (v.Type == DivisionTypes.Armor) armor++;
            }
            return all > 0 ? armor * 10000 / all : 0;
        }

        /// <summary>Своя дивизия с Front == d на месте или в пути.</summary>
        static bool HasDivisionOn(World w, byte self, int d)
        {
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (v.Alive != 0 && v.Front == d) return true;
            }
            return false;
        }

        /// <summary>Первая выдохшаяся дивизия на месте на фронте с d, −1 — нет.</summary>
        static int ExhaustedOn(World w, byte self, int d)
        {
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (w.OnFront(v) && v.Front == d && v.Exhausted != 0) return k;
            }
            return -1;
        }

        /// <summary>На фронт с d уже идёт или стоит невыдохшаяся своя дивизия: смена пришла.</summary>
        static bool HasRelief(World w, byte self, int d)
        {
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (v.Alive != 0 && v.Front == d && v.Exhausted == 0) return true;
            }
            return false;
        }

        /// <summary>Свежая: готова, в тылу, численность ≥ deployStrength, организация ≥ resumeOrg.</summary>
        static bool Fresh(World w, in Division v, AiProfile p) =>
            v.Alive != 0 && v.Front == 0 && v.ReadyTick <= w.Tick && v.Strength_bp >= p.DeployStrength_bp && v.Org_bp >= w.Rules.Army.ResumeOrg_bp;

        /// <summary>Первая свежая дивизия в тылу, −1 — нет.</summary>
        static int FreshRear(World w, byte self, AiProfile p)
        {
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
                if (Fresh(w, w.DivisionAt(w.DivisionSlot(self, k)), p)) return k;
            return -1;
        }

        /// <summary>Дивизия с фронта с наименьшим threat (кроме цели и самого прорыва), −1 — нет.</summary>
        int FromWeakestThreatFront(World w, byte self, int target, int breach)
        {
            int best = -1;
            long bestThreat = long.MaxValue;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(self, k));
                if (v.Alive == 0 || v.Front == 0 || v.Front == target || v.Front == breach) continue;
                if (_threat[v.Front] < bestThreat)
                {
                    bestThreat = _threat[v.Front];
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
