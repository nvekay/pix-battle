using System.IO;
using BattlePix.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace BattlePix.Editor
{
    /// <summary>
    /// Собирает сцену Main и её ассеты кодом, чтобы сцену не приходилось мержить руками.
    /// Меню: BattlePix → Rebuild Main Scene. Batchmode: -executeMethod BattlePix.Editor.SceneBuilder.Build
    /// </summary>
    public static class SceneBuilder
    {
        const string Root = "Assets/BattlePix";
        const string ScenePath = Root + "/Scenes/Main.unity";
        const string MaterialPath = Root + "/Materials/OwnerPalette.mat";
        const string PanelSettingsPath = Root + "/UI/PanelSettings.asset";
        const string ThemePath = Root + "/UI/BattlePixTheme.tss";
        const string UxmlPath = Root + "/UI/DebugPanel.uxml";
        const string ShaderName = "BattlePix/OwnerPalette";

        // Карта 512×256 + место под панели; Pixel Perfect масштабирует целым множителем.
        const int RefResolutionX = 640;
        const int RefResolutionY = 360;

        [MenuItem("BattlePix/Rebuild Main Scene")]
        public static void Build()
        {
            var material = EnsureMaterial();
            var panelSettings = EnsurePanelSettings();
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null) throw new FileNotFoundException(UxmlPath);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Камера: ортографическая + Pixel Perfect Camera (URP).
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = RefResolutionY * 0.5f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color32(12, 16, 20, 255);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 100f;
            camGo.transform.position = new Vector3(0, 0, -10);
            camGo.AddComponent<UniversalAdditionalCameraData>();
            var ppc = camGo.AddComponent<PixelPerfectCamera>();
            ppc.assetsPPU = 1;
            ppc.refResolutionX = RefResolutionX;
            ppc.refResolutionY = RefResolutionY;

            // Карта: один квад, один MeshRenderer.
            var mapGo = new GameObject("Map");
            mapGo.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            var mr = mapGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mapGo.transform.localScale = new Vector3(512, 256, 1);
            var mapView = mapGo.AddComponent<MapView>();

            // UI Toolkit.
            var uiGo = new GameObject("UI");
            var doc = uiGo.AddComponent<UIDocument>();
            doc.panelSettings = panelSettings;
            doc.visualTreeAsset = uxml;

            // Адаптер игры.
            var gameGo = new GameObject("Game");
            var boot = gameGo.AddComponent<GameBootstrap>();
            var so = new SerializedObject(boot);
            so.FindProperty("mapView").objectReferenceValue = mapView;
            so.FindProperty("uiDocument").objectReferenceValue = doc;
            so.FindProperty("mainCamera").objectReferenceValue = cam;
            so.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();

            int renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include).Length;
            Debug.Log($"[BattlePix] Scene built: {ScenePath}, renderers in scene: {renderers}");
        }

        static Material EnsureMaterial()
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new FileNotFoundException("Shader not found: " + ShaderName);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat != null)
            {
                mat.shader = shader;
                EditorUtility.SetDirty(mat);
                return mat;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(MaterialPath));
            mat = new Material(shader) { name = "OwnerPalette" };
            AssetDatabase.CreateAsset(mat, MaterialPath);
            return mat;
        }

        static PanelSettings EnsurePanelSettings()
        {
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, PanelSettingsPath);
            }
            // Раздел 8 спеки: Constant Physical Size, Reference DPI 160. 44 единицы ≈ 7 мм ≈ 44 pt на iOS.
            ps.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            ps.referenceDpi = 160;
            ps.fallbackDpi = 160;
            ps.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (ps.themeStyleSheet == null) throw new FileNotFoundException(ThemePath);
            EditorUtility.SetDirty(ps);
            return ps;
        }
    }
}
