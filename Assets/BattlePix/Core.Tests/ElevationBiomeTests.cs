using NUnit.Framework;

namespace BattlePix.Core.Tests
{
    /// <summary>Спека 02a, acceptance 12.1–12.2: высота и биомы для картинки не трогают мир этапа 1.</summary>
    public class ElevationBiomeTests
    {
        /// <summary>
        /// FNV-1a 64 от Owner, Terrain и столиц на тике 0 для сидов 1–20. Записано на main d1f213b,
        /// до правок MapGen (спека 02a §12.1). Меняется только вместе с правилами генерации суши.
        /// </summary>
        static readonly ulong[] Reference =
        {
            0x1577eabed0c88d43,
            0x0baf56a10826851f,
            0x3359a42e5e0fe8ac,
            0x8173a883cdaf192c,
            0x9c5a104bd083e7d1,
            0x6b1ed26cab04ffd7,
            0x09918c84a6f4eed7,
            0x9f6af617df9548ce,
            0xb16a6be894940168,
            0xec66422327680899,
            0x9bf18e75b3a543a1,
            0xe511308574449ad1,
            0x56872e8aba1e4447,
            0xc9152f6247c72cb1,
            0xad3d6d64ab2d4321,
            0xef2f3c1a48f3b5f2,
            0x1294eb2fde7af82f,
            0x498376a3422b44da,
            0x2c2d346d5145a049,
            0x96ca2fd60c214338,
        };

        internal static ulong WorldShapeHash(World w)
        {
            ulong h = Fnv1a64.Offset;
            h = Fnv1a64.Add(h, w.Owner.ToArray());
            h = Fnv1a64.Add(h, w.Terrain.ToArray());
            for (int i = 0; i < w.NationCount; i++) h = Fnv1a64.AddInt32(h, w.Nations[i].Capital);
            return h;
        }

        [Test]
        public void Seeds1To20_OwnerTerrainCapitals_MatchMainBeforeChange()
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            var got = new ulong[20];
            var sb = new System.Text.StringBuilder();
            for (ulong seed = 1; seed <= 20; seed++)
            {
                using (var sim = new Simulation(config, rules, seed, 0, 0))
                    got[seed - 1] = WorldShapeHash(sim.World);
                sb.Append("            ").Append(Fnv1a64.ToHex(got[seed - 1])).Append(",\n");
            }
            TestContext.WriteLine("REFERENCE\n" + sb);
            Assert.AreEqual(20, Reference.Length, "reference not recorded");
            for (int i = 0; i < 20; i++) Assert.AreEqual(Reference[i], got[i], $"seed {i + 1}: Owner/Terrain/capitals changed");
        }

        [Test]
        public void Seeds1To20_ElevationMonotoneAcrossBands_BiomeMatchesTerrain()
        {
            var config = TestData.Config();
            var rules = TestData.Rules();
            for (ulong seed = 1; seed <= 20; seed++)
            using (var sim = new Simulation(config, rules, seed, 0, 0))
            {
                var w = sim.World;
                var min = new[] { 256, 256, 256, 256 };
                var max = new[] { -1, -1, -1, -1 };
                var biomeCount = new int[10];
                for (int i = 0; i < w.PixelCount; i++)
                {
                    int t = w.Terrain[i], e = w.Elevation[i];
                    var b = (Biome)w.Biome[i];
                    if (e < min[t]) min[t] = e;
                    if (e > max[t]) max[t] = e;
                    biomeCount[(int)b]++;
                    if (t == (byte)Terrain.Water) Assert.AreEqual(Biome.None, b, $"seed {seed}: water biome at {i}");
                    else Assert.AreNotEqual(Biome.None, b, $"seed {seed}: land without biome at {i}");
                    if (t == (byte)Terrain.Hills) Assert.That(b == Biome.Hills || b == Biome.Forest, $"seed {seed}: hills biome {b}");
                    if (t == (byte)Terrain.Mountains) Assert.That(b == Biome.Mountain || b == Biome.Snow, $"seed {seed}: mountain biome {b}");
                    if (t == (byte)Terrain.Land) Assert.That(b != Biome.Hills && b != Biome.Mountain && b != Biome.Snow, $"seed {seed}: plains biome {b}");
                }
                // Корзина Elevation = bin × 255 / 1023: монотонна по bin, а полосы — пороги по bin.
                // Вода может быть выше равнины: острова, срезанные при выборе континента, остаются водой.
                Assert.LessOrEqual(max[(int)Terrain.Land], min[(int)Terrain.Hills], $"seed {seed}: plains above hills");
                Assert.LessOrEqual(max[(int)Terrain.Hills], min[(int)Terrain.Mountains], $"seed {seed}: hills above mountains");
                if (seed == 1)
                    TestContext.WriteLine("biomes seed 1: " + string.Join(", ", biomeCount));
                Assert.Greater(biomeCount[(int)Biome.Forest], 0, $"seed {seed}: no forest");
                Assert.Greater(biomeCount[(int)Biome.Grass], 0, $"seed {seed}: no grass");
            }
        }

        [Test]
        public void BiomeKnobs_DoNotChangeOwnerTerrainCapitals_ButChangeBiome()
        {
            var config = TestData.Config();
            var a = TestData.Rules();
            var b = a.Clone();
            b.MapGen.Biomes.ForestMoist_bp = 4000;
            b.MapGen.Biomes.MoistureCells = new[] { 32, 16 };
            for (ulong seed = 1; seed <= 5; seed++)
            using (var s1 = new Simulation(config, a, seed, 0, 0))
            using (var s2 = new Simulation(config, b, seed, 0, 0))
            using (var s3 = new Simulation(config, a, seed, 0, 0))
            {
                Assert.AreEqual(WorldShapeHash(s1.World), WorldShapeHash(s2.World), $"seed {seed}: biome knobs moved the world");
                CollectionAssert.AreEqual(s1.World.Elevation.ToArray(), s2.World.Elevation.ToArray(), $"seed {seed}: elevation");
                CollectionAssert.AreNotEqual(s1.World.Biome.ToArray(), s2.World.Biome.ToArray(), $"seed {seed}: biome did not change");
                CollectionAssert.AreEqual(s1.World.Biome.ToArray(), s3.World.Biome.ToArray(), $"seed {seed}: biome not deterministic");
                Assert.AreEqual(s1.World.Rng.Get(RngStream.MapGen).State, s2.World.Rng.Get(RngStream.MapGen).State, $"seed {seed}: MapGen stream");
            }
        }
    }
}
