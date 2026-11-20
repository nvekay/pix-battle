using System.Collections.Generic;

namespace BattlePix.Core
{
    /// <summary>FNV-1a 64: offset 0xcbf29ce484222325, prime 0x100000001b3.</summary>
    public static class Fnv1a64
    {
        public const ulong Offset = 0xcbf29ce484222325UL;
        public const ulong Prime = 0x100000001b3UL;

        public static ulong Add(ulong h, byte b) => unchecked((h ^ b) * Prime);

        public static ulong Add(ulong h, byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++) h = unchecked((h ^ bytes[i]) * Prime);
            return h;
        }

        public static ulong AddInt32(ulong h, int v)
        {
            uint u = unchecked((uint)v);
            h = Add(h, (byte)u);
            h = Add(h, (byte)(u >> 8));
            h = Add(h, (byte)(u >> 16));
            h = Add(h, (byte)(u >> 24));
            return h;
        }

        public static ulong AddUInt64(ulong h, ulong v)
        {
            for (int i = 0; i < 8; i++) h = Add(h, (byte)(v >> (8 * i)));
            return h;
        }

        public static string ToHex(ulong h) => "0x" + h.ToString("x16");

        public static bool TryParseHex(string s, out ulong value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// DataHash: FNV-1a 64 по байтам всех файлов данных в порядке сортировки путей (ordinal).
        /// Ключ — относительный путь с прямыми слешами, значение — содержимое файла.
        /// </summary>
        public static ulong DataHash(IReadOnlyList<KeyValuePair<string, byte[]>> files)
        {
            var sorted = new List<KeyValuePair<string, byte[]>>(files);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            ulong h = Offset;
            foreach (var f in sorted) h = Add(h, f.Value);
            return h;
        }
    }
}
