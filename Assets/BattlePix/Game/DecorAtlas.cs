using System;
using UnityEngine;

namespace BattlePix.Game
{
    /// <summary>Прямоугольник картинки в атласе. Rect.y считается от верха PNG, якорь спрайта декора — низ-центр.</summary>
    [Serializable]
    public struct AtlasEntry
    {
        public string Name;
        public RectInt Rect;
    }

    /// <summary>
    /// Атлас картинок (спека 02a §4.4): декор карты (decor.png) и значки UI 16×16 (icons.png).
    /// Генерирует BattlePix → Generate Art (Editor/ArtGen), руками не правится.
    /// </summary>
    public sealed class DecorAtlas : ScriptableObject
    {
        public Texture2D Texture;
        /// <summary>В порядке TerrainBaker.Decor.</summary>
        public AtlasEntry[] Sprites;
        public Texture2D Icons;
        public AtlasEntry[] IconEntries;

        Sprite[] _iconSprites;

        /// <summary>Значок UI как Sprite (для UI Toolkit). null, если такого нет.</summary>
        public Sprite Icon(string name)
        {
            if (Icons == null || IconEntries == null) return null;
            if (_iconSprites == null || _iconSprites.Length != IconEntries.Length) _iconSprites = new Sprite[IconEntries.Length];
            for (int i = 0; i < IconEntries.Length; i++)
            {
                if (IconEntries[i].Name != name) continue;
                if (_iconSprites[i] == null)
                {
                    var r = IconEntries[i].Rect;
                    var rect = new Rect(r.x, Icons.height - r.y - r.height, r.width, r.height);
                    _iconSprites[i] = Sprite.Create(Icons, rect, new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
                    _iconSprites[i].name = name;
                }
                return _iconSprites[i];
            }
            return null;
        }
    }
}
