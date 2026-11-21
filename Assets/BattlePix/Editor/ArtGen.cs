using System;
using System.Collections.Generic;
using System.IO;
using BattlePix.Game;
using UnityEditor;
using UnityEngine;

namespace BattlePix.Editor
{
    /// <summary>
    /// Генератор картинок из текстовых описаний (спека 02a §4.4): декор карты (decor.png) и значки UI (icons.png)
    /// + ассет DecorAtlas. Спрайты декора перенесены из docs/art/mock25.py один в один. Значки 16×16 наши.
    /// Повторный запуск даёт байт в байт те же PNG.
    /// Меню: BattlePix → Generate Art. Batchmode: -executeMethod BattlePix.Editor.ArtGen.Run
    /// </summary>
    public static class ArtGen
    {
        public const string Dir = "Assets/BattlePix/Art/Generated";
        public const string DecorPng = Dir + "/decor.png";
        public const string IconsPng = Dir + "/icons.png";
        public const string AtlasPath = Dir + "/DecorAtlas.asset";

        sealed class Spr
        {
            public string Name;
            public string[] Rows;
            public Dictionary<char, Color32> Map;
        }

        static Color32 C(int r, int g, int b) => new Color32((byte)r, (byte)g, (byte)b, 255);

        static Dictionary<char, Color32> M(params object[] kv)
        {
            var d = new Dictionary<char, Color32>();
            for (int i = 0; i < kv.Length; i += 2) d[(char)kv[i]] = (Color32)kv[i + 1];
            return d;
        }

        // ---------- Декор: порядок = TerrainBaker.Decor ----------

        static readonly Spr[] DecorSprites =
        {
            new Spr { Name = "Tree", Rows = new[] { "..ggg..", ".gGGgg.", "gGGgggg", "gGggggd", "ggggddd", ".gdddd.", "..ddd..", "...t...", "...t..." },
                Map = M('G', C(132, 196, 84), 'g', C(82, 152, 62), 'd', C(46, 104, 48), 't', C(96, 66, 40)) },
            new Spr { Name = "TreeL", Rows = new[] { "...ooo...", "..oGGgo..", ".oGGgggo.", "oGGggggdo", "oGgggggdo", "ogggggddo", "oggggdddo", ".ogdddddo", "..oddddo.", "...ooto..", "....t....", "....t...." },
                Map = M('o', C(34, 70, 36), 'G', C(140, 204, 90), 'g', C(88, 160, 64), 'd', C(52, 112, 50), 't', C(96, 66, 40)) },
            new Spr { Name = "PineL", Rows = new[] { "....o....", "...oPo...", "...opdo..", "..oPpdo..", "..oppdo..", ".oPppddo.", ".opppddo.", "oPpppdddo", "oooopoooo", "....t....", "....t...." },
                Map = M('o', C(22, 52, 40), 'P', C(96, 156, 106), 'p', C(52, 110, 78), 'd', C(32, 76, 58), 't', C(90, 62, 40)) },
            new Spr { Name = "Tree2", Rows = new[] { ".ggg.", "gGggd", "ggggd", ".gdd.", "..t.." },
                Map = M('G', C(150, 206, 96), 'g', C(98, 168, 70), 'd', C(58, 118, 52), 't', C(96, 66, 40)) },
            new Spr { Name = "Pine", Rows = new[] { "..p..", ".pPp.", ".ppd.", "pPppd", ".ppd.", "pPpdd", "ppddd", "..t..", "..t.." },
                Map = M('P', C(92, 150, 104), 'p', C(50, 106, 76), 'd', C(30, 72, 56), 't', C(90, 62, 40)) },
            new Spr { Name = "SPine", Rows = new[] { "..s..", ".sPs.", ".ppd.", "sPpsd", ".ppd.", "sPpdd", "ppddd", "..t.." },
                Map = M('s', C(236, 242, 248), 'P', C(92, 150, 104), 'p', C(50, 106, 76), 'd', C(30, 72, 56), 't', C(90, 62, 40)) },
            new Spr { Name = "Cact", Rows = new[] { ".c.", ".cC", "cCc", "Cc.", ".c.", ".c." },
                Map = M('c', C(70, 140, 70), 'C', C(112, 180, 96)) },
            new Spr { Name = "Rock", Rows = new[] { ".lll.", "llmmd", "lmmdd", ".ddd." },
                Map = M('l', C(168, 164, 158), 'm', C(126, 122, 118), 'd', C(84, 80, 80)) },
            new Spr { Name = "Bush", Rows = new[] { ".gg.", "gGgd", "gggd" },
                Map = M('G', C(140, 200, 90), 'g', C(90, 156, 64), 'd', C(56, 110, 50)) },
        };

        // ---------- Значки UI 16×16 ----------

        static readonly Color32 Outline = C(24, 20, 28);

        static readonly Spr[] IconSprites =
        {
            new Spr { Name = "budget", Rows = new[] {
                "................", "................", "................",
                "oooooooooooooooo",
                "oGGGGGGGGGGGGGGo",
                "oGddGGGGGGGGddGo",
                "oGdGGGLLLLGGGdGo",
                "oGGGGLLddLLGGGGo",
                "oGGGGLLddLLGGGGo",
                "oGdGGGLLLLGGGdGo",
                "oGddGGGGGGGGddGo",
                "oGGGGGGGGGGGGGGo",
                "oooooooooooooooo" },
                Map = M('G', C(104, 168, 92), 'd', C(64, 118, 60), 'L', C(200, 236, 180)) },
            new Spr { Name = "army", Rows = new[] {
                "................", "................", "................",
                "......oooo......",
                "....ooHHHHoo....",
                "...oHHHHHHHHo...",
                "..oHHhHHHHHHHo..",
                "..oHhHHHHHHHHo..",
                "..oHHHHHHHHHHo..",
                ".oooooooooooooo.",
                "oDDDDDDDDDDDDDDo",
                ".oooooooooooooo." },
                Map = M('H', C(110, 128, 84), 'h', C(150, 170, 110), 'D', C(80, 94, 60)) },
            new Spr { Name = "armor", Rows = new[] {
                "................", "................", "................",
                ".....ooooo......",
                "....oTTTTToooooo",
                "....oTtTTTTTTTTo",
                "....oTTTTToooooo",
                "..oooooooooooo..",
                ".oTTTTTTTTTTTTo.",
                ".oTtTTTTTTTTTTo.",
                "..oooooooooooo..",
                "..oWoWoWoWoWoo..",
                "...oooooooooo..." },
                Map = M('T', C(96, 110, 80), 't', C(140, 156, 110), 'W', C(60, 60, 60)) },
            new Spr { Name = "infantry", Rows = new[] {
                "................",
                ".............oo.",
                "............oMo.",
                "...........oMo..",
                "..........oMo...",
                ".........oMo....",
                "........oMo.....",
                ".......oWWo.....",
                "......oWWWo.....",
                ".....oWWoo......",
                "....oWWo........",
                "...oWWo.........",
                "..oWWo..........",
                "..ooo..........." },
                Map = M('M', C(150, 156, 166), 'W', C(150, 104, 60)) },
            new Spr { Name = "hq", Rows = new[] {
                "................",
                "..oo............",
                "..oPoooooooooo..",
                "..oPoFFFFFFFFFo.",
                "..oPoFFfFFFFFFo.",
                "..oPoFFFFFFFFo..",
                "..oPoFFFFFFFFFo.",
                "..oPooooooooooo.",
                "..oPo...........",
                "..oPo...........",
                "..oPo...........",
                "..oPo...........",
                ".oooooo.........",
                ".oBBBBo.........",
                ".oooooo........." },
                Map = M('P', C(150, 112, 72), 'F', C(236, 236, 240), 'f', C(200, 200, 212), 'B', C(110, 84, 56)) },
            new Spr { Name = "attack", Rows = new[] {
                "................", "................",
                "........oo......",
                "........oRo.....",
                "........oRRo....",
                ".oooooooorRRo...",
                ".oRRRRRRRRRRRo..",
                ".oRRRRRRRRRRRRo.",
                ".oRRRRRRRRRRRRo.",
                ".oDDDDDDDDDDDo..",
                ".ooooooooDDDo...",
                "........oDDo....",
                "........oDo.....",
                "........oo......" },
                Map = M('R', C(232, 92, 80), 'r', C(232, 92, 80), 'D', C(176, 60, 52)) },
            new Spr { Name = "defend", Rows = new[] {
                "................", "................", "................",
                "...ooooooooooo..",
                "...osSSSosSSSo..",
                "...oSSSSoSSSSo..",
                "...oddddoddddo..",
                "oooooooooooooooo",
                "osSSSosSSSosSSSo",
                "oSSSSoSSSSoSSSSo",
                "oSSSSoSSSSoSSSSo",
                "oddddoddddoddddo",
                "oooooooooooooooo" },
                Map = M('S', C(170, 138, 90), 's', C(200, 170, 120), 'd', C(132, 104, 66)) },
            new Spr { Name = "frontOpen", Rows = new[] {
                "................",
                ".....oooooo.....",
                "...ooYYYYYYoo...",
                "..oYYooooooYYo..",
                "..oYo......oYo..",
                ".oYo........oYo.",
                ".oYo........oYo.",
                ".oYo........oYo.",
                ".oYo........oYo.",
                ".oYo........oYo.",
                "..oYo......oYo..",
                "..oYYooooooYYo..",
                "...ooYYYYYYoo...",
                ".....oooooo....." },
                Map = M('Y', C(244, 196, 72)) },
            new Spr { Name = "capital", Rows = new[] {
                ".......oo.......",
                "......oWWo......",
                "......oWWo......",
                ".....oWWWWo.....",
                "oooooWWWWWWooooo",
                "oWWWWWWWWWWWWWWo",
                ".oWWWWWWWWWWWWo.",
                "..oWWWWWWWWWWo..",
                "...oWWWWWWWWo...",
                "...oWWWWWWWWo...",
                "..oWWWWooWWWWo..",
                "..oWWWo..oWWWo..",
                ".oWWWo....oWWWo.",
                ".oWWo......oWWo.",
                ".ooo........ooo." },
                Map = M('W', C(246, 246, 246)) },
            new Spr { Name = "territory", Rows = new[] {
                "................", "................",
                ".oooooooooooooo.",
                ".oAAAAAAoBBBBBo.",
                ".oAAAAAAoBBBBBo.",
                ".oAAAAAoBBBBBBo.",
                ".oAAAAAoBBBBBBo.",
                ".oAAAAoBBBBBBBo.",
                ".oAAAAoBBBBBBBo.",
                ".oAAAAAoBBBBBBo.",
                ".oAAAAAoBBBBBBo.",
                ".oAAAAAAoBBBBBo.",
                ".oAAAAAAoBBBBBo.",
                ".oooooooooooooo." },
                Map = M('A', C(120, 160, 230), 'B', C(140, 148, 160)) },
            new Spr { Name = "time", Rows = new[] {
                "................",
                ".....oooooo.....",
                "...ooWWWWWWoo...",
                "..oWWWWoWWWWWo..",
                "..oWWWWoWWWWWo..",
                ".oWWWWWoWWWWWWo.",
                ".oWWWWWoWWWWWWo.",
                ".oWWWWWooooWWWo.",
                ".oWWWWWWWWWWWWo.",
                ".oWWWWWWWWWWWWo.",
                "..oWWWWWWWWWWo..",
                "..oWWWWWWWWWWo..",
                "...ooWWWWWWoo...",
                ".....oooooo....." },
                Map = M('W', C(232, 230, 224)) },
            new Spr { Name = "pause", Rows = new[] {
                "................", "................",
                "...oooo..oooo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oWWo..oWWo...",
                "...oooo..oooo..." },
                Map = M('W', C(232, 230, 224)) },
            new Spr { Name = "play", Rows = new[] {
                "................", "................",
                "....oo..........",
                "....oWoo........",
                "....oWWWoo......",
                "....oWWWWWoo....",
                "....oWWWWWWWoo..",
                "....oWWWWWWWWWo.",
                "....oWWWWWWWoo..",
                "....oWWWWWoo....",
                "....oWWWoo......",
                "....oWoo........",
                "....oo.........." },
                Map = M('W', C(232, 230, 224)) },
            new Spr { Name = "plains", Rows = new[] {
                "................", "................", "................", "................",
                "................", "................", "................",
                "oooooooooooooooo",
                "oGGGGGGGGGGGGGGo",
                "oGgGGGGGgGGGGGGo",
                "oGGGGgGGGGGGgGGo",
                "oDDDDDDDDDDDDDDo",
                "oooooooooooooooo" },
                Map = M('G', C(118, 176, 72), 'g', C(150, 200, 100), 'D', C(100, 72, 46)) },
            new Spr { Name = "hills", Rows = new[] {
                "................", "................", "................",
                ".....ooooooooooo",
                ".....oHHHHHHHHHo",
                ".....oHHhHHHHHHo",
                ".....oWWWWWWWWWo",
                "oooooodWWWWWWWWo",
                "oGGGGGodWWWWWWWo",
                "oGgGGGGoWWWWWWWo",
                "oGGGGGGoWWWWWWWo",
                "oDDDDDDDDDDDDDDo",
                "oooooooooooooooo" },
                Map = M('H', C(130, 148, 86), 'h', C(160, 176, 110), 'W', C(108, 92, 70), 'd', C(80, 66, 50),
                    'G', C(118, 176, 72), 'g', C(150, 200, 100), 'D', C(100, 72, 46)) },
            new Spr { Name = "mountains", Rows = new[] {
                "................", "................",
                ".......oo.......",
                "......oSSo......",
                ".....oSSSSo.....",
                ".....oSSMMo.....",
                "....oMMMMMMo....",
                "....oMMmMMMMo...",
                "...oMMMMMMMMMo..",
                "...oMMMMmMMMMMo.",
                "..oMMMMMMMMMMMMo",
                "..oMMmMMMMMMMMMo",
                ".oMMMMMMMMMMMMMo",
                "oooooooooooooooo" },
                Map = M('S', C(238, 242, 246), 'M', C(122, 116, 110), 'm', C(90, 86, 84)) },
            new Spr { Name = "urgent", Rows = new[] {
                "................",
                ".....oooooo.....",
                ".....oRRRRo.....",
                ".....oRRRRo.....",
                ".....oRRRRo.....",
                ".....oRRRRo.....",
                ".....oRRRRo.....",
                "......oRRo......",
                "......oRRo......",
                "......oooo......",
                "................",
                "......oooo......",
                "......oRRo......",
                "......oRRo......",
                "......oooo......" },
                Map = M('R', C(232, 92, 80)) },
            new Spr { Name = "loss", Rows = new[] {
                "................",
                ".....oooooo.....",
                ".....oAAAAo.....",
                ".....oAAAAo.....",
                ".....oAAAAo.....",
                ".....oAAAAo.....",
                "..ooooAAAAoooo..",
                "..oAAAAAAAAAAo..",
                "...oAAAAAAAAo...",
                "....oAAAAAAo....",
                ".....oAAAAo.....",
                "......oAAo......",
                ".......oo......." },
                Map = M('A', C(244, 170, 60)) },
            new Spr { Name = "report", Rows = new[] {
                "................",
                ".oooooooooooooo.",
                ".oWWWWWWWWWWWWo.",
                ".oWDDDDDDDDDDWo.",
                ".oWDDDDDDDDDDWo.",
                ".oWWWWWWWWWWWWo.",
                ".oWddddWWddddWo.",
                ".oWWWWWWWWWWWWo.",
                ".oWddddWWddddWo.",
                ".oWWWWWWWWWWWWo.",
                ".oWddddWWddddWo.",
                ".oWWWWWWWWWWWWo.",
                ".oooooooooooooo." },
                Map = M('W', C(226, 222, 210), 'D', C(70, 70, 80), 'd', C(150, 150, 160)) },
            new Spr { Name = "rear", Rows = new[] {
                "................",
                ".......oo.......",
                "......oRRo......",
                ".....oRRRRo.....",
                "....oRRRRRRo....",
                "...oRRRRRRRRo...",
                "..oRRRRRRRRRRo..",
                "..oooooooooooo..",
                "...oWWWWWWWWo...",
                "...oWWoooWWWo...",
                "...oWWoDoWWWo...",
                "...oWWoDoWWWo...",
                "...oooooooooo..." },
                Map = M('R', C(196, 92, 72), 'W', C(226, 220, 206), 'D', C(110, 80, 50)) },
        };

        [MenuItem("BattlePix/Generate Art")]
        public static void Generate()
        {
            Directory.CreateDirectory(Dir);
            var decor = Pack(DecorSprites, 0, false, out var decorEntries);
            var icons = Pack(IconSprites, 16, true, out var iconEntries);
            File.WriteAllBytes(DecorPng, decor);
            File.WriteAllBytes(IconsPng, icons);
            AssetDatabase.ImportAsset(DecorPng, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(IconsPng, ImportAssetOptions.ForceUpdate);
            ConfigureImporter(DecorPng);
            ConfigureImporter(IconsPng);

            var atlas = AssetDatabase.LoadAssetAtPath<DecorAtlas>(AtlasPath);
            bool created = atlas == null;
            if (created) atlas = ScriptableObject.CreateInstance<DecorAtlas>();
            atlas.Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(DecorPng);
            atlas.Sprites = decorEntries;
            atlas.Icons = AssetDatabase.LoadAssetAtPath<Texture2D>(IconsPng);
            atlas.IconEntries = iconEntries;
            if (created) AssetDatabase.CreateAsset(atlas, AtlasPath);
            else EditorUtility.SetDirty(atlas);
            AssetDatabase.SaveAssets();
            Debug.Log($"ArtGen: {decorEntries.Length} decor sprites, {iconEntries.Length} icons → {Dir}");
        }

        /// <summary>Batchmode: -executeMethod BattlePix.Editor.ArtGen.Run</summary>
        public static void Run()
        {
            try
            {
                Generate();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        static void ConfigureImporter(string path)
        {
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.textureType = TextureImporterType.Default;
            imp.isReadable = true;
            imp.mipmapEnabled = false;
            imp.filterMode = FilterMode.Point;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.alphaIsTransparency = true;
            imp.sRGBTexture = true;
            imp.SaveAndReimport();
        }

        /// <summary>
        /// Раскладывает картинки в ряд с зазором 1 px. cell > 0 — фиксированная клетка cell×cell (значки).
        /// Прямоугольники записываются с y от верха PNG.
        /// </summary>
        static byte[] Pack(Spr[] sprites, int cell, bool outlineO, out AtlasEntry[] entries)
        {
            entries = new AtlasEntry[sprites.Length];
            int x = 0, height = 0;
            var sizes = new Vector2Int[sprites.Length];
            for (int k = 0; k < sprites.Length; k++)
            {
                var s = sprites[k];
                int w = s.Rows[0].Length, h = s.Rows.Length;
                foreach (var row in s.Rows)
                    if (row.Length != w) throw new FormatException($"{s.Name}: rows of different width");
                if (cell > 0)
                {
                    if (w != cell || h > cell) throw new FormatException($"{s.Name}: must be {cell} wide and at most {cell} tall");
                    w = h = cell;
                }
                sizes[k] = new Vector2Int(w, h);
                entries[k] = new AtlasEntry { Name = s.Name, Rect = new RectInt(x, 0, w, h) };
                x += w + 1;
                if (h > height) height = h;
            }
            int width = x - 1;
            var px = new Color32[width * height];
            for (int k = 0; k < sprites.Length; k++)
            {
                var s = sprites[k];
                var r = entries[k].Rect;
                for (int j = 0; j < s.Rows.Length; j++)
                for (int i = 0; i < s.Rows[j].Length; i++)
                {
                    char ch = s.Rows[j][i];
                    if (ch == '.') continue;
                    Color32 c;
                    if (outlineO && ch == 'o') c = Outline;
                    else if (!s.Map.TryGetValue(ch, out c)) throw new FormatException($"{s.Name}: no color for '{ch}'");
                    int ty = height - 1 - (r.y + j); // Texture2D снизу вверх
                    px[ty * width + r.x + i] = c;
                }
            }
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply(false);
            var png = tex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(tex);
            return png;
        }
    }
}
