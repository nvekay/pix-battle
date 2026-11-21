using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>
    /// Спека 01c, acceptance 2: на каждую причину DivisionInvalid, DivisionLimit, DivisionTypeInvalid, NotEnoughGold, TargetInvalid,
    /// NoContact, AlreadyThere, OrderInvalid, Obsolete (5, 6, 7) — команда отклонена, мир не изменился.
    /// </summary>
    public class ValidationTests
    {
        static Command Cmd(CommandType type, byte nation, int a = 0, int b = 0) => new Command { Type = type, Nation = nation, A = a, B = b };

        /// <summary>
        /// Две одинаковые партии: в одну на следующий тик идёт команда, в другую нет. setup применяется к обеим.
        /// После тика команда отклонена с ожидаемой причиной, хеши обеих партий равны.
        /// </summary>
        static void Rejects(RejectReason expected, Func<World, Command> make, Action<World> setup = null, int warmup = 0, ulong seed = 3)
        {
            using (var withCmd = TestData.IdleMatch(seed))
            using (var without = TestData.IdleMatch(seed))
            {
                TestUtil.Run(withCmd, warmup);
                TestUtil.Run(without, warmup);
                setup?.Invoke(withCmd.World);
                setup?.Invoke(without.World);
                var c = make(withCmd.World);
                c.Tick = withCmd.World.Tick;
                Assert.AreEqual(EnqueueResult.Accepted, withCmd.Enqueue(c));
                withCmd.Step();
                without.Step();
                Assert.AreEqual(1, withCmd.Rejected.Count, "command was not rejected");
                Assert.AreEqual(expected, withCmd.Rejected[0].Reason);
                Assert.AreEqual(without.ComputeHash(), withCmd.ComputeHash(), "world changed");
            }
        }

        static void SetNation(World w, byte id, Func<NationState, NationState> f)
        {
            var nat = w.NationsRW;
            nat[id - 1] = f(nat[id - 1]);
        }

        static void Rich(World w, byte id) => SetNation(w, id, n => { n.Gold = 10000000; return n; });

        /// <summary>Первая пара соседей с общей границей после warmup.</summary>
        internal static (byte a, byte d) Contact(World w)
        {
            for (int a = 1; a <= w.NationCount; a++)
            for (int d = 1; d <= w.NationCount; d++)
                if (a != d && w.FrontLength(a, d) > 0 && w.Nation((byte)a).Alive != 0 && w.Nation((byte)d).Alive != 0)
                    return ((byte)a, (byte)d);
            Assert.Fail("no contact");
            return (0, 0);
        }

        /// <summary>Сосед без общей границы с нацией a.</summary>
        internal static byte NoContact(World w, byte a)
        {
            for (int d = 1; d <= w.NationCount; d++)
                if (d != a && w.FrontLength(a, d) <= 0) return (byte)d;
            Assert.Fail("everyone is in contact");
            return 0;
        }

        internal const int ContactWarmup = 4000;

        [Test] public void NationZero() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.RecruitDivision, 0, 0));
        [Test] public void NationOutOfRange() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.RecruitDivision, 9, 0));
        [Test] public void NationDead() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.RecruitDivision, 2, 0),
            w => SetNation(w, 2, n => { n.Alive = 0; return n; }));

        [Test] public void Obsolete_Recruit() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.Recruit, 1));
        [Test] public void Obsolete_BuildFort() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.BuildFort, 1, w.Nation(1).Capital));
        [Test] public void Obsolete_Strike() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.Strike, 1, 2));
        [Test] public void Obsolete_RecruitUnit() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.RecruitUnit, 1, 0, 0), w => Rich(w, 1));
        [Test] public void Obsolete_AssignFormation() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.AssignFormation, 1, 0, 0));
        [Test] public void Obsolete_SetOrder() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.SetOrder, 1, 0, 1));

        [Test] public void NotEnoughGold_Infantry() => Rejects(RejectReason.NotEnoughGold, w => Cmd(CommandType.RecruitDivision, 1, DivisionTypes.Infantry),
            w => SetNation(w, 1, n => { n.Gold = 44999; return n; }));
        [Test] public void NotEnoughGold_Armor() => Rejects(RejectReason.NotEnoughGold, w => Cmd(CommandType.RecruitDivision, 1, DivisionTypes.Armor),
            w => SetNation(w, 1, n => { n.Gold = 94999; return n; }));

        [Test] public void DivisionTypeInvalid() => Rejects(RejectReason.DivisionTypeInvalid, w => Cmd(CommandType.RecruitDivision, 1, 2), w => Rich(w, 1));
        [Test] public void DivisionTypeNegative() => Rejects(RejectReason.DivisionTypeInvalid, w => Cmd(CommandType.RecruitDivision, 1, -1), w => Rich(w, 1));

        [Test] public void DivisionLimit() => Rejects(RejectReason.DivisionLimit, w => Cmd(CommandType.RecruitDivision, 1, 0),
            w =>
            {
                Rich(w, 1);
                for (int k = 1; k < 32; k++) DivisionSystem.Create(w, 1, k, DivisionTypes.Infantry, 0);
            });

        [Test] public void DivisionInvalid_Empty() => Rejects(RejectReason.DivisionInvalid, w => Cmd(CommandType.AssignDivision, 1, 1, 0));
        [Test] public void DivisionInvalid_Negative() => Rejects(RejectReason.DivisionInvalid, w => Cmd(CommandType.AssignDivision, 1, -1, 0));
        [Test] public void DivisionInvalid_OutOfRange() => Rejects(RejectReason.DivisionInvalid, w => Cmd(CommandType.AssignDivision, 1, 32, 0));

        [Test] public void OrderInvalid() => Rejects(RejectReason.OrderInvalid, w => Cmd(CommandType.SetFrontOrder, 1, 2, 2));

        [Test] public void TargetSelf() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignDivision, 1, 0, 1));
        [Test] public void TargetOutOfRange() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignDivision, 1, 0, 9));
        [Test] public void TargetNegative() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignDivision, 1, 0, -1));
        [Test] public void TargetDead() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignDivision, 1, 0, 2),
            w => SetNation(w, 2, n => { n.Alive = 0; return n; }));
        [Test] public void FrontOrder_TargetSelf() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.SetFrontOrder, 1, 1, 1));
        [Test] public void FrontOrder_TargetZero() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.SetFrontOrder, 1, 0, 1));
        [Test] public void FrontOrder_TargetOutOfRange() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.SetFrontOrder, 1, 9, 1));
        [Test] public void FrontOrder_TargetDead() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.SetFrontOrder, 1, 2, 1),
            w => SetNation(w, 2, n => { n.Alive = 0; return n; }));

        [Test] public void NoContact_AtStart() => Rejects(RejectReason.NoContact, w => Cmd(CommandType.AssignDivision, 1, 0, 2));

        [Test] public void NoContact_AfterWarmup() => Rejects(RejectReason.NoContact,
            w => { var (a, _) = Contact(w); return Cmd(CommandType.AssignDivision, a, 0, NoContact(w, a)); }, null, ContactWarmup);

        [Test] public void AlreadyThere() => Rejects(RejectReason.AlreadyThere,
            w => { var (a, d) = Contact(w); return Cmd(CommandType.AssignDivision, a, 0, d); },
            w =>
            {
                var (a, d) = Contact(w);
                var v = w.DivisionsRW[w.DivisionSlot(a, 0)];
                v.Front = d;
                v.ArriveTick = 0;
                w.DivisionsRW[w.DivisionSlot(a, 0)] = v;
            }, ContactWarmup);

        [Test]
        public void RulesDisabled_InFoundationWorld()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1))
            {
                var types = new[]
                {
                    CommandType.Recruit, CommandType.BuildFort, CommandType.Strike, CommandType.RecruitUnit, CommandType.AssignFormation,
                    CommandType.SetOrder, CommandType.RecruitDivision, CommandType.AssignDivision, CommandType.SetFrontOrder,
                };
                foreach (var t in types)
                    sim.Enqueue(new Command { Tick = 0, Type = t, Nation = 1, A = 0 });
                sim.Step();
                Assert.AreEqual(types.Length, sim.Rejected.Count);
                foreach (var r in sim.Rejected) Assert.AreEqual(RejectReason.RulesDisabled, r.Reason);
            }
        }

        [Test]
        public void EnumValues_AreFrozen()
        {
            Assert.AreEqual(2, (byte)CommandType.Recruit);
            Assert.AreEqual(3, (byte)CommandType.BuildFort);
            Assert.AreEqual(4, (byte)CommandType.Strike);
            Assert.AreEqual(5, (byte)CommandType.RecruitUnit);
            Assert.AreEqual(6, (byte)CommandType.AssignFormation);
            Assert.AreEqual(7, (byte)CommandType.SetOrder);
            Assert.AreEqual(8, (byte)CommandType.RecruitDivision);
            Assert.AreEqual(9, (byte)CommandType.AssignDivision);
            Assert.AreEqual(10, (byte)CommandType.SetFrontOrder);
            Assert.AreEqual(4, (byte)RejectReason.NationInvalid);
            Assert.AreEqual(8, (byte)RejectReason.TooCloseToFort);
            Assert.AreEqual(9, (byte)RejectReason.FortLimit);
            Assert.AreEqual(13, (byte)RejectReason.RulesDisabled);
            Assert.AreEqual(14, (byte)RejectReason.DebugOnly);
            Assert.AreEqual(15, (byte)RejectReason.Obsolete);
            Assert.AreEqual(16, (byte)RejectReason.FormationInvalid);
            Assert.AreEqual(17, (byte)RejectReason.FormationFull);
            Assert.AreEqual(18, (byte)RejectReason.FormationLimit);
            Assert.AreEqual(19, (byte)RejectReason.UnitTypeInvalid);
            Assert.AreEqual(20, (byte)RejectReason.AlreadyThere);
            Assert.AreEqual(21, (byte)RejectReason.OrderInvalid);
            Assert.AreEqual(22, (byte)RejectReason.DivisionInvalid);
            Assert.AreEqual(23, (byte)RejectReason.DivisionLimit);
            Assert.AreEqual(24, (byte)RejectReason.DivisionTypeInvalid);
            Assert.AreEqual(2, (byte)EndReason.Conquest);
            Assert.AreEqual(4, (byte)EndReason.HumanDefeated);
            Assert.AreEqual(1, (byte)Terrain.Land);
            Assert.AreEqual(3, (byte)Terrain.Mountains);
        }
    }

    public class GameCommandApplyTests
    {
        static void Gold(World w, byte id, int gold)
        {
            var nat = w.NationsRW;
            var n = nat[id - 1];
            n.Gold = gold;
            nat[id - 1] = n;
        }

        [Test]
        public void Start_OneInfantryDivision_ReadyInRear()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                var w = sim.World;
                for (byte id = 1; id <= 8; id++)
                {
                    var v = w.DivisionAt(w.DivisionSlot(id, 0));
                    Assert.AreEqual(1, v.Alive);
                    Assert.AreEqual(id, v.Owner);
                    Assert.AreEqual(DivisionTypes.Infantry, v.Type);
                    Assert.AreEqual(0, v.Front);
                    Assert.AreEqual(0, v.ReadyTick);
                    Assert.AreEqual(0, v.ArriveTick);
                    Assert.AreEqual(10000, v.Strength_bp);
                    Assert.AreEqual(10000, v.Org_bp);
                    Assert.AreEqual(0, v.Exhausted);
                    Assert.AreEqual(1, w.Nation(id).Divisions);
                    Assert.AreEqual(0, w.DivisionAt(w.DivisionSlot(id, 1)).Alive);
                    for (int d = 1; d <= 8; d++) Assert.AreEqual(Orders.Defend, w.FrontOrderOf(id, d));
                }
            }
        }

        [Test]
        public void RecruitDivision_BothTypes_FirstFreeSlots_Training()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                var w = sim.World;
                Gold(w, 1, 1000000);
                sim.EnqueueFromUi(CommandType.RecruitDivision, 1, DivisionTypes.Armor, 0);
                sim.EnqueueFromUi(CommandType.RecruitDivision, 1, DivisionTypes.Infantry, 0);
                int applyTick = w.Tick + 1;
                sim.Step();
                sim.Step();
                Assert.AreEqual(0, sim.Rejected.Count);
                var me = w.Nation(1);
                Assert.AreEqual(1000000 - 95000 - 45000, me.Gold); // секунда ещё не прошла
                Assert.AreEqual(3, me.Divisions);
                Assert.AreEqual(1, me.RecruitedArmorDiv);
                Assert.AreEqual(1, me.RecruitedInfDiv);
                var armor = w.DivisionAt(w.DivisionSlot(1, 1));
                var inf = w.DivisionAt(w.DivisionSlot(1, 2));
                Assert.AreEqual(DivisionTypes.Armor, armor.Type);
                Assert.AreEqual(DivisionTypes.Infantry, inf.Type);
                Assert.AreEqual(applyTick + 300, armor.ReadyTick);
                Assert.IsTrue(w.Training(armor));
                Assert.AreEqual(0, armor.Front);
                Assert.AreEqual(10000, armor.Org_bp);
            }
        }

        [Test]
        public void TwoPurchases_SecondRejected_WhenGoldRunsOut()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                Gold(sim.World, 1, 50000);
                // 500 золота: пехотная (450) проходит, танковая (950) уже нет.
                sim.EnqueueFromUi(CommandType.RecruitDivision, 1, DivisionTypes.Infantry, 0);
                sim.EnqueueFromUi(CommandType.RecruitDivision, 1, DivisionTypes.Armor, 0);
                sim.Step();
                sim.Step();
                Assert.AreEqual(1, sim.Rejected.Count);
                Assert.AreEqual(RejectReason.NotEnoughGold, sim.Rejected[0].Reason);
                Assert.AreEqual(DivisionTypes.Armor, sim.Rejected[0].Command.A);
            }
        }

        [Test]
        public void SetFrontOrder_WithoutContact_IsStored()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                Assert.AreEqual(0, sim.World.FrontLength(1, 2));
                sim.EnqueueFromUi(CommandType.SetFrontOrder, 1, 2, Orders.Attack);
                sim.Step();
                sim.Step();
                Assert.AreEqual(0, sim.Rejected.Count);
                Assert.AreEqual(Orders.Attack, sim.World.FrontOrderOf(1, 2));
                Assert.AreEqual(Orders.Defend, sim.World.FrontOrderOf(2, 1), "the other side keeps its own order");
            }
        }

        /// <summary>Спека 01c, acceptance 5: в пути и в подготовке сила не считается; назначенная заранее прибывает в ReadyTick + transferTicks; в тыл — сразу.</summary>
        [Test]
        public void TrainingAndTransfer_NoPowerUntilReadyAndArrive_RearIsImmediate()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, ValidationTests.ContactWarmup);
                var w = sim.World;
                var (a, d) = ValidationTests.Contact(w);
                Gold(w, a, 1000000);
                int garrisonDef = w.Rules.Front.GarrisonPerEdge; // на равнине оборона без дивизий = гарнизон
                sim.EnqueueFromUi(CommandType.RecruitDivision, a, DivisionTypes.Infantry, 0);
                int recruitTick = w.Tick + 1;
                sim.Step();
                sim.Step(); // recruitTick: набрана в слот 1
                sim.EnqueueFromUi(CommandType.AssignDivision, a, 1, d); // назначена заранее, пока готовится
                sim.Step();
                sim.Step(); // тик применения назначения
                Assert.AreEqual(0, sim.Rejected.Count);
                var v = w.DivisionAt(w.DivisionSlot(a, 1));
                Assert.AreEqual(d, v.Front);
                Assert.AreEqual(recruitTick + 300, v.ReadyTick);
                Assert.AreEqual(recruitTick + 300 + 300, v.ArriveTick, "leaves when ready, arrives transferTicks later");
                Assert.IsFalse(w.OnFront(v));
                Assert.IsTrue(w.InTransit(v));
                while (w.Tick < v.ArriveTick) sim.Step(); // последний посчитанный тик — ArriveTick − 1
                if (w.FrontLength(a, d) > 0) Assert.AreEqual(garrisonDef, w.DefPow(a, d, 0), "training and in transit: garrison only");
                sim.Step(); // тик ArriveTick
                Assert.IsTrue(w.OnFront(w.DivisionAt(w.DivisionSlot(a, 1))));
                Assert.Greater(w.DefPow(a, d, 0), garrisonDef, "arrived: division defends");

                sim.EnqueueFromUi(CommandType.AssignDivision, a, 1, 0);
                sim.Step();
                sim.Step();
                v = w.DivisionAt(w.DivisionSlot(a, 1));
                Assert.AreEqual(0, v.Front);
                Assert.AreEqual(0, v.ArriveTick);
                Assert.AreEqual(garrisonDef, w.DefPow(a, d, 0));
            }
        }

        [Test]
        public void ReadyDivision_Transfer_ArrivesAfterTransferTicks_AndReassignInTransitRestarts()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, ValidationTests.ContactWarmup);
                var w = sim.World;
                var (a, d) = ValidationTests.Contact(w);
                sim.EnqueueFromUi(CommandType.AssignDivision, a, 0, d);
                int applyTick = w.Tick + 1;
                sim.Step();
                sim.Step();
                Assert.AreEqual(applyTick + 300, w.DivisionAt(w.DivisionSlot(a, 0)).ArriveTick);
                TestUtil.Run(sim, 100);
                sim.EnqueueFromUi(CommandType.AssignDivision, a, 0, d);
                applyTick = w.Tick + 1;
                sim.Step();
                sim.Step();
                Assert.AreEqual(0, sim.Rejected.Count);
                Assert.AreEqual(applyTick + 300, w.DivisionAt(w.DivisionSlot(a, 0)).ArriveTick);
            }
        }

        /// <summary>Пачка назначений в один тик: дивизия, уже стоящая на фронте, отклоняется AlreadyThere, остальные проходят.</summary>
        [Test]
        public void BatchAssign_OneTick_AlreadyThereOnlyForTheOneOnFront()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, ValidationTests.ContactWarmup);
                var w = sim.World;
                var (a, d) = ValidationTests.Contact(w);
                DivisionSystem.Create(w, a, 1, DivisionTypes.Infantry, 0);
                DivisionSystem.Create(w, a, 2, DivisionTypes.Armor, 0);
                var v = w.DivisionsRW[w.DivisionSlot(a, 1)];
                v.Front = d;
                w.DivisionsRW[w.DivisionSlot(a, 1)] = v;
                for (int k = 0; k < 3; k++) sim.EnqueueFromUi(CommandType.AssignDivision, a, k, d);
                sim.Step();
                sim.Step();
                Assert.AreEqual(1, sim.Rejected.Count);
                Assert.AreEqual(RejectReason.AlreadyThere, sim.Rejected[0].Reason);
                Assert.AreEqual(1, sim.Rejected[0].Command.A);
                Assert.AreEqual(d, w.DivisionAt(w.DivisionSlot(a, 0)).Front);
                Assert.AreEqual(d, w.DivisionAt(w.DivisionSlot(a, 2)).Front);
            }
        }
    }

    /// <summary>Спека 01c, acceptance 4: секунда раздела 5, гистерезис, orgMult; гибель дивизии, дезертирство по 4.4.</summary>
    public class OrgAndLossTests
    {
        static Division Div(World w, byte owner, int k) => w.DivisionAt(w.DivisionSlot(owner, k));

        static void SetOrg(World w, byte owner, int k, int org, byte exhausted)
        {
            var v = w.DivisionsRW[w.DivisionSlot(owner, k)];
            v.Org_bp = (short)org;
            v.Exhausted = exhausted;
            w.DivisionsRW[w.DivisionSlot(owner, k)] = v;
        }

        [Test]
        public void Section5_Second_Org155And96_Strength51And19()
        {
            using (var w = Worlds.Section5(gold: 0))
            {
                var ds = new DivisionSystem();
                w.Tick = 9; // последний тик секунды
                ds.FillPow(w);
                Assert.AreEqual(18912, w.WeightedAttack(1, 2));
                Assert.AreEqual(19560, w.WeightedDefense(2, 1));
                ds.PerSecond(w, 10);
                for (int k = 0; k < 4; k++)
                {
                    Assert.AreEqual(10000 - 155, Div(w, 1, k).Org_bp, $"red org, division {k}");
                    Assert.AreEqual(10000 - 51, Div(w, 1, k).Strength_bp, $"red strength, division {k}");
                }
                for (int k = 0; k < 3; k++)
                {
                    Assert.AreEqual(10000 - 96, Div(w, 2, k).Org_bp, $"blue org, division {k}");
                    Assert.AreEqual(10000 - 19, Div(w, 2, k).Strength_bp, $"blue strength, division {k}");
                }
                Assert.AreEqual(0, w.Nation(1).Gold, "no gold — no reinforcement");
            }
        }

        [Test]
        public void Section5_Second_Reinforcement_PricedByDivision()
        {
            using (var w = Worlds.Section5())
            {
                var ds = new DivisionSystem();
                w.Tick = 9;
                ds.FillPow(w);
                int gold1 = w.Nation(1).Gold, gold2 = w.Nation(2).Gold;
                ds.PerSecond(w, 10);
                // Красные: −51, потом +30 → 9979. Синие: −19, потом +19 → 10000.
                for (int k = 0; k < 4; k++) Assert.AreEqual(9979, Div(w, 1, k).Strength_bp, $"red {k}");
                for (int k = 0; k < 3; k++) Assert.AreEqual(10000, Div(w, 2, k).Strength_bp, $"blue {k}");
                Assert.AreEqual(168, gold1 - w.Nation(1).Gold, "red: 2 × (95000 × 30 / 10000 × 0,2 = 57) + 2 × 27");
                Assert.AreEqual(51, gold2 - w.Nation(2).Gold, "blue: 3 × (45000 × 19 / 10000 × 0,2 = 17)");
            }
        }

        [Test]
        public void NotLastTickOfSecond_NothingHappens()
        {
            using (var w = Worlds.Section5())
            {
                var ds = new DivisionSystem();
                w.Tick = 8;
                ds.FillPow(w);
                ds.PerSecond(w, 10);
                Assert.AreEqual(10000, Div(w, 1, 0).Strength_bp);
                Assert.AreEqual(10000, Div(w, 1, 0).Org_bp);
            }
        }

        [Test]
        public void OrgMult_At4700_Is7350()
        {
            var army = Rules.Default(2).Army;
            Assert.AreEqual(7350, DivisionSystem.OrgMult(army, 4700));
            Assert.AreEqual(10000, DivisionSystem.OrgMult(army, 10000));
            Assert.AreEqual(5000, DivisionSystem.OrgMult(army, 0));
        }

        [Test]
        public void Hysteresis_Below1500Exhausted_5999StillExhausted_6000Resumes()
        {
            using (var w = Worlds.Section5(gold: 0))
            {
                var ds = new DivisionSystem();
                // Красные k0: 1655 − 155 = 1500 — ещё нет; k1: 1654 − 155 = 1499 — выдохлась.
                SetOrg(w, 1, 0, 1655, 0);
                SetOrg(w, 1, 1, 1654, 0);
                // Синие k3, k4 в тылу (+250), силу фронта не меняют: 5749 → 5999 ещё выдохлась; 5750 → 6000 возобновила.
                Worlds.Division(w, 2, 3, 0, DivisionTypes.Infantry, org: 5749);
                Worlds.Division(w, 2, 4, 0, DivisionTypes.Infantry, org: 5750);
                SetOrg(w, 2, 3, 5749, 1);
                SetOrg(w, 2, 4, 5750, 1);
                w.Tick = 9;
                ds.FillPow(w);
                ds.PerSecond(w, 10);
                Assert.AreEqual(1500, Div(w, 1, 0).Org_bp);
                Assert.AreEqual(0, Div(w, 1, 0).Exhausted);
                Assert.AreEqual(1499, Div(w, 1, 1).Org_bp);
                Assert.AreEqual(1, Div(w, 1, 1).Exhausted);
                Assert.AreEqual(5999, Div(w, 2, 3).Org_bp);
                Assert.AreEqual(1, Div(w, 2, 3).Exhausted);
                Assert.AreEqual(6000, Div(w, 2, 4).Org_bp);
                Assert.AreEqual(0, Div(w, 2, 4).Exhausted);
                Assert.IsFalse(w.Attacking(Div(w, 1, 1)), "exhausted division does not attack");
                Assert.IsTrue(w.Attacking(Div(w, 1, 0)));
            }
        }

        [Test]
        public void Exhausted_OnAttackFront_NotPressed_RecoversAtFrontRate()
        {
            using (var w = Worlds.Section5(gold: 0))
            {
                // Все Красные выдохлись: атаки нет, Синие не наступают — Красные восстанавливаются по orgRecoverFront.
                for (int k = 0; k < 4; k++) SetOrg(w, 1, k, 1000, 1);
                var ds = new DivisionSystem();
                w.Tick = 9;
                ds.FillPow(w);
                ds.PerSecond(w, 10);
                for (int k = 0; k < 4; k++) Assert.AreEqual(1100, Div(w, 1, k).Org_bp);
                Assert.AreEqual(10000, Div(w, 2, 0).Org_bp, "blue is not pressed: recovers, capped at 10000");
                Assert.AreEqual(10000, Div(w, 1, 0).Strength_bp, "no attack — no attacker losses");
            }
        }

        [Test]
        public void Division_DiesAtZero_NotReinforced()
        {
            using (var w = Worlds.Section5())
            {
                int slot = w.DivisionSlot(1, 2);
                var v = w.DivisionsRW[slot];
                v.Strength_bp = 51;
                w.DivisionsRW[slot] = v;
                var ds = new DivisionSystem();
                w.Tick = 9;
                ds.FillPow(w);
                ds.PerSecond(w, 10);
                Assert.AreEqual(0, w.DivisionAt(slot).Alive);
                Assert.AreEqual(0, w.DivisionAt(slot).Strength_bp);
                Assert.AreEqual(3, w.Nation(1).Divisions);
                Assert.AreEqual(1, w.Nation(1).DivisionsLost);
                Derived.AssertMatches(w, "after division death");
            }
        }

        [Test]
        public void Desertion_WeakestDivisionLoses2000_TieLargestSlot_GoldZero_DiesAtZero()
        {
            using (var w = Worlds.Section5(gold: 0))
            {
                // Пехотные Красных k2 и k3 по 5000: при равенстве — больший слот (k3).
                foreach (int k in new[] { 2, 3 })
                {
                    var v = w.DivisionsRW[w.DivisionSlot(1, k)];
                    v.Strength_bp = 5000;
                    w.DivisionsRW[w.DivisionSlot(1, k)] = v;
                }
                var nat = w.NationsRW;
                var n = nat[0];
                n.Pixels = 0; // доход 2/с, содержание 2 × 0,7 + 2 × 0,3 = 2,0/с — в ноль; делаем минус
                nat[0] = n;
                DivisionSystem.Create(w, 1, 4, DivisionTypes.Infantry, 1000); // готовящаяся тоже на содержании
                Assert.AreEqual(230, DivisionSystem.Upkeep(w, 1));
                w.Tick = 9;
                var ctx = new CommandContext(TestData.Config(), w.Rules, 0);
                new EconomySystem().Tick(w, ctx);
                Assert.AreEqual(0, w.Nation(1).Gold);
                Assert.AreEqual(3000, Div(w, 1, 3).Strength_bp, "tie → largest slot loses desertLoss");
                Assert.AreEqual(5000, Div(w, 1, 2).Strength_bp);
                Assert.AreEqual(5, w.Nation(1).Divisions);

                var v3 = w.DivisionsRW[w.DivisionSlot(1, 3)];
                v3.Strength_bp = 2000;
                w.DivisionsRW[w.DivisionSlot(1, 3)] = v3;
                w.Tick = 19;
                new EconomySystem().Tick(w, ctx);
                Assert.AreEqual(0, Div(w, 1, 3).Alive, "at zero the division dies");
                Assert.AreEqual(4, w.Nation(1).Divisions);
                Assert.AreEqual(1, w.Nation(1).DivisionsLost);
            }
        }
    }

    /// <summary>Экономика (spec-01 §5.6 с дельтой 01c §4.4).</summary>
    public class EconomyTests
    {
        [Test]
        public void Second_IncomeMinusUpkeepByType()
        {
            using (var sim = TestData.IdleMatch(1))
            {
                var w = sim.World;
                var nat = w.NationsRW;
                var n = nat[0];
                n.Pixels = 8000;
                n.Gold = 5000;
                nat[0] = n;
                DivisionSystem.Create(w, 1, 1, DivisionTypes.Armor, 0);
                // Доход 2 + 4 = 6/с; содержание пехотная 0,3 + танковая 0,7 = 1,0/с → +5,0/с.
                Assert.AreEqual(100, DivisionSystem.Upkeep(w, 1));
                Assert.AreEqual(500, EconomySystem.NetIncome(w, 1));
                var eco = new EconomySystem();
                var ctx = new CommandContext(sim.Config, sim.Rules, 0);
                w.Tick = 8; // не последний тик секунды — ничего
                eco.Tick(w, ctx);
                Assert.AreEqual(5000, w.Nation(1).Gold);
                w.Tick = 9;
                eco.Tick(w, ctx);
                Assert.AreEqual(5500, w.Nation(1).Gold);
            }
        }

        [Test]
        public void DeadNation_GetsNothing()
        {
            using (var sim = TestData.IdleMatch(1))
            {
                var w = sim.World;
                var nat = w.NationsRW;
                var n = nat[2];
                n.Alive = 0;
                n.Gold = 0;
                nat[2] = n;
                w.Tick = 9;
                new EconomySystem().Tick(w, new CommandContext(sim.Config, sim.Rules, 0));
                Assert.AreEqual(0, w.Nation(3).Gold);
            }
        }
    }

    /// <summary>
    /// Спека 01c, acceptance 7 (ядро): партия человека с ≥ 20 командами трёх новых типов, включая пачку назначений в один тик,
    /// проходит реплей бит в бит; подмена команды ловится.
    /// </summary>
    public class HumanReplayTests
    {
        const int Ticks = 9000;

        static ReplayFile Play(ulong seed, out List<Command> accepted, out int batchTicks)
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            accepted = new List<Command>();
            batchTicks = 0;
            using (var sim = new Simulation(config, rules, seed, Ticks, 1))
            {
                var w = sim.World;
                var rng = new Pcg32(seed, 77);
                while (!w.IsOver)
                {
                    if (w.Tick % 50 == 0)
                    {
                        var c = TestUtil.BotCommand(w, 1, ref rng);
                        if (c.HasValue) sim.EnqueueFromUi(c.Value.Type, 1, c.Value.A, c.Value.B);
                    }
                    if (w.Tick % 700 == 350)
                    {
                        // Пачка: все свои дивизии — на первый фронт (или в тыл, если фронта нет), по k в один тик.
                        int target = 0;
                        for (int d = 2; d <= w.NationCount && target == 0; d++)
                            if (w.FrontLength(1, d) > 0 && w.Nation((byte)d).Alive != 0) target = d;
                        for (int k = 0; k < w.MaxDivisionsPerNation; k++)
                            if (GameCommands.OwnDivision(w, 1, k)) sim.EnqueueFromUi(CommandType.AssignDivision, 1, k, target);
                    }
                    sim.Step();
                    int assignsThisTick = 0;
                    foreach (var a in sim.AppliedLastTick) if (a.Type == CommandType.AssignDivision) assignsThisTick++;
                    if (assignsThisTick >= 2) batchTicks++;
                }
                var rejected = new HashSet<(int, int)>();
                foreach (var r in sim.Rejected) rejected.Add((r.Command.Tick, r.Command.Seq));
                foreach (var c in sim.CommandLog)
                    if (!rejected.Contains((c.Tick, c.Seq))) accepted.Add(c);
                return ReplayFile.Capture(sim, "test", 7);
            }
        }

        [Test]
        public void HumanReplay_AllTypes_WithBatch_PassesBitForBit_AndTamperIsCaught()
        {
            var replay = ReplayFile.FromJson(Play(1, out var accepted, out int batchTicks).ToJson());
            int recruit = accepted.FindAll(c => c.Type == CommandType.RecruitDivision).Count;
            int assign = accepted.FindAll(c => c.Type == CommandType.AssignDivision).Count;
            int order = accepted.FindAll(c => c.Type == CommandType.SetFrontOrder).Count;
            TestContext.WriteLine($"ticks {replay.Ticks}, accepted: {accepted.Count} (recruit {recruit}, assign {assign}, order {order}), batch ticks {batchTicks}, logged {replay.Commands.Count}");
            Assert.GreaterOrEqual(accepted.Count, 20);
            Assert.Greater(recruit, 0);
            Assert.Greater(assign, 0);
            Assert.Greater(order, 0);
            Assert.Greater(batchTicks, 0, "a batch of assignments in one tick");
            foreach (var c in replay.Commands) Assert.AreEqual(1, c.Nation, "only human commands are in the replay");

            var config = TestData.Config();
            var rules = TestData.Rules();
            var ok = ReplayPlayer.Verify(replay, config, rules);
            Assert.IsTrue(ok.Ok, ok.Error);
            Assert.AreEqual(replay.Checkpoints.Count, ok.CheckpointsCompared);

            // Подмена: принятый набор после 600-го тика превращается в Noop.
            var victim = accepted.Find(c => c.Type == CommandType.RecruitDivision && c.Tick > 600);
            int idx = replay.Commands.FindIndex(rc => rc.Tick == victim.Tick && rc.Seq == victim.Seq);
            Assert.GreaterOrEqual(idx, 0);
            replay.Commands[idx].Type = CommandType.Noop;
            var bad = ReplayPlayer.Verify(replay, config, rules);
            Assert.IsFalse(bad.Ok);
            Assert.IsNull(bad.Error);
            int expected = Math.Min((victim.Tick / 600 + 1) * 600, replay.Ticks);
            Assert.AreEqual(expected, bad.FirstDivergentTick);
        }
    }
}
