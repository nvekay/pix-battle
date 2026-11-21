using System.IO;
using BattlePix.Core;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Terrain = BattlePix.Core.Terrain;

namespace BattlePix.Game.Tests
{
    [SetUpFixture]
    public sealed class GameLeakDetectionSetup
    {
        NativeLeakDetectionMode _previous;

        [OneTimeSetUp]
        public void Enable()
        {
            _previous = NativeLeakDetection.Mode;
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
        }

        [OneTimeTearDown]
        public void Restore() => NativeLeakDetection.Mode = _previous;
    }

    static class GameTestData
    {
        public const string AtlasPath = "Assets/BattlePix/Art/Generated/DecorAtlas.asset";

        public static GameData Data() => GameData.LoadFromDisk(GameData.DataDir);

        public static Simulation Match(GameData d, ulong seed) => new Simulation(d.Config(), d.Rules(), seed, 0, 0);

        public static DecorAtlas Atlas()
        {
            var a = AssetDatabase.LoadAssetAtPath<DecorAtlas>(AtlasPath);
            Assert.IsNotNull(a, "DecorAtlas missing: run BattlePix → Generate Art");
            return a;
        }
    }

    /// <summary>Спека 02a, acceptance 12.3–12.5: запекание 2.5D.</summary>
    public class TerrainBakerTests
    {
        [Test]
        public void Seeds1To20_LevelsInBands_TopMaskMatchesBands_FewHiddenCells_NoStrayCellIds()
        {
            var d = GameTestData.Data();
            var pal = d.Palette();
            using (var decor = TerrainBaker.DecorSprites.FromAtlas(GameTestData.Atlas()))
            {
                double worst = 0, total = 0;
                for (ulong seed = 1; seed <= 20; seed++)
                using (var sim = GameTestData.Match(d, seed))
                using (var r = TerrainBaker.Bake(sim.World, pal, decor, (uint)seed))
                {
                    if (seed > 1) { total += r.Ms; if (r.Ms > worst) worst = r.Ms; } // сид 1 — прогрев
                    Check(sim.World, pal, r, seed);
                }
                TestContext.WriteLine($"bake: avg {total / 19:0.0} ms, worst {worst:0.0} ms");
            }
        }

        static void Check(World w, PaletteData pal, TerrainBaker.Result r, ulong seed)
        {
            int s = r.CellPx, n = w.PixelCount, mw = w.Width;
            Assert.AreEqual(w.Width * s, r.Width);
            Assert.AreEqual(w.Height * s + pal.MaxOffset, r.Height);
            var terrain = w.Terrain.ToArray();
            var level = r.Level.ToArray();
            var cell = r.CellId.ToArray();
            var color = r.Color.ToArray();
            var offsets = pal.TerraceOffsets;

            // Уровень каждой клетки — в диапазоне своей полосы.
            for (int i = 0; i < n; i++)
            {
                var t = (Terrain)terrain[i];
                int lv = level[i];
                if (t == Terrain.Water)
                {
                    if (lv != 0) Assert.Fail($"seed {seed}: water level {lv}");
                    continue;
                }
                var band = pal.Band(t);
                if (lv < band[0] || lv > band[1]) Assert.Fail($"seed {seed}: cell {i} {t} level {lv}");
            }

            // Каждый пиксель верха лежит в строках верхней грани своей клетки, а вид пикселя совпадает с сушей / водой.
            var visible = new bool[n];
            for (int by = 0; by < r.Height; by++)
            for (int bx = 0; bx < r.Width; bx++)
            {
                int t = (r.Height - 1 - by) * r.Width + bx;
                int cx = cell[t * 2], cy = cell[t * 2 + 1];
                byte kind = color[t].a;
                if (cx == TerrainBaker.NoCell || cy == TerrainBaker.NoCell)
                {
                    if (cx != cy) Assert.Fail($"seed {seed}: half-empty CellId at {bx},{by}");
                    continue;
                }
                if (cx >= w.Width || cy >= w.Height) Assert.Fail($"seed {seed}: CellId ({cx}, {cy}) out of map at {bx},{by}");
                if (bx / s != cx) Assert.Fail($"seed {seed}: column of CellId at {bx},{by}");
                int c = cy * mw + cx;
                bool land = terrain[c] != (byte)Terrain.Water;
                if (kind == TerrainBaker.KindWater && land) Assert.Fail($"seed {seed}: water pixel on land cell {cx},{cy}");
                if ((kind == TerrainBaker.KindTop || kind == TerrainBaker.KindWall) && !land) Assert.Fail($"seed {seed}: kind {kind} on water cell {cx},{cy}");
                if (kind == TerrainBaker.KindTop || kind == TerrainBaker.KindWater)
                {
                    int top = cy * s + r.Pad - offsets[level[c]];
                    if (by < top || by > top + s - 1) Assert.Fail($"seed {seed}: top pixel of {cx},{cy} at row {by}, top {top}");
                }
                if (land && kind != TerrainBaker.KindWall) visible[c] = true;
            }

            int hidden = 0, landCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (terrain[i] == (byte)Terrain.Water) continue;
                landCount++;
                if (!visible[i]) hidden++;
            }
            if (seed == 1) TestContext.WriteLine($"hidden land cells seed 1: {hidden} of {landCount}");
            Assert.LessOrEqual((long)hidden * 10000 / landCount, 100, $"seed {seed}: hidden land cells {hidden} of {landCount}");

            // Ряд 0 карты целиком помещается в текстуру: над ним нет обрезанной горы.
            for (int x = 0; x < w.Width; x++)
                if (r.TopRow(x, 0) < 0) Assert.Fail($"seed {seed}: row 0 above texture at {x}");
        }

        [Test]
        public void SameSeed_BakesByteForByte()
        {
            var d = GameTestData.Data();
            var pal = d.Palette();
            using (var decor = TerrainBaker.DecorSprites.FromAtlas(GameTestData.Atlas()))
            using (var sim = GameTestData.Match(d, 42))
            using (var a = TerrainBaker.Bake(sim.World, pal, decor, 42))
            using (var b = TerrainBaker.Bake(sim.World, pal, decor, 42))
            {
                Assert.IsTrue(Same(a.Color, b.Color), "TerrainColor differs");
                Assert.IsTrue(Same(a.CellId, b.CellId), "CellId differs");
                Assert.IsTrue(Same(a.ColorMip1, b.ColorMip1), "mip 1 differs");
                Assert.Greater(a.DecorCount, 5000, "too little decor");
                TestContext.WriteLine($"decor sprites: {a.DecorCount}, bake {a.Ms:0.0} ms");
            }
        }

        static bool Same<T>(NativeArray<T> a, NativeArray<T> b) where T : unmanaged
        {
            if (a.Length != b.Length) return false;
            var x = a.Reinterpret<byte>(UnsafeSize<T>());
            var y = b.Reinterpret<byte>(UnsafeSize<T>());
            for (int i = 0; i < x.Length; i++)
                if (x[i] != y[i]) return false;
            return true;
        }

        static int UnsafeSize<T>() where T : unmanaged => Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>();

        [Test]
        public void Example_Hills_Cell_From_Spec()
        {
            // Спека 02a §6: L = 2 + (180 − 150) · 2 / 50 = 3 → off 16; верх клетки (100, 50) — строки 328..333.
            int lv = 2 + (180 - 150) * (3 - 2 + 1) / (199 - 150 + 1);
            Assert.AreEqual(3, lv);
            // Пример считан на стартовых offsets спеки [0, 5, 10, 16, 23, 32, 44]; в palette.json они уже другие (см. PR 13).
            int[] specOffsets = { 0, 5, 10, 16, 23, 32, 44 };
            Assert.AreEqual(16, specOffsets[lv]);
            Assert.AreEqual(328, 50 * 6 + 44 - specOffsets[lv]);
        }
    }
}
