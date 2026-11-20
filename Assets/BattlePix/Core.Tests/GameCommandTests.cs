using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Acceptance 12: каждая причина отказа 4–13 — команда отклонена, мир не изменился.</summary>
    public class ValidationTests
    {
        static Command Cmd(CommandType type, byte nation, int a = 0) => new Command { Type = type, Nation = nation, A = a };

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

        /// <summary>Первая пара соседей с общей границей после warmup.</summary>
        static (byte a, byte d) Contact(World w)
        {
            for (int a = 1; a <= w.NationCount; a++)
            for (int d = 1; d <= w.NationCount; d++)
                if (a != d && w.FrontLength(a, d) > 0 && w.Nation((byte)a).Alive != 0 && w.Nation((byte)d).Alive != 0)
                    return ((byte)a, (byte)d);
            Assert.Fail("no contact");
            return (0, 0);
        }

        const int ContactWarmup = 4000;

        [Test] public void NationZero() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.Recruit, 0));
        [Test] public void NationOutOfRange() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.Recruit, 9));
        [Test] public void NationDead() => Rejects(RejectReason.NationInvalid, w => Cmd(CommandType.Recruit, 2),
            w => SetNation(w, 2, n => { n.Alive = 0; return n; }));

        [Test] public void NotEnoughGold_Recruit() => Rejects(RejectReason.NotEnoughGold, w => Cmd(CommandType.Recruit, 1),
            w => SetNation(w, 1, n => { n.Gold = 19999; return n; }));

        [Test] public void NotEnoughGold_Fort() => Rejects(RejectReason.NotEnoughGold, w => Cmd(CommandType.BuildFort, 1, w.Nation(1).Capital),
            w => SetNation(w, 1, n => { n.Gold = 39999; return n; }));

        [Test] public void ArmyCap() => Rejects(RejectReason.ArmyCap, w => Cmd(CommandType.Recruit, 1),
            w => SetNation(w, 1, n => { n.Battalions = 60; n.Gold = 1000000; return n; }));

        [Test] public void NotOwnPixel() => Rejects(RejectReason.NotOwnPixel, w => Cmd(CommandType.BuildFort, 1, w.Nation(2).Capital));

        [Test] public void FortPixelOutOfMap() => Rejects(RejectReason.PixelOutOfMap, w => Cmd(CommandType.BuildFort, 1, w.PixelCount));

        [Test] public void TooCloseToFort() => Rejects(RejectReason.TooCloseToFort, w => Cmd(CommandType.BuildFort, 1, w.Nation(1).Capital + 3),
            w =>
            {
                SetNation(w, 1, n => { n.Gold = 1000000; return n; });
                var c = new Command { Type = CommandType.BuildFort, Nation = 1, A = w.Nation(1).Capital };
                Assert.AreEqual(RejectReason.None, GameCommands.Validate(w, c));
                GameCommands.Apply(w, c);
            });

        [Test] public void FortLimit() => Rejects(RejectReason.FortLimit, w => Cmd(CommandType.BuildFort, 1, w.Nation(1).Capital),
            w => SetNation(w, 1, n => { n.FortsAlive = 8; n.Gold = 1000000; return n; }));

        [Test] public void NoBattalions() => Rejects(RejectReason.NoBattalions, w => { var (a, d) = Contact(w); return Cmd(CommandType.Strike, a, d); },
            w => { var (a, _) = Contact(w); SetNation(w, a, n => { n.Battalions = 0; return n; }); }, ContactWarmup);

        [Test] public void TargetSelf() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.Strike, 1, 1));
        [Test] public void TargetZero() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.Strike, 1, 0));
        [Test] public void TargetOutOfRange() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.Strike, 1, 9));
        [Test] public void TargetDead() => Rejects(RejectReason.TargetInvalid, w => Cmd(CommandType.Strike, 1, 2),
            w => SetNation(w, 2, n => { n.Alive = 0; return n; }));

        [Test] public void NoContact_AtStart() => Rejects(RejectReason.NoContact, w => Cmd(CommandType.Strike, 1, 2));

        [Test]
        public void RulesDisabled_InFoundationWorld()
        {
            using (var sim = new Simulation(TestUtil.Config(pixelsPerTick: 0), 1))
            {
                ulong before = sim.ComputeHash();
                foreach (var t in new[] { CommandType.Recruit, CommandType.BuildFort, CommandType.Strike })
                    sim.Enqueue(new Command { Tick = 0, Type = t, Nation = 1, A = 2 });
                sim.Step();
                Assert.AreEqual(3, sim.Rejected.Count);
                foreach (var r in sim.Rejected) Assert.AreEqual(RejectReason.RulesDisabled, r.Reason);
            }
        }

        [Test]
        public void EnumValues_AreFrozen()
        {
            Assert.AreEqual(2, (byte)CommandType.Recruit);
            Assert.AreEqual(3, (byte)CommandType.BuildFort);
            Assert.AreEqual(4, (byte)CommandType.Strike);
            Assert.AreEqual(4, (byte)RejectReason.NationInvalid);
            Assert.AreEqual(13, (byte)RejectReason.RulesDisabled);
            Assert.AreEqual(14, (byte)RejectReason.DebugOnly);
            Assert.AreEqual(2, (byte)EndReason.Conquest);
            Assert.AreEqual(4, (byte)EndReason.HumanDefeated);
        }
    }

    public class GameCommandApplyTests
    {
        [Test]
        public void Recruit_SpendsGold_AddsBattalion()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                var before = sim.World.Nation(1);
                sim.EnqueueFromUi(CommandType.Recruit, 1, 0, 0);
                sim.Step();
                sim.Step();
                var after = sim.World.Nation(1);
                Assert.AreEqual(0, sim.Rejected.Count);
                Assert.AreEqual(before.Battalions + 1, after.Battalions);
                Assert.AreEqual(1, after.Recruits);
                Assert.AreEqual(before.Gold - 20000, after.Gold); // секунда ещё не прошла: дохода не было
            }
        }

        [Test]
        public void TwoPurchases_SecondRejected_WhenGoldRunsOut()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                // 300 золота: батальон (200) проходит, крепость (400) уже нет.
                sim.EnqueueFromUi(CommandType.Recruit, 1, 0, 0);
                sim.EnqueueFromUi(CommandType.BuildFort, 1, sim.World.Nation(1).Capital, 0);
                sim.Step();
                sim.Step();
                Assert.AreEqual(1, sim.Rejected.Count);
                Assert.AreEqual(RejectReason.NotEnoughGold, sim.Rejected[0].Reason);
                Assert.AreEqual(CommandType.BuildFort, sim.Rejected[0].Command.Type);
            }
        }

        [Test]
        public void BuildFort_StampsCover_PriceGrowsByFortsAlive()
        {
            using (var sim = TestData.IdleMatch(4))
            {
                var w = sim.World;
                var nat = w.NationsRW;
                var n1 = nat[0];
                n1.Gold = 1000000;
                nat[0] = n1;
                int cap = n1.Capital;
                sim.EnqueueFromUi(CommandType.BuildFort, 1, cap, 0);
                sim.Step();
                sim.Step();
                Assert.AreEqual(1000000 - 40000, w.Nation(1).Gold);
                Assert.AreEqual(1, w.Nation(1).FortsAlive);
                Assert.AreEqual(1, w.Nation(1).FortsBuilt);
                Assert.AreEqual(cap, w.Forts[0].Pixel);
                Assert.AreEqual(1, w.Forts[0].Alive);
                Assert.AreNotEqual(0, w.FortCover[cap] & 1);
                Assert.AreNotEqual(0, w.FortCover[cap + 16] & 1);
                Assert.AreEqual(0, w.FortCover[cap + 17] & 1);
                Assert.AreEqual(60000, w.Rules.FortCost(w.Nation(1).FortsAlive));
                Derived.AssertMatches(w, "after fort");
            }
        }

        [Test]
        public void Strike_BurnsBattalion_SetsUntil_ExpiresInStrikeSystem()
        {
            using (var sim = TestData.IdleMatch(3))
            {
                TestUtil.Run(sim, 4000);
                var w = sim.World;
                byte a = 0, d = 0;
                for (int x = 1; x <= 8 && a == 0; x++)
                for (int y = 1; y <= 8; y++)
                    if (x != y && w.FrontLength(x, y) > 0) { a = (byte)x; d = (byte)y; break; }
                Assert.AreNotEqual(0, a);
                int b0 = w.Nation(a).Battalions;
                sim.EnqueueFromUi(CommandType.Strike, a, d, 0);
                int applyTick = w.Tick + 1;
                sim.Step();
                sim.Step();
                int idx = (a - 1) * 8 + (d - 1);
                Assert.AreEqual(applyTick + 200, w.StrikeUntil[idx]);
                Assert.AreEqual(b0 - 1, w.Nation(a).Battalions);
                Assert.AreEqual(1, w.Nation(a).Strikes);
                // Второй удар продлевает от конца первого.
                sim.EnqueueFromUi(CommandType.Strike, a, d, 0);
                sim.Step();
                sim.Step();
                Assert.AreEqual(applyTick + 400, w.StrikeUntil[idx]);
                TestUtil.Run(sim, 400);
                Assert.AreEqual(0, w.StrikeUntil[idx], "expired strike is cleared");
            }
        }
    }

    /// <summary>Acceptance 13: экономика.</summary>
    public class EconomyTests
    {
        static void SetTick(World w, int tick) => w.Tick = tick;

        [Test]
        public void Section6_Second_Plus300()
        {
            using (var sim = TestData.Match(1))
            {
                var w = sim.World;
                var nat = w.NationsRW;
                var n = nat[0];
                n.Pixels = 8000;
                n.Battalions = 12;
                n.Gold = 5000;
                nat[0] = n;
                var eco = new EconomySystem();
                var ctx = new CommandContext(sim.Config, sim.Rules, 0);
                SetTick(w, 8); // не последний тик секунды — ничего
                eco.Tick(w, ctx);
                Assert.AreEqual(5000, w.Nation(1).Gold);
                SetTick(w, 9);
                eco.Tick(w, ctx);
                Assert.AreEqual(5300, w.Nation(1).Gold);
                Assert.AreEqual(300, EconomySystem.NetIncome(w, 1));
            }
        }

        [Test]
        public void Desertion_OneBattalionPerSecond_GoldZero()
        {
            using (var sim = TestData.Match(1))
            {
                var w = sim.World;
                var nat = w.NationsRW;
                var n = nat[0];
                n.Battalions = 100;
                n.Gold = 0;
                nat[0] = n;
                var eco = new EconomySystem();
                var ctx = new CommandContext(sim.Config, sim.Rules, 0);
                SetTick(w, 9);
                eco.Tick(w, ctx);
                Assert.AreEqual(99, w.Nation(1).Battalions);
                Assert.AreEqual(0, w.Nation(1).Gold);
                SetTick(w, 19);
                eco.Tick(w, ctx);
                Assert.AreEqual(98, w.Nation(1).Battalions);
            }
        }

        [Test]
        public void DeadNation_GetsNothing()
        {
            using (var sim = TestData.Match(1))
            {
                var w = sim.World;
                var nat = w.NationsRW;
                var n = nat[2];
                n.Alive = 0;
                n.Gold = 0;
                nat[2] = n;
                SetTick(w, 9);
                new EconomySystem().Tick(w, new CommandContext(sim.Config, sim.Rules, 0));
                Assert.AreEqual(0, w.Nation(3).Gold);
            }
        }
    }

    /// <summary>Acceptance 11 (ядро): партия человека с ≥ 20 командами всех трёх типов проходит реплей бит в бит.</summary>
    public class HumanReplayTests
    {
        const int Ticks = 9000;

        /// <summary>Человек (нация 1) играет простыми правилами: копит, набирает, ставит крепости, бьёт соседа.</summary>
        static ReplayFile Play(out List<Command> accepted)
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            accepted = new List<Command>();
            using (var sim = new Simulation(config, rules, 3, Ticks, 1))
            {
                var w = sim.World;
                int forts = 0;
                while (!w.IsOver)
                {
                    int t = w.Tick;
                    if (t % 50 == 0)
                    {
                        var me = w.Nation(1);
                        if (t % 300 == 0 && forts < 4)
                        {
                            // Крепость на своей земле: столица и точки вокруг неё через 20 px.
                            int cap = me.Capital, x = cap % w.Width + (forts % 2 == 0 ? 1 : -1) * 20 * (forts / 2), y = cap / w.Width;
                            sim.EnqueueFromUi(CommandType.BuildFort, 1, w.Index(Math.Max(0, Math.Min(w.Width - 1, x)), y), 0);
                            forts++;
                        }
                        else if (me.Gold >= 50000) sim.EnqueueFromUi(CommandType.Recruit, 1, 0, 0); // копим на крепость
                        for (int d = 2; d <= 8; d++)
                            if (w.FrontLength(1, d) > 0 && t % 200 == 0)
                            {
                                sim.EnqueueFromUi(CommandType.Strike, 1, d, 0);
                                break;
                            }
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
            var replay = ReplayFile.FromJson(Play(out var accepted).ToJson());
            int recruit = 0, fort = 0, strike = 0;
            foreach (var c in accepted)
            {
                if (c.Type == CommandType.Recruit) recruit++;
                if (c.Type == CommandType.BuildFort) fort++;
                if (c.Type == CommandType.Strike) strike++;
            }
            TestContext.WriteLine($"ticks {replay.Ticks}, accepted: {accepted.Count} (recruit {recruit}, fort {fort}, strike {strike}), logged {replay.Commands.Count}");
            Assert.GreaterOrEqual(accepted.Count, 20);
            Assert.Greater(recruit, 0);
            Assert.Greater(fort, 0);
            Assert.Greater(strike, 0);
            foreach (var c in replay.Commands) Assert.AreEqual(1, c.Nation, "only human commands are in the replay");

            var config = TestData.Config();
            var rules = TestData.Rules();
            var ok = ReplayPlayer.Verify(replay, config, rules);
            Assert.IsTrue(ok.Ok, ok.Error);
            Assert.AreEqual(replay.Checkpoints.Count, ok.CheckpointsCompared);

            // Подмена: принятый набор после 600-го тика превращается в Noop.
            var victim = accepted.Find(c => c.Type == CommandType.Recruit && c.Tick > 600);
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
