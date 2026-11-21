using System;
using System.Collections.Generic;
using System.Globalization;
using BattlePix.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace BattlePix.Game
{
    /// <summary>
    /// Интерфейс партии (спека 02a §9, ПК; армия — спека 01c §9; война и мир — 02b §9): карточка нации, HUD группами,
    /// «Соседи» (мир / война / перемирие, объявление, цель, мир), карточка объявления и выбор района, плашка фронта с выноской,
    /// панель «Дивизии» (плитки по группам), сводка с предложениями мира, значки фронтов на карте, клавиши.
    /// Только читает мир между тиками и отдаёт команды человека через EnqueueFromUi. Числа ядра переводятся в слова,
    /// сырые — в подсказки по наведению.
    /// Назначение: выделить плитки (клик, Shift-клик, рамка, двойной клик по заголовку группы, A — весь тыл), потом клик
    /// по строке фронта, по соседу на карте или «В тыл». На каждую выделенную дивизию — своя AssignDivision в один тик, по k.
    /// </summary>
    public sealed class GameHud
    {
        public const byte Human = 1;

        static readonly NumberFormatInfo Ru = new NumberFormatInfo { NumberDecimalSeparator = ",", NegativeSign = "−" };
        static readonly string[] IconNames =
        {
            "budget", "army", "armor", "infantry", "hq", "attack", "defend", "frontOpen", "capital", "territory", "time",
            "pause", "play", "plains", "hills", "mountains", "urgent", "loss", "report", "rear",
        };

        // Группы панели «Дивизии»: служебные ключи; фронт — id противника (> 0).
        const int GroupTraining = -3, GroupRear = -2, GroupTransit = -1;

        readonly VisualElement _root;
        readonly Rules _rules;
        readonly SimConfig _config;
        readonly NationNames _names;
        readonly Color32[] _palette;
        readonly MapView _map;
        readonly Camera _camera;
        readonly DecorAtlas _atlas;

        Simulation _sim;
        World W => _sim.World;

        /// <summary>Камера едет к точке мира (клик по строке «Фронты», по карточке сводки).</summary>
        public Action<Vector3> FlyTo;

        // HUD
        readonly Label _budget, _budgetDelta, _army, _armyDelta, _timer, _landShare, _leaderLabel, _goalLabel;
        readonly VisualElement _landBar, _landFill, _landGoal, _landLeader;
        readonly Label _tip, _assignHint, _frontsEmpty, _frontsHint;
        readonly VisualElement _frontsList, _markers, _feed, _mapArea, _divPanel, _band;
        readonly ScrollView _divList;
        readonly Button _btnInf, _btnArmor, _btnRear;
        readonly Label _btnInfText, _btnArmorText;

        // Плашка фронта.
        readonly VisualElement _plate, _plateThemRow, _plateCapitalRow, _plateUsIcon, _plateThemIcon, _platePointer;
        readonly Label _plateTitle, _plateUs, _plateThem, _platePlains, _plateHills, _plateMountains, _plateCapital, _plateVerdict;

        FrontRow[] _rows;
        Tile[] _tiles;
        readonly Dictionary<int, Group> _groups = new Dictionary<int, Group>();
        readonly List<int> _groupOrder = new List<int>();
        Marker[] _marks;
        bool[] _sel;            // выделенные дивизии по k
        int _lastClicked = -1;  // для Tab
        int _plateFront;        // сосед, чья плашка открыта; 0 — закрыта
        float _nextMarkers;
        float _tipHideAt;
        readonly Label _hover;
        VisualElement _hoverOwner;
        float _hoverSince;

        // Рамка выделения в панели «Дивизии».
        bool _banding;
        Vector2 _bandStart;
        bool _bandAdd;

        // Состояние прошлой проверки для сводки.
        bool[] _prevAlive, _prevContact, _prevAttacked, _prevExhausted, _prevTraining;
        int[] _lastCapital;

        const int MaxFeed = 3;
        const float FeedSeconds = 4f;
        readonly List<FeedItem> _feedItems = new List<FeedItem>();

        enum FeedKind { Urgent, Loss, Report }

        sealed class FeedItem
        {
            public VisualElement Root;
            public float HideAt;
            public Vector2Int? Cell;
        }

        sealed class FrontRow
        {
            public VisualElement Root, Order, Us, Them, Plain, Hills, Mountains;
            public Label Name, Verdict;
            public Button Attack, Defend;
            // 02b: статус пары, строки цели, кнопки войны и мира.
            public VisualElement Toggle, Bottom;
            public Label Status, Goal, Defense, Info;
            public Button Declare, Peace;
            public Label PeaceText;
            public int SortKey = -1;
        }

        // 02b: карточка объявления, выбор района, предложения ИИ, сводка войны.
        VisualElement _declareCard;
        Label _declareTitle;
        int _declareTarget;
        int _claimTarget;
        Label _claimLabel;
        int _seenEvents;
        bool[] _prevStaging, _prevHeldOnUs;
        readonly Dictionary<int, FeedItem> _offerCards = new Dictionary<int, FeedItem>();
        /// <summary>Сколько дивизий соседа у нашей границы — «стягивает».</summary>
        const int StagingAlert = 3;

        sealed class Tile
        {
            public VisualElement Root, Icon, StrFill, OrgFill, Pause;
            public Label Num, Timer;
            public int Group = int.MinValue;
        }

        sealed class Group
        {
            public VisualElement Root, Body;
            public Label Header;
        }

        sealed class Marker
        {
            public VisualElement Root, Icon, OrgFill;
            public Label Count;
            public Vector2Int? Cell;
        }

        public GameHud(VisualElement root, Rules rules, SimConfig config, NationNames names, Color32[] palette, MapView map,
            Camera camera)
        {
            _root = root;
            _rules = rules;
            _config = config;
            _names = names;
            _palette = palette;
            _map = map;
            _camera = camera;
            _atlas = map.Atlas;

            _budget = root.Q<Label>("hud-budget");
            _budgetDelta = root.Q<Label>("hud-budget-delta");
            _army = root.Q<Label>("hud-army");
            _armyDelta = root.Q<Label>("hud-army-delta");
            _timer = root.Q<Label>("hud-timer");
            _landBar = root.Q<VisualElement>("land-bar");
            _landFill = root.Q<VisualElement>("land-fill");
            _landShare = root.Q<Label>("land-share");
            _landGoal = root.Q<VisualElement>("land-goal");
            _landLeader = root.Q<VisualElement>("land-leader");
            _leaderLabel = root.Q<Label>("land-leader-label");
            _goalLabel = root.Q<Label>("hud-goal");
            _tip = root.Q<Label>("tip");
            _hover = root.Q<Label>("hover");
            _assignHint = root.Q<Label>("assign-hint");
            _frontsEmpty = root.Q<Label>("fronts-empty");
            _frontsHint = root.Q<Label>("fronts-hint");
            _frontsList = root.Q<VisualElement>("fronts-list");
            _markers = root.Q<VisualElement>("markers");
            _feed = root.Q<VisualElement>("feed");
            _mapArea = root.Q<VisualElement>("map-area");
            _divPanel = root.Q<VisualElement>("divisions-panel");
            _divList = root.Q<ScrollView>("divisions-list");
            _band = root.Q<VisualElement>("select-band");
            _btnInf = root.Q<Button>("btn-new-inf");
            _btnArmor = root.Q<Button>("btn-new-armor");
            _btnRear = root.Q<Button>("btn-rear");
            _btnInfText = _btnInf.Q<Label>(className: "btn-text");
            _btnArmorText = _btnArmor.Q<Label>(className: "btn-text");

            _plate = root.Q<VisualElement>("front-plate");
            _plateTitle = root.Q<Label>("plate-title");
            _plateUs = root.Q<Label>("plate-us");
            _plateThem = root.Q<Label>("plate-them");
            _plateUsIcon = root.Q<VisualElement>("plate-us-icon");
            _plateThemIcon = root.Q<VisualElement>("plate-them-icon");
            _plateThemRow = root.Q<VisualElement>("plate-them-row");
            _platePlains = root.Q<Label>("plate-plains");
            _plateHills = root.Q<Label>("plate-hills");
            _plateMountains = root.Q<Label>("plate-mountains");
            _plateCapitalRow = root.Q<VisualElement>("plate-capital-row");
            _plateCapital = root.Q<Label>("plate-capital");
            _plateVerdict = root.Q<Label>("plate-verdict");
            _platePointer = root.Q<VisualElement>("plate-pointer");
            _plate.RegisterCallback<PointerDownEvent>(e => e.StopPropagation()); // клик по плашке не уходит в карту

            // Значки из атласа: элементы с классом icon-<имя>.
            foreach (var name in IconNames)
            {
                var sprite = Icon(name);
                if (sprite == null) continue;
                root.Query<VisualElement>(className: "icon-" + name).ForEach(e => e.style.backgroundImage = new StyleBackground(sprite));
            }

            // Карточка нации.
            var accent = (Color)palette[Human];
            root.Q<VisualElement>("nation-swatch").style.backgroundColor = accent;
            root.Q<Label>("nation-form").text = names.Form(Human);
            root.Q<Label>("nation-name").text = names.Nominative(Human);
            _landFill.style.backgroundColor = accent;
            _goalLabel.text = $"{_rules.End.VictoryShare_bp / 100}% победа";
            _tip.RegisterCallback<PointerDownEvent>(_ => _tip.AddToClassList("hidden"));

            Bind(_btnInf, () => RecruitReason(DivisionTypes.Infantry), () => _rules.DivisionCost(DivisionTypes.Infantry),
                () => _sim.EnqueueFromUi(CommandType.RecruitDivision, Human, DivisionTypes.Infantry, 0),
                $"Пехотная дивизия: 3 пехотных батальона. Дёшево, держит холмы и горы. Готова через {TrainSeconds(DivisionTypes.Infantry)} с");
            Bind(_btnArmor, () => RecruitReason(DivisionTypes.Armor), () => _rules.DivisionCost(DivisionTypes.Armor),
                () => _sim.EnqueueFromUi(CommandType.RecruitDivision, Human, DivisionTypes.Armor, 0),
                $"Танковая дивизия: 2 бронетанковых и 1 пехотный батальон. Рвёт равнину, вязнет в горах. Готова через {TrainSeconds(DivisionTypes.Armor)} с");
            Bind(_btnRear, RearReason, () => 0, () => AssignSelected(0), "Отвести выделенные дивизии в тыл (R)");

            BuildDeclareCard();
            _claimLabel = new Label("") { pickingMode = PickingMode.Ignore };
            _claimLabel.AddToClassList("panel");
            _claimLabel.AddToClassList("hover");
            _claimLabel.AddToClassList("hidden");
            root.Add(_claimLabel);
            var title = root.Q<VisualElement>("fronts-panel")?.Q<Label>(className: "panel-title");
            if (title != null) title.text = "Соседи";

            // Рамка выделения: тянуть мышью по пустому месту панели «Дивизии».
            _divList.RegisterCallback<PointerDownEvent>(OnBandDown, TrickleDown.NoTrickleDown);
            _divPanel.RegisterCallback<PointerMoveEvent>(OnBandMove);
            _divPanel.RegisterCallback<PointerUpEvent>(OnBandUp);
            _divPanel.RegisterCallback<PointerDownEvent>(e => e.StopPropagation()); // клик по панели не уходит в карту
        }

        int TrainSeconds(int type) => _rules.Divisions[type].TrainTicks / _config.TicksPerSecond;

        Sprite Icon(string name) => _atlas != null ? _atlas.Icon(name) : null;

        VisualElement IconEl(string name, string extraClass = null)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList("icon");
            if (extraClass != null) e.AddToClassList(extraClass);
            SetIcon(e, name);
            return e;
        }

        void SetIcon(VisualElement e, string name)
        {
            var s = Icon(name);
            e.style.backgroundImage = s != null ? new StyleBackground(s) : new StyleBackground(StyleKeyword.None);
        }

        // ---------- партия ----------

        public void BeginMatch(Simulation sim)
        {
            _sim = sim;
            int n = W.NationCount, maxK = W.MaxDivisionsPerNation;
            _frontsList.Clear();
            _divList.Clear();
            _markers.Clear();
            foreach (var f in _feedItems) f.Root.RemoveFromHierarchy();
            _feedItems.Clear();
            _rows = new FrontRow[n + 1];
            for (int d = 1; d <= n; d++)
                if (d != Human) _rows[d] = MakeRow((byte)d);
            _groups.Clear();
            _groupOrder.Clear();
            MakeGroup(GroupTraining, "Готовятся");
            MakeGroup(GroupRear, "Тыл");
            MakeGroup(GroupTransit, "В пути");
            for (int d = 1; d <= n; d++)
                if (d != Human) MakeGroup(d, _names.Nominative(d));
            _tiles = new Tile[maxK];
            for (int k = 0; k < maxK; k++) _tiles[k] = MakeTile(k);
            _marks = new Marker[n + 1];
            for (int d = 1; d <= n; d++) _marks[d] = MakeMarker();
            _sel = new bool[maxK];
            _lastClicked = -1;
            _plateFront = 0;
            _prevAlive = new bool[n + 1];
            _prevContact = new bool[n + 1];
            _prevAttacked = new bool[n + 1];
            _prevExhausted = new bool[n + 1];
            _prevTraining = new bool[maxK];
            _lastCapital = new int[n + 1];
            _prevStaging = new bool[n + 1];
            _prevHeldOnUs = new bool[n + 1];
            _seenEvents = sim.World.WarEvents.Count;
            _offerCards.Clear();
            _declareTarget = 0;
            _claimTarget = 0;
            for (int d = 1; d <= n; d++)
            {
                _prevAlive[d] = W.Nation((byte)d).Alive != 0;
                _prevContact[d] = Contact(d);
                _lastCapital[d] = W.Nation((byte)d).Capital;
            }
            for (int k = 0; k < maxK; k++) _prevTraining[k] = W.Training(W.DivisionAt(W.DivisionSlot(Human, k)));
            _nextMarkers = 0;
            HidePanels();
            Refresh();
        }

        bool Alive => W.Nation(Human).Alive != 0;
        bool Contact(int d) => d != Human && W.Nation((byte)d).Alive != 0 && W.FrontLength(Human, d) > 0;
        Division Div(int k) => W.DivisionAt(W.DivisionSlot(Human, k));

        int SelectedCount()
        {
            int c = 0;
            for (int k = 0; k < _sel.Length; k++) if (_sel[k]) c++;
            return c;
        }

        // ---------- обновление ----------

        /// <summary>Четыре раза в секунду: HUD, строки фронтов, плитки, плашка.</summary>
        public void Refresh()
        {
            if (_sim == null) return;
            var w = W;
            var me = w.Nation(Human);
            long upkeep = Alive ? DivisionSystem.Upkeep(w, Human) : 0;
            long reinforce = Alive ? DivisionSystem.ReinforceDemand(w, Human) : 0;
            long net = Alive ? EconomySystem.Income(w, Human) - upkeep - reinforce : 0;
            _budget.text = Gold(me.Gold);
            _budgetDelta.text = PerSec(net) + "/с";
            SetGoodBad(_budgetDelta, net);
            _budgetDelta.tooltip = $"Чистый доход: доход {PerSec(Alive ? EconomySystem.Income(w, Human) : 0)}/с минус армия {PerSec(upkeep + reinforce)}/с";
            _army.text = $"{me.Divisions} див.";
            _armyDelta.text = PerSec(-(upkeep + reinforce)) + "/с";
            _armyDelta.tooltip = $"Содержание {PerSec(-upkeep)}/с" + (reinforce > 0 ? $", пополнение {PerSec(-reinforce)}/с" : "");
            int left = Math.Max(0, _rules.End.TimeoutTicks - w.Tick) / _config.TicksPerSecond;
            _timer.text = $"{left / 60:00}:{left % 60:00}";
            UpdateTerritory();

            int fronts = 0;
            bool reorder = false;
            for (int d = 1; d <= w.NationCount; d++)
            {
                if (d == Human) continue;
                bool on = Contact(d);
                _rows[d].Root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
                if (on)
                {
                    fronts++;
                    UpdateRow((byte)d);
                }
                // Сверху войны, потом перемирия, потом мир (02b §9).
                byte st = w.WarOf(Human, d).Status;
                int key = st == WarStatus.War ? 0 : st == WarStatus.Truce ? 1 : 2;
                if (_rows[d].SortKey != key) { _rows[d].SortKey = key; reorder = true; }
            }
            if (reorder)
                for (int key = 0; key <= 2; key++)
                    for (int d = 1; d <= w.NationCount; d++)
                        if (d != Human && _rows[d].SortKey == key) _rows[d].Root.BringToFront();
            if (_declareTarget != 0 && WarSystem.ValidateDeclare(w, Human, _declareTarget, 0) != RejectReason.None) CloseDeclare();
            _frontsEmpty.style.display = fronts == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _frontsHint.style.display = fronts == 0 ? DisplayStyle.None : DisplayStyle.Flex;

            UpdateDivisions();

            _btnInfText.text = $"+ Пехотная {Gold(_rules.DivisionCost(DivisionTypes.Infantry))}";
            _btnArmorText.text = $"+ Танковая {Gold(_rules.DivisionCost(DivisionTypes.Armor))}";
            _btnInf.EnableInClassList("disabled-look", RecruitReason(DivisionTypes.Infantry) != RejectReason.None);
            _btnArmor.EnableInClassList("disabled-look", RecruitReason(DivisionTypes.Armor) != RejectReason.None);
            _btnRear.EnableInClassList("disabled-look", RearReason() != RejectReason.None);
            int sel = SelectedCount();
            _assignHint.text = sel > 0
                ? $"Выделено {Divisions(sel)}: клик по фронту справа или по соседу на карте · R — в тыл · Esc — снять"
                : "Клик, Shift-клик или рамка — выделить · двойной клик по группе — всю · A — весь тыл";

            if (_plateFront != 0)
            {
                if (Contact(_plateFront)) FillPlate((byte)_plateFront);
                else HidePanels();
            }
            _map.SetFocusNation(_plateFront);
        }

        void UpdateTerritory()
        {
            var w = W;
            int victory = _rules.End.VictoryShare_bp;
            int scale = victory * 5 / 4; // полоса до 125% порога, чтобы отметка порога была видна
            int mine = w.TotalLand > 0 ? (int)((long)w.Nation(Human).Pixels * 10000 / w.TotalLand) : 0;
            int leader = 0, leaderShare = 0;
            for (int d = 1; d <= w.NationCount; d++)
            {
                int s = w.TotalLand > 0 ? (int)((long)w.Nation((byte)d).Pixels * 10000 / w.TotalLand) : 0;
                if (s > leaderShare) { leaderShare = s; leader = d; }
            }
            _landFill.style.width = Length.Percent(Mathf.Min(100f, mine * 100f / scale));
            _landShare.text = $"{mine / 100}%";
            _landGoal.style.left = Length.Percent(victory * 100f / scale);
            bool showLeader = leader != 0 && leader != Human;
            _landLeader.style.display = showLeader ? DisplayStyle.Flex : DisplayStyle.None;
            _leaderLabel.style.display = showLeader ? DisplayStyle.Flex : DisplayStyle.None;
            if (showLeader)
            {
                float pos = Mathf.Min(100f, leaderShare * 100f / scale);
                _landLeader.style.left = Length.Percent(pos);
                _landLeader.style.marginLeft = -6;
                _landLeader.style.backgroundColor = (Color)_palette[leader];
                _leaderLabel.text = $"лидер {_names.Nominative(leader)} {leaderShare / 100}%";
            }
            _landBar.tooltip = $"Наша доля суши {mine / 100}%, для победы {victory / 100}%";
        }

        static void SetGoodBad(Label l, long v)
        {
            l.EnableInClassList("good", v > 0);
            l.EnableInClassList("bad", v < 0);
            l.EnableInClassList("dim", v == 0);
        }

        /// <summary>Давление «мы | они»: кто наступает, тот сравнивает атаку с обороной другого (как считает ядро).</summary>
        void Pressure(byte d, out long us, out long them)
        {
            var w = W;
            long wa = w.WeightedAttack(Human, d), ta = w.WeightedAttack(d, Human);
            us = (wa > 0 ? wa : 0) + (ta > 0 ? w.WeightedDefense(Human, d) : 0);
            them = (wa > 0 ? w.WeightedDefense(d, Human) : 0) + (ta > 0 ? ta : 0);
        }

        /// <summary>Свои дивизии на фронте с d: на месте, в пути, средняя организация на месте, все наступающие выдохлись.</summary>
        void FrontState(byte owner, byte d, out int onPlace, out int transit, out int armor, out int avgOrg, out bool exhausted)
        {
            var w = W;
            onPlace = transit = armor = 0;
            long org = 0;
            int attackers = 0, tired = 0;
            bool attackOrder = w.FrontOrderOf(owner, d) == Orders.Attack;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(owner, k));
                if (v.Alive == 0 || v.Front != d) continue;
                if (!w.OnFront(v)) { transit++; continue; }
                onPlace++;
                if (v.Type == DivisionTypes.Armor) armor++;
                org += v.Org_bp;
                if (attackOrder)
                {
                    attackers++;
                    if (v.Exhausted != 0) tired++;
                }
            }
            avgOrg = onPlace > 0 ? (int)(org / onPlace) : 0;
            exhausted = attackers > 0 && tired == attackers;
        }

        void UpdateRow(byte d)
        {
            var w = W;
            var r = _rows[d];
            int len = w.FrontLength(Human, d);
            r.Plain.style.flexGrow = w.FrontTerrainLength(Human, d, 0);
            r.Hills.style.flexGrow = w.FrontTerrainLength(Human, d, 1);
            r.Mountains.style.flexGrow = w.FrontTerrainLength(Human, d, 2);
            Pressure(d, out long us, out long them);
            if (us + them == 0) { us = 1; them = 1; }
            r.Us.style.flexGrow = us;
            r.Them.style.flexGrow = them;

            FrontState(Human, d, out int onPlace, out int transit, out _, out int avgOrg, out bool exhausted);
            bool attackOrder = w.FrontOrderOf(Human, d) == Orders.Attack;
            SetIcon(r.Order, onPlace > 0 ? (exhausted ? "pause" : attackOrder ? "attack" : "defend") : transit > 0 ? "time" : "frontOpen");
            r.Attack.EnableInClassList("active", attackOrder);
            r.Defend.EnableInClassList("active", !attackOrder);

            int net = w.NetFlips10s(Human, d);
            r.Verdict.text = net > 0 ? $"берём ~{net} px" : net < 0 ? $"теряем ~{-net} px" : exhausted ? "стоим — выдохлись" : "стоим";
            r.Verdict.EnableInClassList("good", net > 0);
            r.Verdict.EnableInClassList("bad", net < 0);
            r.Verdict.EnableInClassList("dim", net == 0);
            string orderText = onPlace > 0
                ? (attackOrder ? (exhausted ? "наступаем, но все выдохлись — ждём организацию или свежие дивизии" : "наступаем") : "держим оборону")
                : transit > 0 ? "дивизии в пути" : "фронт открыт: наших дивизий нет";
            r.Root.tooltip = $"{_names.Nominative(d)}: {orderText}\nНаших дивизий на месте {onPlace}, в пути {transit}" + (onPlace > 0 ? $", организация {avgOrg / 100}%" : "")
                             + $"\nФронт {len} px · равнина {w.FrontTerrainLength(Human, d, 0)}, холмы {w.FrontTerrainLength(Human, d, 1)}, горы {w.FrontTerrainLength(Human, d, 2)}\n"
                             + $"Наша атака A = {w.WeightedAttack(Human, d)}, их оборона D = {w.WeightedDefense(d, Human)}\n"
                             + $"Их атака {w.WeightedAttack(d, Human)}, наша оборона {w.WeightedDefense(Human, d)}\nСдвиг за 10 с: {(net >= 0 ? "+" : "−")}{Math.Abs(net)} px";
            r.Root.EnableInClassList("selected", _plateFront == d);
            r.Root.EnableInClassList("drop-target", SelectedCount() > 0);
            UpdateWarPart(d, r);
        }

        /// <summary>Строка соседа по статусу пары (02b §9): мир — «Объявить войну», война — цель и мир, перемирие — срок и дань.</summary>
        void UpdateWarPart(byte d, FrontRow r)
        {
            var w = W;
            var war = w.WarOf(Human, d);
            int tps = _config.TicksPerSecond;
            bool atWar = war.Status == WarStatus.War, truce = war.Status == WarStatus.Truce;
            r.Root.EnableInClassList("at-war", atWar);
            r.Root.EnableInClassList("in-truce", truce);
            r.Status.text = atWar ? "ВОЙНА" : truce ? "ПЕРЕМИРИЕ " + Clock((war.TruceUntil - w.Tick + tps - 1) / tps) : "МИР";
            r.Status.EnableInClassList("st-war", atWar);
            r.Status.EnableInClassList("st-truce", truce);
            r.Status.EnableInClassList("st-peace", !atWar && !truce);
            var show = atWar ? DisplayStyle.Flex : DisplayStyle.None;
            r.Toggle.style.display = show;
            r.Bottom.style.display = show;
            r.Order.style.display = show;
            r.Verdict.style.display = show;
            r.Goal.style.display = show;
            r.Defense.style.display = show;
            r.Peace.style.display = show;
            r.Declare.style.display = !atWar && !truce ? DisplayStyle.Flex : DisplayStyle.None;
            r.Info.style.display = atWar ? DisplayStyle.None : DisplayStyle.Flex;

            if (!atWar)
            {
                int ours = DivisionsAtBorder(Human, d), theirs = DivisionsAtBorder(d, Human);
                if (truce)
                {
                    string info = $"Перемирие до {Clock(war.TruceUntil / tps)}";
                    if (war.TributePayer != 0 && w.Tick < war.TributeUntil)
                    {
                        string rate = PerSec(EconomySystem.Income(w, war.TributePayer) * _rules.War.Tribute_bp / 10000).TrimStart('+');
                        info += war.TributePayer == Human
                            ? $" · мы платим {rate}/с до {Clock(war.TributeUntil / tps)}"
                            : $" · платят нам {rate}/с до {Clock(war.TributeUntil / tps)}";
                    }
                    r.Info.text = info;
                }
                else
                    r.Info.text = $"У границы наших дивизий: {ours}" + (theirs >= StagingAlert ? $" · стягивает {theirs}!" : "");
                r.Info.EnableInClassList("bad", !truce && theirs >= StagingAlert);
                var dr = WarSystem.ValidateDeclare(w, Human, d, 0);
                r.Declare.EnableInClassList("disabled-look", dr != RejectReason.None);
                r.Declare.tooltip = dr == RejectReason.None
                    ? $"Объявить войну {_names.Dative(d)}: выбрать цель — район или репарации"
                    : ReasonText(dr, 0, d);
                return;
            }

            // Цель нападающего и прогресс защиты.
            bool weAttack = war.Attacker == Human;
            byte attacker = war.Attacker;
            int need = _rules.War.GoalHoldSeconds;
            int goalPct = GoalPct(w, attacker, d == attacker ? (byte)Human : d);
            string goal = war.Goal == WarGoals.Claims
                ? $"{(weAttack ? "Претензии: держим" : "Их претензии: держат")} {Percent(w.ClaimHeld(Human, d), war.ClaimTotal)}% района из {_rules.War.ClaimHold_bp / 100}%"
                : $"{(weAttack ? "Репарации: заняли" : "Их репарации: заняли")} {Percent(w.Occupied(attacker, war.Defender(Human, d)), war.PreWarDef)}% {(weAttack ? "их" : "нашей")} земли из {_rules.War.RepOccupy_bp / 100}%";
            int hold = war.HoldAtt;
            if (hold > 0) goal += $" · {(weAttack ? "держим" : "держат")} {Math.Min(hold, need)} / {need} с";
            r.Goal.text = goal;
            r.Goal.EnableInClassList("gold", weAttack && goalPct >= 10000);
            r.Goal.EnableInClassList("bad", !weAttack && hold > 0);
            byte defender = war.Defender(Human, d);
            int defHold = war.HoldDef;
            string defense = weAttack
                ? $"Они: заняли {Percent(w.Occupied(d, Human), war.PreWarAtt)}% нашей земли из {_rules.War.DefenseWin_bp / 100}%"
                : $"Мы: заняли {Percent(w.Occupied(Human, d), war.PreWarAtt)}% их земли из {_rules.War.DefenseWin_bp / 100}%";
            if (defHold > 0) defense += $" · {(defender == Human ? "держим" : "держат")} {Math.Min(defHold, need)} / {need} с";
            r.Defense.text = defense;
            r.Defense.EnableInClassList("bad", weAttack && defHold > 0);
            r.Defense.EnableInClassList("gold", !weAttack && defHold >= need);

            // Кнопка мира.
            bool own = w.GoalFulfilled(Human, d, Human);
            bool theirOffer = war.OfferFrom == d && w.Tick < war.OfferUntil;
            bool ourOffer = war.OfferFrom == Human && w.Tick < war.OfferUntil;
            int cooldown = weAttack ? war.OfferCooldownAtt : war.OfferCooldownDef;
            string text;
            if (own) text = !weAttack ? "Навязать мир" : war.Goal == WarGoals.Claims ? "Забрать район" : "Взыскать репарации";
            else if (theirOffer) text = "Принять белый мир";
            else if (ourOffer) text = $"Ждём ответа {(war.OfferUntil - w.Tick + tps - 1) / tps} с";
            else if (w.Tick < cooldown) text = $"Белый мир через {(cooldown - w.Tick + tps - 1) / tps} с";
            else text = "Предложить белый мир";
            r.PeaceText.text = text;
            r.Peace.EnableInClassList("peace-gold", own);
            r.Peace.EnableInClassList("disabled-look", !own && !theirOffer && (ourOffer || w.Tick < cooldown));
            r.Peace.tooltip = own
                ? (!weAttack ? $"Мы отбились: {_names.Nominative(d)} вернёт занятое и полдохода 5 минут платит нам"
                    : war.Goal == WarGoals.Claims ? "Район и всё занятое — наши навсегда, остальное занятое возвращаем"
                    : "Занятое вернём, они платят половину дохода 5 минут")
                : "Белый мир: каждый оставляет занятое. Потом перемирие 5 минут";
        }

        /// <summary>Своих дивизий owner с Front = d (на месте и в пути).</summary>
        int DivisionsAtBorder(byte owner, byte d)
        {
            var w = W;
            int c = 0;
            for (int k = 0; k < w.MaxDivisionsPerNation; k++)
            {
                var v = w.DivisionAt(w.DivisionSlot(owner, k));
                if (v.Alive != 0 && v.Front == d) c++;
            }
            return c;
        }

        static int GoalPct(World w, byte side, byte other) => WarSystem.GoalProgress_bp(w, side, other, side);

        static int Percent(long have, long of) => of > 0 ? (int)(have * 100 / of) : 0;

        static string Clock(int seconds)
        {
            if (seconds < 0) seconds = 0;
            return $"{seconds / 60}:{seconds % 60:00}";
        }

        // ---------- 02b: объявление, район, мир ----------

        void BuildDeclareCard()
        {
            _declareCard = new VisualElement();
            _declareCard.AddToClassList("panel");
            _declareCard.AddToClassList("declare-card");
            _declareCard.AddToClassList("hidden");
            _declareTitle = new Label("") { pickingMode = PickingMode.Ignore };
            _declareTitle.AddToClassList("text-30");
            _declareCard.Add(_declareTitle);
            Option("Территориальные претензии", "Возьми и удержи 70% района 20 с. Район и всё занятое — твои навсегда.", () => StartClaim(_declareTarget));
            Option("Репарации", "Займи 10% их земли и удержи 20 с. Занятое вернёшь, они платят половину дохода 5 минут.", () => Declare(_declareTarget, 0));
            var risk = new Label("Если они займут 10% нашей земли, мир навяжут они") { pickingMode = PickingMode.Ignore };
            risk.AddToClassList("caption");
            risk.AddToClassList("declare-risk");
            _declareCard.Add(risk);
            var close = new Button(CloseDeclare) { text = "Отмена (Esc)" };
            close.AddToClassList("btn");
            _declareCard.Add(close);
            _declareCard.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _root.Add(_declareCard);

            void Option(string title, string body, Action act)
            {
                var b = new Button(act);
                b.AddToClassList("btn");
                b.AddToClassList("declare-option");
                var t = new Label(title) { pickingMode = PickingMode.Ignore };
                t.AddToClassList("text-20");
                t.AddToClassList("gold");
                var d = new Label(body) { pickingMode = PickingMode.Ignore };
                d.AddToClassList("caption");
                d.AddToClassList("declare-body");
                b.Add(t);
                b.Add(d);
                _declareCard.Add(b);
            }
        }

        void OpenDeclare(byte d)
        {
            if (_sim == null || W.IsOver || !Alive) return;
            var r = WarSystem.ValidateDeclare(W, Human, d, 0);
            if (r != RejectReason.None) { ShowTip(ReasonText(r, 0, d)); return; }
            _declareTarget = d;
            _declareTitle.text = $"Война с {_names.Instrumental(d)}";
            var row = _rows[d].Root.worldBound;
            var origin = _root.worldBound.position;
            _declareCard.style.top = Mathf.Max(100, row.y - origin.y);
            _declareCard.RemoveFromClassList("hidden");
        }

        void CloseDeclare()
        {
            _declareTarget = 0;
            _declareCard?.AddToClassList("hidden");
        }

        void StartClaim(int d)
        {
            CloseDeclare();
            if (d == 0) return;
            _claimTarget = d;
            ShowTip($"Выберите район на земле {_names.Genitive(d)} у границы: клик — объявить войну, Esc или ПКМ — отмена", 6f);
        }

        void StopClaim()
        {
            _claimTarget = 0;
            _claimLabel?.AddToClassList("hidden");
            _map.SetClaimPreview(0, 0, 0, false, false);
        }

        void Declare(int d, int b)
        {
            CloseDeclare();
            if (_sim == null || W.IsOver || !Alive || d == 0) return;
            var r = WarSystem.ValidateDeclare(W, Human, d, b);
            if (r != RejectReason.None) { ShowTip(ReasonText(r, 0, d)); return; }
            _sim.EnqueueFromUi(CommandType.DeclareWar, Human, d, b);
            ShowTip(b == 0 ? $"Война с {_names.Instrumental(d)}: репарации" : $"Война с {_names.Instrumental(d)}: претензии на район", 3f);
            Refresh();
        }

        /// <summary>Каждый кадр в режиме выбора района: круг под курсором, зелёный — можно, красный — нельзя, причина у курсора.</summary>
        public void UpdateClaimHover(Vector2 screenMouse)
        {
            if (_claimTarget == 0 || _sim == null) return;
            if (!Alive || WarSystem.ValidateDeclare(W, Human, _claimTarget, 0) != RejectReason.None) { StopClaim(); return; }
            var w = W;
            bool onMap = _map.ScreenToCell(_camera, screenMouse, out int x, out int y);
            if (!onMap)
            {
                _map.SetClaimPreview(0, 0, 0, false, false);
                _claimLabel.AddToClassList("hidden");
                return;
            }
            var check = WarSystem.CheckClaim(w, Human, _claimTarget, w.Index(x, y), out int total);
            _map.SetClaimPreview(x, y, _rules.War.ClaimRadius, check == ClaimCheck.Ok, true);
            _claimLabel.text = check == ClaimCheck.Ok ? $"Район: {total} px их земли · клик — объявить войну" : ClaimText(check);
            _claimLabel.EnableInClassList("bad", check != ClaimCheck.Ok);
            _claimLabel.RemoveFromClassList("hidden");
            var panel = _root.panel;
            if (panel == null) return;
            var pp = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screenMouse.x, Screen.height - screenMouse.y));
            _claimLabel.style.left = Mathf.Round(pp.x + 20);
            _claimLabel.style.top = Mathf.Round(pp.y + 24);
        }

        static string ClaimText(ClaimCheck c) =>
            c == ClaimCheck.TooFar ? "Слишком далеко от границы"
            : c == ClaimCheck.TouchesCapital ? "Задевает их столицу"
            : c == ClaimCheck.TooSmall ? "Мало их земли: район почти в воде"
            : "Это не их земля";

        void OnPeaceClicked(byte d)
        {
            if (_sim == null || W.IsOver || !Alive) return;
            var w = W;
            var war = w.WarOf(Human, d);
            if (war.OfferFrom == d && w.Tick < war.OfferUntil && !w.GoalFulfilled(Human, d, Human))
            {
                Answer(d, 1);
                return;
            }
            var r = WarSystem.ValidatePropose(w, Human, d);
            if (r != RejectReason.None) { ShowTip(ReasonText(r, 0, d)); return; }
            if (war.OfferFrom == Human && w.Tick < war.OfferUntil && !w.GoalFulfilled(Human, d, Human)) { ShowTip("Ждём ответа на наше предложение"); return; }
            _sim.EnqueueFromUi(CommandType.ProposePeace, Human, d, 0);
            ShowTip(w.GoalFulfilled(Human, d, Human) ? "Мир на наших условиях" : $"Предлагаем белый мир {_names.Dative(d)}", 2.5f);
            Refresh();
        }

        void Answer(int from, int yes)
        {
            if (_sim == null || W.IsOver || !Alive) return;
            var r = WarSystem.ValidateAnswer(W, Human, from, yes);
            if (r != RejectReason.None) { ShowTip(ReasonText(r, 0, from)); return; }
            _sim.EnqueueFromUi(CommandType.AnswerPeace, Human, from, yes);
            if (_offerCards.TryGetValue(from, out var card)) { card.Root.RemoveFromHierarchy(); _feedItems.Remove(card); _offerCards.Remove(from); }
            ShowTip(yes == 1 ? $"Белый мир с {_names.Instrumental(from)}" : $"Отказ {_names.Dative(from)}", 2f);
            Refresh();
        }

        /// <summary>Группа плитки: готовится, тыл, в пути или фронт.</summary>
        int GroupOf(in Division v)
        {
            var w = W;
            if (w.Training(v)) return GroupTraining;
            if (v.Front == 0) return GroupRear;
            if (!w.OnFront(v)) return GroupTransit;
            return v.Front;
        }

        void UpdateDivisions()
        {
            var w = W;
            int tps = _config.TicksPerSecond;
            var counts = new Dictionary<int, int>();
            foreach (var key in _groupOrder) counts[key] = 0;
            for (int k = 0; k < _tiles.Length; k++)
            {
                var t = _tiles[k];
                var v = Div(k);
                bool alive = v.Alive != 0 && Alive;
                if (!alive)
                {
                    if (t.Group != int.MinValue)
                    {
                        t.Root.RemoveFromHierarchy();
                        t.Group = int.MinValue;
                    }
                    _sel[k] = false;
                    continue;
                }
                int g = GroupOf(v);
                if (t.Group != g)
                {
                    // В группе плитки стоят по k: вставляем перед первой с большим k.
                    t.Root.RemoveFromHierarchy();
                    var body = _groups[g].Body;
                    int at = body.childCount;
                    for (int i = 0; i < body.childCount; i++)
                        if (body[i].userData is int other && other > k) { at = i; break; }
                    body.Insert(at, t.Root);
                    t.Group = g;
                }
                counts[g]++;
                bool armor = v.Type == DivisionTypes.Armor;
                SetIcon(t.Icon, armor ? "armor" : "infantry");
                t.StrFill.style.width = Length.Percent(v.Strength_bp / 100f);
                t.StrFill.EnableInClassList("low", v.Strength_bp < 3000);
                t.OrgFill.style.width = Length.Percent(v.Org_bp / 100f);
                t.OrgFill.EnableInClassList("exhausted", v.Exhausted != 0);
                t.Pause.style.display = v.Exhausted != 0 ? DisplayStyle.Flex : DisplayStyle.None;
                bool training = w.Training(v);
                bool transit = !training && v.Front != 0 && !w.OnFront(v);
                int secs = training ? (v.ReadyTick - w.Tick + tps - 1) / tps : transit ? (v.ArriveTick - w.Tick + tps - 1) / tps : 0;
                t.Timer.text = training || transit ? secs.ToString(CultureInfo.InvariantCulture) : ""; // секунды, золотом справа; «с» не влезает в 72 px
                t.Timer.style.display = training || transit ? DisplayStyle.Flex : DisplayStyle.None;
                t.Root.EnableInClassList("training", training);
                t.Root.EnableInClassList("selected", _sel[k]);
                t.Root.tooltip = TileTooltip(k, v);
            }
            foreach (var key in _groupOrder)
            {
                var grp = _groups[key];
                int c = counts[key];
                grp.Root.style.display = c > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                if (key > 0)
                {
                    FrontState(Human, (byte)key, out _, out _, out _, out _, out bool exh);
                    bool atk = w.FrontOrderOf(Human, key) == Orders.Attack;
                    grp.Header.text = !W.AtWar(Human, key) ? $"{_names.Nominative(key)} · у границы" : $"{_names.Nominative(key)} · {(atk ? (exh ? "выдохлись" : "наступаем") : "оборона")}";
                }
            }
        }

        string TileTooltip(int k, in Division v)
        {
            var w = W;
            string type = v.Type == DivisionTypes.Armor ? "танковая" : "пехотная";
            string org = $"организация {v.Org_bp / 100}%" + (v.Exhausted != 0 ? ", выдохлась" : "");
            string where;
            int tps = _config.TicksPerSecond;
            if (w.Training(v))
                where = $"готовится ещё {(v.ReadyTick - w.Tick + tps - 1) / tps} с" + (v.Front != 0 ? $", потом на фронт с {_names.Instrumental(v.Front)}" : "");
            else if (v.Front == 0) where = "в тылу";
            else if (!w.OnFront(v)) where = $"идёт на фронт с {_names.Instrumental(v.Front)}, {(v.ArriveTick - w.Tick + tps - 1) / tps} с";
            else where = $"фронт с {_names.Instrumental(v.Front)}";
            return $"{k + 1}-я {type} · численность {v.Strength_bp / 100}% · {org} · {where}";
        }

        // ---------- построение элементов ----------

        FrontRow MakeRow(byte d)
        {
            var r = new FrontRow { Root = new VisualElement() };
            r.Root.AddToClassList("front-row");
            var top = new VisualElement { pickingMode = PickingMode.Ignore };
            top.AddToClassList("front-top");
            var sw = new VisualElement { pickingMode = PickingMode.Ignore };
            sw.AddToClassList("swatch");
            sw.style.backgroundColor = (Color)_palette[d];
            top.Add(sw);
            r.Name = Cell(top, "front-name", _names.Nominative(d));
            r.Status = Cell(top, "front-status", "МИР");
            r.Order = IconEl("frontOpen", "front-order");
            top.Add(r.Order);
            r.Verdict = Cell(top, "front-verdict", "");
            var toggle = new VisualElement();
            toggle.AddToClassList("order-toggle");
            r.Attack = OrderButton(toggle, "attack", $"Наступать на {_names.Accusative(d)}: берём землю, дивизии выдыхаются и несут потери, держат хуже");
            r.Defend = OrderButton(toggle, "defend", "Оборона: землю не берём, организация не горит, пока на нас не давят");
            top.Add(toggle);
            r.Toggle = toggle;
            r.Attack.clicked += () => SetFrontOrder(d, Orders.Attack);
            r.Defend.clicked += () => SetFrontOrder(d, Orders.Defend);
            r.Root.Add(top);

            var bottom = new VisualElement { pickingMode = PickingMode.Ignore };
            bottom.AddToClassList("front-bottom");
            var pressure = new VisualElement { pickingMode = PickingMode.Ignore };
            pressure.AddToClassList("pressure");
            r.Us = Part(pressure, (Color)_palette[Human]);
            r.Them = Part(pressure, (Color)_palette[d]);
            bottom.Add(pressure);
            var strip = new VisualElement { pickingMode = PickingMode.Ignore };
            strip.AddToClassList("terrain-strip");
            r.Plain = Part(strip, null, "terrain-plain");
            r.Hills = Part(strip, null, "terrain-hills");
            r.Mountains = Part(strip, null, "terrain-mountains");
            bottom.Add(strip);
            r.Root.Add(bottom);
            r.Bottom = bottom;
            r.Goal = Cell(r.Root, "front-line", "");
            r.Defense = Cell(r.Root, "front-line", "");
            r.Info = Cell(r.Root, "front-line", "");
            r.Declare = new Button(() => OpenDeclare(d)) { text = "Объявить войну" };
            r.Declare.AddToClassList("btn");
            r.Declare.AddToClassList("front-btn");
            r.Root.Add(r.Declare);
            r.Peace = new Button(() => OnPeaceClicked(d));
            r.Peace.AddToClassList("btn");
            r.Peace.AddToClassList("front-btn");
            r.PeaceText = new Label("Предложить белый мир") { pickingMode = PickingMode.Ignore };
            r.Peace.Add(r.PeaceText);
            r.Root.Add(r.Peace);

            r.Root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || InButton(evt.target as VisualElement, r.Root)) return;
                OnFrontClicked(d);
                evt.StopPropagation();
            });
            _frontsList.Add(r.Root);
            return r;

            Button OrderButton(VisualElement parent, string icon, string tooltip)
            {
                var b = new Button { tooltip = tooltip };
                b.AddToClassList("btn");
                b.AddToClassList("order-btn");
                b.Add(IconEl(icon));
                parent.Add(b);
                return b;
            }

            static VisualElement Part(VisualElement parent, Color? color, string cls = null)
            {
                var e = new VisualElement { pickingMode = PickingMode.Ignore };
                if (cls != null) e.AddToClassList(cls);
                if (color.HasValue) e.style.backgroundColor = color.Value;
                e.style.flexGrow = 1;
                e.style.flexShrink = 1;
                e.style.flexBasis = 0;
                parent.Add(e);
                return e;
            }
        }

        static bool InButton(VisualElement t, VisualElement stop)
        {
            for (; t != null && t != stop; t = t.parent)
                if (t is Button) return true;
            return false;
        }

        static Label Cell(VisualElement parent, string cls, string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            parent.Add(l);
            return l;
        }

        void MakeGroup(int key, string title)
        {
            var g = new Group { Root = new VisualElement() };
            g.Root.AddToClassList("div-group");
            g.Header = new Label(title);
            g.Header.AddToClassList("div-group-head");
            g.Header.tooltip = "Двойной клик — выделить всю группу";
            g.Header.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount < 2) return;
                if (!evt.shiftKey) Array.Clear(_sel, 0, _sel.Length);
                for (int k = 0; k < _tiles.Length; k++)
                    if (_tiles[k].Group == key) _sel[k] = true;
                Refresh();
                evt.StopPropagation();
            });
            g.Root.Add(g.Header);
            g.Body = new VisualElement();
            g.Body.AddToClassList("div-group-body");
            g.Root.Add(g.Body);
            g.Root.style.display = DisplayStyle.None;
            _divList.Add(g.Root);
            _groups[key] = g;
            _groupOrder.Add(key);
        }

        Tile MakeTile(int k)
        {
            var t = new Tile { Root = new VisualElement { userData = k } };
            t.Root.AddToClassList("div-tile");
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList("div-tile-head");
            t.Icon = IconEl("infantry", "div-tile-icon");
            head.Add(t.Icon);
            t.Num = Cell(head, "div-tile-num", (k + 1).ToString(CultureInfo.InvariantCulture));
            t.Timer = Cell(head, "div-tile-timer", "");
            t.Pause = IconEl("pause", "div-tile-pause");
            head.Add(t.Pause);
            t.Root.Add(head);
            t.StrFill = Bar(t.Root, "div-bar-str");
            t.OrgFill = Bar(t.Root, "div-bar-org");
            t.Root.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.shiftKey) _sel[k] = !_sel[k];
                else
                {
                    bool only = _sel[k] && SelectedCount() == 1;
                    Array.Clear(_sel, 0, _sel.Length);
                    _sel[k] = !only;
                }
                _lastClicked = k;
                Refresh();
                evt.StopPropagation();
            });
            return t;

            static VisualElement Bar(VisualElement parent, string cls)
            {
                var bg = new VisualElement { pickingMode = PickingMode.Ignore };
                bg.AddToClassList("div-bar");
                var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.AddToClassList("div-bar-fill");
                fill.AddToClassList(cls);
                bg.Add(fill);
                parent.Add(bg);
                return fill;
            }
        }

        Marker MakeMarker()
        {
            var m = new Marker { Root = new VisualElement { pickingMode = PickingMode.Ignore } };
            m.Root.AddToClassList("hq-marker");
            var top = new VisualElement { pickingMode = PickingMode.Ignore };
            top.AddToClassList("hq-marker-top");
            m.Icon = IconEl("hq");
            top.Add(m.Icon);
            m.Count = new Label("") { pickingMode = PickingMode.Ignore };
            top.Add(m.Count);
            m.Root.Add(top);
            var bg = new VisualElement { pickingMode = PickingMode.Ignore };
            bg.AddToClassList("hq-org");
            m.OrgFill = new VisualElement { pickingMode = PickingMode.Ignore };
            m.OrgFill.AddToClassList("hq-org-fill");
            bg.Add(m.OrgFill);
            m.Root.Add(bg);
            m.Root.style.display = DisplayStyle.None;
            _markers.Add(m.Root);
            return m;
        }

        /// <summary>
        /// Кнопка с причиной отказа: серая, если команда не пройдёт; причина видна при наведении (tooltip) и по клику.
        /// </summary>
        void Bind(Button b, Func<RejectReason> reason, Func<int> cost, Action ok, string normalTooltip)
        {
            b.clicked += () =>
            {
                if (_sim == null || W.IsOver) return;
                var r = reason();
                if (r != RejectReason.None) { ShowTip(ReasonText(r, cost())); return; }
                ok();
                Refresh();
            };
            b.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (_sim == null) return;
                var r = reason();
                b.tooltip = r != RejectReason.None ? ReasonText(r, cost()) : normalTooltip;
            });
        }

        // ---------- рамка выделения ----------

        void OnBandDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || _sim == null) return;
            // Начинаем рамку только с пустого места: не с плитки и не с заголовка группы.
            for (var t = evt.target as VisualElement; t != null && t != _divList; t = t.parent)
                if (t.ClassListContains("div-tile") || t.ClassListContains("div-group-head")) return;
            _banding = true;
            _bandAdd = evt.shiftKey;
            _bandStart = evt.position;
            _divPanel.CapturePointer(evt.pointerId);
            UpdateBand(evt.position);
            _band.RemoveFromClassList("hidden");
        }

        void OnBandMove(PointerMoveEvent evt)
        {
            if (!_banding) return;
            UpdateBand(evt.position);
        }

        void OnBandUp(PointerUpEvent evt)
        {
            if (!_banding) return;
            _banding = false;
            _divPanel.ReleasePointer(evt.pointerId);
            _band.AddToClassList("hidden");
            var rect = BandRect(evt.position);
            if (!_bandAdd) Array.Clear(_sel, 0, _sel.Length);
            if (rect.width >= 4 || rect.height >= 4)
                for (int k = 0; k < _tiles.Length; k++)
                {
                    var t = _tiles[k];
                    if (t.Group == int.MinValue || t.Root.resolvedStyle.display == DisplayStyle.None) continue;
                    if (t.Root.worldBound.Overlaps(rect)) _sel[k] = true;
                }
            Refresh();
        }

        Rect BandRect(Vector2 now)
        {
            float x0 = Mathf.Min(_bandStart.x, now.x), y0 = Mathf.Min(_bandStart.y, now.y);
            return new Rect(x0, y0, Mathf.Abs(now.x - _bandStart.x), Mathf.Abs(now.y - _bandStart.y));
        }

        void UpdateBand(Vector2 now)
        {
            var r = BandRect(now);
            var origin = _divPanel.worldBound.position;
            _band.style.left = r.x - origin.x;
            _band.style.top = r.y - origin.y;
            _band.style.width = r.width;
            _band.style.height = r.height;
        }

        // ---------- действия ----------

        RejectReason RecruitReason(byte type)
        {
            if (_sim == null || !Alive) return RejectReason.NationInvalid;
            return GameCommands.ValidateRecruit(W, Human, type);
        }

        RejectReason RearReason()
        {
            if (_sim == null || !Alive) return RejectReason.NationInvalid;
            if (SelectedCount() == 0) return RejectReason.DivisionInvalid;
            for (int k = 0; k < _sel.Length; k++)
                if (_sel[k] && Div(k).Front != 0) return RejectReason.None;
            return RejectReason.AlreadyThere;
        }

        void SetFrontOrder(byte d, byte order)
        {
            if (_sim == null || W.IsOver || !Alive) return;
            var r = GameCommands.ValidateFrontOrder(W, Human, d, order);
            if (r != RejectReason.None) { ShowTip(ReasonText(r, 0)); return; }
            if (W.FrontOrderOf(Human, d) == order) return;
            _sim.EnqueueFromUi(CommandType.SetFrontOrder, Human, d, order);
            ShowTip(order == Orders.Attack ? $"Наступление на {_names.Accusative(d)}" : $"Фронт с {_names.Instrumental(d)}: оборона", 2f);
            Refresh();
        }

        void OnFrontClicked(byte d)
        {
            if (SelectedCount() > 0)
            {
                AssignSelected(d);
                return;
            }
            _plateFront = _plateFront == d ? 0 : d;
            if (_plateFront != 0)
            {
                ShowPlate(d);
                if (FrontPoint(d, out var cell)) FlyTo?.Invoke(_map.CellToWorld(cell.x, cell.y));
            }
            else HidePanels();
            Refresh();
        }

        /// <summary>
        /// По одной AssignDivision на каждую выделенную дивизию в один тик, по k. Дивизия, уже стоящая там, ядром
        /// отклоняется AlreadyThere — это не ошибка (01c §10). Подсказка с причиной — только если не пройдёт ни одна.
        /// </summary>
        void AssignSelected(int d)
        {
            if (_sim == null || W.IsOver || !Alive) return;
            int sent = 0;
            RejectReason first = RejectReason.DivisionInvalid;
            for (int k = 0; k < _sel.Length; k++)
            {
                if (!_sel[k]) continue;
                var r = GameCommands.ValidateAssign(W, Human, k, d);
                if (r == RejectReason.None) sent++;
                else if (first == RejectReason.DivisionInvalid || first == RejectReason.AlreadyThere) first = r;
                if (r == RejectReason.None || r == RejectReason.AlreadyThere)
                    _sim.EnqueueFromUi(CommandType.AssignDivision, Human, k, d);
            }
            if (sent == 0)
            {
                ShowTip(ReasonText(first, 0));
                return;
            }
            ShowTip(d == 0 ? $"В тыл: {Divisions(sent)}" : $"На фронт с {_names.Instrumental((byte)d)}: {Divisions(sent)}, в пути {_rules.Army.TransferTicks / _config.TicksPerSecond} с", 2.5f);
            Deselect();
            Refresh();
        }

        /// <summary>Клик по карте. true — обработан (назначение или плашка соседа).</summary>
        public bool OnMapClick(bool onMap, int x, int y)
        {
            if (_sim == null || W.IsOver) return false;
            if (!onMap)
            {
                Cancel();
                return true;
            }
            var w = W;
            if (_claimTarget != 0)
            {
                if (!onMap) return true;
                int c = w.Index(x, y);
                var check = WarSystem.CheckClaim(w, Human, _claimTarget, c, out _);
                if (check != ClaimCheck.Ok) { ShowTip(ClaimText(check), 2f); return true; }
                int target = _claimTarget;
                StopClaim();
                Declare(target, c + 1);
                return true;
            }
            int i = w.Index(x, y);
            int o = w.IsLand(i) ? w.Owner[i] : 0;
            bool selected = SelectedCount() > 0;
            if (o == 0 || o == Human)
            {
                if (selected) { ShowTip("Назначить можно только на фронт с соседом: кликните по его земле"); return true; }
                if (_plateFront != 0) { HidePanels(); Refresh(); }
                return false;
            }
            if (selected && Alive)
            {
                AssignSelected(o);
                return true;
            }
            if (Contact(o))
            {
                _plateFront = o;
                ShowPlate((byte)o);
                Refresh();
                return true;
            }
            ShowTip($"{_names.Full(o)}: общей границы с нами нет");
            return true;
        }

        /// <summary>
        /// Клавиши партии: Tab — следующая дивизия, A — выделить весь тыл, R — выделенные в тыл,
        /// Q / E — наступление / оборона на открытой плашкой фронте (или на фронтах выделенных дивизий).
        /// </summary>
        public void OnKeys(UnityEngine.InputSystem.Keyboard kb)
        {
            if (_sim == null || kb == null || W.IsOver) return;
            if (_root.panel?.focusController?.focusedElement is TextField) return;
            int maxK = W.MaxDivisionsPerNation;
            if (kb.tabKey.wasPressedThisFrame)
            {
                for (int step = 1; step <= maxK; step++)
                {
                    int k = (_lastClicked + step + maxK) % maxK;
                    if (!GameCommands.OwnDivision(W, Human, k)) continue;
                    Array.Clear(_sel, 0, _sel.Length);
                    _sel[k] = true;
                    _lastClicked = k;
                    break;
                }
                Refresh();
            }
            if (kb.aKey.wasPressedThisFrame)
            {
                Array.Clear(_sel, 0, _sel.Length);
                for (int k = 0; k < maxK; k++)
                {
                    var v = Div(k);
                    if (v.Alive != 0 && v.Front == 0 && !W.Training(v)) _sel[k] = true;
                }
                Refresh();
            }
            if (kb.qKey.wasPressedThisFrame) OrderKey(Orders.Attack);
            if (kb.eKey.wasPressedThisFrame) OrderKey(Orders.Defend);
            if (kb.rKey.wasPressedThisFrame)
            {
                var r = RearReason();
                if (r != RejectReason.None) ShowTip(ReasonText(r, 0));
                else AssignSelected(0);
            }
        }

        void OrderKey(byte order)
        {
            if (_plateFront != 0) { SetFrontOrder((byte)_plateFront, order); return; }
            var done = new bool[W.NationCount + 1];
            bool any = false;
            for (int k = 0; k < _sel.Length; k++)
            {
                if (!_sel[k]) continue;
                byte f = Div(k).Front;
                if (f == 0 || done[f]) continue;
                done[f] = true;
                any = true;
                SetFrontOrder(f, order);
            }
            if (!any) ShowTip("Приказ отдаётся фронту: откройте фронт кликом или выделите дивизии на нём");
        }

        /// <summary>Только для скриншота бенча: открыть плашку первого фронта и выделить дивизии в тылу.</summary>
        public Vector3? ShowFirstFrontForShot()
        {
            Vector3? at = null;
            int pick = 0;
            for (int d = 1; d <= W.NationCount && pick == 0; d++)
                if (Contact(d) && W.AtWar(Human, d)) pick = d;
            for (int d = 1; d <= W.NationCount; d++)
                if (Contact(d) && (pick == 0 || d == pick))
                {
                    _plateFront = d;
                    ShowPlate((byte)d);
                    if (FrontPoint((byte)d, out var c)) at = _map.CellToWorld(c.x, c.y);
                    break;
                }
            Array.Clear(_sel, 0, _sel.Length);
            for (int k = 0; k < _sel.Length; k++)
                if (GameCommands.OwnDivision(W, Human, k) && Div(k).Front == 0) _sel[k] = true;
            Refresh();
            return at;
        }

        /// <summary>ПКМ или Esc: снять выделение, закрыть плашку и подсказку.</summary>
        public void Cancel()
        {
            StopClaim();
            CloseDeclare();
            Deselect();
            HidePanels();
            if (_sim != null) Refresh();
        }

        void Deselect()
        {
            if (_sel != null) Array.Clear(_sel, 0, _sel.Length);
        }

        public void HidePanels()
        {
            _plateFront = 0;
            _plate.AddToClassList("hidden");
            _tip.AddToClassList("hidden");
            _map.SetFocusNation(0);
        }

        void ShowPlate(byte d)
        {
            FillPlate(d);
            _plate.RemoveFromClassList("hidden");
        }

        public void ShowTip(string text, float seconds = 4f)
        {
            _tip.text = text;
            _tip.RemoveFromClassList("hidden");
            _tipHideAt = Time.unscaledTime + seconds;
        }

        /// <summary>
        /// Плашка фронта (спека 02a §9.2, 01c §9): те же AttPow / DefPow ядра, что двигают границу, но словами.
        /// Таблицы по рельефу — в подсказке по наведению на строку «Они».
        /// </summary>
        void FillPlate(byte d)
        {
            var w = W;
            int len = Math.Max(1, w.FrontLength(Human, d));
            FrontState(Human, d, out int ours, out int ourTransit, out int ourArmor, out int ourOrg, out bool weTired);
            FrontState(d, Human, out int theirs, out _, out _, out int theirOrg, out bool theyTired);
            _plateTitle.text = w.AtWar(Human, d) ? $"Фронт с {_names.Instrumental(d)}" : $"Граница с {_names.Instrumental(d)}";
            long wa = w.WeightedAttack(Human, d), wd = w.WeightedDefense(Human, d);
            long ta = w.WeightedAttack(d, Human), td = w.WeightedDefense(d, Human);
            bool weOrder = w.FrontOrderOf(Human, d) == Orders.Attack;
            bool theyOrder = w.FrontOrderOf(d, Human) == Orders.Attack;
            bool weAttack = wa > 0, theyAttack = ta > 0;
            SetIcon(_plateUsIcon, ours == 0 ? "frontOpen" : weTired ? "pause" : weOrder ? "attack" : "defend");
            string transit = ourTransit > 0 ? $" · в пути {ourTransit}" : "";
            _plateUs.text = ours == 0
                ? $"Мы: только гарнизон · оборона {wd / len}{transit}"
                : $"Мы: {Divisions(ours)} ({ourArmor} танк., {ours - ourArmor} пех.) · {(weOrder ? (weTired ? "наступление выдохлось" : "наступление") : "оборона")} · организация {ourOrg / 100}% · {(weAttack ? wa : wd) / len} на ребро{transit}";
            SetIcon(_plateThemIcon, theirs == 0 ? "frontOpen" : theyTired ? "pause" : theyOrder ? "attack" : "defend");
            _plateThem.text = theirs == 0
                ? $"Они: только гарнизон · оборона {td / len}"
                : $"Они: {Divisions(theirs)} · {(theyOrder ? (theyTired ? "наступление выдохлось" : "наступление") : "оборона")} · организация {theirOrg / 100}%";
            _plateThemRow.tooltip = $"Их оборона на ребро {Triple(tr => w.DefPow(d, Human, tr))} (равнина / холмы / горы)"
                                    + (theyAttack ? $"\nИх атака на ребро {Triple(tr => w.AttPow(d, Human, tr))}" : "")
                                    + $"\nНаша {(weAttack ? "атака" : "оборона")} на ребро {Triple(tr => weAttack ? w.AttPow(Human, d, tr) : w.DefPow(Human, d, tr))}";
            _platePlains.text = $"{w.FrontTerrainLength(Human, d, 0) * 100 / len}%";
            _plateHills.text = $"{w.FrontTerrainLength(Human, d, 1) * 100 / len}%";
            _plateMountains.text = $"{w.FrontTerrainLength(Human, d, 2) * 100 / len}%";
            bool capital = CapitalNearFront(d);
            _plateCapitalRow.style.display = capital ? DisplayStyle.Flex : DisplayStyle.None;
            _plateCapital.text = $"Их столица рядом ×{_rules.Front.CapitalMult_bp / 10000}";

            int net = w.NetFlips10s(Human, d);
            string move = net > 0 ? $"берём ~{net} px за 10 с" : net < 0 ? $"теряем ~{-net} px за 10 с" : "фронт стоит";
            string verdict;
            if (weTired && net <= 0)
                verdict = $"Наступление выдохлось — {move}. Подождите организацию или подведите свежие дивизии";
            else if (weAttack && (!theyAttack || wa * Math.Max(1, wd) >= ta * Math.Max(1, td)))
                verdict = wa >= td ? $"Перевес {Ratio(wa, td)} — {move}" : $"Сил мало: {Ratio(wa, td)} — {move}";
            else if (theyAttack)
                verdict = ta >= wd ? $"У них перевес {Ratio(ta, wd)} — {move}" : $"Держим: у них {Ratio(ta, wd)} — {move}";
            else verdict = ours == 0 ? $"Никто не наступает — {move}. Поставьте дивизии" : $"Никто не наступает — {move}";
            if (!w.AtWar(Human, d))
                verdict = w.InTruce(Human, d) ? "Перемирие: граница закрыта, наступать нельзя" : "Мир: граница стоит. Чтобы наступать, объявите войну";
            _plateVerdict.text = verdict;
            _plateVerdict.EnableInClassList("good", net > 0);
            _plateVerdict.EnableInClassList("bad", net < 0);
        }

        static string Divisions(int n) =>
            n % 10 == 1 && n % 100 != 11 ? $"{n} дивизия"
            : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? $"{n} дивизии" : $"{n} дивизий";

        static string Triple(Func<int, int> f) => $"{f(0)} / {f(1)} / {f(2)}";

        static string Ratio(long a, long b)
        {
            if (b <= 0) return "∞ : 1";
            return ((double)a / b).ToString("0.0", Ru) + " : 1";
        }

        /// <summary>Есть ли на фронте пиксели соседа d в радиусе его столицы.</summary>
        bool CapitalNearFront(byte d)
        {
            var w = W;
            var frontier = w.Frontier;
            int bit = 1 << (d - 1);
            for (int i = 0; i < frontier.Length; i++)
            {
                int p = frontier[i];
                if (w.Owner[p] == d && (w.CapitalCover[p] & bit) != 0 && TouchesNation(p, Human)) return true;
            }
            return false;
        }

        // ---------- подсказки по наведению ----------

        /// <summary>
        /// Свои подсказки по наведению: в собранной игре UI Toolkit свойство tooltip не показывает.
        /// Под курсором ищется ближайший элемент с непустым tooltip; через 0,3 с текст встаёт рядом с курсором.
        /// </summary>
        public void UpdateHover(Vector2 screenMouse)
        {
            var panel = _root.panel;
            if (panel == null || _hover == null) return;
            var p = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screenMouse.x, Screen.height - screenMouse.y));
            VisualElement owner = null;
            for (var e = panel.Pick(p); e != null; e = e.parent)
                if (!string.IsNullOrEmpty(e.tooltip)) { owner = e; break; }
            float now = Time.unscaledTime;
            if (owner != _hoverOwner)
            {
                _hoverOwner = owner;
                _hoverSince = now;
                _hover.AddToClassList("hidden");
            }
            if (owner == null || _banding || now - _hoverSince < 0.3f) return;
            _hover.text = owner.tooltip;
            _hover.RemoveFromClassList("hidden");
            var rootBound = _root.worldBound;
            float hw = _hover.resolvedStyle.width, hh = _hover.resolvedStyle.height;
            if (float.IsNaN(hw)) hw = 300;
            if (float.IsNaN(hh)) hh = 40;
            float x = p.x + 20, y = p.y + 24;
            if (x + hw > rootBound.width - 8) x = p.x - hw - 12;
            if (y + hh > rootBound.height - 8) y = p.y - hh - 12;
            _hover.style.left = Mathf.Round(Mathf.Max(8, x));
            _hover.style.top = Mathf.Round(Mathf.Max(8, y));
        }

        // ---------- значки на карте и позиции ----------

        /// <summary>
        /// Раз в секунду: на каждом своём фронте значок с числом своих дивизий на месте и полоской их средней организации
        /// (стрелка — наступаем, пауза — все наступающие выдохлись) в точке фронта, ближайшей к среднему его пограничных
        /// пикселей человека; открытый фронт — пустой круг. Позиции — каждый кадр; там же плашка фронта едет за своей точкой,
        /// сводка и подсказка гаснут.
        /// </summary>
        public void UpdateMarkers(float now)
        {
            if (_sim == null || _marks == null) return;
            var w = W;
            int n = w.NationCount;
            if (now >= _nextMarkers)
            {
                _nextMarkers = now + 1f;
                RecountMarkers();
            }
            for (int i = _feedItems.Count - 1; i >= 0; i--)
                if (now >= _feedItems[i].HideAt)
                {
                    _feedItems[i].Root.RemoveFromHierarchy();
                    _feedItems.RemoveAt(i);
                }
            if (_tipHideAt > 0 && now >= _tipHideAt)
            {
                _tip.AddToClassList("hidden");
                _tipHideAt = 0;
            }

            var panel = _markers.panel;
            if (panel == null || _camera == null) return;
            var origin = _markers.worldBound.position;
            for (int d = 1; d <= n; d++)
            {
                var m = _marks[d];
                if (!m.Cell.HasValue) continue;
                var pos = RuntimePanelUtils.CameraTransformWorldToPanel(panel, _map.CellToWorld(m.Cell.Value.x, m.Cell.Value.y), _camera) - origin;
                float mw = m.Root.resolvedStyle.width;
                if (float.IsNaN(mw)) mw = 40;
                m.Root.style.left = Mathf.Round(pos.x - mw * 0.5f);
                m.Root.style.top = Mathf.Round(pos.y - 18);
            }

            // Плашка над точкой фронта, выноска вниз к ней; зажата в экран.
            if (_plateFront != 0 && FrontPoint((byte)_plateFront, out var pc))
            {
                var area = _mapArea.worldBound;
                var pos = RuntimePanelUtils.CameraTransformWorldToPanel(panel, _map.CellToWorld(pc.x, pc.y), _camera) - area.position;
                float ph = _plate.resolvedStyle.height;
                if (float.IsNaN(ph) || ph <= 0) ph = 220;
                float wantTop = pos.y - ph - 32;
                float left = Mathf.Clamp(pos.x - 49, 16, Mathf.Max(16, area.width - 436));
                // Сверху не помещается — встаём под точкой фронта (выноска тогда не рисуется).
                if (wantTop < 100) wantTop = pos.y + 32;
                float top = Mathf.Clamp(wantTop, 100, Mathf.Max(100, area.height - ph - 16));
                _plate.style.left = Mathf.Round(left);
                _plate.style.top = Mathf.Round(top);
                bool pointerOk = Mathf.Abs(top - wantTop) < 1f && pos.x - left > 8 && pos.x - left < 412;
                _platePointer.style.display = pointerOk ? DisplayStyle.Flex : DisplayStyle.None;
                _platePointer.style.left = Mathf.Round(pos.x - left - 12);
            }
        }

        void RecountMarkers()
        {
            var w = W;
            int n = w.NationCount;
            var sumX = new long[n + 1];
            var sumY = new long[n + 1];
            var count = new int[n + 1];
            var frontier = w.Frontier;
            int width = w.Width;
            for (int i = 0; i < frontier.Length; i++)
            {
                int p = frontier[i];
                if (w.Owner[p] != Human) continue;
                byte edge = w.EdgeMask[p];
                int seen = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (!Core.Grid.Neighbour(p, edge, k, width, out int q) || !w.IsLand(q)) continue;
                    int o = w.Owner[q];
                    if (o == 0 || o == Human || (seen & (1 << o)) != 0) continue;
                    seen |= 1 << o;
                    sumX[o] += p % width;
                    sumY[o] += p / width;
                    count[o]++;
                }
            }
            for (int d = 1; d <= n; d++)
            {
                var m = _marks[d];
                if (d == Human || count[d] == 0 || !Contact(d) || !Alive)
                {
                    m.Root.style.display = DisplayStyle.None;
                    m.Cell = null;
                    continue;
                }
                long mx = sumX[d] / count[d], my = sumY[d] / count[d];
                int best = -1;
                long bestD = long.MaxValue;
                for (int i = 0; i < frontier.Length; i++)
                {
                    int p = frontier[i];
                    if (w.Owner[p] != Human || !TouchesNation(p, (byte)d)) continue;
                    long dx = p % width - mx, dy = p / width - my, d2 = dx * dx + dy * dy;
                    if (d2 < bestD) { bestD = d2; best = p; }
                }
                FrontState(Human, (byte)d, out int onPlace, out _, out _, out int avgOrg, out bool tired);
                bool attack = onPlace > 0 && w.FrontOrderOf(Human, d) == Orders.Attack;
                SetIcon(m.Icon, onPlace == 0 ? "frontOpen" : tired ? "pause" : attack ? "attack" : "hq");
                m.Count.text = onPlace == 0 ? "" : onPlace.ToString(CultureInfo.InvariantCulture);
                m.OrgFill.parent.style.display = onPlace == 0 ? DisplayStyle.None : DisplayStyle.Flex;
                m.OrgFill.style.width = Length.Percent(avgOrg / 100f);
                m.OrgFill.EnableInClassList("exhausted", tired);
                m.Root.EnableInClassList("attack", attack && !tired);
                m.Root.EnableInClassList("empty", onPlace == 0);
                m.Root.EnableInClassList("tired", tired);
                m.Cell = new Vector2Int(best % width, best / width);
                m.Root.style.display = DisplayStyle.Flex;
            }
        }

        /// <summary>Точка фронта с d (та же, где значок фронта); пока значки не посчитаны — первый пограничный пиксель.</summary>
        bool FrontPoint(byte d, out Vector2Int cell)
        {
            cell = default;
            if (_marks != null && _marks[d].Cell.HasValue) { cell = _marks[d].Cell.Value; return true; }
            var w = W;
            var frontier = w.Frontier;
            for (int i = 0; i < frontier.Length; i++)
            {
                int p = frontier[i];
                if (w.Owner[p] == Human && TouchesNation(p, d)) { cell = new Vector2Int(p % w.Width, p / w.Width); return true; }
            }
            return false;
        }

        bool TouchesNation(int p, byte d)
        {
            var w = W;
            byte edge = w.EdgeMask[p];
            for (int k = 0; k < 4; k++)
                if (Core.Grid.Neighbour(p, edge, k, w.Width, out int q) && w.Owner[q] == d && w.IsLand(q)) return true;
            return false;
        }

        // ---------- сводка (02a §9.2 + 01c §9) ----------

        public void CheckEvents()
        {
            if (_sim == null) return;
            var w = W;
            int n = w.NationCount, maxK = w.MaxDivisionsPerNation;
            bool alive = Alive;
            for (int d = 1; d <= n; d++)
            {
                if (d == Human) continue;
                bool contact = Contact(d);
                if (alive && contact && !_prevContact[d])
                    Feed(FeedKind.Urgent, $"Новый фронт с {_names.Instrumental(d)} — он открыт", FrontCell((byte)d));
                if (alive && !contact && _prevContact[d] && w.Nation((byte)d).Alive != 0)
                    Feed(FeedKind.Report, $"Фронт с {_names.Instrumental(d)} закрылся, дивизии с него — в тылу", null);
                _prevContact[d] = contact;
                bool attacked = alive && contact && w.WeightedAttack(d, Human) > 0;
                if (attacked && !_prevAttacked[d])
                {
                    FrontState(Human, (byte)d, out int ours, out _, out _, out _, out _);
                    Feed(FeedKind.Urgent, ours == 0 ? $"{_names.Nominative(d)} наступает на нас — фронт открыт" : $"{_names.Nominative(d)} наступает на нас", FrontCell((byte)d));
                }
                _prevAttacked[d] = attacked;
                bool tired = false;
                if (alive && contact) FrontState(Human, (byte)d, out _, out _, out _, out _, out tired);
                if (tired && !_prevExhausted[d]) Feed(FeedKind.Loss, $"Наступление на {_names.Accusative(d)} выдохлось", FrontCell((byte)d));
                _prevExhausted[d] = tired;
                bool natAlive = w.Nation((byte)d).Alive != 0;
                if (_prevAlive[d] && !natAlive)
                {
                    int cap = _lastCapital[d];
                    Feed(FeedKind.Report, $"Пала столица {_names.Genitive(d)}", cap >= 0 ? new Vector2Int(cap % w.Width, cap / w.Width) : (Vector2Int?)null);
                }
                _prevAlive[d] = natAlive;
                if (natAlive) _lastCapital[d] = w.Nation((byte)d).Capital;
            }
            CheckWarEvents();
            for (int k = 0; k < maxK; k++)
            {
                var v = Div(k);
                bool training = v.Alive != 0 && w.Training(v);
                if (alive && v.Alive != 0 && _prevTraining[k] && !training)
                    Feed(FeedKind.Report, $"{k + 1}-я {(v.Type == DivisionTypes.Armor ? "танковая" : "пехотная")} дивизия готова" + (v.Front != 0 ? $", идёт на фронт с {_names.Instrumental(v.Front)}" : ", в тылу"), null);
                _prevTraining[k] = training;
            }
        }

        Vector2Int? FrontCell(byte d) => FrontPoint(d, out var c) ? c : (Vector2Int?)null;

        /// <summary>Сводка войны и мира (02b §9) по журналу ядра и состоянию пар.</summary>
        void CheckWarEvents()
        {
            var w = W;
            int tps = _config.TicksPerSecond;
            bool alive = Alive;
            for (; _seenEvents < w.WarEvents.Count; _seenEvents++)
            {
                var e = w.WarEvents[_seenEvents];
                bool ours = e.A == Human || e.D == Human;
                byte other = e.A == Human ? e.D : e.A;
                switch (e.Kind)
                {
                    case WarEventKind.Declared:
                        if (e.D == Human)
                            Feed(FeedKind.Urgent, $"{_names.Nominative(e.A)} объявляет нам войну: " +
                                (e.Goal == WarGoals.Claims ? "претензии на наш район" : "требует репараций"), FrontCell(e.A));
                        else if (!ours)
                            Feed(FeedKind.Report, $"{_names.Nominative(e.A)} объявляет войну {_names.Dative(e.D)}", null);
                        break;
                    case WarEventKind.Offer:
                        if (e.D == Human && alive) OfferCard(e.A);
                        break;
                    case WarEventKind.OfferRejected:
                        if (e.D == Human) Feed(FeedKind.Report, $"{_names.Nominative(e.A)} отказывает в белом мире", null);
                        break;
                    case WarEventKind.Peace:
                    {
                        if (!ours) break;
                        CloseOffer(other);
                        var war = w.WarOf(e.A, e.D);
                        string until = Clock(war.TruceUntil / tps);
                        string rate = (e.Value / 100.0).ToString("0.0", Ru);
                        switch (e.Outcome)
                        {
                            case WarOutcome.Claims:
                                if (e.A == Human) Feed(FeedKind.Report, $"Мир с {_names.Instrumental(other)}: район наш", null);
                                else Feed(FeedKind.Loss, $"Мир навязан: {_names.Nominative(other)} забирает район", null);
                                break;
                            case WarOutcome.Reparations:
                            case WarOutcome.DefenseWin:
                                if (war.TributePayer == Human) Feed(FeedKind.Loss, $"Мы платим репарации {_names.Dative(other)}: {rate} в секунду до {Clock(war.TributeUntil / tps)}", null);
                                else Feed(FeedKind.Report, $"{_names.Nominative(other)} платит нам {rate} в секунду до {Clock(war.TributeUntil / tps)}", null);
                                break;
                            case WarOutcome.WhiteByTimer:
                                Feed(FeedKind.Report, $"Война с {_names.Instrumental(other)} кончилась сама: белый мир", null);
                                break;
                            default:
                                Feed(FeedKind.Report, $"Перемирие с {_names.Instrumental(other)} до {until}", null);
                                break;
                        }
                        break;
                    }
                }
            }
            for (int d = 1; d <= w.NationCount; d++)
            {
                if (d == Human) continue;
                var war = w.WarOf(Human, d);
                bool staging = alive && war.Status == WarStatus.Peace && Contact(d) && DivisionsAtBorder((byte)d, Human) >= StagingAlert;
                if (staging && !_prevStaging[d]) Feed(FeedKind.Urgent, $"{_names.Nominative(d)} стягивает дивизии к нашей границе", FrontCell((byte)d));
                _prevStaging[d] = staging;
                bool held = alive && war.Status == WarStatus.War && war.Attacker == d && war.HoldAtt > 0;
                if (held && !_prevHeldOnUs[d])
                    Feed(FeedKind.Urgent, war.Goal == WarGoals.Claims
                        ? $"{_names.Nominative(d)} удерживает наш район: {war.HoldAtt} / {_rules.War.GoalHoldSeconds} с"
                        : $"{_names.Nominative(d)} держит нашу землю: {war.HoldAtt} / {_rules.War.GoalHoldSeconds} с до репараций", FrontCell((byte)d));
                _prevHeldOnUs[d] = held;
                if (_offerCards.ContainsKey(d) && !(war.Status == WarStatus.War && war.OfferFrom == d && w.Tick < war.OfferUntil)) CloseOffer(d);
            }
        }

        /// <summary>Предложение белого мира от ИИ: карточка в сводке с «Принять» / «Отказать», живёт, пока живо предложение. Пауза не ставится.</summary>
        void OfferCard(byte from)
        {
            CloseOffer(from);
            var root = new VisualElement();
            root.AddToClassList("panel");
            root.AddToClassList("feed-card");
            root.AddToClassList("offer");
            var stripe = new VisualElement { pickingMode = PickingMode.Ignore };
            stripe.AddToClassList("feed-stripe");
            root.Add(stripe);
            int sec = W.Tick / _config.TicksPerSecond;
            Cell(root, "feed-head", $"СРОЧНО · {sec / 60}:{sec % 60:00}");
            Cell(root, "feed-body", $"{_names.Nominative(from)} предлагает белый мир");
            Cell(root, "feed-body", "каждый оставляет занятое");
            var row = new VisualElement();
            row.AddToClassList("offer-row");
            var yes = new Button(() => Answer(from, 1)) { text = "Принять" };
            yes.AddToClassList("btn");
            var no = new Button(() => Answer(from, 0)) { text = "Отказать" };
            no.AddToClassList("btn");
            row.Add(yes);
            row.Add(no);
            root.Add(row);
            root.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            var item = new FeedItem { Root = root, HideAt = Time.unscaledTime + 3600f, Cell = FrontCell(from) };
            _feed.Add(root);
            _feedItems.Add(item);
            _offerCards[from] = item;
        }

        void CloseOffer(int from)
        {
            if (!_offerCards.TryGetValue(from, out var card)) return;
            card.Root.RemoveFromHierarchy();
            _feedItems.Remove(card);
            _offerCards.Remove(from);
        }

        void Feed(FeedKind kind, string text, Vector2Int? cell)
        {
            var root = new VisualElement();
            root.AddToClassList("panel");
            root.AddToClassList("feed-card");
            root.AddToClassList(kind == FeedKind.Urgent ? "urgent" : kind == FeedKind.Loss ? "loss" : "report");
            var stripe = new VisualElement { pickingMode = PickingMode.Ignore };
            stripe.AddToClassList("feed-stripe");
            root.Add(stripe);
            int sec = W.Tick / _config.TicksPerSecond;
            Cell(root, "feed-head", (kind == FeedKind.Urgent ? "СРОЧНО" : kind == FeedKind.Loss ? "ПОТЕРЯ" : "СВОДКА") + $" · {sec / 60}:{sec % 60:00}");
            Cell(root, "feed-body", text);
            root.tooltip = text + (cell.HasValue ? "\nКлик — к месту" : "");
            var item = new FeedItem { Root = root, HideAt = Time.unscaledTime + FeedSeconds, Cell = cell };
            root.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                if (item.Cell.HasValue) FlyTo?.Invoke(_map.CellToWorld(item.Cell.Value.x, item.Cell.Value.y));
                e.StopPropagation();
            });
            _feed.Add(root); // column-reverse: новые сверху
            _feedItems.Add(item);
            while (_feedItems.Count > MaxFeed + _offerCards.Count)
            {
                int victim = 0;
                while (victim < _feedItems.Count - 1 && _offerCards.ContainsValue(_feedItems[victim])) victim++;
                _feedItems[victim].Root.RemoveFromHierarchy();
                _feedItems.RemoveAt(victim);
            }
        }

        // ---------- тексты ----------

        static string Gold(long c) => (c / 100).ToString(CultureInfo.InvariantCulture);
        static string PerSec(long c) => (c >= 0 ? "+" : "−") + (Math.Abs(c) / 100.0).ToString("0.0", Ru);

        /// <summary>Тексты причин отказа, словарь 02a: бюджет вместо золота.</summary>
        string ReasonText(RejectReason r, int cost) => ReasonText(r, cost, 0);

        string ReasonText(RejectReason r, int cost, int enemy)
        {
            var w = W;
            switch (r)
            {
                case RejectReason.AlreadyAtWar: return "Мы уже воюем";
                case RejectReason.TruceActive:
                {
                    int tps = _config.TicksPerSecond;
                    int left = enemy > 0 ? (w.WarOf(Human, enemy).TruceUntil - w.Tick + tps - 1) / tps : 0;
                    return $"Перемирие ещё {Clock(left)}";
                }
                case RejectReason.ClaimInvalid: return "Район не подходит: нужен у границы, на их земле, не у их столицы";
                case RejectReason.NotAtWar: return "Мы с ними не воюем";
                case RejectReason.NoOffer: return "Предложение уже не действует";
                case RejectReason.PeaceCooldown: return "Предлагать белый мир можно не чаще раза в минуту";
                case RejectReason.NotEnoughGold:
                {
                    long missing = cost - w.Nation(Human).Gold;
                    long net = EconomySystem.Income(w, Human) - DivisionSystem.Upkeep(w, Human) - DivisionSystem.ReinforceDemand(w, Human);
                    string when = net > 0 ? $"накопим через ~{(missing + net - 1) / net} с" : "доход не покрывает расходы на армию";
                    return $"Не хватает {(missing + 99) / 100} в бюджете, {when}";
                }
                case RejectReason.DivisionLimit: return $"Больше {w.MaxDivisionsPerNation} дивизий не набрать";
                case RejectReason.NoContact: return "Нет общей границы";
                case RejectReason.AlreadyThere: return "Выделенные дивизии уже там";
                case RejectReason.TargetInvalid: return "Фронт — только с живым соседом";
                case RejectReason.DivisionInvalid: return "Сначала выделите дивизии: клик по плитке, Shift-клик, рамка или A — весь тыл";
                case RejectReason.NationInvalid: return "Наша столица пала";
                default: return r.ToString();
            }
        }
    }
}
