using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>
    /// Спека 01b, acceptance 7: на каждую причину Obsolete…OrderInvalid, NotEnoughGold, ArmyCap, NoContact, TargetInvalid —
    /// команда отклонена, мир не изменился.
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
        static byte NoContact(World w, byte a)
        {
            for (int d = 1; d <= w.NationCount; d++)
                if (d != a && w.FrontLength(a, d) <= 0) return (byte)d;
            Assert.Fail("everyone is in contact");
            return 0;
        }

        internal const int ContactWarmup = 4000;

        [Test] public void NationZero() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.RecruitUnit, 0, 0, 0));
        [Test] public void NationOutOfRange() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.RecruitUnit, 9, 0, 0));
        [Test] public void NationDead() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.RecruitUnit, 2, 0, 0),
            w => SetNation(w, 2, n => { n.Alive = 0; return n; }));

        [Test] public void Obsolete_Recruit() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.Recruit, 1));
        [Test] public void Obsolete_BuildFort() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.BuildFort, 1, w.Nation(1).Capital));
        [Test] public void Obsolete_Strike() => Rejects(RejectReason.Obsolete, w => Cmd(CommandType.Strike, 1, 2));

        [Test] public void NotEnoughGold_Infantry() => Rejects(RejectReason.NotEnoughGold, w => Cmd(CommandType.RecruitUnit, 1, 0, UnitTypes.Infantry),
            w => SetNation(w, 1, n => { n.Gold = 14999; return n; }));
        [Test] public void NotEnoughGold_Armor() => Rejects(RejectReason.NotEnoughGold, w => Cmd(CommandType.RecruitUnit, 1, 0, UnitTypes.Armor),
            w => SetNation(w, 1, n => { n.Gold = 39999; return n; }));
        [Test] public void NotEnoughGold_NewFormation() => Rejects(RejectReason.NotEnoughGold, w => Cmd(CommandType.RecruitUnit, 1, -1, UnitTypes.Infantry),
            w => SetNation(w, 1, n => { n.Gold = 44999; return n; })); // штаб 300 + пехота 150

        [Test] public void ArmyCap() => Rejects(RejectReason.ArmyCap, w => Cmd(CommandType.RecruitUnit, 1, 0, 0),
            w => { Rich(w, 1); SetNation(w, 1, n => { n.Battalions = 60; return n; }); });

        [Test] public void UnitTypeInvalid() => Rejects(RejectReason.UnitTypeInvalid, w => Cmd(CommandType.RecruitUnit, 1, 0, 2), w => Rich(w, 1));
        [Test] public void UnitTypeNegative() => Rejects(RejectReason.UnitTypeInvalid, w => Cmd(CommandType.RecruitUnit, 1, 0, -1), w => Rich(w, 1));

        [Test] public void FormationInvalid_Recruit() => Rejects(RejectReason.FormationInvalid, w => Cmd(CommandType.RecruitUnit, 1, 1, 0), w => Rich(w, 1));
        [Test] public void FormationInvalid_OutOfRange() => Rejects(RejectReason.FormationInvalid, w => Cmd(CommandType.RecruitUnit, 1, 16, 0), w => Rich(w, 1));
        [Test] public void FormationInvalid_Assign() => Rejects(RejectReason.FormationInvalid, w => Cmd(CommandType.AssignFormation, 1, 3, 0));
        [Test] public void FormationInvalid_Order() => Rejects(RejectReason.FormationInvalid, w => Cmd(CommandType.SetOrder, 1, -1, 1));

        [Test] public void FormationFull() => Rejects(RejectReason.FormationFull, w => Cmd(CommandType.RecruitUnit, 1, 0, 0),
            w =>
            {
                Rich(w, 1);
                for (int i = 3; i < 12; i++) FormationSystem.AddBattalion(w, w.FormationSlot(1, 0), UnitTypes.Infantry);
            });

        [Test] public void FormationLimit() => Rejects(RejectReason.FormationLimit, w => Cmd(CommandType.RecruitUnit, 1, -1, 0),
            w =>
            {
                Rich(w, 1);
                for (int k = 1; k < 16; k++) FormationSystem.CreateFormation(w, 1, k);
            });

        [Test] public void OrderInvalid() => Rejects(RejectReason.OrderInvalid, w => Cmd(CommandType.SetOrder, 1, 0, 2));

        [Test] public void TargetSelf() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignFormation, 1, 0, 1));
        [Test] public void TargetOutOfRange() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignFormation, 1, 0, 9));
        [Test] public void TargetNegative() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignFormation, 1, 0, -1));
        [Test] public void TargetDead() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.AssignFormation, 1, 0, 2),
            w => SetNation(w, 2, n => { n.Alive = 0; return n; }));

        [Test] public void NoContact_AtStart() => Rejects(RejectReason.NoContact, w => Cmd(CommandType.AssignFormation, 1, 0, 2));

        [Test] public void NoContact_AfterWarmup() => Rejects(RejectReason.NoContact,
            w => { var (a, _) = Contact(w); return Cmd(CommandType.AssignFormation, a, 0, NoContact(w, a)); }, null, ContactWarmup);

        [Test] public void AlreadyThere() => Rejects(RejectReason.AlreadyThere,
            w => { var (a, d) = Contact(w); return Cmd(CommandType.AssignFormation, a, 0, d); },
            w =>
            {
                var (a, d) = Contact(w);
                var f = w.FormationsRW[w.FormationSlot(a, 0)];
                f.Front = d;
                f.ArriveTick = 0;
                w.FormationsRW[w.FormationSlot(a, 0)] = f;
            }, ContactWarmup);

        [Test]
        public void RulesDisabled_InFoundationWorld()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1))
            {
                var types = new[] { CommandType.Recruit, CommandType.BuildFort, CommandType.Strike, CommandType.RecruitUnit, CommandType.AssignFormation, CommandType.SetOrder };
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
            Assert.AreEqual(2, (byte)EndReason.Conquest);
            Assert.AreEqual(4, (byte)EndReason.HumanDefeated);
            Assert.AreEqual(1, (byte)Terrain.Land);
            Assert.AreEqual(3, (byte)Terrain.Mountains);
        }
    }

    public class GameCommandApplyTests
    {
        [Test]
        public void StartFormation_ThreeInfantry_InRear()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                var w = sim.World;
                for (byte id = 1; id <= 8; id++)
                {
                    var f = w.FormationAt(w.FormationSlot(id, 0));
                    Assert.AreEqual(1, f.Alive);
                    Assert.AreEqual(id, f.Owner);
                    Assert.AreEqual(0, f.Front);
                    Assert.AreEqual(Orders.Defend, f.Order);
                    Assert.AreEqual(3, FormationSystem.BattalionCount(w, w.FormationSlot(id, 0)));
                    Assert.AreEqual(10000, FormationSystem.AverageStrength(w, w.FormationSlot(id, 0)));
                    Assert.AreEqual(1, w.Nation(id).Formations);
                    Assert.AreEqual(3, w.Nation(id).Battalions);
                    Assert.AreEqual(0, w.FormationAt(w.FormationSlot(id, 1)).Alive);
                }
            }
        }

        [Test]
        public void RecruitUnit_IntoFormation_AndNewFormation()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                var w = sim.World;
                var nat = w.NationsRW;
                var n1 = nat[0];
                n1.Gold = 1000000;
                nat[0] = n1;
                sim.EnqueueFromUi(CommandType.RecruitUnit, 1, 0, UnitTypes.Armor);
                sim.EnqueueFromUi(CommandType.RecruitUnit, 1, -1, UnitTypes.Infantry);
                sim.Step();
                sim.Step();
                Assert.AreEqual(0, sim.Rejected.Count);
                var me = w.Nation(1);
                Assert.AreEqual(1000000 - 40000 - 30000 - 15000, me.Gold); // секунда ещё не прошла
                Assert.AreEqual(5, me.Battalions);
                Assert.AreEqual(2, me.Formations);
                Assert.AreEqual(1, me.RecruitedArmor);
                Assert.AreEqual(1, me.RecruitedInf);
                Assert.AreEqual(UnitTypes.Armor, w.BattalionAt(w.FormationSlot(1, 0) * w.MaxBattalions + 3).Type);
                var f1 = w.FormationAt(w.FormationSlot(1, 1));
                Assert.AreEqual(1, f1.Alive);
                Assert.AreEqual(0, f1.Front);
                Assert.AreEqual(1, FormationSystem.BattalionCount(w, w.FormationSlot(1, 1)));
            }
        }

        [Test]
        public void TwoPurchases_SecondRejected_WhenGoldRunsOut()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                // 300 золота: пехота (150) проходит, броня (400) уже нет.
                sim.EnqueueFromUi(CommandType.RecruitUnit, 1, 0, UnitTypes.Infantry);
                sim.EnqueueFromUi(CommandType.RecruitUnit, 1, 0, UnitTypes.Armor);
                sim.Step();
                sim.Step();
                Assert.AreEqual(1, sim.Rejected.Count);
                Assert.AreEqual(RejectReason.NotEnoughGold, sim.Rejected[0].Reason);
                Assert.AreEqual(UnitTypes.Armor, sim.Rejected[0].Command.B);
            }
        }

        [Test]
        public void SetOrder_AppliesInRear()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                sim.EnqueueFromUi(CommandType.SetOrder, 1, 0, Orders.Attack);
                sim.Step();
                sim.Step();
                Assert.AreEqual(Orders.Attack, sim.World.FormationAt(sim.World.FormationSlot(1, 0)).Order);
            }
        }

        /// <summary>Спека 01b, acceptance 8: в пути сила не считается до ArriveTick, на ArriveTick считается; в тыл — сразу.</summary>
        [Test]
        public void Transfer_NoPowerUntilArriveTick_ThenPower_RearIsImmediate()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, ValidationTests.ContactWarmup);
                var w = sim.World;
                var (a, d) = ValidationTests.Contact(w);
                int garrisonDef = w.Rules.Front.GarrisonPerEdge; // на равнине оборона без соединений = гарнизон
                sim.EnqueueFromUi(CommandType.AssignFormation, a, 0, d);
                int applyTick = w.Tick + 1;
                sim.Step(); // до applyTick
                sim.Step(); // applyTick: команда применена
                var f = w.FormationAt(w.FormationSlot(a, 0));
                Assert.AreEqual(d, f.Front);
                Assert.AreEqual(applyTick + 300, f.ArriveTick);
                Assert.IsFalse(w.OnFront(f));
                while (w.Tick < f.ArriveTick) sim.Step(); // последний посчитанный тик — ArriveTick − 1
                if (w.FrontLength(a, d) > 0) Assert.AreEqual(garrisonDef, w.DefPow(a, d, 0), "in transit: garrison only");
                sim.Step(); // тик ArriveTick
                Assert.IsTrue(w.OnFront(w.FormationAt(w.FormationSlot(a, 0))));
                Assert.Greater(w.DefPow(a, d, 0), garrisonDef, "arrived: formation defends");

                sim.EnqueueFromUi(CommandType.AssignFormation, a, 0, 0);
                sim.Step();
                sim.Step();
                f = w.FormationAt(w.FormationSlot(a, 0));
                Assert.AreEqual(0, f.Front);
                Assert.AreEqual(0, f.ArriveTick);
                Assert.AreEqual(garrisonDef, w.DefPow(a, d, 0));
            }
        }

        [Test]
        public void Reassign_InTransit_RestartsTimer()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, ValidationTests.ContactWarmup);
                var w = sim.World;
                var (a, d) = ValidationTests.Contact(w);
                sim.EnqueueFromUi(CommandType.AssignFormation, a, 0, d);
                TestUtil.Run(sim, 100);
                sim.EnqueueFromUi(CommandType.AssignFormation, a, 0, d);
                int applyTick = w.Tick + 1;
                sim.Step();
                sim.Step();
                Assert.AreEqual(0, sim.Rejected.Count);
                Assert.AreEqual(applyTick + 300, w.FormationAt(w.FormationSlot(a, 0)).ArriveTick);
            }
        }
    }

    /// <summary>Спека 01b, acceptance 6: секунда из раздела 6 (43, 22, 216), гибель батальона, дезертирство по 5.4.</summary>
    public class LossTests
    {
        static int Str(World w, byte owner, int k, int j) => w.BattalionAt(w.FormationSlot(owner, k) * w.MaxBattalions + j).Strength_bp;

        [Test]
        public void Section6_Second_Losses43And22_Reinforce216()
        {
            using (var w = Worlds.Section6(true))
            {
                var fs = new FormationSystem();
                w.Tick = 9; // последний тик секунды
                fs.FillPow(w);
                Assert.AreEqual(24036, w.WeightedAttack(1, 2));
                Assert.AreEqual(21060, w.WeightedDefense(2, 1));
                int gold1 = w.Nation(1).Gold, gold2 = w.Nation(2).Gold;
                fs.LossesAndReinforcement(w, 10);
                // Красные: −43, потом +30 пополнения → 9987. Синие: −22, потом +22 → 10000.
                for (int j = 0; j < 11; j++) Assert.AreEqual(9987, Str(w, 1, 0, j), $"red 1st, battalion {j}");
                for (int j = 0; j < 8; j++) Assert.AreEqual(9987, Str(w, 1, 1, j), $"red 2nd, battalion {j}");
                for (int j = 0; j < 10; j++) Assert.AreEqual(10000, Str(w, 2, 0, j), $"blue, battalion {j}");
                Assert.AreEqual(216, gold1 - w.Nation(1).Gold, "red reinforcement: 16 × 9 + 3 × 24");
                Assert.AreEqual(60, gold2 - w.Nation(2).Gold, "blue reinforcement: 10 × (15000 × 22 / 10000 × 2000 / 10000 = 6)");
            }
        }

        [Test]
        public void Losses_Only_WithoutGold_NoReinforcement()
        {
            using (var w = Worlds.Section6(true, gold: 0))
            {
                var fs = new FormationSystem();
                w.Tick = 9;
                fs.FillPow(w);
                fs.LossesAndReinforcement(w, 10);
                Assert.AreEqual(10000 - 43, Str(w, 1, 0, 0));
                Assert.AreEqual(10000 - 22, Str(w, 2, 0, 0));
                Assert.AreEqual(0, w.Nation(1).Gold);
            }
        }

        [Test]
        public void NotLastTickOfSecond_NothingHappens()
        {
            using (var w = Worlds.Section6(true))
            {
                var fs = new FormationSystem();
                w.Tick = 8;
                fs.FillPow(w);
                fs.LossesAndReinforcement(w, 10);
                Assert.AreEqual(10000, Str(w, 1, 0, 0));
            }
        }

        [Test]
        public void Battalion_DiesAtZero_NotReinforced()
        {
            using (var w = Worlds.Section6(true))
            {
                int bs = w.FormationSlot(1, 0) * w.MaxBattalions + 2;
                var b = w.BattalionsRW[bs];
                b.Strength_bp = 43;
                w.BattalionsRW[bs] = b;
                var fs = new FormationSystem();
                w.Tick = 9;
                fs.FillPow(w);
                fs.LossesAndReinforcement(w, 10);
                Assert.AreEqual(0, w.BattalionAt(bs).Alive);
                Assert.AreEqual(0, w.BattalionAt(bs).Strength_bp);
                Assert.AreEqual(18, w.Nation(1).Battalions);
                Assert.AreEqual(1, w.Nation(1).BattalionsLost);
                Derived.AssertMatches(w, "after battalion death");
            }
        }

        [Test]
        public void Desertion_WeakestBattalion_TieLargestSlot_GoldZero()
        {
            using (var w = Worlds.Section6(true, gold: 0))
            {
                // Пехота 2-го соединения: два батальона по 5000 (слоты j = 1 и j = 5), остальные полные.
                foreach (int j in new[] { 1, 5 })
                {
                    int bs = w.FormationSlot(1, 1) * w.MaxBattalions + j;
                    var b = w.BattalionsRW[bs];
                    b.Strength_bp = 5000;
                    w.BattalionsRW[bs] = b;
                }
                var nat = w.NationsRW;
                var n = nat[0];
                n.Pixels = 0; // доход 2/с, содержание 16 × 0,1 + 3 × 0,3 + 2 × 0,2 = 2,9/с
                nat[0] = n;
                w.Tick = 9;
                new EconomySystem().Tick(w, new CommandContext(TestData.Config(), w.Rules, 0));
                Assert.AreEqual(0, w.Nation(1).Gold);
                Assert.AreEqual(18, w.Nation(1).Battalions);
                Assert.AreEqual(1, w.Nation(1).BattalionsLost);
                Assert.AreEqual(0, w.BattalionAt(w.FormationSlot(1, 1) * w.MaxBattalions + 5).Alive, "tie → largest slot");
                Assert.AreEqual(1, w.BattalionAt(w.FormationSlot(1, 1) * w.MaxBattalions + 1).Alive);
            }
        }
    }

    /// <summary>Экономика (spec-01 §5.6 с дельтой 01b §5.4).</summary>
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
                FormationSystem.AddBattalion(w, w.FormationSlot(1, 0), UnitTypes.Armor);
                // Доход 2 + 4 = 6/с; содержание 3 пехоты × 0,1 + 1 броня × 0,3 + штаб 0,2 = 0,8/с → +5,2/с.
                Assert.AreEqual(80, FormationSystem.Upkeep(w, 1));
                Assert.AreEqual(520, EconomySystem.NetIncome(w, 1));
                var eco = new EconomySystem();
                var ctx = new CommandContext(sim.Config, sim.Rules, 0);
                w.Tick = 8; // не последний тик секунды — ничего
                eco.Tick(w, ctx);
                Assert.AreEqual(5000, w.Nation(1).Gold);
                w.Tick = 9;
                eco.Tick(w, ctx);
                Assert.AreEqual(5520, w.Nation(1).Gold);
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

    /// <summary>Спека 01b, acceptance 12 (ядро): партия человека с ≥ 20 командами всех трёх новых типов проходит реплей бит в бит.</summary>
    public class HumanReplayTests
    {
        const int Ticks = 9000;

        static ReplayFile Play(ulong seed, out List<Command> accepted)
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            accepted = new List<Command>();
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
                    sim.Step();
                }
                var rejected = new HashSet<(int, int)>();
                foreach (var r in sim.Rejected) rejected.Add((r.Command.Tick, r.Command.Seq));
                foreach (var c in sim.CommandLog)
                    if (!rejected.Contains((c.Tick, c.Seq))) accepted.Add(c);
                return ReplayFile.Capture(sim, "test", 7);
            }
        }

        [Test]
        public void HumanReplay_AllTypes_PassesBitForBit_AndTamperIsCaught()
        {
            var replay = ReplayFile.FromJson(Play(1, out var accepted).ToJson());
            int recruit = accepted.FindAll(c => c.Type == CommandType.RecruitUnit).Count;
            int assign = accepted.FindAll(c => c.Type == CommandType.AssignFormation).Count;
            int order = accepted.FindAll(c => c.Type == CommandType.SetOrder).Count;
            TestContext.WriteLine($"ticks {replay.Ticks}, accepted: {accepted.Count} (recruit {recruit}, assign {assign}, order {order}), logged {replay.Commands.Count}");
            Assert.GreaterOrEqual(accepted.Count, 20);
            Assert.Greater(recruit, 0);
            Assert.Greater(assign, 0);
            Assert.Greater(order, 0);
            foreach (var c in replay.Commands) Assert.AreEqual(1, c.Nation, "only human commands are in the replay");

            var config = TestData.Config();
            var rules = TestData.Rules();
            var ok = ReplayPlayer.Verify(replay, config, rules);
            Assert.IsTrue(ok.Ok, ok.Error);
            Assert.AreEqual(replay.Checkpoints.Count, ok.CheckpointsCompared);

            // Подмена: принятый набор после 600-го тика превращается в Noop.
            var victim = accepted.Find(c => c.Type == CommandType.RecruitUnit && c.Tick > 600);
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
