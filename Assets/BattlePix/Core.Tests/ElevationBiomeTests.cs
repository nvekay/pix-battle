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
    }
}
