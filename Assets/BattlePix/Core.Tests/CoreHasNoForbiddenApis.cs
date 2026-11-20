using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace BattlePix.Core.Tests
{
    /// <summary>
    /// Ядро — чистый C#: проходим по всем .cs в Core/ и падаем на запрещённых API.
    /// Комментарии тоже проверяются: проще не писать эти слова в ядре вообще.
    /// </summary>
    public class CoreHasNoForbiddenApis
    {
        static readonly (string name, Regex rx)[] Forbidden =
        {
            ("UnityEngine", new Regex(@"\bUnityEngine\b")),
            ("MonoBehaviour", new Regex(@"\bMonoBehaviour\b")),
            ("ScriptableObject", new Regex(@"\bScriptableObject\b")),
            ("GameObject", new Regex(@"\bGameObject\b")),
            ("float", new Regex(@"\bfloat\d?(x\d)?\b")),
            ("double", new Regex(@"\bdouble\d?(x\d)?\b")),
            ("half", new Regex(@"\bhalf\d?\b")),
            ("decimal", new Regex(@"\b[Dd]ecimal\b")),
            ("Single/Double", new Regex(@"\bSystem\.(Single|Double)\b")),
            ("Mathf", new Regex(@"\bMathf\b")),
            ("System.Random", new Regex(@"\bSystem\.Random\b")),
            ("new Random", new Regex(@"\bnew\s+Random\s*\(")),
            ("DateTime", new Regex(@"\bDateTime\b")),
            ("Stopwatch", new Regex(@"\bStopwatch\b")),
            ("Environment.TickCount", new Regex(@"\bEnvironment\.TickCount")),
            ("Time.", new Regex(@"\bTime\.(time|deltaTime|realtimeSinceStartup)")),
        };

        [Test]
        public void CoreSources_HaveNoForbiddenApis()
        {
            string coreDir = Path.Combine(Application.dataPath, "BattlePix", "Core");
            Assert.IsTrue(Directory.Exists(coreDir), coreDir);
            var files = Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories);
            Assert.IsNotEmpty(files);

            var report = new StringBuilder();
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    foreach (var (name, rx) in Forbidden)
                        if (rx.IsMatch(lines[i]))
                            report.AppendLine($"{Path.GetFileName(file)}:{i + 1}: {name}: {lines[i].Trim()}");
            }
            Assert.IsEmpty(report.ToString(), "Forbidden APIs in BattlePix.Core:\n" + report);
        }

        [Test]
        public void CoreAsmdef_DoesNotReferenceGameOrEditor()
        {
            string asmdef = File.ReadAllText(Path.Combine(Application.dataPath, "BattlePix", "Core", "BattlePix.Core.asmdef"));
            StringAssert.DoesNotContain("BattlePix.Game", asmdef);
            StringAssert.DoesNotContain("BattlePix.Editor", asmdef);
            StringAssert.Contains("\"allowUnsafeCode\": false", asmdef);
        }
    }
}
