using BattlePix.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace BattlePix.Game
{
    /// <summary>
    /// Значки столиц и подписи государств на карте (спека 02a §5.7): элементы UI Toolkit в слое markers, без GameObject.
    /// Элементы создаются раз за партию; позиции — каждый кадр после камеры, без выделений.
    /// </summary>
    public sealed class MapLabels
    {
        const float StarSize = 32f;
        const float LabelAbove = 28f;

        readonly VisualElement _layer;
        readonly NationNames _names;
        readonly Color32[] _palette;
        readonly Sprite _star;
        readonly byte _human;

        VisualElement[] _stars;
        Label[] _labels;
        int _fontSize = -1;

        public MapLabels(VisualElement layer, NationNames names, Color32[] palette, DecorAtlas atlas, byte human)
        {
            _layer = layer;
            _names = names;
            _palette = palette;
            _star = atlas != null ? atlas.Icon("capital") : null;
            _human = human;
        }

        public void BeginMatch(World world)
        {
            if (_stars != null)
                for (int i = 1; i < _stars.Length; i++)
                {
                    _stars[i].RemoveFromHierarchy();
                    _labels[i].RemoveFromHierarchy();
                }
            int n = world.NationCount;
            _stars = new VisualElement[n + 1];
            _labels = new Label[n + 1];
            for (int id = 1; id <= n; id++)
            {
                var star = new VisualElement { pickingMode = PickingMode.Ignore };
                star.AddToClassList("capital-star");
                star.style.position = Position.Absolute;
                star.style.width = StarSize;
                star.style.height = StarSize;
                if (_star != null) star.style.backgroundImage = new StyleBackground(_star);
                star.style.unityBackgroundImageTintColor = (Color)_palette[id];
                var label = new Label(_names.Nominative(id) + (id == _human ? " · вы" : "")) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("country-label");
                if (id == _human) label.AddToClassList("own");
                label.style.position = Position.Absolute;
                // Сначала подписи, потом звёзды: звезда поверх своей подписи не закрывает текст, но всегда видна.
                _layer.Insert(0, label);
                _layer.Add(star);
                _stars[id] = star;
                _labels[id] = label;
            }
            _fontSize = -1;
        }

        public void Update(World world, MapView map, Camera cam, int zoomIndex)
        {
            if (_stars == null || cam == null) return;
            var panel = _layer.panel;
            if (panel == null) return;
            int font = zoomIndex == 0 ? 20 : 30;
            bool fontChanged = font != _fontSize;
            _fontSize = font;
            var origin = _layer.worldBound.position;
            for (int id = 1; id < _stars.Length; id++)
            {
                var s = world.Nations[id - 1];
                bool show = s.Alive != 0 && s.Capital >= 0;
                var star = _stars[id];
                var label = _labels[id];
                star.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                label.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                if (!show) continue;
                if (fontChanged) label.style.fontSize = font;
                var w = map.CellToWorld(s.Capital % world.Width, s.Capital / world.Width);
                var p = RuntimePanelUtils.CameraTransformWorldToPanel(panel, w, cam) - origin;
                star.style.left = Mathf.Round(p.x - StarSize * 0.5f);
                star.style.top = Mathf.Round(p.y - StarSize * 0.5f);
                float lw = label.resolvedStyle.width;
                if (float.IsNaN(lw)) lw = 0;
                label.style.left = Mathf.Round(p.x - lw * 0.5f);
                label.style.top = Mathf.Round(p.y - StarSize * 0.5f - LabelAbove - font * 0.5f);
            }
        }
    }
}
