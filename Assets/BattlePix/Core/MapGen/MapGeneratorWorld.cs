namespace BattlePix.Core
{
    internal static partial class MapGenerator
    {
        /// <summary>Генерирует карту в мир: рельеф, столицы, стартовые пятна по id и стартовые ресурсы.</summary>
        public static MapGenStats Generate(World world)
        {
            var rules = world.Rules;
            var r = Run(world.Width, world.Height, rules.NationCount, rules.MapGen, ref world.Rng.Get(RngStream.MapGen));

            var terrain = world.TerrainRW;
            for (int i = 0; i < r.Terrain.Length; i++) terrain[i] = r.Terrain[i];
            world.TotalLand = r.LandCount;

            // Слои для картинки (спека 02a §5.1): высота без вызовов потока MapGen, биомы из своего потока.
            var elevation = Elevation(r.Bin);
            var biome = Biomes(world.Width, world.Height, r.Terrain, r.Bin, elevation, rules.MapGen, ref world.Rng.Get(RngStream.Biome));
            var elevationRW = world.ElevationRW;
            var biomeRW = world.BiomeRW;
            for (int i = 0; i < elevation.Length; i++)
            {
                elevationRW[i] = elevation[i];
                biomeRW[i] = biome[i];
            }

            // Стартовые пятна по id и стартовые ресурсы.
            var nations = world.NationsRW;
            var owner = world.OwnerRW;
            int rad = rules.MapGen.StartRadius;
            int w = world.Width, h = world.Height;
            for (int k = 0; k < rules.NationCount; k++)
            {
                byte id = (byte)(k + 1);
                int cap = r.Capitals[k];
                int cx = cap % w, cy = cap / w;
                int pixels = 0;
                for (int dy = -rad; dy <= rad; dy++)
                for (int dx = -rad; dx <= rad; dx++)
                {
                    if (dx * dx + dy * dy > rad * rad) continue;
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    int i = y * w + x;
                    if (r.Terrain[i] == (byte)Core.Terrain.Water || owner[i] != 0) continue;
                    owner[i] = id;
                    pixels++;
                }
                var n = nations[k];
                n.Capital = cap;
                n.Pixels = pixels;
                n.Gold = rules.Economy.StartGold_c;
                nations[k] = n;

                // Стартовое соединение (01b §5.7): слот 0, тыл, оборона, startInfantry пехоты на 100%.
                FormationSystem.CreateFormation(world, id, 0);
                for (int b = 0; b < rules.Formations.StartInfantry; b++)
                    FormationSystem.AddBattalion(world, world.FormationSlot(id, 0), UnitTypes.Infantry);
            }
            world.MapGen = r.Stats;
            world.MarkAllDirty();
            return r.Stats;
        }
    }
}
