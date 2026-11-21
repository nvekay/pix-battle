using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace BattlePix.Core
{
    /// <summary>Что случилось в журнале войны и мира (для отчёта и сводки UI). Значения только дописываются.</summary>
    public enum WarEventKind : byte
    {
        None = 0,
        /// <summary>A объявил войну D с целью Goal; Value — центр района или −1.</summary>
        Declared = 1,
        /// <summary>Мир между A (нападающий) и D: Outcome, Pixels — закреплено и перешло миром, Value — дань в сотых за секунду.</summary>
        Peace = 2,
        /// <summary>A предложил белый мир D.</summary>
        Offer = 3,
        /// <summary>A отказал D в белом мире.</summary>
        OfferRejected = 4,
        /// <summary>Война A с D кончилась гибелью одной из сторон (Value — id павшей).</summary>
        EndedByDeath = 5,
    }

    /// <summary>Исход войны в журнале. Значения только дописываются.</summary>
    public enum WarOutcome : byte
    {
        None = 0,
        WhiteByOffer = 1,
        Claims = 2,
        Reparations = 3,
        DefenseWin = 4,
        WhiteByTimer = 5,
        NationDied = 6,
    }

    public struct WarEvent
    {
        public int Tick;
        public WarEventKind Kind;
        public byte A, D;
        public byte Goal;
        public WarOutcome Outcome;
        public int Pixels;
        public int Value;
    }

    /// <summary>Почему район претензий нельзя поставить (для UI повторяет проверку ядра).</summary>
    public enum ClaimCheck : byte
    {
        Ok = 0,
        /// <summary>Центр вне карты или не на их исконной земле, которую они держат.</summary>
        NotTheirLand = 1,
        /// <summary>Центр дальше claimMaxDepth от общей границы.</summary>
        TooFar = 2,
        /// <summary>В круг попадает пиксель столицы противника (правка 02b: зона столицы ×3 не запрещает, только сама столица).</summary>
        TouchesCapital = 3,
        /// <summary>Их исконной суши в круге меньше claimMinPixels.</summary>
        TooSmall = 4,
    }

    /// <summary>
    /// Война и мир (спека 02b §3–4): команды DeclareWar / ProposePeace / AnswerPeace, раз в секунду — цели, сроки, перемирие;
    /// в начале тика — разрешение мира по PendingPeace (проход по карте, карманы, перемирие, дань, дивизии в тыл).
    /// Пары — по возрастанию a &lt; b. Случайности нет.
    /// </summary>
    public sealed class WarSystem
    {
        // ---------- старт ----------

        /// <summary>После генерации: Core = Owner, занятого нет.</summary>
        internal static void InitCore(World w)
        {
            var owner = w.OwnerRW;
            var core = w.CoreRW;
            for (int p = 0; p < owner.Length; p++) core[p] = owner[p];
            var occ = w.OccupiedLenRW;
            for (int i = 0; i < occ.Length; i++) occ[i] = 0;
        }

        // ---------- круг района ----------

        /// <summary>Сколько пикселей круга claimRadius вокруг center с Core = core (и Owner = holder, если holder ≠ 0).</summary>
        public static int CountCircle(World w, int center, int core, int holder)
        {
            if (center < 0 || center >= w.PixelCount) return 0;
            int r = w.Rules.War.ClaimRadius, width = w.Width, height = w.Height;
            int cx = center % width, cy = center / width, count = 0;
            var c = w.CoreRW;
            var o = w.OwnerRW;
            for (int dy = -r; dy <= r; dy++)
            {
                int y = cy + dy;
                if (y < 0 || y >= height) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= width || dx * dx + dy * dy > r * r) continue;
                    int p = y * width + x;
                    if (c[p] == core && (holder == 0 || o[p] == holder)) count++;
                }
            }
            return count;
        }

        /// <summary>Пиксель p в круге claimRadius вокруг center (для рендера и тестов).</summary>
        public static bool InCircle(World w, int center, int p)
        {
            int r = w.Rules.War.ClaimRadius, width = w.Width;
            int dx = p % width - center % width, dy = p / width - center / width;
            return dx * dx + dy * dy <= r * r;
        }

        /// <summary>Проверка района претензий нации self на землю target с центром в пикселе p (02b §3.3, четыре условия по порядку).</summary>
        public static ClaimCheck CheckClaim(World w, byte self, int target, int p, out int total)
        {
            total = 0;
            if (p < 0 || p >= w.PixelCount || w.Owner[p] != target || w.Core[p] != target) return ClaimCheck.NotTheirLand;
            if (!NearBorder(w, self, target, p, w.Rules.War.ClaimMaxDepth)) return ClaimCheck.TooFar;
            int cap = w.Nation((byte)target).Capital;
            if (cap >= 0 && InCircle(w, p, cap)) return ClaimCheck.TouchesCapital;
            total = CountCircle(w, p, target, 0);
            if (total < w.Rules.War.ClaimMinPixels) return ClaimCheck.TooSmall;
            return ClaimCheck.Ok;
        }

        /// <summary>Есть пограничный пиксель пары (self, target) на расстоянии Чебышёва ≤ depth от p (проход по Frontier).</summary>
        static bool NearBorder(World w, byte self, int target, int p, int depth)
        {
            int width = w.Width, height = w.Height, px = p % width, py = p / width;
            var frontier = w.FrontierRW;
            var owner = w.OwnerRW;
            var terrain = w.TerrainRW;
            var edge = w.EdgeMaskRW;
            for (int i = 0; i < frontier.Length; i++)
            {
                int q = frontier[i];
                int dx = q % width - px, dy = q / width - py;
                if (dx < 0) dx = -dx;
                if (dy < 0) dy = -dy;
                if ((dx > dy ? dx : dy) > depth) continue;
                int o = owner[q];
                int other = o == target ? self : o == self ? target : -1;
                if (other < 0) continue;
                for (int k = 0; k < 4; k++)
                    if (Grid.Neighbour(q, edge[q], k, width, out int nb) && terrain[nb] != (byte)Terrain.Water && owner[nb] == other)
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Центр района для ИИ (02b §4.9, правка): пограничные пиксели пары на стороне c по возрастанию индекса, каждый 4-й —
        /// кандидат; из допустимых — самый дальний от столицы c (при равенстве меньший индекс). Допустимых нет — −1.
        /// </summary>
        public static int SuggestClaimCenter(World w, byte self, byte c)
        {
            var list = new System.Collections.Generic.List<int>();
            int width = w.Width;
            var frontier = w.FrontierRW;
            var owner = w.OwnerRW;
            var terrain = w.TerrainRW;
            var edge = w.EdgeMaskRW;
            for (int i = 0; i < frontier.Length; i++)
            {
                int q = frontier[i];
                if (owner[q] != c) continue;
                for (int k = 0; k < 4; k++)
                    if (Grid.Neighbour(q, edge[q], k, width, out int nb) && terrain[nb] != (byte)Terrain.Water && owner[nb] == self)
                    {
                        list.Add(q);
                        break;
                    }
            }
            if (list.Count == 0) return -1;
            list.Sort();
            int cap = w.Nation(c).Capital;
            int capX = cap >= 0 ? cap % width : 0, capY = cap >= 0 ? cap / width : 0;
            int best = -1;
            long bestD = -1;
            for (int i = 0; i < list.Count; i += 4)
            {
                int q = list[i];
                long dx = q % width - capX, dy = q / width - capY, d2 = dx * dx + dy * dy;
                if (d2 <= bestD) continue;
                if (CheckClaim(w, self, c, q, out _) != ClaimCheck.Ok) continue;
                bestD = d2;
                best = q;
            }
            return best;
        }

        // ---------- команды (02b §3.3) ----------

        static bool ValidTarget(World w, byte self, int target) =>
            target >= 1 && target <= w.NationCount && target != self && w.Nation((byte)target).Alive != 0;

        /// <summary>DeclareWar без проверки, жива ли нация (для UI и ИИ).</summary>
        public static RejectReason ValidateDeclare(World w, byte self, int target, int b)
        {
            if (!ValidTarget(w, self, target)) return RejectReason.TargetInvalid;
            if (w.FrontLength(self, target) <= 0) return RejectReason.NoContact;
            var war = w.WarOf(self, target);
            if (war.Status == WarStatus.War) return RejectReason.AlreadyAtWar;
            if (war.Status == WarStatus.Truce) return RejectReason.TruceActive;
            if (b < 0) return RejectReason.ClaimInvalid;
            if (b >= 1 && CheckClaim(w, self, target, b - 1, out _) != ClaimCheck.Ok) return RejectReason.ClaimInvalid;
            return RejectReason.None;
        }

        internal static void ApplyDeclare(World w, byte self, int target, int b)
        {
            int center = b >= 1 ? b - 1 : -1;
            int total = 0;
            if (center >= 0) CheckClaim(w, self, target, center, out total);
            int idx = w.WarIndex(self, target);
            w.WarsRW[idx] = new War
            {
                Status = WarStatus.War,
                Attacker = self,
                Goal = center >= 0 ? WarGoals.Claims : WarGoals.Reparations,
                ClaimCenter = center,
                ClaimTotal = total,
                StartTick = w.Tick,
                PreWarAtt = w.Nation(self).Pixels,
                PreWarDef = w.Nation((byte)target).Pixels,
            };
            w.ClaimHeldLenRW[idx] = 0;
            w.PeaceByTimer[idx] = false;
            w.WarEvents.Add(new WarEvent
            {
                Tick = w.Tick, Kind = WarEventKind.Declared, A = self, D = (byte)target,
                Goal = center >= 0 ? WarGoals.Claims : WarGoals.Reparations, Value = center,
            });
        }

        /// <summary>Своя цель стороны self выполнена (Hold ≥ goalHoldSeconds).</summary>
        static bool OwnGoal(World w, in War war, byte self) =>
            (self == war.Attacker ? war.HoldAtt : war.HoldDef) >= w.Rules.War.GoalHoldSeconds;

        static bool LiveOfferFrom(World w, in War war, int from) => war.OfferFrom == from && w.Tick < war.OfferUntil;

        /// <summary>ProposePeace без проверки, жива ли нация.</summary>
        public static RejectReason ValidatePropose(World w, byte self, int target)
        {
            if (!ValidTarget(w, self, target)) return RejectReason.TargetInvalid;
            var war = w.WarOf(self, target);
            if (war.Status != WarStatus.War || war.PendingPeace != PeaceKinds.None) return RejectReason.NotAtWar;
            if (OwnGoal(w, war, self) || LiveOfferFrom(w, war, target)) return RejectReason.None;
            int cooldown = self == war.Attacker ? war.OfferCooldownAtt : war.OfferCooldownDef;
            if (w.Tick < cooldown) return RejectReason.PeaceCooldown;
            return RejectReason.None;
        }

        internal static void ApplyPropose(World w, byte self, int target)
        {
            int idx = w.WarIndex(self, target);
            var war = w.WarsRW[idx];
            if (OwnGoal(w, war, self))
                war.PendingPeace = self == war.Attacker
                    ? (war.Goal == WarGoals.Claims ? PeaceKinds.Claims : PeaceKinds.Reparations)
                    : PeaceKinds.DefenseWin;
            else if (LiveOfferFrom(w, war, target))
                war.PendingPeace = PeaceKinds.White;
            else
            {
                var r = w.Rules.War;
                war.OfferFrom = self;
                war.OfferUntil = w.Tick + r.OfferTicks;
                if (self == war.Attacker) war.OfferCooldownAtt = w.Tick + r.OfferCooldownTicks;
                else war.OfferCooldownDef = w.Tick + r.OfferCooldownTicks;
                w.WarEvents.Add(new WarEvent { Tick = w.Tick, Kind = WarEventKind.Offer, A = self, D = (byte)target });
            }
            w.WarsRW[idx] = war;
        }

        /// <summary>AnswerPeace без проверки, жива ли нация. from — тот, кто предложил.</summary>
        public static RejectReason ValidateAnswer(World w, byte self, int from, int answer)
        {
            if (!ValidTarget(w, self, from)) return RejectReason.TargetInvalid;
            var war = w.WarOf(self, from);
            if (war.Status != WarStatus.War || war.PendingPeace != PeaceKinds.None) return RejectReason.NotAtWar;
            if (!LiveOfferFrom(w, war, from)) return RejectReason.NoOffer;
            if (answer != 0 && answer != 1) return RejectReason.OrderInvalid;
            return RejectReason.None;
        }

        internal static void ApplyAnswer(World w, byte self, int from, int answer)
        {
            int idx = w.WarIndex(self, from);
            var war = w.WarsRW[idx];
            if (answer == 1) war.PendingPeace = PeaceKinds.White;
            else
            {
                war.OfferFrom = 0;
                w.WarEvents.Add(new WarEvent { Tick = w.Tick, Kind = WarEventKind.OfferRejected, A = self, D = (byte)from });
            }
            w.WarsRW[idx] = war;
        }

        // ---------- раз в секунду (02b §4.2) ----------

        /// <summary>Последний тик секунды, после закрытия фронтов, до экономики: цели, сроки, предложения, перемирие.</summary>
        public void Second(World w, int tps)
        {
            if (w.Tick % tps != tps - 1) return;
            var r = w.Rules.War;
            int n = w.NationCount, stride = n + 1;
            var wars = w.WarsRW;
            var occ = w.OccupiedLenRW;
            for (int a = 1; a <= n; a++)
            for (int b = a + 1; b <= n; b++)
            {
                int idx = (a - 1) * n + (b - 1);
                var war = wars[idx];
                if (war.Status == WarStatus.War)
                {
                    byte att = war.Attacker, def = war.Defender(a, b);
                    bool attOk;
                    if (war.Goal == WarGoals.Claims)
                    {
                        int held = CountCircle(w, war.ClaimCenter, def, att);
                        w.ClaimHeldLenRW[idx] = held;
                        attOk = (long)held * 10000 >= (long)r.ClaimHold_bp * war.ClaimTotal;
                    }
                    else
                    {
                        w.ClaimHeldLenRW[idx] = 0;
                        attOk = (long)occ[att * stride + def] * 10000 >= (long)r.RepOccupy_bp * war.PreWarDef;
                    }
                    bool defOk = (long)occ[def * stride + att] * 10000 >= (long)r.DefenseWin_bp * war.PreWarAtt;
                    war.HoldAtt = attOk ? (short)(war.HoldAtt < 32000 ? war.HoldAtt + 1 : 32000) : (short)0;
                    war.HoldDef = defOk ? (short)(war.HoldDef < 32000 ? war.HoldDef + 1 : 32000) : (short)0;
                    if (w.Tick - war.StartTick >= r.MaxWarTicks && war.PendingPeace == PeaceKinds.None)
                    {
                        war.PendingPeace = PeaceKinds.White;
                        w.PeaceByTimer[idx] = true;
                    }
                    if (war.OfferFrom != 0 && w.Tick >= war.OfferUntil) war.OfferFrom = 0;
                    wars[idx] = war;
                }
                else if (war.Status == WarStatus.Truce && w.Tick >= war.TruceUntil)
                {
                    wars[idx] = new War
                    {
                        ClaimCenter = -1,
                        TributePayer = war.TributePayer, TributePayee = war.TributePayee, TributeUntil = war.TributeUntil,
                    };
                }
            }
        }

        // ---------- разрешение мира (02b §4.3) ----------

        /// <summary>Первой системой тика: пары с PendingPeace ≠ 0 по возрастанию a &lt; b.</summary>
        public void Resolve(World w)
        {
            int n = w.NationCount;
            bool any = false;
            var wars = w.WarsRW;
            for (int a = 1; a <= n; a++)
            for (int b = a + 1; b <= n; b++)
            {
                int idx = (a - 1) * n + (b - 1);
                if (wars[idx].Status != WarStatus.War || wars[idx].PendingPeace == PeaceKinds.None) continue;
                MakePeace(w, a, b);
                any = true;
            }
            if (!any) return;
            FrontSystem.RebuildFrontier(w);
            FrontSystem.RecountFrontLen(w);
            w.MarkAllDirty();
        }

        /// <summary>Мир по паре: проход по карте, карманы, пересчёт Pixels и OccupiedLen, перемирие и дань, дивизии в тыл.</summary>
        internal static void MakePeace(World w, int a, int b)
        {
            var r = w.Rules.War;
            int n = w.NationCount, idx = w.WarIndex(a, b);
            var war = w.WarsRW[idx];
            byte att = war.Attacker, def = war.Defender(a, b);
            byte kind = war.PendingPeace;

            var settled = new NativeArray<int>(2, Allocator.TempJob);
            var labels = new NativeArray<byte>(w.PixelCount, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var queue = new NativeArray<int>(w.PixelCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var pixels = new NativeArray<int>(n + 1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                new PeaceJob
                {
                    Owner = w.OwnerRW, Core = w.CoreRW, A = att, D = def, Kind = kind,
                    Center = war.ClaimCenter, R = r.ClaimRadius, W = w.Width, H = w.Height, Settled = settled,
                }.Run();
                new PocketJob
                {
                    Owner = w.OwnerRW, Core = w.CoreRW, Terrain = w.TerrainRW, EdgeMask = w.EdgeMaskRW, W = w.Width,
                    A = att, D = def, CapA = w.Nation(att).Capital, CapD = w.Nation(def).Capital,
                    Labels = labels, Queue = queue, Settled = settled,
                }.Run();
                new RecountJob
                {
                    Owner = w.OwnerRW, Core = w.CoreRW, Pixels = pixels, OccupiedLen = w.OccupiedLenRW, Stride = n + 1,
                }.Run();
                var nations = w.NationsRW;
                for (int i = 0; i < n; i++)
                {
                    var s = nations[i];
                    s.Pixels = pixels[i + 1];
                    nations[i] = s;
                }

                // Перемирие и дань.
                var next = new War { Status = WarStatus.Truce, ClaimCenter = -1, TruceUntil = w.Tick + r.TruceTicks };
                int tributeRate = 0;
                if (kind == PeaceKinds.Reparations || kind == PeaceKinds.DefenseWin)
                {
                    next.TributePayer = kind == PeaceKinds.Reparations ? def : att;
                    next.TributePayee = kind == PeaceKinds.Reparations ? att : def;
                    next.TributeUntil = w.Tick + r.TributeTicks;
                    tributeRate = (int)((long)EconomySystem.Income(w, next.TributePayer) * r.Tribute_bp / 10000);
                }
                w.WarsRW[idx] = next;
                w.ClaimHeldLenRW[idx] = 0;

                // Дивизии пары в тыл сразу, приказы пары — оборона.
                var divisions = w.DivisionsRW;
                for (int slot = 0; slot < divisions.Length; slot++)
                {
                    var v = divisions[slot];
                    if (v.Alive == 0 || !((v.Owner == att && v.Front == def) || (v.Owner == def && v.Front == att))) continue;
                    v.Front = 0;
                    v.ArriveTick = 0;
                    divisions[slot] = v;
                }
                w.FrontOrderRW[(att - 1) * n + (def - 1)] = Orders.Defend;
                w.FrontOrderRW[(def - 1) * n + (att - 1)] = Orders.Defend;

                // Часы повода (правка 02b): война кончилась у обеих сторон.
                foreach (byte side in new[] { att, def })
                {
                    var sn = w.NationsRW[side - 1];
                    sn.LastWarEndTick = w.Tick;
                    w.NationsRW[side - 1] = sn;
                }

                WarOutcome outcome = kind == PeaceKinds.Claims ? WarOutcome.Claims
                    : kind == PeaceKinds.Reparations ? WarOutcome.Reparations
                    : kind == PeaceKinds.DefenseWin ? WarOutcome.DefenseWin
                    : w.PeaceByTimer[idx] ? WarOutcome.WhiteByTimer : WarOutcome.WhiteByOffer;
                w.PeaceByTimer[idx] = false;
                w.WarEvents.Add(new WarEvent
                {
                    Tick = w.Tick, Kind = WarEventKind.Peace, A = att, D = def, Goal = war.Goal, Outcome = outcome,
                    Pixels = settled[0] + settled[1], Value = tributeRate,
                });
            }
            finally
            {
                settled.Dispose();
                labels.Dispose();
                queue.Dispose();
                pixels.Dispose();
            }
        }

        // ---------- гибель нации (02b §4.5) ----------

        /// <summary>Все пары с d — мир, поля обнулены, дань с d и в пользу d снята. Война с d в журнал.</summary>
        internal static void OnNationDied(World w, byte d)
        {
            int n = w.NationCount;
            for (int x = 1; x <= n; x++)
            {
                if (x == d) continue;
                int idx = w.WarIndex(x, d);
                var war = w.WarsRW[idx];
                if (war.Status == WarStatus.War)
                {
                    byte def = war.Defender(x, d);
                    w.WarEvents.Add(new WarEvent
                    {
                        Tick = w.Tick, Kind = WarEventKind.EndedByDeath, A = war.Attacker, D = def, Goal = war.Goal,
                        Outcome = WarOutcome.NationDied, Value = d,
                    });
                    var s = w.NationsRW[x - 1];
                    s.LastWarEndTick = w.Tick;
                    w.NationsRW[x - 1] = s;
                }
                w.WarsRW[idx] = new War { ClaimCenter = -1 };
                w.ClaimHeldLenRW[idx] = 0;
                w.PeaceByTimer[idx] = false;
            }
        }

        /// <summary>OccupiedLen с нуля по карте.</summary>
        internal static void RecountOccupied(World w)
        {
            var pixels = new NativeArray<int>(w.NationCount + 1, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                new RecountJob { Owner = w.OwnerRW, Core = w.CoreRW, Pixels = pixels, OccupiedLen = w.OccupiedLenRW, Stride = w.NationCount + 1 }.Run();
            }
            finally
            {
                pixels.Dispose();
            }
        }

        // ---------- для UI и ИИ ----------

        /// <summary>
        /// Насколько выполнено условие стороны side в войне a–b, bp от порога (10 000 = выполнено, может быть больше):
        /// у нападающего — район (на последнюю секунду) или занятая доля его довоенной земли, у защитника — занятая доля земли нападающего.
        /// Нет войны — 0.
        /// </summary>
        public static int GoalProgress_bp(World w, int a, int b, int side)
        {
            if (!w.AtWar(a, b)) return 0;
            var r = w.Rules.War;
            var war = w.WarOf(a, b);
            byte att = war.Attacker, def = war.Defender(a, b);
            long have, need;
            if (side == att)
            {
                if (war.Goal == WarGoals.Claims)
                {
                    have = (long)w.ClaimHeld(a, b) * 10000;
                    need = (long)r.ClaimHold_bp * war.ClaimTotal;
                }
                else
                {
                    have = (long)w.Occupied(att, def) * 10000;
                    need = (long)r.RepOccupy_bp * war.PreWarDef;
                }
            }
            else
            {
                have = (long)w.Occupied(def, att) * 10000;
                need = (long)r.DefenseWin_bp * war.PreWarAtt;
            }
            if (need <= 0) return 10000;
            long p = have * 10000 / need;
            return p > int.MaxValue ? int.MaxValue : (int)p;
        }

        /// <summary>Своих войн нации (которые она начала).</summary>
        public static int OwnWars(World w, byte self)
        {
            int c = 0;
            for (int d = 1; d <= w.NationCount; d++)
                if (d != self && w.AtWar(self, d) && w.WarOf(self, d).Attacker == self) c++;
            return c;
        }

        /// <summary>Войн нации всего.</summary>
        public static int Wars(World w, byte self)
        {
            int c = 0;
            for (int d = 1; d <= w.NationCount; d++)
                if (d != self && w.AtWar(self, d)) c++;
            return c;
        }
    }

    /// <summary>Шаг 1 тика: мир по PendingPeace (02b §4.3).</summary>
    public sealed class WarResolveStep : ISimSystem
    {
        readonly WarSystem _s;
        public WarResolveStep(WarSystem s) { _s = s; }
        public void Tick(World world, CommandContext ctx) => _s.Resolve(world);
    }

    /// <summary>Шаг тика перед экономикой: цели, сроки, перемирие раз в секунду (02b §4.2).</summary>
    public sealed class WarSecondStep : ISimSystem
    {
        readonly WarSystem _s;
        public WarSecondStep(WarSystem s) { _s = s; }
        public void Tick(World world, CommandContext ctx) => _s.Second(world, ctx.Config.TicksPerSecond);
    }

    /// <summary>
    /// Проход мира по карте (02b §4.3): занятое пары закрепляется или возвращается по виду мира; у претензий весь
    /// район Owner = Core = D переходит A. Settled[0] — пикселей, закреплённых и перешедших миром.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct PeaceJob : IJob
    {
        public NativeArray<byte> Owner;
        public NativeArray<byte> Core;
        public byte A, D, Kind;
        public int Center, R, W, H;
        public NativeArray<int> Settled;

        public void Execute()
        {
            bool settle = Kind == PeaceKinds.White || Kind == PeaceKinds.Claims;
            int count = 0;
            for (int p = 0; p < Owner.Length; p++)
            {
                byte o = Owner[p], c = Core[p];
                if (o == A && c == D)
                {
                    if (settle)
                    {
                        Core[p] = A;
                        count++;
                    }
                    else Owner[p] = D;
                }
                else if (o == D && c == A)
                {
                    if (Kind == PeaceKinds.White)
                    {
                        Core[p] = D;
                        count++;
                    }
                    else Owner[p] = A;
                }
            }
            if (Kind == PeaceKinds.Claims && Center >= 0)
            {
                int cx = Center % W, cy = Center / W;
                for (int dy = -R; dy <= R; dy++)
                {
                    int y = cy + dy;
                    if (y < 0 || y >= H) continue;
                    for (int dx = -R; dx <= R; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= W || dx * dx + dy * dy > R * R) continue;
                        int p = y * W + x;
                        if (Owner[p] != D || Core[p] != D) continue;
                        Owner[p] = A;
                        Core[p] = A;
                        count++;
                    }
                }
            }
            Settled[0] = count;
        }
    }

    /// <summary>
    /// Карманы при мире (02b §4.6): связные по 4-соседству компоненты суши A или D без своей столицы, у которых все соседи-суша —
    /// другая сторона пары (и хотя бы один сосед есть), переходят другой стороне целиком: Owner = Core = другая.
    /// Решения принимаются по карте до переходов. Labels: 0 — не обойдён, 1 — остаётся, 2 — к A, 3 — к D. Settled[1] — пикселей.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct PocketJob : IJob
    {
        public NativeArray<byte> Owner;
        public NativeArray<byte> Core;
        [ReadOnly] public NativeArray<byte> Terrain;
        [ReadOnly] public NativeArray<byte> EdgeMask;
        public int W;
        public byte A, D;
        public int CapA, CapD;
        public NativeArray<byte> Labels;
        public NativeArray<int> Queue;
        public NativeArray<int> Settled;

        public void Execute()
        {
            int n = Owner.Length;
            for (int p = 0; p < n; p++)
            {
                if (Labels[p] != 0 || Terrain[p] == (byte)BattlePix.Core.Terrain.Water) continue;
                byte own = Owner[p];
                if (own != A && own != D) continue;
                byte other = own == A ? D : A;
                int cap = own == A ? CapA : CapD;
                int head = 0, tail = 0;
                Queue[tail++] = p;
                Labels[p] = 1;
                bool hasCap = false, onlyOther = true, anyNeighbour = false;
                while (head < tail)
                {
                    int q = Queue[head++];
                    if (q == cap) hasCap = true;
                    byte edge = EdgeMask[q];
                    for (int k = 0; k < 4; k++)
                    {
                        if (!Grid.Neighbour(q, edge, k, W, out int nb) || Terrain[nb] == (byte)BattlePix.Core.Terrain.Water) continue;
                        byte o = Owner[nb];
                        if (o == own)
                        {
                            if (Labels[nb] == 0)
                            {
                                Labels[nb] = 1;
                                Queue[tail++] = nb;
                            }
                        }
                        else
                        {
                            anyNeighbour = true;
                            if (o != other) onlyOther = false;
                        }
                    }
                }
                if (hasCap || !onlyOther || !anyNeighbour) continue;
                byte mark = other == A ? (byte)2 : (byte)3;
                for (int i = 0; i < tail; i++) Labels[Queue[i]] = mark;
            }
            int count = 0;
            for (int p = 0; p < n; p++)
            {
                byte l = Labels[p];
                if (l < 2) continue;
                byte to = l == 2 ? A : D;
                Owner[p] = to;
                Core[p] = to;
                count++;
            }
            Settled[1] = count;
        }
    }

    /// <summary>Пересчёт с нуля: Pixels по владельцам и OccupiedLen[holder, core] по занятым пикселям.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct RecountJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Owner;
        [ReadOnly] public NativeArray<byte> Core;
        public NativeArray<int> Pixels;
        public NativeArray<int> OccupiedLen;
        public int Stride;

        public void Execute()
        {
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] = 0;
            for (int i = 0; i < OccupiedLen.Length; i++) OccupiedLen[i] = 0;
            for (int p = 0; p < Owner.Length; p++)
            {
                byte o = Owner[p], c = Core[p];
                Pixels[o]++;
                if (c != 0 && o != c) OccupiedLen[o * Stride + c]++;
            }
        }
    }
}
