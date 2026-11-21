using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BattlePix.Core;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace BattlePix.Game
{
    /// <summary>Файлы из StreamingAssets/Data. Ядро получает только строки JSON, чтение файлов — забота адаптера.</summary>
    public sealed class GameData
    {
        public const string ConfigFile = "sim_config.json";
        public const string NationsFile = "nations.json";
        public const string RulesFile = "rules.json";
        public const string AiFile = "ai.json";
        /// <summary>Только рендер (спека 02a §4.3): в DataHash не входит, смена цвета не ломает реплеи.</summary>
        public const string PaletteFile = "palette.json";

        /// <summary>
        /// На Android StreamingAssets нельзя перечислить, поэтому там читаем этот список.
        /// Добавил файл в Data/ — допиши его сюда.
        /// </summary>
        static readonly string[] KnownFiles = { AiFile, NationsFile, RulesFile, ConfigFile };

        /// <summary>Файлы, которые читает только рендер. Грузятся так же, но в DataHash не входят.</summary>
        static readonly string[] RenderFiles = { PaletteFile };

        /// <summary>Файлы, от которых зависит симуляция: по ним считается DataHash.</summary>
        public readonly List<KeyValuePair<string, byte[]>> Files = new List<KeyValuePair<string, byte[]>>();
        public readonly List<KeyValuePair<string, byte[]>> Render = new List<KeyValuePair<string, byte[]>>();

        static bool IsRenderFile(string name) => Array.IndexOf(RenderFiles, name) >= 0;

        public ulong DataHash => Fnv1a64.DataHash(Files);

        public SimConfig Config() => SimConfig.FromJson(Text(ConfigFile));

        public Rules Rules() => BattlePix.Core.Rules.FromJson(Text(NationsFile), Text(RulesFile), Text(AiFile));

        public string Text(string name)
        {
            foreach (var f in Files)
                if (f.Key == name) return Encoding.UTF8.GetString(StripBom(f.Value));
            foreach (var f in Render)
                if (f.Key == name) return Encoding.UTF8.GetString(StripBom(f.Value));
            throw new FileNotFoundException("Data file not loaded: " + name);
        }

        public PaletteData Palette() => PaletteData.FromJson(Text(PaletteFile));

        static byte[] StripBom(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                var r = new byte[b.Length - 3];
                Array.Copy(b, 3, r, 0, r.Length);
                return r;
            }
            return b;
        }

        public static string DataDir => Path.Combine(Application.streamingAssetsPath, "Data");

        /// <summary>Чтение с диска: редактор, ПК-сборка, headless-раннер.</summary>
        public static GameData LoadFromDisk(string dataDir)
        {
            var data = new GameData();
            foreach (var path in Directory.GetFiles(dataDir, "*", SearchOption.AllDirectories))
            {
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = path.Substring(dataDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                (IsRenderFile(rel) ? data.Render : data.Files).Add(new KeyValuePair<string, byte[]>(rel, File.ReadAllBytes(path)));
            }
            return data;
        }

        /// <summary>Корутина загрузки: на Android через UnityWebRequest, иначе с диска.</summary>
        public static IEnumerator Load(Action<GameData> done, Action<string> fail)
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                GameData d = null;
                try { d = LoadFromDisk(DataDir); }
                catch (Exception e) { fail(e.Message); yield break; }
                done(d);
                yield break;
            }

            var data = new GameData();
            var names = new List<string>(KnownFiles);
            names.AddRange(RenderFiles);
            foreach (var name in names)
            {
                using (var req = UnityWebRequest.Get(DataDir + "/" + name))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        fail($"{name}: {req.error}");
                        yield break;
                    }
                    (IsRenderFile(name) ? data.Render : data.Files).Add(new KeyValuePair<string, byte[]>(name, req.downloadHandler.data));
                }
            }
            done(data);
        }
    }

    /// <summary>Имя и цвет нации для игры. Ядро из nations.json читает только id.</summary>
    public sealed class NationInfo
    {
        [JsonProperty("id")] public int Id;
        [JsonProperty("name")] public string Name = "";
        [JsonProperty("color")] public string Color = "#000000";

        public static List<NationInfo> ParseList(string json) =>
            JsonConvert.DeserializeObject<List<NationInfo>>(json) ?? new List<NationInfo>();

        /// <summary>Палитра на 256 цветов, индекс = id владельца. Индекс 0 — ничья земля.</summary>
        public static Color32[] BuildPalette(List<NationInfo> nations)
        {
            var palette = new Color32[256];
            for (int i = 0; i < palette.Length; i++) palette[i] = new Color32(255, 0, 255, 255); // нет цвета — видно сразу
            foreach (var n in nations)
            {
                if (n.Id < 0 || n.Id > 255) continue;
                if (ColorUtility.TryParseHtmlString(n.Color, out var c)) palette[n.Id] = c;
            }
            return palette;
        }
    }
}
