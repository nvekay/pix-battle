using System.Collections.Generic;
using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>ИИ войны и мира (спека 02b §4.9).</summary>
    public class WarAiTests
    {
        /// <summary>За партию ИИ стягивает дивизии к мирной границе, объявляет войну обеими целями и заключает мир.</summary>
        [Test]
        public void Ai_StagesDeclaresBothGoals_AndMakesPeace()
        {
            using (var sim = TestData.Match(7, 20000))
            {
                var w = sim.World;
                int staged = 0, declaredAfterStaging = 0;
                var stagedOn = new HashSet<int>();
                while (!w.IsOver)
                {
                    sim.Step();
                    foreach (var c in sim.AppliedLastTick)
                    {
                        if (c.Type == CommandType.AssignDivision && c.B != 0 && !w.AtWar(c.Nation, c.B) && w.WarOf(c.Nation, c.B).Status == WarStatus.Peace)
                        {
                            staged++;
                            stagedOn.Add(c.Nation * 256 + c.B);
                        }
                        if (c.Type == CommandType.DeclareWar && stagedOn.Contains(c.Nation * 256 + c.A)) declaredAfterStaging++;
                    }
                }
                int claims = w.WarEvents.FindAll(e => e.Kind == WarEventKind.Declared && e.Goal == WarGoals.Claims).Count;
                int reps = w.WarEvents.FindAll(e => e.Kind == WarEventKind.Declared && e.Goal == WarGoals.Reparations).Count;
                int peace = w.WarEvents.FindAll(e => e.Kind == WarEventKind.Peace).Count;
                TestContext.WriteLine($"staged {staged}, declared after staging {declaredAfterStaging}, claims {claims}, reparations {reps}, peace {peace}");
                Assert.Greater(staged, 0, "AI moves divisions to a peaceful border before declaring");
                Assert.Greater(declaredAfterStaging, 0);
                Assert.Greater(claims, 0);
                Assert.Greater(reps, 0);
                Assert.Greater(peace, 0);
            }
        }

        /// <summary>Правило 0: ИИ отвечает на предложение человека белым миром в пределах thinkInterval.</summary>
        [Test]
        public void Ai_AnswersHumanOffer()
        {
            using (var sim = TestData.Match(3, 0, human: 1))
            {
                var w = sim.World;
                byte d = 0;
                while (d == 0 && w.Tick < 6000)
                {
                    sim.Step();
                    for (int x = 2; x <= w.NationCount && d == 0; x++)
                        if (WarSystem.ValidateDeclare(w, 1, x, 0) == RejectReason.None) d = (byte)x;
                }
                Assert.AreNotEqual(0, d, "human has a neighbour at peace");
                sim.EnqueueFromUi(CommandType.DeclareWar, 1, d, 0);
                sim.Step();
                sim.Step();
                Assert.IsTrue(w.AtWar(1, d));
                sim.EnqueueFromUi(CommandType.ProposePeace, 1, d, 0);
                sim.Step();
                sim.Step();
                Assert.AreEqual(1, w.WarOf(1, d).OfferFrom);
                bool answered = false;
                for (int t = 0; t < 30 && !answered; t++)
                {
                    sim.Step();
                    foreach (var c in sim.AppliedLastTick)
                        if (c.Type == CommandType.AnswerPeace && c.Nation == d && c.A == 1) answered = true;
                }
                Assert.IsTrue(answered, "AI answered the offer");
            }
        }

        /// <summary>Абляция no_claims: только репарации.</summary>
        [Test]
        public void NoClaimsProfile_DeclaresOnlyReparations()
        {
            var rules = TestData.Rules().Clone();
            for (int id = 1; id <= rules.NationCount; id++) rules.AssignProfile(id, "no_claims");
            using (var sim = TestData.Match(7, 12000, 0, rules))
            {
                TestUtil.Run(sim, 12000);
                var declared = sim.World.WarEvents.FindAll(e => e.Kind == WarEventKind.Declared);
                Assert.Greater(declared.Count, 0);
                foreach (var e in declared) Assert.AreEqual(WarGoals.Reparations, e.Goal);
            }
        }

        [Test]
        public void GoalProgress_IsRelativeToThreshold()
        {
            using (var w = WarWorlds.Make(100, 80, 2, (x, y) => x < 10 ? (byte)1 : (byte)2, new[] { (2, 40), (95, 40) }))
            {
                Worlds.War(w, 1, 2);
                var war = w.WarsRW[w.WarIndex(1, 2)];
                war.PreWarDef = 6000;
                war.PreWarAtt = 6400;
                w.WarsRW[w.WarIndex(1, 2)] = war;
                int k = 0;
                for (int p = 0; p < w.PixelCount && k < 300; p++)
                    if (w.Owner[p] == 2) { w.OwnerRW[p] = 1; k++; }
                WarWorlds.SyncPixels(w);
                Assert.AreEqual(5000, WarSystem.GoalProgress_bp(w, 1, 2, 1), "300 of 600 needed");
                Assert.AreEqual(0, WarSystem.GoalProgress_bp(w, 1, 2, 2));
            }
        }
    }
}
