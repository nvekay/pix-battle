using System.Collections.Generic;

namespace BattlePix.Game
{
    /// <summary>
    /// Имена государств для текстов UI (спека 02a §9.3): падежи из nations.json, без склонения по окончаниям.
    /// «фронт с Ольстраном» (твор.), «столица Кесталя» (род.), «в пути к Норвене» (дат.).
    /// </summary>
    public sealed class NationNames
    {
        readonly Dictionary<int, NationInfo> _nations = new Dictionary<int, NationInfo>();

        public NationNames(List<NationInfo> nations)
        {
            foreach (var n in nations) _nations[n.Id] = n;
        }

        public string Nominative(int id) => _nations.TryGetValue(id, out var n) && n.Name != "" ? n.Name : "Государство " + id;

        /// <summary>«Республика Альвария».</summary>
        public string Full(int id)
        {
            string form = Form(id);
            return form != "" ? form + " " + Nominative(id) : Nominative(id);
        }

        public string Form(int id) => _nations.TryGetValue(id, out var n) && n.Form != null ? n.Form : "";

        public string Genitive(int id) => Case(id, "gen");
        public string Dative(int id) => Case(id, "dat");
        public string Instrumental(int id) => Case(id, "ins");

        string Case(int id, string key) =>
            _nations.TryGetValue(id, out var n) && n.Cases != null && n.Cases.TryGetValue(key, out var s) && s != "" ? s : Nominative(id);
    }
}
