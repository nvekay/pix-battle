namespace BattlePix.Core
{
    internal static partial class MapGenerator
    {
        /// <summary>
        /// Высота для картинки (спека 02a §5.1): Elevation = bin × 255 / (Bins − 1), деление к нулю, на каждый пиксель.
        /// Корзины уже посчитаны для порога суши и полос рельефа, поэтому Elevation монотонна по полосам. Без вызовов потока.
        /// </summary>
        internal static byte[] Elevation(int[] bin)
        {
            var e = new byte[bin.Length];
            for (int i = 0; i < bin.Length; i++) e[i] = (byte)(bin[i] * 255 / (Bins - 1));
            return e;
        }

        /// <summary>
        /// Биомы (спека 02a §5.1). Влажность — Fbm из потока Biome (поток MapGen не трогается), температура — от широты
        /// и высоты. Правила применяются по порядку, последнее выигрывает.
        /// </summary>
        internal static byte[] Biomes(int w, int h, byte[] terrain, int[] bin, byte[] elevation, MapGenRules mg, ref Pcg32 rng)
        {
            var b = mg.Biomes;
            int n = w * h;
            var noise = new long[n];
            Fbm(w, h, b.MoistureCells, mg.Persistence_bp, ref rng, noise);
            long mMin = long.MaxValue, mMax = long.MinValue;
            int bMin = int.MaxValue, bMax = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (noise[i] < mMin) mMin = noise[i];
                if (noise[i] > mMax) mMax = noise[i];
                if (terrain[i] == (byte)Core.Terrain.Water) continue;
                if (bin[i] < bMin) bMin = bin[i];
                if (bin[i] > bMax) bMax = bin[i];
            }
            long mRange = mMax - mMin;
            int bRange = bMax - bMin;

            // Снег — верхние snowShare гор по Elevation: наибольшая высота, выше которой набралось не меньше доли.
            var hist = new int[256];
            int mountains = 0;
            for (int i = 0; i < n; i++)
                if (terrain[i] == (byte)Core.Terrain.Mountains) { hist[elevation[i]]++; mountains++; }
            long snowNeed = (long)mountains * b.SnowShareOfMountains_bp / 10000;
            int snowFrom = 256;
            if (snowNeed > 0)
            {
                long cum = 0;
                for (int e = 255; e >= 0; e--)
                {
                    cum += hist[e];
                    if (cum >= snowNeed) { snowFrom = e; break; }
                }
            }

            var queue = new int[n];
            var coast = b.BeachMaxDist > 0 ? CoastDistance(w, h, terrain, queue) : null;

            var biome = new byte[n];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                byte t = terrain[i];
                if (t == (byte)Core.Terrain.Water)
                {
                    biome[i] = (byte)Biome.None;
                    continue;
                }
                int moist = mRange > 0 ? (int)((noise[i] - mMin) * 10000 / mRange) : 5000;
                int hl = bRange > 0 ? (bin[i] - bMin) * 10000 / bRange : 0;
                int temp = (h > 1 ? y * 10000 / (h - 1) : 0) - hl / 2;

                var bb = Biome.Grass;
                if (moist > b.ForestMoist_bp) bb = Biome.Forest;
                if (moist < b.SteppeMoist_bp) bb = Biome.Steppe;
                if (moist < b.DesertMoist_bp && temp > b.DesertTemp_bp) bb = Biome.Desert;
                if (temp < b.TundraTemp_bp) bb = Biome.Tundra;
                if (t == (byte)Core.Terrain.Hills) bb = moist > b.HillsForestMoist_bp ? Biome.Forest : Biome.Hills;
                if (t == (byte)Core.Terrain.Mountains) bb = elevation[i] >= snowFrom ? Biome.Snow : Biome.Mountain;
                if (t == (byte)Core.Terrain.Land && coast != null && coast[i] <= b.BeachMaxDist && temp > b.TundraTemp_bp) bb = Biome.Beach;
                biome[i] = (byte)bb;
            }
            return biome;
        }
    }
}
