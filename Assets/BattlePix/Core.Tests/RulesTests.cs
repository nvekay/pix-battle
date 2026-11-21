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
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Terrain), Newtonsoft.Json.JsonConvert.SerializeObject(r.Terrain));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Units), Newtonsoft.Json.JsonConvert.SerializeObject(r.Units));
            Assert.AreEqual(Newtonsoft.Json.JsonConvert.SerializeObject(d.Formations), Newtonsoft.Json.JsonConvert.SerializeObject(r.Formations));
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
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules.Replace("\"garrisonPerEdge\": 40", "\"garrisonPerEdge\": 0"), ai));
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules, ai.Replace("\"default\": \"base\"", "\"default\": \"nope\"")));
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations.Replace("\"id\": 5", "\"id\": 15"), rules, ai), "ids not contiguous");
            Assert.Throws<System.FormatException>(() => Rules.FromJson("[{\"id\":0,\"name\":\"\",\"color\":\"\"},{\"id\":1,\"name\":\"\",\"color\":\"\"}]", rules, ai),
                "one nation is not a match");
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules.Replace("\"format\": 2", "\"format\": 1"), ai), "old rules format");
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules.Replace("\"maxBattalions\": 12", "\"maxBattalions\": 0"), ai));
            Assert.Throws<System.FormatException>(() => Rules.FromJson(nations, rules.Replace("\"terrainDefense_bp\": [10000, 12500, 15000]", "\"terrainDefense_bp\": [10000]"), ai));
        }

        [Test]
        public void Assign_AndProfileOverride()
        {
            var r = Rules.FromJson(TestData.Text("nations.json"), TestData.Text("rules.json"),
                TestData.Text("ai.json").Replace("\"assign\": {}", "\"assign\": { \"3\": \"spread\" }"));
            Assert.AreEqual("spread", r.ProfileName(3));
            Assert.IsTrue(r.Profile(3).Spread);
            Assert.IsFalse(r.Profile(1).Spread);
            r.AssignProfile(3, "no_armor");
            Assert.AreEqual(1000000, r.Profile(3).ArmorPlainShare_bp);
            Assert.Throws<System.FormatException>(() => r.AssignProfile(1, "missing"));
        }

        [Test]
        public void UnitsValidation()
        {
            var f = new RulesFile();
            f.Units = new UnitRules[0];
            Assert.Throws<System.FormatException>(() => f.Validate(), "no unit types");
            f.Units = new UnitRules[9];
            for (int i = 0; i < 9; i++) f.Units[i] = UnitRules.CreateDefault()[0];
            Assert.Throws<System.FormatException>(() => f.Validate(), "more than 8 types");
            f.Units = UnitRules.CreateDefault();
            f.Validate();
        }
    }
}
