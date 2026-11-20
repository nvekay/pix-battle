using System.IO;
using UnityEngine;

namespace BattlePix.Game
{
    /// <summary>Строка сборки для реплеев и отчётов: "&lt;version&gt;+&lt;git sha&gt;".</summary>
    public static class BuildInfo
    {
        static string _cached;

        public static string Version
        {
            get
            {
                if (_cached != null) return _cached;
                _cached = Application.version + "+" + (TryReadGitSha() ?? "nogit");
                return _cached;
            }
        }

        /// <summary>Читает sha из .git без запуска git. Работает в редакторе и в headless; в сборке вернёт null.</summary>
        static string TryReadGitSha()
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName;
                if (root == null) return null;
                string gitDir = Path.Combine(root, ".git");
                string headPath = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(headPath)) return null;
                string head = File.ReadAllText(headPath).Trim();
                if (!head.StartsWith("ref: ")) return Short(head);
                string refName = head.Substring(5);
                string refPath = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(refPath)) return Short(File.ReadAllText(refPath).Trim());
                string packed = Path.Combine(gitDir, "packed-refs");
                if (File.Exists(packed))
                    foreach (var line in File.ReadAllLines(packed))
                        if (line.EndsWith(" " + refName)) return Short(line.Substring(0, line.IndexOf(' ')));
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        static string Short(string sha) => sha.Length > 8 ? sha.Substring(0, 8) : sha;
    }
}
