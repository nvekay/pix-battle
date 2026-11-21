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
    /// связывает HUD, плашки, уведомления и экран конца. Переходный UI PR 2 переделки (спека 01b): кнопки управляют
    /// первым соединением, клик по соседу назначает его на фронт. Панели «Фронты» и «Соединения» — PR 4.
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

        GameData _data;
        SimConfig _config;
        Rules _rules;
        Color32[] _palette;
        NationNames _names;
        Simulation _sim;
        TickClock _clock;
        ulong _dataHash;

        bool _endShown;

        // Для уведомлений: состояние прошлого кадра.
        bool[] _prevAlive;
        byte[] _prevFront;

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
        Button _btnPause, _btnX1, _btnX2, _btnX4, _btnRecruit, _btnFort, _btnStrike;
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
        /// играет за человека простыми правилами (набор, назначение, приказ), пишет fps в лог, сохраняет скриншот и реплей.
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
            var sent = new int[8];
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
                        _sim.EnqueueFromUi(cmd.Value.Type, Human, cmd.Value.A, cmd.Value.B);
                        sent[(int)cmd.Value.Type]++;
                    }
                }
                yield return null;
            }
            float elapsed = Time.unscaledTime - start;
            UnityEngine.Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[BattlePix] bench: {0}x{1}, speed x{2}, {3:0.0} fps avg, worst frame {4:0.0} ms, tick {5}, commands recruit {6} assign {7} order {8}",
                Screen.width, Screen.height, speed, frames / elapsed, worst * 1000f, _sim.World.Tick,
                sent[(int)CommandType.RecruitUnit], sent[(int)CommandType.AssignFormation], sent[(int)CommandType.SetOrder]));

            if (TryGetArg("-benchShot", out var shot))
            {
                ScreenCapture.CaptureScreenshot(shot);
                yield return new WaitForSecondsRealtime(1f);
            }
            OnSaveReplay();
            UnityEngine.Debug.Log("[BattlePix] bench: " + _toast.text);
            Application.Quit(0);
        }

        /// <summary>
        /// Простой «человек» для бенча: только команды, которые сейчас проходят валидацию. Набирает пехоту и броню,
        /// новые соединения, ставит их на фронты и переключает приказы — все три новых типа команд для реплея.
        /// </summary>
        Command? BenchCommand(ref Pcg32 rng, int t)
        {
            var w = _sim.World;
            if (w.Nation(Human).Alive == 0) return null;
            int maxF = w.MaxFormationsPerNation;
            var options = new List<Command>();
            for (int k = -1; k < maxF; k++)
            {
                if (k >= 0 && !GameCommands.OwnFormation(w, Human, k)) continue;
                for (int type = 0; type < _rules.Units.Length; type++)
                    if (GameCommands.ValidateRecruit(w, Human, k, type) == RejectReason.None)
                        options.Add(new Command { Nation = Human, Type = CommandType.RecruitUnit, A = k, B = type });
                if (k < 0) continue;
                for (int d = 0; d <= w.NationCount; d++)
                    if (d != Human && GameCommands.ValidateAssign(w, Human, k, d) == RejectReason.None)
                        options.Add(new Command { Nation = Human, Type = CommandType.AssignFormation, A = k, B = d });
                var f = w.FormationAt(w.FormationSlot(Human, k));
                options.Add(new Command { Nation = Human, Type = CommandType.SetOrder, A = k, B = 1 - f.Order });
            }
            if (options.Count == 0 || t % 50 != 0) return null;
            return options[rng.NextInt(options.Count)];
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
            mapView.SetMarkers(w);
            mapView.SetStrikeMask(0);
            mapView.SetFocusNation(FirstFormation().Front);
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
            _prevFront = new byte[w.Formations.Length];
            for (int i = 0; i < n; i++) _prevAlive[i] = w.Nations[i].Alive != 0;
            for (int i = 0; i < _prevFront.Length; i++) _prevFront[i] = w.Formations[i].Front;
        }

        /// <summary>Уведомления переходного UI: фронт закрылся и соединение ушло в тыл, пала чья-то столица.</summary>
        void CheckEvents()
        {
            var w = _sim.World;
            int n = w.NationCount, maxF = w.MaxFormationsPerNation;
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(Human, k);
                var f = w.Formations[slot];
                byte was = _prevFront[slot];
                if (f.Alive != 0 && was != 0 && f.Front == 0 && (w.FrontLength(Human, was) <= 0 || w.Nation(was).Alive == 0))
                    ShowToast($"Закрылся фронт: {_names.Nominative(was)}, {k + 1}-е в тылу");
                _prevFront[slot] = f.Front;
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

            _btnRecruit.clicked += () => OnRecruit(UnitTypes.Infantry);
            _btnFort.clicked += () => OnRecruit(UnitTypes.Armor);
            _btnStrike.clicked += OnToggleOrder;

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

        string Gold(long c) => (c / 100).ToString(CultureInfo.InvariantCulture);
        string PerSec(long c) => (c >= 0 ? "+" : "") + (c / 100.0).ToString("0.0", Ru);

        /// <summary>Первое соединение человека (слот 0): им управляют кнопки переходного UI.</summary>
        Formation FirstFormation() => _sim.World.FormationAt(_sim.World.FormationSlot(Human, 0));

        void UpdateHud()
        {
            var w = _sim.World;
            var me = w.Nation(Human);
            var units = _rules.Units;
            int net = EconomySystem.NetIncome(w, Human);
            long upkeep = me.Alive != 0 ? FormationSystem.Upkeep(w, Human) : 0;
            long reinforce = me.Alive != 0 ? FormationSystem.ReinforceDemand(w, Human) : 0;
            _hudGold.text = "Золото " + Gold(me.Gold);
            _hudIncome.text = PerSec(net - reinforce) + "/с";
            _hudArmy.text = $"{me.Battalions} бат. · {PerSec(-upkeep)}/с" + (reinforce > 0 ? $" · пополнение {PerSec(-reinforce)}/с" : "");
            _hudLand.text = string.Format(CultureInfo.InvariantCulture, "{0}% / {1}%",
                w.TotalLand > 0 ? (long)me.Pixels * 100 / w.TotalLand : 0, _rules.End.VictoryShare_bp / 100);
            int left = Math.Max(0, _rules.End.TimeoutTicks - w.Tick) / _config.TicksPerSecond;
            _hudTimer.text = $"{left / 60:00}:{left % 60:00}";

            var f = FirstFormation();
            _btnRecruit.text = "+ П " + Gold(units[UnitTypes.Infantry].Cost_c);
            _btnFort.text = "+ Б " + Gold(units[UnitTypes.Armor].Cost_c);
            _btnStrike.text = f.Order == Orders.Attack ? "1-е: наступает" : "1-е: держит";
            _btnRecruit.EnableInClassList("disabled-look", RecruitReason(UnitTypes.Infantry) != RejectReason.None);
            _btnFort.EnableInClassList("disabled-look", RecruitReason(UnitTypes.Armor) != RejectReason.None);
            _btnStrike.EnableInClassList("active", f.Order == Orders.Attack);
        }

        RejectReason RecruitReason(byte type)
        {
            var w = _sim.World;
            if (w.Nation(Human).Alive == 0) return RejectReason.NationInvalid;
            return GameCommands.ValidateRecruit(w, Human, 0, type);
        }

        /// <summary>Тексты причин отказа (таблица раздела 10 спеки 01b).</summary>
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
                case RejectReason.FormationFull: return $"В соединении уже {w.MaxBattalions} батальонов";
                case RejectReason.FormationLimit: return "Больше соединений не создать";
                case RejectReason.NoContact: return "С ними нет общей границы";
                case RejectReason.AlreadyThere: return "Соединение уже стоит на этом фронте";
                case RejectReason.TargetInvalid: return "Фронт — только с соседней нацией";
                case RejectReason.NationInvalid: return "Наша столица пала";
                default: return r.ToString();
            }
        }

        void OnRecruit(byte type)
        {
            if (_sim.World.IsOver) return;
            var r = RecruitReason(type);
            if (r != RejectReason.None) { ShowTip(ReasonText(r, _rules.Units[type].Cost_c)); return; }
            _sim.EnqueueFromUi(CommandType.RecruitUnit, Human, 0, type);
        }

        void OnToggleOrder()
        {
            if (_sim.World.IsOver || _sim.World.Nation(Human).Alive == 0) return;
            _sim.EnqueueFromUi(CommandType.SetOrder, Human, 0, 1 - FirstFormation().Order);
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

            // Клик по соседу — 1-е соединение на фронт с ним; по своей земле с зажатым Shift — в тыл.
            if (onMap && w.Nation(Human).Alive != 0)
            {
                int i = w.Index(x, y);
                int o = w.IsLand(i) ? w.Owner[i] : 0;
                if (o != 0 && o != Human)
                {
                    var r = GameCommands.ValidateAssign(w, Human, 0, o);
                    if (r == RejectReason.None) _sim.EnqueueFromUi(CommandType.AssignFormation, Human, 0, o);
                    else if (r != RejectReason.AlreadyThere) { ShowTip(ReasonText(r, 0)); return; }
                }
                else if (o == Human && evt.shiftKey && FirstFormation().Front != 0)
                    _sim.EnqueueFromUi(CommandType.AssignFormation, Human, 0, 0);
            }

            ShowPixelInfo(onMap ? InfoText(w, x, y) : "Мимо карты", evt.localPosition);
        }

        string InfoText(World w, int x, int y)
        {
            int i = w.Index(x, y);
            if (!w.IsLand(i)) return "Вода";

            int o = w.Owner[i];
            if (o == 0) return "Ничья земля";
            if (o == Human)
            {
                var me = w.Nation(Human);
                return $"Наша земля · доход {PerSec(EconomySystem.NetIncome(w, Human))}/с · батальонов {me.Battalions} · соединений {me.Formations}";
            }
            return FrontText(w, (byte)o, i);
        }

        static readonly string[] TerrainNames = { "равнина", "холмы", "горы" };

        /// <summary>Плашка фронта: те же AttPow/DefPow из ядра (01b §5.1), что двигают границу.</summary>
        string FrontText(World w, byte o, int pixel)
        {
            string name = _names.Nominative(o);
            int len = w.FrontLength(Human, o);
            if (len <= 0) return $"{name} · с нами нет общей границы";
            var sb = new System.Text.StringBuilder();
            sb.Append($"{name} · фронт {len} px");
            sb.Append("\nМы: атака " + Triple(tr => w.AttPow(Human, o, tr)) + " · оборона " + Triple(tr => w.DefPow(Human, o, tr)));
            sb.Append("\nОни: атака " + Triple(tr => w.AttPow(o, Human, tr)) + " · оборона " + Triple(tr => w.DefPow(o, Human, tr)));
            sb.Append("\nрельеф их земли " + Triple(tr => w.FrontTerrainLength(Human, o, tr) * 100 / len) + "%");
            int tPix = w.Terrain[pixel] - 1;
            if (tPix >= 0 && tPix < TerrainNames.Length) sb.Append(" · здесь " + TerrainNames[tPix]);
            if ((w.CapitalCover[pixel] & (1 << (o - 1))) != 0) sb.Append($" · их столица рядом ×{_rules.Front.CapitalMult_bp / 10000}");
            int net = w.NetFlips10s(Human, o);
            sb.Append($"\nза 10 с: {(net >= 0 ? "+" : "−")}{Math.Abs(net)} px");
            return sb.ToString();
        }

        static string Triple(Func<int, int> f) => $"{f(0)} / {f(1)} / {f(2)}";

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
