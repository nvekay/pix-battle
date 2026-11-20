using System.IO;
using UnityEngine;

namespace BattlePix.Core.Tests
{
    /// <summary>Правила и конфиг из настоящих файлов Data/: тесты проверяют то, что едет в игру.</summary>
    static class TestData
    {
        static string DataDir => Path.Combine(Application.streamingAssetsPath, "Data");

        public static string Text(string file) => File.ReadAllText(Path.Combine(DataDir, file));

        public static SimConfig Config() => SimConfig.FromJson(Text("sim_config.json"));

        public static Rules Rules() => Core.Rules.FromJson(Text("nations.json"), Text("rules.json"), Text("ai.json"));

        public static Simulation Match(ulong seed, int tickLimit = 0, byte human = 0, Rules rules = null) =>
            new Simulation(Config(), rules ?? Rules(), seed, tickLimit, human);

        /// <summary>Правила из Data/, но ИИ ничего не делает: для тестов отдельных команд.</summary>
        public static Rules IdleAi()
        {
            var r = Rules().Clone();
            r.Ai.Profiles["idle"] = new AiProfile
            {
                FortLossThresholdPx = 1000000, TargetDensity_milli = 0, MinBattalions = 0, StrikeMinBattalions = 1000000,
            };
            for (int id = 1; id <= r.NationCount; id++) r.AssignProfile(id, "idle");
            return r;
        }

        public static Simulation IdleMatch(ulong seed, int tickLimit = 0, byte human = 0) => Match(seed, tickLimit, human, IdleAi());
    }
}
