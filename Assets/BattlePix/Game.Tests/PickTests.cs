using NUnit.Framework;

namespace BattlePix.Game.Tests
{
    /// <summary>Спека 02a, acceptance 12.9: клик попадает в клетку того пикселя, на который нажали.</summary>
    public class PickTests
    {
        [Test]
        public void PickCell_ReturnsCellOfTexelUnderPoint_SkyIsNone()
        {
            // Синтетическая текстура 4×3 (строка 0 — низ): каждой текселе своя клетка, верхняя строка — небо.
            int bw = 4, bh = 3;
            var ids = new ushort[bw * bh * 2];
            for (int tr = 0; tr < bh; tr++)
            for (int bx = 0; bx < bw; bx++)
            {
                int i = tr * bw + bx;
                bool sky = tr == bh - 1;
                ids[i * 2] = sky ? TerrainBaker.NoCell : (ushort)(bx * 10 + tr);
                ids[i * 2 + 1] = sky ? TerrainBaker.NoCell : (ushort)(100 + tr);
            }
            Assert.IsTrue(MapView.PickCell(ids, bw, bh, 2.5f, 0.2f, out int x, out int y));
            Assert.AreEqual(20, x);
            Assert.AreEqual(100, y);
            Assert.IsTrue(MapView.PickCell(ids, bw, bh, 3.99f, 1.99f, out x, out y));
            Assert.AreEqual(31, x);
            Assert.AreEqual(101, y);
            Assert.IsTrue(MapView.PickCell(ids, bw, bh, 0f, 1f, out x, out y), "texel edge belongs to the texel above-right");
            Assert.AreEqual(1, x);
            Assert.IsFalse(MapView.PickCell(ids, bw, bh, 1.5f, 2.5f, out _, out _), "sky");
            Assert.IsFalse(MapView.PickCell(ids, bw, bh, -0.01f, 0.5f, out _, out _), "left of map");
            Assert.IsFalse(MapView.PickCell(ids, bw, bh, 4f, 0.5f, out _, out _), "right of map");
        }

        [Test]
        public void BakedPick_HitsTopFaceOfEveryVisibleCell()
        {
            // На настоящем запекании: центр верхней грани (CellToWorld) возвращает ту же клетку для видимых клеток.
            var d = GameTestData.Data();
            var pal = d.Palette();
            using (var decor = TerrainBaker.DecorSprites.FromAtlas(GameTestData.Atlas()))
            using (var sim = GameTestData.Match(d, 7))
            using (var r = TerrainBaker.Bake(sim.World, pal, decor, 7))
            {
                var ids = r.CellId.ToArray();
                int s = r.CellPx, ok = 0, total = 0;
                for (int y = 0; y < r.MapHeight; y += 3)
                for (int x = 0; x < r.MapWidth; x += 3)
                {
                    // Мир: y снизу; центр верхней грани = (x·S + S/2, H_b − (верх + S/2)).
                    float wx = x * s + s * 0.5f, wy = r.Height - (r.TopRow(x, y) + s * 0.5f);
                    total++;
                    if (MapView.PickCell(ids, r.Width, r.Height, wx, wy, out int px, out int py) && px == x && py == y) ok++;
                }
                // Промах — у клеток, чью середину грани закрыл более высокий южный сосед (по видимой части они кликаются).
                TestContext.WriteLine($"pick top face: {ok} of {total}");
                Assert.GreaterOrEqual((long)ok * 10000 / total, 9500);
            }
        }
    }
}
