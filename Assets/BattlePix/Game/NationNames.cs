using System.Collections.Generic;

namespace BattlePix.Game
{
    /// <summary>Имена наций для текстов UI: «Синие» → «по Синим», «столица Синих».</summary>
    public sealed class NationNames
    {
        readonly Dictionary<int, string> _names = new Dictionary<int, string>();

        public NationNames(List<NationInfo> nations)
        {
            foreach (var n in nations) _names[n.Id] = n.Name;
        }

        public string Nominative(int id) => _names.TryGetValue(id, out var s) ? s : "Нация " + id;

        /// <summary>Дательный падеж прилагательного во множественном числе: -ые/-ие → -ым/-им.</summary>
        public string Dative(int id) => Inflect(Nominative(id), "ым", "им");

        /// <summary>Родительный: -ые/-ие → -ых/-их.</summary>
        public string Genitive(int id) => Inflect(Nominative(id), "ых", "их");

        static string Inflect(string s, string y, string i)
        {
            if (s.EndsWith("ые")) return s.Substring(0, s.Length - 2) + y;
            if (s.EndsWith("ие")) return s.Substring(0, s.Length - 2) + i;
            return s;
        }
    }
}
