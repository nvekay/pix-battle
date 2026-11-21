using System;
using System.Collections.Generic;
using System.Globalization;
using BattlePix.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace BattlePix.Game
{
    /// <summary>
    /// Интерфейс партии (спека 02a §9, ПК): карточка нации, HUD группами, «Фронты», плашка фронта с выноской,
    /// «Соединения», сводка, значки штабов на карте, клавиши. Только читает мир между тиками и отдаёт команды
    /// человека через EnqueueFromUi. Числа ядра переводятся в слова, сырые — в подсказки по наведению.
    /// Назначение: клик по карточке выделяет соединение, потом клик по строке фронта, по соседу на карте или «В тыл».
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
        readonly VisualElement _frontsList, _markers, _feed, _mapArea;
        readonly ScrollView _formationsList;
        readonly Button _btnNewHq, _btnNewType, _btnRear;

        // Плашка фронта.
        readonly VisualElement _plate, _plateThemRow, _plateCapitalRow, _plateUsIcon, _plateThemIcon, _platePointer;
        readonly Label _plateTitle, _plateUs, _plateThem, _platePlains, _plateHills, _plateMountains, _plateCapital, _plateVerdict;

        FrontRow[] _rows;
        Card[] _cards;
        Marker[] _hq;
        byte _newType = UnitTypes.Infantry;
        int _selected = -1;     // выделенное соединение k, −1 — нет
        int _plateFront;        // сосед, чья плашка открыта; 0 — закрыта
        float _nextMarkers;
        float _tipHideAt;
        readonly Label _hover;
        VisualElement _hoverOwner;
        float _hoverSince;

        // Состояние прошлой проверки для сводки.
        bool[] _prevAlive, _prevContact, _prevAttacked, _prevBled;
        byte[] _prevFront;
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
        }

        sealed class Card
        {
            public VisualElement Root, Fill;
            public Label Title, Inf, Armor, Where, AddInfText, AddArmorText;
            public Button Attack, Defend, AddInf, AddArmor;
        }

        sealed class Marker
        {
            public VisualElement Root, Icon;
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
            _formationsList = root.Q<ScrollView>("formations-list");
            _btnNewHq = root.Q<Button>("btn-new-hq");
            _btnNewType = root.Q<Button>("btn-new-type");
            _btnRear = root.Q<Button>("btn-rear");

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

            Bind(_btnNewHq, () => RecruitReason(-1, _newType), () => GameCommands.RecruitCost(W, -1, _newType),
                () => _sim.EnqueueFromUi(CommandType.RecruitUnit, Human, -1, _newType), "Новое соединение со штабом и одним батальоном");
            _btnNewType.clicked += () =>
            {
                _newType = _newType == UnitTypes.Infantry ? UnitTypes.Armor : UnitTypes.Infantry;
                Refresh();
            };
            Bind(_btnRear, RearReason, () => 0, () =>
            {
                _sim.EnqueueFromUi(CommandType.AssignFormation, Human, _selected, 0);
                Deselect();
            }, "Отвести выделенное соединение в тыл (R)");
        }

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
            int n = W.NationCount, maxF = W.MaxFormationsPerNation;
            _frontsList.Clear();
            _formationsList.Clear();
            _markers.Clear();
            foreach (var f in _feedItems) f.Root.RemoveFromHierarchy();
            _feedItems.Clear();
            _rows = new FrontRow[n + 1];
            for (int d = 1; d <= n; d++)
                if (d != Human) _rows[d] = MakeRow((byte)d);
            _cards = new Card[maxF];
            for (int k = 0; k < maxF; k++) _cards[k] = MakeCard(k);
            _hq = new Marker[n + 1];
            for (int d = 1; d <= n; d++) _hq[d] = MakeMarker();
            _selected = -1;
            _plateFront = 0;
            _prevAlive = new bool[n + 1];
            _prevContact = new bool[n + 1];
            _prevAttacked = new bool[n + 1];
            _prevBled = new bool[maxF];
            _prevFront = new byte[maxF];
            _lastCapital = new int[n + 1];
            for (int d = 1; d <= n; d++)
            {
                _prevAlive[d] = W.Nation((byte)d).Alive != 0;
                _prevContact[d] = Contact(d);
                _lastCapital[d] = W.Nation((byte)d).Capital;
            }
            for (int k = 0; k < maxF; k++) _prevFront[k] = W.FormationAt(W.FormationSlot(Human, k)).Front;
            _nextMarkers = 0;
            HidePanels();
            Refresh();
        }

        bool Alive => W.Nation(Human).Alive != 0;
        bool Contact(int d) => d != Human && W.Nation((byte)d).Alive != 0 && W.FrontLength(Human, d) > 0;

        // ---------- обновление ----------

        /// <summary>Четыре раза в секунду: HUD, строки фронтов, карточки, плашка.</summary>
        public void Refresh()
        {
            if (_sim == null) return;
            var w = W;
            var me = w.Nation(Human);
            long upkeep = Alive ? FormationSystem.Upkeep(w, Human) : 0;
            long reinforce = Alive ? FormationSystem.ReinforceDemand(w, Human) : 0;
            long net = Alive ? EconomySystem.Income(w, Human) - upkeep - reinforce : 0;
            _budget.text = Gold(me.Gold);
            _budgetDelta.text = PerSec(net) + "/с";
            SetGoodBad(_budgetDelta, net);
            _budgetDelta.tooltip = $"Чистый доход: доход {PerSec(Alive ? EconomySystem.Income(w, Human) : 0)}/с минус армия {PerSec(upkeep + reinforce)}/с";
            _army.text = $"{me.Battalions} бат.";
            _armyDelta.text = PerSec(-(upkeep + reinforce)) + "/с";
            _armyDelta.tooltip = $"Содержание {PerSec(-upkeep)}/с" + (reinforce > 0 ? $", пополнение {PerSec(-reinforce)}/с" : "");
            int left = Math.Max(0, _rules.End.TimeoutTicks - w.Tick) / _config.TicksPerSecond;
            _timer.text = $"{left / 60:00}:{left % 60:00}";
            UpdateTerritory();

            int fronts = 0;
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
            }
            _frontsEmpty.style.display = fronts == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _frontsHint.style.display = fronts == 0 ? DisplayStyle.None : DisplayStyle.Flex;

            for (int k = 0; k < _cards.Length; k++) UpdateCard(k);
            if (_selected >= 0 && !GameCommands.OwnFormation(w, Human, _selected)) Deselect();

            _btnNewHq.text = $"Новый штаб {Gold(_rules.Formations.HqCost_c)} + {UnitLetter(_newType)} {Gold(_rules.Units[_newType].Cost_c)}";
            _btnNewHq.EnableInClassList("disabled-look", RecruitReason(-1, _newType) != RejectReason.None);
            _btnNewType.text = _newType == UnitTypes.Infantry ? "Пехота" : "Броня";
            _btnRear.EnableInClassList("disabled-look", RearReason() != RejectReason.None);
            _assignHint.text = _selected >= 0
                ? $"{_selected + 1}-е выделено: клик по фронту справа или по соседу на карте · Q наступать · E оборона · R в тыл · ПКМ или Esc — отмена"
                : "Клик по карточке выделяет соединение · Tab — следующее";

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

            int order = OrderOnFront(d);
            SetIcon(r.Order, order == 2 ? "attack" : order == 1 ? "defend" : order == 3 ? "time" : "frontOpen");

            int net = w.NetFlips10s(Human, d);
            r.Verdict.text = net > 0 ? $"берём ~{net} px" : net < 0 ? $"теряем ~{-net} px" : "стоим";
            r.Verdict.EnableInClassList("good", net > 0);
            r.Verdict.EnableInClassList("bad", net < 0);
            r.Verdict.EnableInClassList("dim", net == 0);
            string orderText = order == 2 ? "наступаем" : order == 1 ? "держим оборону" : order == 3 ? "соединение в пути" : "фронт открыт: наших соединений нет";
            r.Root.tooltip = $"{_names.Nominative(d)}: {orderText}\nФронт {len} px · равнина {w.FrontTerrainLength(Human, d, 0)}, холмы {w.FrontTerrainLength(Human, d, 1)}, горы {w.FrontTerrainLength(Human, d, 2)}\n"
                             + $"Наша атака A = {w.WeightedAttack(Human, d)}, их оборона D = {w.WeightedDefense(d, Human)}\n"
                             + $"Их атака {w.WeightedAttack(d, Human)}, наша оборона {w.WeightedDefense(Human, d)}\nСдвиг за 10 с: {(net >= 0 ? "+" : "−")}{Math.Abs(net)} px";
            r.Root.EnableInClassList("selected", _plateFront == d);
            r.Root.EnableInClassList("drop-target", _selected >= 0);
        }

        /// <summary>Наш приказ на фронте: 2 — наступаем, 1 — держим, 3 — только в пути, 0 — пусто.</summary>
        int OrderOnFront(byte d)
        {
            var w = W;
            bool attack = false, hold = false, transit = false;
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
            {
                var f = w.FormationAt(w.FormationSlot(Human, k));
                if (f.Alive == 0 || f.Front != d) continue;
                if (!w.OnFront(f)) { transit = true; continue; }
                if (f.Order == Orders.Attack) attack = true;
                else hold = true;
            }
            if (attack) return 2;
            if (hold) return 1;
            if (transit) return 3;
            return 0;
        }

        void UpdateCard(int k)
        {
            var w = W;
            var c = _cards[k];
            int slot = w.FormationSlot(Human, k);
            var f = w.FormationAt(slot);
            bool alive = f.Alive != 0 && Alive;
            c.Root.style.display = alive ? DisplayStyle.Flex : DisplayStyle.None;
            if (!alive) return;
            int inf = 0, armor = 0;
            for (int j = 0; j < w.MaxBattalions; j++)
            {
                var b = w.BattalionAt(slot * w.MaxBattalions + j);
                if (b.Alive == 0) continue;
                if (b.Type == UnitTypes.Armor) armor++;
                else inf++;
            }
            c.Inf.text = "×" + inf;
            c.Armor.text = "×" + armor;
            int avg = FormationSystem.AverageStrength(w, slot);
            c.Fill.style.width = Length.Percent(avg / 100f);
            c.Fill.EnableInClassList("low", avg < 3000);
            c.Fill.parent.tooltip = inf + armor == 0 ? "Пусто, ждёт набора" : $"Численность {avg / 100}%";
            if (f.Front == 0) c.Where.text = inf + armor == 0 ? "в тылу, пусто" : "в тылу";
            else if (!w.OnFront(f))
                c.Where.text = $"к {_names.Dative(f.Front)} · {(f.ArriveTick - w.Tick + _config.TicksPerSecond - 1) / _config.TicksPerSecond} с";
            else c.Where.text = "фронт: " + _names.Nominative(f.Front);
            c.Attack.EnableInClassList("active", f.Order == Orders.Attack);
            c.Defend.EnableInClassList("active", f.Order == Orders.Defend);
            c.AddInfText.text = "П " + Gold(_rules.Units[UnitTypes.Infantry].Cost_c);
            c.AddArmorText.text = "Б " + Gold(_rules.Units[UnitTypes.Armor].Cost_c);
            c.AddInf.EnableInClassList("disabled-look", RecruitReason(k, UnitTypes.Infantry) != RejectReason.None);
            c.AddArmor.EnableInClassList("disabled-look", RecruitReason(k, UnitTypes.Armor) != RejectReason.None);
            c.Root.EnableInClassList("selected", _selected == k);
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
            r.Order = IconEl("frontOpen", "front-order");
            top.Add(r.Order);
            r.Verdict = Cell(top, "front-verdict", "");
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

            r.Root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                OnFrontClicked(d);
                evt.StopPropagation();
            });
            _frontsList.Add(r.Root);
            return r;

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

        static Label Cell(VisualElement parent, string cls, string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            parent.Add(l);
            return l;
        }

        Card MakeCard(int k)
        {
            var c = new Card { Root = new VisualElement() };
            c.Root.AddToClassList("card");
            var head = new VisualElement { pickingMode = PickingMode.Ignore };
            head.AddToClassList("card-head");
            c.Title = Cell(head, "text-30", $"{k + 1}-е");
            var comp = new VisualElement { pickingMode = PickingMode.Ignore };
            comp.AddToClassList("card-comp");
            comp.Add(IconEl("army"));
            c.Inf = Cell(comp, "text-20", "×0");
            comp.Add(IconEl("armor"));
            c.Armor = Cell(comp, "text-20", "×0");
            head.Add(comp);
            c.Root.Add(head);
            var bg = new VisualElement();
            bg.AddToClassList("bar-bg");
            c.Fill = new VisualElement { pickingMode = PickingMode.Ignore };
            c.Fill.AddToClassList("bar-fill");
            bg.Add(c.Fill);
            c.Root.Add(bg);
            c.Where = Cell(c.Root, "card-where", "");
            var orders = new VisualElement();
            orders.AddToClassList("card-row");
            c.Attack = Btn(orders, "attack", null, "Наступать (Q)", out _);
            c.Defend = Btn(orders, "defend", null, "Обороняться (E)", out _);
            c.Root.Add(orders);
            var adds = new VisualElement();
            adds.AddToClassList("card-row");
            c.AddInf = Btn(adds, "budget", "П", null, out c.AddInfText);
            c.AddArmor = Btn(adds, "budget", "Б", null, out c.AddArmorText);
            c.Root.Add(adds);

            c.Attack.clicked += () => SetOrder(k, Orders.Attack);
            c.Defend.clicked += () => SetOrder(k, Orders.Defend);
            Bind(c.AddInf, () => RecruitReason(k, UnitTypes.Infantry), () => _rules.Units[UnitTypes.Infantry].Cost_c,
                () => _sim.EnqueueFromUi(CommandType.RecruitUnit, Human, k, UnitTypes.Infantry), "Набрать батальон пехоты: дёшево, держит холмы и горы");
            Bind(c.AddArmor, () => RecruitReason(k, UnitTypes.Armor), () => _rules.Units[UnitTypes.Armor].Cost_c,
                () => _sim.EnqueueFromUi(CommandType.RecruitUnit, Human, k, UnitTypes.Armor), "Набрать батальон брони: рвёт равнину, вязнет в горах");
            // Клик по телу карточки (не по кнопке) выделяет соединение; повторный — снимает.
            c.Root.RegisterCallback<ClickEvent>(evt =>
            {
                for (var t = evt.target as VisualElement; t != null && t != c.Root; t = t.parent)
                    if (t is Button) return;
                _selected = _selected == k ? -1 : k;
                Refresh();
            });
            _formationsList.Add(c.Root);
            return c;

            Button Btn(VisualElement parent, string icon, string text, string tooltip, out Label label)
            {
                var b = new Button();
                if (tooltip != null) b.tooltip = tooltip;
                b.AddToClassList("btn");
                b.Add(IconEl(icon));
                label = null;
                if (text != null)
                {
                    label = new Label(text) { pickingMode = PickingMode.Ignore };
                    label.AddToClassList("text-20");
                    b.Add(label);
                }
                parent.Add(b);
                return b;
            }
        }

        Marker MakeMarker()
        {
            var m = new Marker { Root = new VisualElement { pickingMode = PickingMode.Ignore } };
            m.Root.AddToClassList("hq-marker");
            m.Icon = IconEl("hq");
            m.Root.Add(m.Icon);
            m.Count = new Label("") { pickingMode = PickingMode.Ignore };
            m.Root.Add(m.Count);
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

        // ---------- действия ----------

        RejectReason RecruitReason(int k, byte type)
        {
            if (_sim == null || !Alive) return RejectReason.NationInvalid;
            return GameCommands.ValidateRecruit(W, Human, k, type);
        }

        RejectReason RearReason()
        {
            if (_sim == null || !Alive) return RejectReason.NationInvalid;
            if (_selected < 0) return RejectReason.FormationInvalid;
            var f = W.FormationAt(W.FormationSlot(Human, _selected));
            return f.Front == 0 ? RejectReason.AlreadyThere : RejectReason.None;
        }

        void SetOrder(int k, byte order)
        {
            if (_sim == null || W.IsOver || !Alive) return;
            _sim.EnqueueFromUi(CommandType.SetOrder, Human, k, order);
        }

        void OnFrontClicked(byte d)
        {
            if (_selected >= 0)
            {
                Assign(d);
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

        void Assign(byte d)
        {
            var r = GameCommands.ValidateAssign(W, Human, _selected, d);
            if (r != RejectReason.None)
            {
                ShowTip(ReasonText(r, 0));
                return;
            }
            _sim.EnqueueFromUi(CommandType.AssignFormation, Human, _selected, d);
            ShowTip($"{_selected + 1}-е идёт на фронт с {_names.Instrumental(d)}", 2.5f);
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
            int i = w.Index(x, y);
            int o = w.IsLand(i) ? w.Owner[i] : 0;
            if (o == 0 || o == Human)
            {
                if (_selected >= 0) { ShowTip("Назначить можно только на фронт с соседом: кликните по его земле"); return true; }
                if (_plateFront != 0) { HidePanels(); Refresh(); }
                return false;
            }
            if (_selected >= 0 && Alive)
            {
                Assign((byte)o);
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

        /// <summary>Клавиши партии: Tab — следующее соединение, Q / E — приказ, R — в тыл (для выделенного).</summary>
        public void OnKeys(UnityEngine.InputSystem.Keyboard kb)
        {
            if (_sim == null || kb == null || W.IsOver) return;
            if (_root.panel?.focusController?.focusedElement is TextField) return;
            if (kb.tabKey.wasPressedThisFrame)
            {
                int maxF = W.MaxFormationsPerNation;
                for (int step = 1; step <= maxF; step++)
                {
                    int k = ((_selected < 0 ? -1 : _selected) + step + maxF) % maxF;
                    if (!GameCommands.OwnFormation(W, Human, k)) continue;
                    _selected = k;
                    break;
                }
                Refresh();
            }
            if (_selected < 0) return;
            if (kb.qKey.wasPressedThisFrame) { SetOrder(_selected, Orders.Attack); Refresh(); }
            if (kb.eKey.wasPressedThisFrame) { SetOrder(_selected, Orders.Defend); Refresh(); }
            if (kb.rKey.wasPressedThisFrame)
            {
                var r = RearReason();
                if (r != RejectReason.None) ShowTip(ReasonText(r, 0));
                else
                {
                    _sim.EnqueueFromUi(CommandType.AssignFormation, Human, _selected, 0);
                    Deselect();
                    Refresh();
                }
            }
        }

        /// <summary>Только для скриншота бенча: открыть плашку первого фронта и выделить 1-е соединение.</summary>
        public Vector3? ShowFirstFrontForShot()
        {
            Vector3? at = null;
            for (int d = 1; d <= W.NationCount; d++)
                if (Contact(d))
                {
                    _plateFront = d;
                    ShowPlate((byte)d);
                    if (FrontPoint((byte)d, out var c)) at = _map.CellToWorld(c.x, c.y);
                    break;
                }
            _selected = GameCommands.OwnFormation(W, Human, 0) ? 0 : -1;
            Refresh();
            return at;
        }

        /// <summary>ПКМ или Esc: снять выделение, закрыть плашку и подсказку.</summary>
        public void Cancel()
        {
            Deselect();
            HidePanels();
            if (_sim != null) Refresh();
        }

        void Deselect()
        {
            _selected = -1;
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
        /// Плашка фронта (спека 02a §9.2): те же AttPow / DefPow ядра, что двигают границу, но словами.
        /// Таблицы по рельефу — в подсказке по наведению на строку «Они».
        /// </summary>
        void FillPlate(byte d)
        {
            var w = W;
            int len = Math.Max(1, w.FrontLength(Human, d));
            int ours = 0, theirs = 0;
            bool weAttack = false, theyAttack = false;
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
            {
                var f = w.FormationAt(w.FormationSlot(Human, k));
                if (w.OnFront(f) && f.Front == d) { ours++; weAttack |= f.Order == Orders.Attack; }
                var g = w.FormationAt(w.FormationSlot(d, k));
                if (w.OnFront(g) && g.Front == Human) { theirs++; theyAttack |= g.Order == Orders.Attack; }
            }
            _plateTitle.text = $"Фронт с {_names.Instrumental(d)}";
            long wa = w.WeightedAttack(Human, d), wd = w.WeightedDefense(Human, d);
            long ta = w.WeightedAttack(d, Human), td = w.WeightedDefense(d, Human);
            weAttack = wa > 0;
            theyAttack = ta > 0;
            SetIcon(_plateUsIcon, ours == 0 ? "frontOpen" : weAttack ? "attack" : "defend");
            _plateUs.text = ours == 0
                ? $"Мы: только гарнизон · оборона {wd / len}"
                : $"Мы: {Formations(ours)} · {(weAttack ? "наступление" : "оборона")} · {(weAttack ? wa : wd) / len} на ребро";
            SetIcon(_plateThemIcon, theyAttack ? "attack" : "defend");
            _plateThem.text = theirs == 0
                ? $"Они: только гарнизон · оборона {td / len}"
                : $"Они: {Formations(theirs)} · {(theyAttack ? "наступление" : "оборона")} · {(theyAttack ? ta : td) / len}";
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
            if (weAttack && (!theyAttack || wa * Math.Max(1, wd) >= ta * Math.Max(1, td)))
                verdict = wa >= td ? $"Перевес {Ratio(wa, td)} — {move}" : $"Сил мало: {Ratio(wa, td)} — {move}";
            else if (theyAttack)
                verdict = ta >= wd ? $"У них перевес {Ratio(ta, wd)} — {move}" : $"Держим: у них {Ratio(ta, wd)} — {move}";
            else verdict = ours == 0 ? $"Никто не наступает — {move}. Поставьте соединение" : $"Никто не наступает — {move}";
            _plateVerdict.text = verdict;
            _plateVerdict.EnableInClassList("good", net > 0);
            _plateVerdict.EnableInClassList("bad", net < 0);
        }

        static string Formations(int n) => n == 1 ? "1 соединение" : n < 5 ? $"{n} соединения" : $"{n} соединений";

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
            if (owner == null || now - _hoverSince < 0.3f) return;
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
        /// Раз в секунду: на каждом своём фронте значок штаба с числом соединений (стрелка — наступаем) в точке фронта,
        /// ближайшей к среднему его пограничных пикселей человека; открытый фронт — пустой круг. Позиции — каждый кадр;
        /// там же плашка фронта едет за своей точкой, сводка и подсказка гаснут.
        /// </summary>
        public void UpdateMarkers(float now)
        {
            if (_sim == null || _hq == null) return;
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
                var m = _hq[d];
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
                var m = _hq[d];
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
                int onPlace = 0;
                bool attack = false;
                for (int k = 0; k < w.MaxFormationsPerNation; k++)
                {
                    var f = w.FormationAt(w.FormationSlot(Human, k));
                    if (!w.OnFront(f) || f.Front != d) continue;
                    onPlace++;
                    attack |= f.Order == Orders.Attack;
                }
                SetIcon(m.Icon, onPlace == 0 ? "frontOpen" : attack ? "attack" : "hq");
                m.Count.text = onPlace == 0 ? "" : onPlace.ToString(CultureInfo.InvariantCulture);
                m.Root.EnableInClassList("attack", attack);
                m.Root.EnableInClassList("empty", onPlace == 0);
                m.Cell = new Vector2Int(best % width, best / width);
                m.Root.style.display = DisplayStyle.Flex;
            }
        }

        /// <summary>Точка фронта с d (та же, где значок штаба); пока значки не посчитаны — первый пограничный пиксель.</summary>
        bool FrontPoint(byte d, out Vector2Int cell)
        {
            cell = default;
            if (_hq != null && _hq[d].Cell.HasValue) { cell = _hq[d].Cell.Value; return true; }
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

        // ---------- сводка (спека 02a §9.2: те же пять событий 1b, новых нет) ----------

        public void CheckEvents()
        {
            if (_sim == null) return;
            var w = W;
            int n = w.NationCount, maxF = w.MaxFormationsPerNation;
            bool alive = Alive;
            for (int d = 1; d <= n; d++)
            {
                if (d == Human) continue;
                bool contact = Contact(d);
                if (alive && contact && !_prevContact[d])
                    Feed(FeedKind.Urgent, $"Новый фронт с {_names.Instrumental(d)} — он открыт", FrontCell((byte)d));
                _prevContact[d] = contact;
                bool attacked = alive && contact && w.WeightedAttack(d, Human) > 0;
                if (attacked && !_prevAttacked[d]) Feed(FeedKind.Urgent, $"{_names.Nominative(d)} наступает на нас", FrontCell((byte)d));
                _prevAttacked[d] = attacked;
                bool natAlive = w.Nation((byte)d).Alive != 0;
                if (_prevAlive[d] && !natAlive)
                {
                    int cap = _lastCapital[d];
                    Feed(FeedKind.Report, $"Пала столица {_names.Genitive(d)}", cap >= 0 ? new Vector2Int(cap % w.Width, cap / w.Width) : (Vector2Int?)null);
                }
                _prevAlive[d] = natAlive;
                if (natAlive) _lastCapital[d] = w.Nation((byte)d).Capital;
            }
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(Human, k);
                var f = w.FormationAt(slot);
                byte was = _prevFront[k];
                if (alive && f.Alive != 0 && was != 0 && f.Front == 0 && (w.FrontLength(Human, was) <= 0 || w.Nation(was).Alive == 0))
                    Feed(FeedKind.Report, $"Фронт с {_names.Instrumental(was)} закрылся, {k + 1}-е в тылу", null);
                _prevFront[k] = f.Front;
                bool bled = alive && f.Alive != 0 && FormationSystem.BattalionCount(w, slot) > 0 && FormationSystem.AverageStrength(w, slot) < 3000;
                if (bled && !_prevBled[k]) Feed(FeedKind.Loss, $"{k + 1}-е соединение обескровлено", f.Front != 0 ? FrontCell(f.Front) : null);
                _prevBled[k] = bled;
            }
        }

        Vector2Int? FrontCell(byte d) => FrontPoint(d, out var c) ? c : (Vector2Int?)null;

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
            while (_feedItems.Count > MaxFeed)
            {
                _feedItems[0].Root.RemoveFromHierarchy();
                _feedItems.RemoveAt(0);
            }
        }

        // ---------- тексты ----------

        static string Gold(long c) => (c / 100).ToString(CultureInfo.InvariantCulture);
        static string PerSec(long c) => (c >= 0 ? "+" : "−") + (Math.Abs(c) / 100.0).ToString("0.0", Ru);
        static string UnitLetter(byte t) => t == UnitTypes.Armor ? "Б" : "П";

        /// <summary>Тексты причин отказа (таблица раздела 10 спеки 01b), словарь 02a: бюджет вместо золота.</summary>
        string ReasonText(RejectReason r, int cost)
        {
            var w = W;
            switch (r)
            {
                case RejectReason.NotEnoughGold:
                {
                    long missing = cost - w.Nation(Human).Gold;
                    long net = EconomySystem.Income(w, Human) - FormationSystem.Upkeep(w, Human) - FormationSystem.ReinforceDemand(w, Human);
                    string when = net > 0 ? $"накопим через ~{(missing + net - 1) / net} с" : "доход не покрывает расходы на армию";
                    return $"Не хватает {(missing + 99) / 100} в бюджете, {when}";
                }
                case RejectReason.FormationFull: return $"В соединении уже {w.MaxBattalions} батальонов";
                case RejectReason.FormationLimit: return "Больше соединений не создать";
                case RejectReason.ArmyCap: return $"Армия на пределе: {_rules.Economy.BattalionCap} батальонов";
                case RejectReason.NoContact: return "С ними нет общей границы";
                case RejectReason.AlreadyThere: return _selected >= 0 && W.FormationAt(W.FormationSlot(Human, _selected)).Front == 0
                    ? "Соединение уже в тылу" : "Соединение уже стоит на этом фронте";
                case RejectReason.TargetInvalid: return "Фронт — только с живым соседом";
                case RejectReason.FormationInvalid: return "Сначала выделите соединение кликом по карточке";
                case RejectReason.NationInvalid: return "Наша столица пала";
                default: return r.ToString();
            }
        }
    }
}
