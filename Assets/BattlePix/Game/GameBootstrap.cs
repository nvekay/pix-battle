using System;
using System.Collections;
using System.Collections.Generic;
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
    /// связывает HUD, режимы постройки и удара, плашки, уведомления и экран конца.
    /// Сам мир не меняет: только команды человека через Simulation.EnqueueFromUi.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] MapView mapView;
        [SerializeField] UIDocument uiDocument;
        [SerializeField] Camera mainCamera;
        [SerializeField] string defaultSeed = "42";

        /// <summary>Человек — нация 1.</summary>
        const byte Human = 1;

        enum Mode { None, Build, Strike }

        GameData _data;
        SimConfig _config;
        Rules _rules;
        Color32[] _palette;
        NationNames _names;
        Simulation _sim;
        TickClock _clock;
        ulong _dataHash;

        Mode _mode;
        int _buildPixel = -1;
        int _strikeTarget;
        bool _endShown;

        // Для уведомлений: состояние прошлого кадра.
        bool[] _prevAlive;
        bool[] _prevFortAlive;
        bool[] _prevStrikeOnUs;

        // Метрики для строки отладки.
        readonly Stopwatch _tickWatch = new Stopwatch();
        double _msPerTickAvg;
        int _ticksThisSecond;
        float _tpsWindowStart;
        int _tps;
        float _nextUiUpdate;

        // UI
        Label _status, _pixelInfo, _tip, _toast, _modeText;
        Label _hudGold, _hudIncome, _hudArmy, _hudLand, _hudTimer;
        Label _endTitle, _endReason, _endStats;
        TextField _seedField;
        Button _btnPause, _btnX1, _btnX2, _btnX4, _btnRecruit, _btnFort, _btnStrike, _btnConfirm;
        VisualElement _mapArea, _debugPanel, _modeBar, _endScreen;
        float _toastHideAt;

        static readonly NumberFormatInfo Ru = new NumberFormatInfo { NumberDecimalSeparator = ",", NegativeSign = "−" };

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
            _config = data.Config();
            _rules = data.Rules();
            var nations = NationInfo.ParseList(data.Text(GameData.NationsFile));
            _palette = NationInfo.BuildPalette(nations);
            _names = new NationNames(nations);
            mapView.Init(_config.Width, _config.Height, _palette);
            // Сид новой партии — от времени запуска (адаптер, не ядро). Бенч и отладка задают его явно.
            ulong seed = TryGetArg("-seed", out var s) ? ParseSeed(s) : TryGetArg("-bench", out _) ? ParseSeed(defaultSeed) : TimeSeed();
            StartNewMatch(seed);
            if (TryGetArg("-bench", out var benchSeconds)) StartCoroutine(Bench(benchSeconds));
        }

        static ulong TimeSeed() => (ulong)DateTime.Now.Ticks & 0xFFFFFFFFUL;

        // ---------- отладка: замер fps в собранной игре ----------

        /// <summary>
        /// BattlePix.exe -bench 20 [-benchSpeed 4] [-benchShot shot.png]: гонит партию на заданной скорости,
        /// играет за человека простыми правилами (набор, крепость, удар), пишет fps в лог, сохраняет скриншот и реплей.
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
            var sent = new int[5];
            while (Time.unscaledTime - start < seconds && !_sim.World.IsOver)
            {
                frames++;
                worst = Mathf.Max(worst, Time.unscaledDeltaTime);
                int t = _sim.World.Tick;
                if (t % 10 == 0 && t != lastCmdTick)
                {
                    lastCmdTick = t;
                    var cmd = BenchCommand(ref rng, t);
                    if (cmd.HasValue)
                    {
                        _sim.EnqueueFromUi(cmd.Value.Type, Human, cmd.Value.A, 0);
                        sent[(int)cmd.Value.Type]++;
                    }
                }
                yield return null;
            }
            float elapsed = Time.unscaledTime - start;
            UnityEngine.Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BattlePix] bench: {0}x{1}, speed x{2}, {3:0.0} fps avg, worst frame {4:0.0} ms, tick {5}, commands recruit {6} fort {7} strike {8}",
                Screen.width, Screen.height, speed, frames / elapsed, worst * 1000f, _sim.World.Tick,
                sent[(int)CommandType.Recruit], sent[(int)CommandType.BuildFort], sent[(int)CommandType.Strike]));

            if (TryGetArg("-benchShot", out var shot))
            {
                ScreenCapture.CaptureScreenshot(shot);
                yield return new WaitForSecondsRealtime(1f);
            }
            OnSaveReplay();
            UnityEngine.Debug.Log("[BattlePix] bench: " + _toast.text);
            Application.Quit(0);
        }

        /// <summary>Простой «человек» для бенча: только команды, которые сейчас проходят валидацию.</summary>
        Command? BenchCommand(ref Pcg32 rng, int t)
        {
            var w = _sim.World;
            var me = w.Nation(Human);
            if (t % 100 == 0)
            {
                for (int i = 0; i < 40; i++)
                {
                    int x = me.Capital % w.Width + rng.NextInt(61) - 30, y = me.Capital / w.Width + rng.NextInt(61) - 30;
                    if (!w.InMap(x, y)) continue;
                    var c = new Command { Nation = Human, Type = CommandType.BuildFort, A = w.Index(x, y) };
                    if (GameCommands.Validate(w, c) == RejectReason.None) return c;
                }
            }
            if (t % 200 == 0)
                for (int d = 1; d <= w.NationCount; d++)
                {
                    var c = new Command { Nation = Human, Type = CommandType.Strike, A = d };
                    if (me.Battalions > 3 && GameCommands.Validate(w, c) == RejectReason.None) return c;
                }
            var r = new Command { Nation = Human, Type = CommandType.Recruit };
            bool saving = t % 3000 < 1200 && me.FortsAlive < 3; // копит на крепость
            if (!saving && me.Gold >= 30000 && GameCommands.Validate(w, r) == RejectReason.None) return r;
            return null;
        }

        static bool TryGetArg(string key, out string value)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, key);
            value = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            return i >= 0;
        }

        // ---------- партия ----------

        void StartNewMatch(ulong seed)
        {
            _sim?.Dispose();
            _sim = new Simulation(_config, _rules, seed, 0, Human);
            _clock = new TickClock(_config.TicksPerSecond, _config.MaxTicksPerFrame);
            SetSpeedButtons(1);
            _msPerTickAvg = 0;
            _endShown = false;
            _seedField.value = seed.ToString(CultureInfo.InvariantCulture);
            ExitMode();
            HidePanels();
            _endScreen.AddToClassList("hidden");
            mapView.BeginMatch(_sim.World, Human);
            SnapshotEvents();
            UpdateHud();
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
            float now = Time.unscaledTime;
            var w = _sim.World;
            mapView.UploadIfDirty(w, now);
            mapView.SetMarkers(w, _mode == Mode.Build ? _buildPixel : -1, _rules.Forts.FortRadius);
            mapView.SetStrikeMask(HumanStrikeMask(w));
            mapView.SetFocusNation(_mode == Mode.Strike ? _strikeTarget : 0);
            if (ticks > 0) CheckEvents();

            if (now - _tpsWindowStart >= 1f)
            {
                _tps = _ticksThisSecond;
                _ticksThisSecond = 0;
                _tpsWindowStart = now;
            }
            if (now >= _nextUiUpdate)
            {
                _nextUiUpdate = now + 0.25f;
                UpdateHud();
                UpdateStatus();
            }
            if (_toastHideAt > 0 && now >= _toastHideAt)
            {
                _toast.AddToClassList("hidden");
                _toastHideAt = 0;
            }
            if (w.IsOver && !_endShown) ShowEnd();
        }

        static int HumanStrikeMask(World w)
        {
            int mask = 0, n = w.NationCount;
            for (int d = 1; d <= n; d++)
                if (w.StrikeUntil[(Human - 1) * n + (d - 1)] > w.Tick) mask |= 1 << (d - 1);
            return mask;
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

        // ---------- уведомления ----------

        void SnapshotEvents()
        {
            var w = _sim.World;
            int n = w.NationCount;
            _prevAlive = new bool[n];
            _prevStrikeOnUs = new bool[n];
            _prevFortAlive = new bool[w.Forts.Length];
            for (int i = 0; i < n; i++) _prevAlive[i] = w.Nations[i].Alive != 0;
            for (int i = 0; i < _prevFortAlive.Length; i++) _prevFortAlive[i] = w.Forts[i].Alive != 0;
        }

        /// <summary>Три уведомления, больше никаких: удар по нам, пала наша крепость, пала чья-то столица.</summary>
        void CheckEvents()
        {
            var w = _sim.World;
            int n = w.NationCount;
            for (int d = 1; d <= n; d++)
            {
                bool on = w.StrikeUntil[(d - 1) * n + (Human - 1)] > w.Tick;
                if (on && !_prevStrikeOnUs[d - 1]) ShowToast($"{_names.Nominative(d)} ударили по нам");
                _prevStrikeOnUs[d - 1] = on;
            }
            int max = w.MaxFortsPerNation;
            for (int i = 0; i < _prevFortAlive.Length; i++)
            {
                bool alive = w.Forts[i].Alive != 0;
                if (_prevFortAlive[i] && !alive && i / max == Human - 1 && w.Nation(Human).Alive != 0) ShowToast("Пала наша крепость");
                _prevFortAlive[i] = alive;
            }
            for (int d = 1; d <= n; d++)
            {
                bool alive = w.Nations[d - 1].Alive != 0;
                if (_prevAlive[d - 1] && !alive && d != Human) ShowToast($"Пала столица {_names.Genitive(d)}");
                _prevAlive[d - 1] = alive;
            }
        }

        // ---------- UI ----------

        void BindUi()
        {
            var root = uiDocument.rootVisualElement;
            _status = root.Q<Label>("status");
            _pixelInfo = root.Q<Label>("pixel-info");
            _tip = root.Q<Label>("tip");
            _toast = root.Q<Label>("toast");
            _modeText = root.Q<Label>("mode-text");
            _hudGold = root.Q<Label>("hud-gold");
            _hudIncome = root.Q<Label>("hud-income");
            _hudArmy = root.Q<Label>("hud-army");
            _hudLand = root.Q<Label>("hud-land");
            _hudTimer = root.Q<Label>("hud-timer");
            _endTitle = root.Q<Label>("end-title");
            _endReason = root.Q<Label>("end-reason");
            _endStats = root.Q<Label>("end-stats");
            _seedField = root.Q<TextField>("seed");
            _seedField.value = defaultSeed;
            _mapArea = root.Q<VisualElement>("map-area");
            _debugPanel = root.Q<VisualElement>("debug-panel");
            _modeBar = root.Q<VisualElement>("mode-bar");
            _endScreen = root.Q<VisualElement>("end-screen");

            _btnPause = root.Q<Button>("btn-pause");
            _btnX1 = root.Q<Button>("btn-x1");
            _btnX2 = root.Q<Button>("btn-x2");
            _btnX4 = root.Q<Button>("btn-x4");
            _btnRecruit = root.Q<Button>("btn-recruit");
            _btnFort = root.Q<Button>("btn-fort");
            _btnStrike = root.Q<Button>("btn-strike");
            _btnConfirm = root.Q<Button>("btn-confirm");

            _btnPause.clicked += () => { _clock?.Pause(); SetSpeedButtons(0); };
            _btnX1.clicked += () => SetSpeed(1);
            _btnX2.clicked += () => SetSpeed(2);
            _btnX4.clicked += () => SetSpeed(4);
            root.Q<Button>("btn-debug").clicked += () => _debugPanel.ToggleInClassList("hidden");
            root.Q<Button>("btn-step").clicked += () =>
            {
                if (_clock == null) return;
                if (!_clock.Paused) { _clock.Pause(); SetSpeedButtons(0); }
                _clock.RequestStep();
            };
            root.Q<Button>("btn-new").clicked += OnNewMatchFromField;
            root.Q<Button>("btn-save").clicked += OnSaveReplay;

            _btnRecruit.clicked += OnRecruit;
            _btnFort.clicked += OnFort;
            _btnStrike.clicked += OnStrike;
            _btnConfirm.clicked += OnConfirm;
            root.Q<Button>("btn-cancel").clicked += ExitMode;

            root.Q<Button>("btn-again").clicked += () => StartNewMatch(TimeSeed());
            root.Q<Button>("btn-same").clicked += () => StartNewMatch(_sim.Seed);
            root.Q<Button>("btn-end-save").clicked += OnSaveReplay;

            // Плашка и тап-подсказка закрываются любым тапом мимо них.
            root.RegisterCallback<PointerDownEvent>(evt =>
            {
                bool panelOpen = !_pixelInfo.ClassListContains("hidden") || !_tip.ClassListContains("hidden");
                if (panelOpen && evt.target != _pixelInfo && evt.target != _tip)
                {
                    HidePanels();
                    if (evt.target == _mapArea && _mode == Mode.None) evt.StopPropagation();
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

        string Gold(long c) => (c / 100).ToString(CultureInfo.InvariantCulture);
        string PerSec(long c) => (c >= 0 ? "+" : "") + (c / 100.0).ToString("0.0", Ru);

        void UpdateHud()
        {
            var w = _sim.World;
            var me = w.Nation(Human);
            var e = _rules.Economy;
            int net = EconomySystem.NetIncome(w, Human);
            int upkeep = me.Battalions * e.Upkeep_c_per_battalion;
            _hudGold.text = "Золото " + Gold(me.Gold);
            _hudIncome.text = PerSec(net) + "/с";
            _hudArmy.text = $"{me.Battalions} бат. · {PerSec(-upkeep)}/с";
            _hudLand.text = string.Format(CultureInfo.InvariantCulture, "{0}% / {1}%",
                w.TotalLand > 0 ? (long)me.Pixels * 100 / w.TotalLand : 0, _rules.End.VictoryShare_bp / 100);
            int left = Math.Max(0, _rules.End.TimeoutTicks - w.Tick) / _config.TicksPerSecond;
            _hudTimer.text = $"{left / 60:00}:{left % 60:00}";

            _btnRecruit.text = "Батальон " + Gold(e.BattalionCost_c);
            _btnFort.text = "Крепость " + Gold(_rules.FortCost(me.FortsAlive));
            _btnRecruit.EnableInClassList("disabled-look", RecruitReason() != RejectReason.None);
            _btnFort.EnableInClassList("disabled-look", FortReason() != RejectReason.None);
            _btnStrike.EnableInClassList("disabled-look", StrikeReason() != RejectReason.None);
            _btnStrike.EnableInClassList("active", _mode == Mode.Strike);
            _btnFort.EnableInClassList("active", _mode == Mode.Build);
        }

        RejectReason RecruitReason() =>
            GameCommands.Validate(_sim.World, new Command { Nation = Human, Type = CommandType.Recruit });

        /// <summary>Причины, не зависящие от места: предел крепостей и золото.</summary>
        RejectReason FortReason()
        {
            var w = _sim.World;
            var me = w.Nation(Human);
            if (me.Alive == 0) return RejectReason.NationInvalid;
            if (me.FortsAlive >= w.MaxFortsPerNation) return RejectReason.FortLimit;
            if (me.Gold < _rules.FortCost(me.FortsAlive)) return RejectReason.NotEnoughGold;
            return RejectReason.None;
        }

        RejectReason StrikeReason()
        {
            var me = _sim.World.Nation(Human);
            if (me.Alive == 0) return RejectReason.NationInvalid;
            return me.Battalions < 1 ? RejectReason.NoBattalions : RejectReason.None;
        }

        /// <summary>Тексты причин отказа (таблица раздела 10 спеки).</summary>
        string ReasonText(RejectReason r, int cost)
        {
            var w = _sim.World;
            switch (r)
            {
                case RejectReason.NotEnoughGold:
                {
                    long missing = cost - w.Nation(Human).Gold;
                    int net = EconomySystem.NetIncome(w, Human);
                    string when = net > 0 ? $"это ~{(missing + net - 1) / net} с" : "доход не покрывает содержание";
                    return $"Не хватает {(missing + 99) / 100} золота, {when}";
                }
                case RejectReason.ArmyCap: return $"Армия на пределе: {_rules.Economy.BattalionCap} батальонов";
                case RejectReason.NotOwnPixel: return "Крепость — только на своей земле";
                case RejectReason.TooCloseToFort: return "Слишком близко к своей крепости";
                case RejectReason.FortLimit: return "Больше крепостей не построить";
                case RejectReason.NoBattalions: return "Нет батальонов для удара";
                case RejectReason.NoContact: return "С ними нет общей границы";
                case RejectReason.TargetInvalid: return "Удар — только по соседней нации";
                case RejectReason.PixelOutOfMap: return "Мимо карты";
                case RejectReason.NationInvalid: return "Наша столица пала";
                default: return r.ToString();
            }
        }

        void OnRecruit()
        {
            if (_sim.World.IsOver) return;
            var r = RecruitReason();
            if (r != RejectReason.None) { ShowTip(ReasonText(r, _rules.Economy.BattalionCost_c)); return; }
            _sim.EnqueueFromUi(CommandType.Recruit, Human, 0, 0);
        }

        void OnFort()
        {
            if (_sim.World.IsOver) return;
            if (_mode == Mode.Build) { ExitMode(); return; }
            var r = FortReason();
            if (r != RejectReason.None) { ShowTip(ReasonText(r, _rules.FortCost(_sim.World.Nation(Human).FortsAlive))); return; }
            EnterMode(Mode.Build, "Тапните по своей земле, где поставить крепость");
        }

        void OnStrike()
        {
            if (_sim.World.IsOver) return;
            if (_mode == Mode.Strike) { ExitMode(); return; }
            var r = StrikeReason();
            if (r != RejectReason.None) { ShowTip(ReasonText(r, 0)); return; }
            EnterMode(Mode.Strike, "Тапните по соседу, по которому ударить");
        }

        void EnterMode(Mode mode, string text)
        {
            _mode = mode;
            _buildPixel = -1;
            _strikeTarget = 0;
            _modeText.text = text;
            _btnConfirm.AddToClassList("hidden");
            _modeBar.RemoveFromClassList("hidden");
            HidePanels();
            UpdateHud();
        }

        void ExitMode()
        {
            _mode = Mode.None;
            _buildPixel = -1;
            _strikeTarget = 0;
            _modeBar?.AddToClassList("hidden");
            if (_sim != null) UpdateHud();
        }

        void OnConfirm()
        {
            var w = _sim.World;
            if (_mode == Mode.Build && _buildPixel >= 0)
            {
                var r = GameCommands.ValidateFort(w, Human, _buildPixel);
                if (r != RejectReason.None) { ShowTip(ReasonText(r, _rules.FortCost(w.Nation(Human).FortsAlive))); return; }
                _sim.EnqueueFromUi(CommandType.BuildFort, Human, _buildPixel, 0);
            }
            else if (_mode == Mode.Strike && _strikeTarget > 0)
            {
                var r = GameCommands.Validate(w, new Command { Nation = Human, Type = CommandType.Strike, A = _strikeTarget });
                if (r != RejectReason.None) { ShowTip(ReasonText(r, 0)); return; }
                _sim.EnqueueFromUi(CommandType.Strike, Human, _strikeTarget, 0);
            }
            ExitMode();
        }

        void OnMapTap(PointerDownEvent evt)
        {
            if (_sim == null || mainCamera == null || _sim.World.IsOver) return;
            var panelRoot = uiDocument.rootVisualElement.panel.visualTree.worldBound;
            if (panelRoot.width <= 0 || panelRoot.height <= 0) return;

            // Панель: y сверху вниз; экран: y снизу вверх.
            Vector2 p = evt.position;
            var screen = new Vector3(
                p.x / panelRoot.width * Screen.width,
                Screen.height - p.y / panelRoot.height * Screen.height,
                -mainCamera.transform.position.z);
            var worldPos = mainCamera.ScreenToWorldPoint(screen);
            bool onMap = mapView.WorldToPixel(worldPos, out int x, out int y);
            var w = _sim.World;

            if (_mode == Mode.Build)
            {
                if (!onMap) { ExitMode(); return; }
                int i = w.Index(x, y);
                var r = GameCommands.ValidateFort(w, Human, i);
                if (r == RejectReason.None || r == RejectReason.NotEnoughGold)
                {
                    _buildPixel = i;
                    _modeText.text = r == RejectReason.None
                        ? $"Крепость здесь: ×{_rules.Forts.FortMult_bp / 10000} защиты в радиусе {_rules.Forts.FortRadius} px"
                        : ReasonText(r, _rules.FortCost(w.Nation(Human).FortsAlive));
                    _btnConfirm.text = "Поставить за " + Gold(_rules.FortCost(w.Nation(Human).FortsAlive));
                    _btnConfirm.EnableInClassList("hidden", r != RejectReason.None);
                }
                else ShowTip(ReasonText(r, 0));
                return;
            }
            if (_mode == Mode.Strike)
            {
                if (!onMap) { ExitMode(); return; }
                int i = w.Index(x, y);
                int o = w.IsLand(i) ? w.Owner[i] : 0;
                var r = o == 0 || o == Human
                    ? RejectReason.TargetInvalid
                    : GameCommands.Validate(w, new Command { Nation = Human, Type = CommandType.Strike, A = o });
                if (r != RejectReason.None) { ShowTip(ReasonText(r, 0)); return; }
                _strikeTarget = o;
                _modeText.text = $"Удар по {_names.Dative(o)}: −1 батальон, {_rules.Strikes.StrikeDurationTicks / _config.TicksPerSecond} с";
                _btnConfirm.text = "Ударить";
                _btnConfirm.RemoveFromClassList("hidden");
                return;
            }

            ShowPixelInfo(onMap ? InfoText(w, x, y) : "Мимо карты", evt.localPosition);
        }

        string InfoText(World w, int x, int y)
        {
            int i = w.Index(x, y);
            if (!w.IsLand(i)) return "Вода";

            // Крепость под пальцем (в пределах 3 px).
            var forts = w.Forts;
            for (int s = 0; s < forts.Length; s++)
            {
                var f = forts[s];
                if (f.Alive == 0) continue;
                int dx = f.Pixel % w.Width - x, dy = f.Pixel / w.Width - y;
                if (dx * dx + dy * dy <= 9)
                    return $"Крепость: {(f.Owner == Human ? "наша" : _names.Nominative(f.Owner))} · ×{_rules.Forts.FortMult_bp / 10000} защиты в радиусе {_rules.Forts.FortRadius} px";
            }

            int o = w.Owner[i];
            if (o == 0) return "Ничья земля";
            if (o == Human)
            {
                var me = w.Nation(Human);
                return $"Наша земля · доход {PerSec(EconomySystem.NetIncome(w, Human))}/с · батальонов {me.Battalions} · крепостей {me.FortsAlive}";
            }
            return FrontText(w, (byte)o, i);
        }

        /// <summary>Плашка фронта: те же числа из формул 5.4, что двигают границу.</summary>
        string FrontText(World w, byte o, int pixel)
        {
            string name = _names.Nominative(o);
            int len = w.FrontLength(Human, o);
            if (len <= 0) return $"{name} · с нами нет общей границы";
            var inv = Ru;
            double us = w.ArmyOnFront(Human, o) / 1000.0, them = w.ArmyOnFront(o, Human) / 1000.0;
            int powUs = w.Pow(Human, o), powThem = w.Pow(o, Human);
            string press = powThem >= powUs
                ? $"они давят {((double)powThem / powUs).ToString("0.0", inv)} : 1"
                : $"мы давим {((double)powUs / powThem).ToString("0.0", inv)} : 1";
            var sb = new System.Text.StringBuilder();
            sb.Append($"{name}\nМы: {us.ToString("0.0", inv)} бат. на {len} px · Они: {them.ToString("0.0", inv)} на {len} px → {press}");
            int n = w.NationCount;
            var reasons = new List<string>();
            int strikeMult = _rules.Strikes.StrikeMult_bp / 10000;
            if (w.StrikeUntil[(Human - 1) * n + (o - 1)] > w.Tick) reasons.Add($"наш удар ×{strikeMult}");
            if (w.StrikeUntil[(o - 1) * n + (Human - 1)] > w.Tick) reasons.Add($"их удар ×{strikeMult}");
            if ((w.FortCover[pixel] & (1 << (o - 1))) != 0) reasons.Add($"их крепость рядом ×{_rules.Forts.FortMult_bp / 10000}");
            if ((w.CapitalCover[pixel] & (1 << (o - 1))) != 0) reasons.Add($"их столица рядом ×{_rules.Front.CapitalMult_bp / 10000}");
            if (reasons.Count > 0) sb.Append("\n" + string.Join(" · ", reasons));
            int net = w.NetFlips10s(Human, o);
            sb.Append($"\nза 10 с: {(net >= 0 ? "+" : "−")}{Math.Abs(net)} px");
            return sb.ToString();
        }

        void ShowPixelInfo(string text, Vector3 local)
        {
            _pixelInfo.text = text;
            _pixelInfo.style.left = Mathf.Max(0, local.x + 12);
            _pixelInfo.style.top = Mathf.Max(0, local.y - 60);
            _pixelInfo.RemoveFromClassList("hidden");
        }

        void ShowTip(string text)
        {
            _tip.text = text;
            _tip.RemoveFromClassList("hidden");
        }

        void HidePanels()
        {
            _pixelInfo?.AddToClassList("hidden");
            _tip?.AddToClassList("hidden");
        }

        void ShowEnd()
        {
            _endShown = true;
            ExitMode();
            var w = _sim.World;
            bool win = w.Winner == Human;
            string victory = (_rules.End.VictoryShare_bp / 100) + "%";
            string reason;
            switch (w.EndReason)
            {
                case EndReason.HumanDefeated: reason = "Пала столица"; break;
                case EndReason.Timeout:
                    reason = win ? "Время вышло, крупнейшие — мы" : $"Время вышло, крупнейшие — {_names.Nominative(w.Winner)}";
                    break;
                case EndReason.Conquest:
                    bool byShare = (long)w.Nation(w.Winner).Pixels * 10000 >= (long)_rules.End.VictoryShare_bp * w.TotalLand;
                    reason = win
                        ? (byShare ? $"Вы взяли {victory} суши" : "Все противники пали")
                        : (byShare ? $"{_names.Nominative(w.Winner)} взяли {victory} суши" : $"{_names.Nominative(w.Winner)} остались одни");
                    break;
                default: reason = w.EndReason.ToString(); break;
            }
            _endTitle.text = win ? "Победа" : "Поражение";
            _endReason.text = reason;
            var me = w.Nation(Human);
            int minute = w.Tick / (60 * _config.TicksPerSecond);
            _endStats.text = $"Минута {minute} · наша доля {(w.TotalLand > 0 ? (long)me.Pixels * 100 / w.TotalLand : 0)}%";
            _endScreen.RemoveFromClassList("hidden");
        }

        void OnNewMatchFromField()
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

        void ShowToast(string text, float seconds = 3f)
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
