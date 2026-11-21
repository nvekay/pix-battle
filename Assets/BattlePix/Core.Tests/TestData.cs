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
            // Думает раз в int.MaxValue тиков со сдвигом id − 1: в тестах (≤ 30 000 тиков) — только на тике id − 1.
            // Пороги сил недостижимы, денег на набор с таким запасом нет: и этот единственный раз ничего не делает.
            r.Ai.Profiles["idle"] = new AiProfile
            {
                ThinkIntervalTicks = int.MaxValue, RetreatStrength_bp = 0, RearStrength_bp = 0, DeployStrength_bp = 1000000,
                AttackStrength_bp = 1000000, ThreatRatio_bp = int.MaxValue, RecruitReserve_c = int.MaxValue / 2,
            };
            for (int id = 1; id <= r.NationCount; id++) r.AssignProfile(id, "idle");
            return r;
        }

        public static Simulation IdleMatch(ulong seed, int tickLimit = 0, byte human = 0) => Match(seed, tickLimit, human, IdleAi());
    }
}
