using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Синтетические миры для войны и мира (спека 02b).</summary>
    static class WarWorlds
    {
        /// <summary>Мир width × height, вся суша; owner(x, y) задаёт владельца; столицы — пиксели (x, y) по нациям 1..N.</summary>
        public static World Make(int width, int height, int nations, Func<int, int, byte> owner, (int x, int y)[] capitals, Rules rules = null)
        {
            rules = rules ?? Rules.Default(nations);
            var w = new World(width, height, nations, 1, rules);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = y * width + x;
                w.TerrainRW[p] = (byte)Terrain.Land;
                w.OwnerRW[p] = owner(x, y);
            }
            for (byte id = 1; id <= nations; id++)
                Worlds.Nation(w, id, capitals[id - 1].y * width + capitals[id - 1].x, 0, 100000);
            Worlds.CoreFromOwner(w);
            SyncPixels(w);
            FrontSystem.StampCapitals(w);
            Worlds.Recount(w);
            w.TotalLand = width * height;
            return w;
        }

        /// <summary>Pixels наций по карте и OccupiedLen с нуля — после ручной правки Owner / Core.</summary>
        public static void SyncPixels(World w)
        {
            var count = new int[w.NationCount + 1];
            for (int p = 0; p < w.PixelCount; p++) count[w.Owner[p]]++;
            for (int i = 0; i < w.NationCount; i++)
            {
                var s = w.NationsRW[i];
                s.Pixels = count[i + 1];
                w.NationsRW[i] = s;
            }
            WarSystem.RecountOccupied(w);
        }

        public static void Set(World w, int x, int y, byte owner, byte core)
        {
            int p = y * w.Width + x;
            w.OwnerRW[p] = owner;
            w.CoreRW[p] = core;
        }

        public static void Pending(World w, byte a, byte b, byte kind)
        {
            int idx = w.WarIndex(a, b);
            var war = w.WarsRW[idx];
            war.PendingPeace = kind;
            w.WarsRW[idx] = war;
        }

        /// <summary>Пиксели круга claimRadius вокруг center по возрастанию индекса.</summary>
        public static List<int> Circle(World w, int center)
        {
            var list = new List<int>();
            for (int p = 0; p < w.PixelCount; p++)
                if (WarSystem.InCircle(w, center, p)) list.Add(p);
            return list;
        }
    }

    /// <summary>02b acceptance 5: четыре вида мира пиксель в пиксель по таблице 4.3, дивизии в тыл, перемирие и дань.</summary>
    public class PeaceTableTests
    {
        // 60×30: Альвария-нападающий A (1) x < 30, столица (2, 15); Ольстран-защитник D (2) x ≥ 30, столица (57, 15).
        // A держит блок земли D x 30..34, y 10..19; D держит блок земли A x 25..29, y 0..4. Район r = 3 с центром (36, 15)
        // задевает блок A, поэтому после претензий остаётся связным с A и не становится карманом.
        static World Make(byte kind)
        {
            var rules = Rules.Default(2);
            rules.War.ClaimRadius = 3;
            var w = WarWorlds.Make(60, 30, 2, (x, y) => x < 30 ? (byte)1 : (byte)2, new[] { (2, 15), (57, 15) }, rules);
            for (int y = 10; y < 20; y++) for (int x = 30; x < 35; x++) WarWorlds.Set(w, x, y, 1, 2);
            for (int y = 0; y < 5; y++) for (int x = 25; x < 30; x++) WarWorlds.Set(w, x, y, 2, 1);
            WarWorlds.SyncPixels(w);
            Worlds.Recount(w);
            int center = 15 * 60 + 36;
            Worlds.War(w, 1, 2, kind == PeaceKinds.Claims ? WarGoals.Claims : WarGoals.Reparations, kind == PeaceKinds.Claims ? center : -1,
                WarSystem.CountCircle(w, center, 2, 0));
            Worlds.Division(w, 1, 0, 2, DivisionTypes.Infantry);
            Worlds.Division(w, 2, 0, 1, DivisionTypes.Infantry);
            Worlds.Division(w, 2, 1, 0, DivisionTypes.Infantry);
            Worlds.Order(w, 1, 2, Orders.Attack);
            Worlds.Order(w, 2, 1, Orders.Attack);
            w.Tick = 500;
            WarWorlds.Pending(w, 1, 2, kind);
            return w;
        }

        /// <summary>Оракул таблицы 4.3 по исходной карте.</summary>
        static (byte[] owner, byte[] core) Expected(World w, byte kind, int center)
        {
            var owner = w.Owner.ToArray();
            var core = w.Core.ToArray();
            const byte A = 1, D = 2;
            for (int p = 0; p < owner.Length; p++)
            {
                byte o = owner[p], c = core[p];
                if (o == A && c == D)
                {
                    if (kind == PeaceKinds.White || kind == PeaceKinds.Claims) core[p] = A;
                    else owner[p] = D;
                }
                else if (o == D && c == A)
                {
                    if (kind == PeaceKinds.White) core[p] = D;
                    else owner[p] = A;
                }
                else if (kind == PeaceKinds.Claims && o == D && c == D && WarSystem.InCircle(w, center, p))
                {
                    owner[p] = A;
                    core[p] = A;
                }
            }
            return (owner, core);
        }

        static void Check(byte kind)
        {
            using (var w = Make(kind))
            {
                int center = 15 * 60 + 36;
                var (owner, core) = Expected(w, kind, center);
                int income2 = EconomySystem.Income(w, 2);
                new WarSystem().Resolve(w);
                CollectionAssert.AreEqual(owner, w.Owner.ToArray(), "Owner by table 4.3");
                CollectionAssert.AreEqual(core, w.Core.ToArray(), "Core by table 4.3");
                Derived.AssertMatches(w, "after peace");

                var war = w.WarOf(1, 2);
                Assert.AreEqual(WarStatus.Truce, war.Status);
                Assert.AreEqual(500 + 3000, war.TruceUntil);
                Assert.AreEqual(0, war.Attacker);
                Assert.AreEqual(-1, war.ClaimCenter);
                Assert.AreEqual(0, war.PendingPeace);
                byte payer = kind == PeaceKinds.Reparations ? (byte)2 : kind == PeaceKinds.DefenseWin ? (byte)1 : (byte)0;
                Assert.AreEqual(payer, war.TributePayer, "tribute payer");
                Assert.AreEqual(payer == 0 ? 0 : 3 - payer, war.TributePayee, "tribute payee");
                Assert.AreEqual(payer == 0 ? 0 : 500 + 3000, war.TributeUntil);

                // Дивизии пары — в тыл сразу, приказы — оборона; тыловая не тронута.
                Assert.AreEqual(0, w.DivisionAt(w.DivisionSlot(1, 0)).Front);
                Assert.AreEqual(0, w.DivisionAt(w.DivisionSlot(2, 0)).Front);
                Assert.AreEqual(0, w.DivisionAt(w.DivisionSlot(1, 0)).ArriveTick);
                Assert.AreEqual(Orders.Defend, w.FrontOrderOf(1, 2));
                Assert.AreEqual(Orders.Defend, w.FrontOrderOf(2, 1));
                Assert.AreEqual(500, w.Nation(1).OwnWarEndTick, "attacker's own war ended");
                Assert.AreEqual(0, w.Nation(2).OwnWarEndTick);
                Assert.IsFalse(w.AtWar(1, 2));

                var e = w.WarEvents[w.WarEvents.Count - 1];
                Assert.AreEqual(WarEventKind.Peace, e.Kind);
                Assert.AreEqual((WarOutcome)(kind == PeaceKinds.White ? 1 : kind), e.Outcome);
                if (payer == 2) Assert.AreEqual(EconomySystem.Income(w, 2) * 5000 / 10000, e.Value);
                TestContext.WriteLine($"kind {kind}: settled {e.Pixels}, income of D before {income2}");
            }
        }

        [Test] public void White_SettlesBothSides() => Check(PeaceKinds.White);
        [Test] public void Claims_SettlesAttacker_ReturnsDefender_TakesDistrict() => Check(PeaceKinds.Claims);
        [Test] public void Reparations_ReturnsBoth_DefenderPays() => Check(PeaceKinds.Reparations);
        [Test] public void DefenseWin_ReturnsBoth_AttackerPays() => Check(PeaceKinds.DefenseWin);
    }

    /// <summary>02b acceptance 5: числа раздела 5 (709, 600, 640), сброс HoldAtt, мир на своих условиях, белый мир по сроку.</summary>
    public class WarGoalTests
    {
        // 100×80: A (1) — полоса x < 10 (800 px), остальное — D (2). Довоенная земля из примера: A 6 400, D 6 000; ClaimTotal 1 012.
        static World Make(byte goal, int center = -1)
        {
            var w = WarWorlds.Make(100, 80, 2, (x, y) => x < 10 ? (byte)1 : (byte)2, new[] { (2, 40), (95, 40) });
            Worlds.War(w, 1, 2, goal, center, goal == WarGoals.Claims ? 1012 : 0);
            var war = w.WarsRW[w.WarIndex(1, 2)];
            war.PreWarAtt = 6400;
            war.PreWarDef = 6000;
            war.StartTick = 0;
            w.WarsRW[w.WarIndex(1, 2)] = war;
            return w;
        }

        static void Second(World w, WarSystem s)
        {
            w.Tick = (w.Tick / 10) * 10 + 9;
            s.Second(w, 10);
            w.Tick += 1;
        }

        /// <summary>Первые k пикселей списка — за нацией holder (Core не меняется).</summary>
        static void Hold(World w, List<int> pixels, int k, byte holder, byte back)
        {
            for (int i = 0; i < pixels.Count; i++) w.OwnerRW[pixels[i]] = i < k ? holder : back;
            WarWorlds.SyncPixels(w);
        }

        [Test]
        public void Claims_709Of1012_HoldsAnd708DoesNot_ResetOnLoss_ThenPeaceOnOwnTerms()
        {
            int center = 40 * 100 + 50;
            using (var w = Make(WarGoals.Claims, center))
            {
                var circle = WarWorlds.Circle(w, center);
                Assert.AreEqual(1009, circle.Count, "lattice circle r = 18");
                var s = new WarSystem();
                Hold(w, circle, 708, 1, 2);
                Second(w, s);
                Assert.AreEqual(708, w.ClaimHeld(1, 2));
                Assert.AreEqual(0, w.WarOf(1, 2).HoldAtt, "708 × 10000 < 7000 × 1012");
                Hold(w, circle, 709, 1, 2);
                Second(w, s);
                Assert.AreEqual(1, w.WarOf(1, 2).HoldAtt, "709 holds");

                Hold(w, circle, 731, 1, 2);
                for (int i = 0; i < 19; i++) Second(w, s);
                Assert.AreEqual(20, w.WarOf(1, 2).HoldAtt);
                Assert.IsTrue(w.GoalFulfilled(1, 2, 1));
                Assert.IsFalse(w.GoalFulfilled(1, 2, 2));

                // Отбили 30 пикселей: 701 < 709 — счёт сначала, кнопка гаснет.
                Hold(w, circle, 701, 1, 2);
                Second(w, s);
                Assert.AreEqual(0, w.WarOf(1, 2).HoldAtt);
                Assert.IsFalse(w.GoalFulfilled(1, 2, 1));
                Assert.AreEqual(RejectReason.None, WarSystem.ValidatePropose(w, 1, 2), "not fulfilled: an ordinary offer");

                Hold(w, circle, 731, 1, 2);
                for (int i = 0; i < 20; i++) Second(w, s);
                Assert.AreEqual(RejectReason.None, WarSystem.ValidatePropose(w, 1, 2));
                WarSystem.ApplyPropose(w, 1, 2);
                Assert.AreEqual(PeaceKinds.Claims, w.WarOf(1, 2).PendingPeace, "peace on own terms without consent");
                Assert.AreEqual(0, w.WarOf(1, 2).OfferFrom);
            }
        }

        [Test]
        public void Reparations_600Of6000()
        {
            using (var w = Make(WarGoals.Reparations))
            {
                var dLand = new List<int>();
                for (int p = 0; p < w.PixelCount && dLand.Count < 600; p++) if (w.Owner[p] == 2 && p % 100 >= 10 && p % 100 < 60) dLand.Add(p);
                var s = new WarSystem();
                Hold(w, dLand, 599, 1, 2);
                Second(w, s);
                Assert.AreEqual(0, w.WarOf(1, 2).HoldAtt);
                Hold(w, dLand, 600, 1, 2);
                Assert.AreEqual(600, w.Occupied(1, 2));
                Second(w, s);
                Assert.AreEqual(1, w.WarOf(1, 2).HoldAtt);
                for (int i = 0; i < 19; i++) Second(w, s);
                WarSystem.ApplyPropose(w, 1, 2);
                Assert.AreEqual(PeaceKinds.Reparations, w.WarOf(1, 2).PendingPeace);
            }
        }

        [Test]
        public void DefenseWin_640Of6400_DefenderImposesPeace()
        {
            using (var w = Make(WarGoals.Claims, 40 * 100 + 50))
            {
                var aLand = new List<int>();
                for (int p = 0; p < w.PixelCount; p++) if (w.Owner[p] == 1 && p != w.Nation(1).Capital) aLand.Add(p);
                var s = new WarSystem();
                Hold(w, aLand, 639, 2, 1);
                Second(w, s);
                Assert.AreEqual(0, w.WarOf(1, 2).HoldDef);
                Hold(w, aLand, 640, 2, 1);
                for (int i = 0; i < 20; i++) Second(w, s);
                Assert.AreEqual(20, w.WarOf(1, 2).HoldDef);
                Assert.IsTrue(w.GoalFulfilled(1, 2, 2));
                Assert.AreEqual(RejectReason.None, WarSystem.ValidatePropose(w, 2, 1));
                WarSystem.ApplyPropose(w, 2, 1);
                Assert.AreEqual(PeaceKinds.DefenseWin, w.WarOf(1, 2).PendingPeace);
            }
        }

        [Test]
        public void MaxWarTicks_WhitePeaceByItself_LoggedAsTimer()
        {
            using (var w = Make(WarGoals.Reparations))
            {
                var s = new WarSystem();
                w.Tick = 4799;
                s.Second(w, 10);
                Assert.AreEqual(0, w.WarOf(1, 2).PendingPeace, "4799 < 4800");
                w.Tick = 4809;
                s.Second(w, 10);
                Assert.AreEqual(PeaceKinds.White, w.WarOf(1, 2).PendingPeace);
                w.Tick = 4810;
                s.Resolve(w);
                Assert.AreEqual(WarStatus.Truce, w.WarOf(1, 2).Status);
                Assert.AreEqual(WarOutcome.WhiteByTimer, w.WarEvents[w.WarEvents.Count - 1].Outcome);
            }
        }

        [Test]
        public void Offer_ExpiresAfterOfferTicks_CooldownPerSide()
        {
            using (var w = Make(WarGoals.Reparations))
            {
                var s = new WarSystem();
                w.Tick = 100;
                WarSystem.ApplyPropose(w, 1, 2);
                Assert.AreEqual(1, w.WarOf(1, 2).OfferFrom);
                Assert.AreEqual(200, w.WarOf(1, 2).OfferUntil);
                Assert.AreEqual(RejectReason.PeaceCooldown, WarSystem.ValidatePropose(w, 1, 2), "own cooldown");
                Assert.AreEqual(RejectReason.None, WarSystem.ValidatePropose(w, 2, 1), "defender may accept by proposing");
                Assert.AreEqual(RejectReason.None, WarSystem.ValidateAnswer(w, 2, 1, 0));
                w.Tick = 209;
                s.Second(w, 10);
                Assert.AreEqual(0, w.WarOf(1, 2).OfferFrom, "expired");
                Assert.AreEqual(RejectReason.NoOffer, WarSystem.ValidateAnswer(w, 2, 1, 1));
                Assert.AreEqual(RejectReason.None, WarSystem.ValidatePropose(w, 2, 1), "the other side has its own cooldown");
                w.Tick = 699;
                Assert.AreEqual(RejectReason.PeaceCooldown, WarSystem.ValidatePropose(w, 1, 2));
                w.Tick = 700;
                Assert.AreEqual(RejectReason.None, WarSystem.ValidatePropose(w, 1, 2));
            }
        }
    }

    /// <summary>02b acceptance 6: карманы при мире.</summary>
    public class PocketTests
    {
        // 30×10: C (3) x 0..2, столица (1, 5); A (1) x 3..19, столица (15, 5); D (2) x 20..29, столица (25, 5).
        // Карман D (8, 4..5) в A → к A; компонента D (3, 7..8) касается C → остаётся; A (24, 1) в D → к D; основная D со столицей остаётся.
        [Test]
        public void Pockets_GoToTheSurroundingSide_NotIfTouchingThirdOrWithCapital()
        {
            Func<int, int, byte> owner = (x, y) =>
            {
                if (x == 8 && (y == 4 || y == 5)) return 2;
                if (x == 3 && (y == 7 || y == 8)) return 2;
                if (x == 24 && y == 1) return 1;
                return x < 3 ? (byte)3 : x < 20 ? (byte)1 : (byte)2;
            };
            using (var w = WarWorlds.Make(30, 10, 3, owner, new[] { (15, 5), (25, 5), (1, 5) }))
            {
                Worlds.War(w, 1, 2);
                WarWorlds.Pending(w, 1, 2, PeaceKinds.White);
                new WarSystem().Resolve(w);
                byte At(int x, int y) => w.Owner[y * 30 + x];
                byte CoreAt(int x, int y) => w.Core[y * 30 + x];
                Assert.AreEqual(1, At(8, 4));
                Assert.AreEqual(1, At(8, 5));
                Assert.AreEqual(1, CoreAt(8, 5), "pocket: Owner = Core");
                Assert.AreEqual(2, At(3, 7), "touches a third nation — stays");
                Assert.AreEqual(2, At(3, 8));
                Assert.AreEqual(2, At(24, 1), "A pocket inside D goes to D");
                Assert.AreEqual(2, CoreAt(24, 1));
                Assert.AreEqual(2, At(25, 5), "main D with capital stays");
                Assert.AreEqual(2, At(20, 0));
                Assert.AreEqual(3, w.WarEvents[w.WarEvents.Count - 1].Pixels, "two D pixels + one A pixel by pockets");
                Derived.AssertMatches(w, "after pockets");
            }
        }

        [Test]
        public void SurroundedNationWithCapital_IsNotAPocket()
        {
            // D (2) — блок 3×3 с центром-столицей внутри A.
            Func<int, int, byte> owner = (x, y) => x >= 10 && x < 13 && y >= 3 && y < 6 ? (byte)2 : (byte)1;
            using (var w = WarWorlds.Make(20, 10, 2, owner, new[] { (2, 2), (11, 4) }))
            {
                Worlds.War(w, 1, 2);
                WarWorlds.Pending(w, 1, 2, PeaceKinds.White);
                new WarSystem().Resolve(w);
                Assert.AreEqual(9, w.Nation(2).Pixels);
            }
        }
    }

    /// <summary>02b §4.4: репарации — половина валового дохода, не уводят в минус, срок, плательщик мёртв.</summary>
    public class TributeTests
    {
        static World Make()
        {
            var w = WarWorlds.Make(10, 10, 2, (x, y) => x < 5 ? (byte)1 : (byte)2, new[] { (1, 5), (8, 5) });
            var nat = w.NationsRW;
            var a = nat[0]; a.Pixels = 6400; a.Gold = 0; nat[0] = a;
            var d = nat[1]; d.Pixels = 6000; d.Gold = 100000; nat[1] = d;
            w.WarsRW[w.WarIndex(1, 2)] = new War { Status = WarStatus.Truce, ClaimCenter = -1, TruceUntil = 3000, TributePayer = 2, TributePayee = 1, TributeUntil = 3000 };
            return w;
        }

        [Test]
        public void HalfOfIncome_250PerSecond()
        {
            using (var w = Make())
            {
                Assert.AreEqual(500, EconomySystem.Income(w, 2));
                w.Tick = 9;
                new EconomySystem().Tick(w, new CommandContext(TestData.Config(), w.Rules, 0));
                Assert.AreEqual(100000 + 500 - 250, w.Nation(2).Gold);
                Assert.AreEqual(520 + 250, w.Nation(1).Gold);
                Assert.AreEqual(250, w.TributePaid);
            }
        }

        [Test]
        public void PaysWhatItHas_NoDesertion()
        {
            using (var w = Make())
            {
                w.Rules.War.Tribute_bp = 10000;
                var nat = w.NationsRW;
                var d = nat[1]; d.Gold = 0; nat[1] = d;
                Worlds.Division(w, 2, 0, 0, DivisionTypes.Infantry); // содержание 30
                w.Tick = 9;
                new EconomySystem().Tick(w, new CommandContext(TestData.Config(), w.Rules, 0));
                Assert.AreEqual(0, w.Nation(2).Gold);
                Assert.AreEqual(10000, w.DivisionAt(w.DivisionSlot(2, 0)).Strength_bp, "no desertion from tribute");
                Assert.AreEqual(520 + 470, w.Nation(1).Gold);
            }
        }

        [Test]
        public void Expired_OrPayerDead_Removed()
        {
            using (var w = Make())
            {
                w.Tick = 3009;
                new EconomySystem().Tick(w, new CommandContext(TestData.Config(), w.Rules, 0));
                Assert.AreEqual(0, w.WarOf(1, 2).TributePayer);
                Assert.AreEqual(100000 + 500, w.Nation(2).Gold);
            }
            using (var w = Make())
            {
                FrontSystem.KillNation(w, 2);
                Assert.AreEqual(0, w.WarOf(1, 2).TributePayer, "dead payer: tribute removed");
                Assert.AreEqual(WarStatus.Peace, w.WarOf(1, 2).Status);
            }
        }
    }

    /// <summary>02b acceptance 3, 4, 8 на настоящих картах.</summary>
    public class WarMatchTests
    {
        /// <summary>Без войны пиксели между нациями не переходят 3 000 тиков (ИИ выключен); расползание по ничьей идёт.</summary>
        [Test]
        public void NoWar_NoFlipsBetweenNations_ExpansionStillWorks()
        {
            for (ulong seed = 1; seed <= 10; seed++)
                using (var sim = TestData.IdleMatch(seed))
                {
                    var w = sim.World;
                    int start = 0;
                    for (int i = 0; i < w.NationCount; i++) start += w.Nations[i].Pixels;
                    for (int t = 0; t < 3000; t++)
                    {
                        sim.Step();
                        var flips = w.FlipHistory;
                        for (int i = 0; i < flips.Length; i++)
                            if (flips[i] != 0) Assert.Fail($"seed {seed}, tick {w.Tick}: a pixel changed hands without war");
                    }
                    int end = 0;
                    for (int i = 0; i < w.NationCount; i++) end += w.Nations[i].Pixels;
                    Assert.Greater(end, start, $"seed {seed}: expansion");
                    Derived.AssertMatches(w, $"seed {seed}");
                }
        }

        /// <summary>Объявление → предложение → принятие: мир, дивизии в тыл, приказы — оборона, перемирие до TruceUntil, потом снова можно.</summary>
        [Test]
        public void DeclareOfferAccept_Truce_ThenPeaceAgain()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, ValidationTests.ContactWarmup);
                var w = sim.World;
                var (a, d) = ValidationTests.Contact(w);
                sim.EnqueueFromUi(CommandType.AssignDivision, a, 0, d);
                sim.EnqueueFromUi(CommandType.SetFrontOrder, a, d, Orders.Attack);
                sim.EnqueueFromUi(CommandType.DeclareWar, a, d, 0);
                sim.Step();
                sim.Step();
                Assert.AreEqual(0, sim.Rejected.Count);
                Assert.IsTrue(w.AtWar(a, d));
                Assert.AreEqual(a, w.WarOf(a, d).Attacker);
                Assert.AreEqual(WarGoals.Reparations, w.WarOf(a, d).Goal);
                TestUtil.Run(sim, 400); // дивизия дошла и наступает
                Assert.IsTrue(w.Attacking(w.DivisionAt(w.DivisionSlot(a, 0))));

                sim.EnqueueFromUi(CommandType.ProposePeace, a, d, 0);
                sim.Step();
                sim.Step();
                Assert.AreEqual(a, w.WarOf(a, d).OfferFrom);
                sim.EnqueueFromUi(CommandType.AnswerPeace, d, a, 1);
                sim.Step();
                sim.Step();
                Assert.AreEqual(0, sim.Rejected.Count);
                var war = w.WarOf(a, d);
                Assert.AreEqual(WarStatus.Truce, war.Status);
                int peaceTick = war.TruceUntil - 3000;
                Assert.AreEqual(0, w.DivisionAt(w.DivisionSlot(a, 0)).Front, "division to rear at once");
                Assert.AreEqual(Orders.Defend, w.FrontOrderOf(a, d));
                Assert.AreEqual(RejectReason.TruceActive, WarSystem.ValidateDeclare(w, a, d, 0));
                Assert.AreEqual(RejectReason.TruceActive, WarSystem.ValidateDeclare(w, d, a, 0));
                Derived.AssertMatches(w, "after white peace");
                while (w.Tick < war.TruceUntil) sim.Step();
                Assert.AreEqual(WarStatus.Truce, w.WarOf(a, d).Status, "truce ends on the next second step");
                TestUtil.Run(sim, 10);
                Assert.AreEqual(WarStatus.Peace, w.WarOf(a, d).Status);
                Assert.AreEqual(RejectReason.None, WarSystem.ValidateDeclare(w, d, a, 0));
                var peace = w.WarEvents.FindLast(e => e.Kind == WarEventKind.Peace);
                Assert.AreEqual(WarOutcome.WhiteByOffer, peace.Outcome);
                Assert.AreEqual(peaceTick, peace.Tick);
            }
        }

        /// <summary>Дивизии у мирной границы: шанса нет, потерь нет, организация как на фронте, при объявлении работают сразу.</summary>
        [Test]
        public void DivisionsAtPeacefulBorder_NoLosses_WorkAtOnceOnDeclaration()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, ValidationTests.ContactWarmup);
                var w = sim.World;
                var (a, d) = ValidationTests.Contact(w);
                sim.EnqueueFromUi(CommandType.AssignDivision, a, 0, d);
                sim.EnqueueFromUi(CommandType.SetFrontOrder, a, d, Orders.Attack);
                TestUtil.Run(sim, 320);
                var v = w.DivisionAt(w.DivisionSlot(a, 0));
                Assert.IsTrue(w.OnFront(v));
                Assert.IsFalse(w.Attacking(v), "no war — no attack");
                Assert.AreEqual(10000, v.Strength_bp);
                Assert.AreEqual(10000, v.Org_bp);
                for (int tr = 0; tr < 3; tr++) Assert.AreEqual(0, w.AttPow(a, d, tr));
                sim.EnqueueFromUi(CommandType.DeclareWar, a, d, 0);
                sim.Step();
                sim.Step();
                Assert.IsTrue(w.Attacking(w.DivisionAt(w.DivisionSlot(a, 0))), "works at once, no transfer");
                Assert.Greater(w.AttPow(a, d, 0), 0);
            }
        }
    }
}
