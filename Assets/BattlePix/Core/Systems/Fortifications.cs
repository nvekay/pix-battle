namespace BattlePix.Core
{
    /// <summary>Круги крепостей в FortCover. FortCover — производное от Forts: бит id − 1, если живая крепость нации накрывает пиксель.</summary>
    internal static class Fortifications
    {
        /// <summary>Поставить или снять бит нации в круге fortRadius вокруг пикселя.</summary>
        public static void Stamp(World world, int pixel, byte owner, bool set)
        {
            var cover = world.FortCoverRW;
            int w = world.Width, h = world.Height, r = world.Rules.Forts.FortRadius;
            int cx = pixel % w, cy = pixel / w;
            ushort bit = (ushort)(1 << (owner - 1));
            for (int dy = -r; dy <= r; dy++)
            {
                int y = cy + dy;
                if (y < 0 || y >= h) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= w || dx * dx + dy * dy > r * r) continue;
                    int i = y * w + x;
                    cover[i] = set ? (ushort)(cover[i] | bit) : (ushort)(cover[i] & ~bit);
                }
            }
        }

        /// <summary>Крепость в слоте пала: снять её круг и заново поставить круги оставшихся живых крепостей нации рядом.</summary>
        public static void OnFortFallen(World world, int slot)
        {
            var f = world.FortsRW[slot];
            Stamp(world, f.Pixel, f.Owner, false);
            int max = world.MaxFortsPerNation, w = world.Width, r = world.Rules.Forts.FortRadius;
            long reach = 4L * r * r; // круги пересекаются, только если центры ближе 2r
            for (int k = 0; k < max; k++)
            {
                var g = world.FortsRW[(f.Owner - 1) * max + k];
                if (g.Alive == 0) continue;
                long dx = g.Pixel % w - f.Pixel % w, dy = g.Pixel / w - f.Pixel / w;
                if (dx * dx + dy * dy <= reach) Stamp(world, g.Pixel, g.Owner, true);
            }
        }

        /// <summary>CapitalCover: круги capitalRadius вокруг столиц живых наций. После генерации карты.</summary>
        public static void StampCapitals(World world)
        {
            var cover = world.CapitalCoverRW;
            int w = world.Width, h = world.Height, r = world.Rules.Front.CapitalRadius;
            for (int k = 0; k < world.NationCount; k++)
            {
                var n = world.NationsRW[k];
                if (n.Alive == 0 || n.Capital < 0) continue;
                int cx = n.Capital % w, cy = n.Capital / w;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h || dx * dx + dy * dy > r * r) continue;
                    int i = y * w + x;
                    cover[i] = (ushort)(cover[i] | (1 << k));
                }
            }
        }

        /// <summary>FortCover с нуля по живым крепостям (для тестов производного состояния).</summary>
        public static ushort[] Recompute(World world)
        {
            int n = world.PixelCount, w = world.Width, h = world.Height, r = world.Rules.Forts.FortRadius;
            var cover = new ushort[n];
            var forts = world.Forts;
            for (int s = 0; s < forts.Length; s++)
            {
                var f = forts[s];
                if (f.Alive == 0) continue;
                int cx = f.Pixel % w, cy = f.Pixel / w;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h || dx * dx + dy * dy > r * r) continue;
                    cover[y * w + x] |= (ushort)(1 << (f.Owner - 1));
                }
            }
            return cover;
        }
    }
}
