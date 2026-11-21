using System;
using System.IO;
using BattlePix.Core;
using BattlePix.Game;
using UnityEditor;
using UnityEngine;

namespace BattlePix.Editor
{
    /// <summary>
    /// Картинка запекания для ревью (спека 02a §13, PR 13): Logs/bake-&lt;seed&gt;.png, без тонировки владением.
    /// Меню: BattlePix → Bake Preview (сид 42). Batchmode: -executeMethod BattlePix.Editor.BakePreview.Run [-seed N]
    /// </summary>
    public static class BakePreview
    {
        [MenuItem("BattlePix/Bake Preview")]
        public static void Menu() => Debug.Log("Bake preview: " + Write(42));

        public static void Run()
        {
            try
            {
                ulong seed = 42;
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-seed") seed = ulong.Parse(args[i + 1]);
                Debug.Log("Bake preview: " + Write(seed));
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        public static string Write(ulong seed)
        {
            var data = GameData.LoadFromDisk(GameData.DataDir);
            var pal = data.Palette();
            var atlas = AssetDatabase.LoadAssetAtPath<DecorAtlas>(ArtGen.AtlasPath);
            if (atlas == null) throw new FileNotFoundException("run BattlePix → Generate Art first", ArtGen.AtlasPath);
            using (var sim = new Simulation(data.Config(), data.Rules(), seed, 0, 0))
            using (var decor = TerrainBaker.DecorSprites.FromAtlas(atlas))
            using (var r = TerrainBaker.Bake(sim.World, pal, decor, (uint)seed))
            {
                var tex = new Texture2D(r.Width, r.Height, TextureFormat.RGBA32, false);
                var px = r.Color.ToArray();
                for (int i = 0; i < px.Length; i++) px[i].a = 255;
                tex.SetPixels32(px);
                tex.Apply(false);
                Directory.CreateDirectory("Logs");
                string path = $"Logs/bake-{seed}.png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);

                // Кадр 1:1 вокруг столицы нации 1 — так игра выглядит по умолчанию.
                int cap = sim.World.Nations[0].Capital, cw = Math.Min(1920, r.Width), ch = Math.Min(1080, r.Height);
                int cx = Mathf.Clamp((cap % sim.World.Width) * r.CellPx - cw / 2, 0, r.Width - cw);
                int cy = Mathf.Clamp((cap / sim.World.Width) * r.CellPx + r.Pad - ch / 2, 0, r.Height - ch);
                var crop = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
                var cpx = new Color32[cw * ch];
                for (int j = 0; j < ch; j++)
                for (int i = 0; i < cw; i++)
                    cpx[(ch - 1 - j) * cw + i] = px[r.TexIndex(cx + i, cy + j)];
                crop.SetPixels32(cpx);
                crop.Apply(false);
                File.WriteAllBytes($"Logs/bake-{seed}-1x1.png", crop.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(crop);

                // Значки ×4 на фоне панели.
                var icons = atlas.Icons.GetPixels32();
                int iw = atlas.Icons.width, ih = atlas.Icons.height, z = 4;
                var big = new Texture2D(iw * z, ih * z, TextureFormat.RGBA32, false);
                var bpx = new Color32[iw * z * ih * z];
                for (int j = 0; j < ih * z; j++)
                for (int i = 0; i < iw * z; i++)
                {
                    var c = icons[(j / z) * iw + i / z];
                    bpx[j * iw * z + i] = c.a > 0 ? c : new Color32(24, 29, 39, 255);
                }
                big.SetPixels32(bpx);
                big.Apply(false);
                File.WriteAllBytes("Logs/icons-x4.png", big.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(big);
                return $"{path} ({r.Width}×{r.Height}, {r.DecorCount} decor sprites, bake {r.Ms:0.0} ms)";
            }
        }
    }
}
