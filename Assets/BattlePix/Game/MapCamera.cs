using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BattlePix.Game
{
    /// <summary>
    /// Камера карты (спека 02a §5.5): ортографическая, 1 юнит = 1 пиксель запечённой текстуры.
    /// Три зума — k/2, k, 2k экранных пикселей на пиксель текстуры, k = max(1, высота экрана / 1080).
    /// Колесо — зум к курсору, ПКМ или средняя кнопка — перетаскивание, стрелки — сдвиг на ¼ экрана, Home — к столице.
    /// Всё на unscaled time: пауза не мешает.
    /// </summary>
    public sealed class MapCamera
    {
        const float DragThresholdPx = 4f;
        const float FlySeconds = 0.25f;

        readonly Camera _cam;
        readonly MapView _map;
        readonly Func<Vector2, bool> _overUi;

        int _k = 1;
        int _zoom = 1;      // 0 — ½, 1 — 1:1, 2 — ×2
        Vector2 _center;
        int _screenW, _screenH;

        bool _dragging, _dragArmed;
        Vector2 _dragStartMouse, _dragStartCenter;
        int _dragButton = -1;

        bool _flying;
        Vector2 _flyFrom, _flyTo;
        float _flyT;

        /// <summary>true в кадре, когда ПКМ отпущена без сдвига (это «отмена», как раньше).</summary>
        public bool RightClickCancel { get; private set; }
        /// <summary>Идёт перетаскивание — клики по карте в этот момент не считаются.</summary>
        public bool Dragging => _dragging;
        public int ZoomIndex => _zoom;
        /// <summary>Экранных пикселей на пиксель текстуры.</summary>
        public float Scale => _zoom == 0 ? _k * 0.5f : _zoom == 1 ? _k : _k * 2f;
        /// <summary>Целый масштаб интерфейса (PanelSettings): тот же k.</summary>
        public int UiScale => _k;
        public Vector2 Center => _center;

        public MapCamera(Camera cam, MapView map, Func<Vector2, bool> overUi)
        {
            _cam = cam;
            _map = map;
            _overUi = overUi;
            _cam.orthographic = true;
        }

        public void Reset(Vector3 focusWorld)
        {
            _zoom = 1;
            _flying = false;
            _dragging = _dragArmed = false;
            _center = focusWorld;
            Apply();
        }

        /// <summary>Зум ½, центр карты: скриншот всей карты для бенча (спека 02a §12.6).</summary>
        public void ShowWholeMap()
        {
            _flying = false;
            _zoom = 0;
            _center = new Vector2(_map.BakeWidth * 0.5f, _map.BakeHeight * 0.5f);
            Apply();
        }

        /// <summary>Сразу встать на точку с заданным зумом (без перелёта).</summary>
        public void ShowAt(Vector3 world, int zoom)
        {
            _flying = false;
            _zoom = Mathf.Clamp(zoom, 0, 2);
            _center = world;
            Apply();
        }

        public void FlyTo(Vector3 world)
        {
            if (_zoom == 0) SetZoom(1, null);
            _flyFrom = _center;
            _flyTo = world;
            _flyT = 0;
            _flying = true;
        }

        public void Update(Vector3 homeWorld)
        {
            RightClickCancel = false;
            if (Screen.width != _screenW || Screen.height != _screenH) Apply(); // смена размера окна: k, зумы, центр сохраняется

            var mouse = Mouse.current;
            var kb = Keyboard.current;
            if (mouse != null)
            {
                Vector2 mp = mouse.position.ReadValue();
                float wheel = mouse.scroll.ReadValue().y;
                if (wheel != 0 && !_overUi(mp))
                {
                    _flying = false;
                    SetZoom(_zoom + (wheel > 0 ? 1 : -1), mp);
                }

                // Перетаскивание: ПКМ или средняя кнопка, начинается над картой.
                if (!_dragArmed)
                {
                    int b = mouse.rightButton.wasPressedThisFrame ? 1 : mouse.middleButton.wasPressedThisFrame ? 2 : -1;
                    if (b >= 0 && !_overUi(mp))
                    {
                        _dragArmed = true;
                        _dragging = false;
                        _dragButton = b;
                        _dragStartMouse = mp;
                        _dragStartCenter = _center;
                    }
                    else if (b == 1) RightClickCancel = true; // ПКМ над панелью — тоже отмена
                }
                if (_dragArmed)
                {
                    bool held = _dragButton == 1 ? mouse.rightButton.isPressed : mouse.middleButton.isPressed;
                    Vector2 delta = mp - _dragStartMouse;
                    if (!_dragging && delta.magnitude > DragThresholdPx) _dragging = true;
                    if (_dragging)
                    {
                        _flying = false;
                        _center = _dragStartCenter - delta / Scale;
                        Apply();
                    }
                    if (!held)
                    {
                        if (!_dragging && _dragButton == 1) RightClickCancel = true;
                        _dragArmed = false;
                        _dragging = false;
                    }
                }
            }

            if (kb != null)
            {
                float stepX = Screen.width / Scale * 0.25f, stepY = Screen.height / Scale * 0.25f;
                var move = Vector2.zero;
                if (kb.leftArrowKey.wasPressedThisFrame) move.x -= stepX;
                if (kb.rightArrowKey.wasPressedThisFrame) move.x += stepX;
                if (kb.upArrowKey.wasPressedThisFrame) move.y += stepY;
                if (kb.downArrowKey.wasPressedThisFrame) move.y -= stepY;
                if (move != Vector2.zero)
                {
                    _flying = false;
                    _center += move;
                    Apply();
                }
                if (kb.homeKey.wasPressedThisFrame) FlyTo(homeWorld);
                if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame) SetZoom(_zoom + 1, null);
                if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame) SetZoom(_zoom - 1, null);
            }

            if (_flying)
            {
                _flyT += Time.unscaledDeltaTime / FlySeconds;
                float t = Mathf.Clamp01(_flyT);
                t = t * t * (3 - 2 * t);
                _center = Vector2.Lerp(_flyFrom, _flyTo, t);
                if (_flyT >= 1) _flying = false;
                Apply();
            }
        }

        /// <summary>Шаг зума; точка под курсором (если задан) остаётся на месте.</summary>
        void SetZoom(int zoom, Vector2? anchorScreen)
        {
            zoom = Mathf.Clamp(zoom, 0, 2);
            if (zoom == _zoom) return;
            Vector2 before = default;
            if (anchorScreen.HasValue) before = ScreenToWorld(anchorScreen.Value);
            _zoom = zoom;
            if (anchorScreen.HasValue)
            {
                // Новый центр: та же мировая точка под курсором при новом масштабе.
                Vector2 offsetPx = anchorScreen.Value - new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                _center = before - offsetPx / Scale;
            }
            Apply();
        }

        Vector2 ScreenToWorld(Vector2 s) => _center + (s - new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)) / Scale;

        void Apply()
        {
            _screenW = Screen.width;
            _screenH = Screen.height;
            _k = Mathf.Max(1, Screen.height / 1080);
            float z = Scale;
            _cam.orthographicSize = Screen.height / (2f * z);

            // Центр зажат в пределах карты; если карта меньше экрана — по центру карты.
            float halfW = Screen.width / (2f * z), halfH = Screen.height / (2f * z);
            float mw = _map.BakeWidth, mh = _map.BakeHeight;
            _center.x = mw <= 2 * halfW ? mw * 0.5f : Mathf.Clamp(_center.x, halfW, mw - halfW);
            _center.y = mh <= 2 * halfH ? mh * 0.5f : Mathf.Clamp(_center.y, halfH, mh - halfH);

            // Позиция округляется до 1/z юнита, чтобы пиксели текстуры не плавали. Для нечётного размера экрана
            // центр экрана попадает между пикселями: сдвигаем на полпикселя экрана.
            float px = Mathf.Round(_center.x * z) / z + (Screen.width % 2) * 0.5f / z;
            float py = Mathf.Round(_center.y * z) / z + (Screen.height % 2) * 0.5f / z;
            var p = _cam.transform.position;
            _cam.transform.position = new Vector3(px, py, p.z);
            _map.SetZoomIndex(_zoom);
        }
    }
}
