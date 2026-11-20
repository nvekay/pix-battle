using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using BattlePix.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace BattlePix.Game
{
    /// <summary>
    /// Адаптер игры: грузит данные, крутит Simulation по TickClock, отдаёт мир рендеру между тиками,
    /// связывает отладочную панель. Сам мир не меняет: только команды через Simulation.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] MapView mapView;
        [SerializeField] UIDocument uiDocument;
        [SerializeField] Camera mainCamera;
        [SerializeField] string defaultSeed = "42";

        GameData _data;
        SimConfig _config;
        Color32[] _palette;
        Simulation _sim;
        TickClock _clock;
        ulong _dataHash;

        // Метрики для строки состояния.
        readonly Stopwatch _tickWatch = new Stopwatch();
        double _msPerTickAvg;
        int _ticksThisSecond;
        float _tpsWindowStart;
        int _tps;
        float _nextStatusUpdate;

        // UI
        Label _status, _pixelInfo, _toast;
        TextField _seedField;
        Button _btnPause, _btnX1, _btnX2, _btnX4;
        VisualElement _mapArea;
        float _toastHideAt;

        void Start()
        {
            if (mainCamera == null) mainCamera = Camera.main;
            BindUi();
            SetStatus("Загрузка данных…");
            StartCoroutine(GameData.Load(OnDataLoaded, err => SetStatus("Ошибка данных: " + err)));
        }

        void OnDataLoaded(GameData data)
        {
            _data = data;
            _dataHash = data.DataHash;
            _config = SimConfig.FromJson(data.Text(GameData.ConfigFile));
            _palette = NationInfo.BuildPalette(NationInfo.ParseList(data.Text(GameData.NationsFile)));
            mapView.Init(_config.Width, _config.Height, _palette);
            StartNewMatch(ParseSeed(defaultSeed));
            if (TryGetArg("-bench", out var benchSeconds)) StartCoroutine(Bench(benchSeconds));
        }

        // ---------- отладка: замер fps в собранной игре ----------

        /// <summary>
        /// BattlePix.exe -bench 20 [-benchSpeed 4] [-benchShot shot.png]: гонит партию на заданной скорости,
        /// раз в 10 тиков отдаёт команду «как из UI», пишет fps в лог, сохраняет скриншот и реплей, выходит.
        /// Реплей потом проверяется раннером в -replay: так сверяем игру и headless.
        /// </summary>
        IEnumerator Bench(string secondsArg)
        {
            float seconds = float.TryParse(secondsArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 20f;
            int speed = TryGetArg("-benchSpeed", out var sp) && int.TryParse(sp, out var v) ? v : 4;
            Application.runInBackground = true; // замер не должен зависеть от фокуса окна
            SetSpeed(speed);
            yield return new WaitForSecondsRealtime(2f); // прогрев

            int frames = 0;
            float worst = 0f, start = Time.unscaledTime;
            var rng = new Pcg32(_sim.Seed, 0xBE7C);
            int lastCmdTick = -1;
            while (Time.unscaledTime - start < seconds)
            {
                frames++;
                worst = Mathf.Max(worst, Time.unscaledDeltaTime);
                int t = _sim.World.Tick;
                if (t % 10 == 0 && t != lastCmdTick)
                {
                    lastCmdTick = t;
                    _sim.EnqueueFromUi(CommandType.DebugSetOwner, 0, rng.NextInt(_sim.World.PixelCount), rng.NextInt(9));
                }
                yield return null;
            }
            float elapsed = Time.unscaledTime - start;
            UnityEngine.Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BattlePix] bench: {0}x{1}, speed x{2}, {3:0.0} fps avg, worst frame {4:0.0} ms, tick {5}, {6} commands",
                Screen.width, Screen.height, speed, frames / elapsed, worst * 1000f, _sim.World.Tick, _sim.CommandLog.Count));

            if (TryGetArg("-benchShot", out var shot))
            {
                ScreenCapture.CaptureScreenshot(shot);
                yield return new WaitForSecondsRealtime(1f);
            }
            OnSaveReplay();
            UnityEngine.Debug.Log("[BattlePix] bench: " + _toast.text);
            Application.Quit(0);
        }

        static bool TryGetArg(string key, out string value)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, key);
            value = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            return i >= 0;
        }

        void StartNewMatch(ulong seed)
        {
            _sim?.Dispose();
            _sim = new Simulation(_config, seed);
            _clock = new TickClock(_config.TicksPerSecond, _config.MaxTicksPerFrame);
            SetSpeedButtons(1);
            _msPerTickAvg = 0;
            HidePixelInfo();
            mapView.UploadIfDirty(_sim.World);
        }

        void Update()
        {
            if (_sim == null) return;

            int ticks = _clock.TicksForFrame(Time.unscaledDeltaTime);
            for (int i = 0; i < ticks && !_sim.World.IsOver; i++)
            {
                _tickWatch.Restart();
                _sim.Step();
                _tickWatch.Stop();
                double ms = _tickWatch.Elapsed.TotalMilliseconds;
                _msPerTickAvg = _msPerTickAvg <= 0 ? ms : _msPerTickAvg * 0.95 + ms * 0.05;
                _ticksThisSecond++;
            }

            // Рендер читает мир только здесь, между тиками.
            mapView.UploadIfDirty(_sim.World);

            float now = Time.unscaledTime;
            if (now - _tpsWindowStart >= 1f)
            {
                _tps = _ticksThisSecond;
                _ticksThisSecond = 0;
                _tpsWindowStart = now;
            }
            if (now >= _nextStatusUpdate)
            {
                _nextStatusUpdate = now + 0.25f;
                UpdateStatus();
            }
            if (_toast != null && _toastHideAt > 0 && now >= _toastHideAt)
            {
                _toast.AddToClassList("hidden");
                _toastHideAt = 0;
            }
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus && Application.platform == RuntimePlatform.Android && _clock != null)
            {
                _clock.Pause();
                SetSpeedButtons(0);
            }
        }

        void OnDestroy()
        {
            _sim?.Dispose();
            _sim = null;
        }

        // ---------- UI ----------

        void BindUi()
        {
            var root = uiDocument.rootVisualElement;
            _status = root.Q<Label>("status");
            _pixelInfo = root.Q<Label>("pixel-info");
            _toast = root.Q<Label>("toast");
            _seedField = root.Q<TextField>("seed");
            _seedField.value = defaultSeed;
            _mapArea = root.Q<VisualElement>("map-area");

            _btnPause = root.Q<Button>("btn-pause");
            _btnX1 = root.Q<Button>("btn-x1");
            _btnX2 = root.Q<Button>("btn-x2");
            _btnX4 = root.Q<Button>("btn-x4");

            _btnPause.clicked += () => { _clock?.Pause(); SetSpeedButtons(0); };
            _btnX1.clicked += () => SetSpeed(1);
            _btnX2.clicked += () => SetSpeed(2);
            _btnX4.clicked += () => SetSpeed(4);
            root.Q<Button>("btn-step").clicked += () =>
            {
                if (_clock == null) return;
                if (!_clock.Paused) { _clock.Pause(); SetSpeedButtons(0); }
                _clock.RequestStep();
            };
            root.Q<Button>("btn-new").clicked += OnNewMatch;
            root.Q<Button>("btn-save").clicked += OnSaveReplay;

            // Тап по карте показывает пиксель; любой следующий тап (по карте или мимо плашки) её закрывает.
            root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (!_pixelInfo.ClassListContains("hidden") && evt.target != _pixelInfo)
                {
                    HidePixelInfo();
                    if (evt.target == _mapArea) evt.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            _mapArea.RegisterCallback<PointerDownEvent>(OnMapTap);
        }

        void SetSpeed(int speed)
        {
            _clock?.SetSpeed(speed);
            SetSpeedButtons(speed);
        }

        void SetSpeedButtons(int speed)
        {
            _btnPause?.EnableInClassList("active", speed == 0);
            _btnX1?.EnableInClassList("active", speed == 1);
            _btnX2?.EnableInClassList("active", speed == 2);
            _btnX4?.EnableInClassList("active", speed == 4);
        }

        void OnMapTap(PointerDownEvent evt)
        {
            if (_sim == null || mainCamera == null) return;
            var panelRoot = uiDocument.rootVisualElement.panel.visualTree.worldBound;
            if (panelRoot.width <= 0 || panelRoot.height <= 0) return;

            // Панель: y сверху вниз; экран: y снизу вверх.
            Vector2 p = evt.position;
            var screen = new Vector3(
                p.x / panelRoot.width * Screen.width,
                Screen.height - p.y / panelRoot.height * Screen.height,
                -mainCamera.transform.position.z);
            var worldPos = mainCamera.ScreenToWorldPoint(screen);

            string text;
            if (mapView.WorldToPixel(worldPos, out int x, out int y))
            {
                int owner = _sim.World.Owner[_sim.World.Index(x, y)];
                text = $"({x}, {y})  владелец: {(owner == 0 ? "ничья" : owner.ToString())}";
            }
            else
            {
                text = "Мимо карты";
            }

            var local = _mapArea.WorldToLocal(p);
            _pixelInfo.text = text;
            _pixelInfo.style.left = Mathf.Max(0, local.x + 12);
            _pixelInfo.style.top = Mathf.Max(0, local.y - 52);
            _pixelInfo.RemoveFromClassList("hidden");
        }

        void HidePixelInfo() => _pixelInfo?.AddToClassList("hidden");

        void OnNewMatch()
        {
            if (_config == null) return;
            if (!ulong.TryParse(_seedField.value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
            {
                ShowToast("Сид — целое число от 0 до 18446744073709551615");
                return;
            }
            StartNewMatch(seed);
        }

        void OnSaveReplay()
        {
            if (_sim == null) return;
            try
            {
                var replay = ReplayFile.Capture(_sim, BuildInfo.Version, _dataHash);
                string dir = ReplayDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{_sim.Seed}.json");
                File.WriteAllText(path, replay.ToJson());
                ShowToast("Реплей сохранён: " + path, 8f);
            }
            catch (Exception e)
            {
                ShowToast("Не удалось сохранить реплей: " + e.Message, 8f);
            }
        }

        static string ReplayDir()
        {
            if (Application.isEditor)
                return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Replays");
            return Path.Combine(Application.persistentDataPath, "Replays");
        }

        void ShowToast(string text, float seconds = 4f)
        {
            _toast.text = text;
            _toast.RemoveFromClassList("hidden");
            _toastHideAt = Time.unscaledTime + seconds;
        }

        void UpdateStatus()
        {
            var w = _sim.World;
            string hash = _sim.HasHash ? Fnv1a64.ToHex(_sim.LastHash) : "—";
            string speed = _clock.Paused ? "пауза" : "×" + _clock.Speed;
            SetStatus($"сид {_sim.Seed}   тик {w.Tick}   хеш {hash}   {_tps} тик/с   {_msPerTickAvg:0.000} мс/тик   {speed}");
        }

        void SetStatus(string s)
        {
            if (_status != null) _status.text = s;
        }

        static ulong ParseSeed(string s) =>
            ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 42UL;
    }
}
