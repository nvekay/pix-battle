using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    public class RulesTests
    {
        [Test]
        public void ShippedData_Parses_AndMatchesDtoDefaults()
        {
            var r = TestData.Rules();
            Assert.AreEqual(8, r.NationCount);
            var d = Rules.Default(8);
            // Значения по умолчанию в DTO совпадают с данными (правило: констант баланса в коде нет, кроме этих).
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.MapGen), Newtonsoft.Json.JsonConvert.SerializeObject(r.MapGen));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Front), Newtonsoft.Json.JsonConvert.SerializeObject(r.Front));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Economy), Newtonsoft.Json.JsonConvert.SerializeObject(r.Economy));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Forts), Newtonsoft.Json.JsonConvert.SerializeObject(r.Forts));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Strikes), Newtonsoft.Json.JsonConvert.SerializeObject(r.Strikes));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.End), Newtonsoft.Json.JsonConvert.SerializeObject(r.End));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Ai), Newtonsoft.Json.JsonConvert.SerializeObject(r.Ai));
            Assert.AreEqual("base", r.ProfileName(1));
        }

        [Test]
        public void ShippedConfig_HasDebugPaintDisabled()
        {
            Assert.IsFalse(TestData.Config().DebugPaint.Enabled);
        }

        [Test]
        public void BadData_IsRejected()
        {
            string nations = TestData.Text("nations.json"), rules = TestData.Text("rules.json"), ai = TestData.Text("ai.json");
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules.Replace("\"warChance_bp\"", "\"warChanceTypo_bp\""), ai),
                "unknown field");
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules.Replace("\"garrisonPerEdge\": 10", "\"garrisonPerEdge\": 0"), ai));
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules, ai.Replace("\"default\": \"base\"", "\"default\": \"nope\"")));
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations.Replace("\"id\": 5", "\"id\": 15"), rules, ai), "ids not contiguous");
            Assert.Throws<System.FormatException>(() => Rules.FromJson("[{\"id\":0,\"name\":\"\",\"color\":\"\"},{\"id\":1,\"name\":\"\",\"color\":\"\"}]", rules, ai),
                "one nation is not a match");
        }

        [Test]
        public void Assign_AndProfileOverride()
        {
            var r = Rules.FromJson(TestData.Text("nations.json"), TestData.Text("rules.json"),
                TestData.Text("ai.json").Replace("\"assign\": {}", "\"assign\": { \"3\": \"no_forts\" }"));
            Assert.AreEqual("no_forts", r.ProfileName(3));
            Assert.AreEqual(1000000, r.Profile(3).FortLossThresholdPx);
            r.AssignProfile(3, "no_strikes");
            Assert.AreEqual(1000000, r.Profile(3).StrikeMinBattalions);
            Assert.Throws<System.FormatException>(() => r.AssignProfile(1, "missing"));
        }

        [Test]
        public void FortCost_FollowsTable_ThenTimesOneAndAHalf()
        {
            var r = Rules.Default(8);
            Assert.AreEqual(40000, r.FortCost(0));
            Assert.AreEqual(202500, r.FortCost(4));
            Assert.AreEqual(303750, r.FortCost(5));
            Assert.AreEqual(455625, r.FortCost(6));
        }
    }
}
