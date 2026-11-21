using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BattlePix.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace BattlePix.Game
{
    /// <summary>
    /// UI фронтов и соединений (спека 01b §10, ПК): HUD, панель «Фронты», панель «Соединения», плашка фронта,
    /// значки штабов на карте и тосты. Только читает мир между тиками и отдаёт команды человека через EnqueueFromUi.
    /// Назначение: клик по карточке выделяет соединение, потом клик по строке фронта, по соседу на карте или «В тыл».
    /// </summary>
    public sealed class GameHud
    {
        public const byte Human = 1;

        static readonly NumberFormatInfo Ru = new NumberFormatInfo { NumberDecimalSeparator = ",", NegativeSign = "−" };
        static readonly string[] TerrainNames = { "равнина", "холмы", "горы" };

        readonly Rules _rules;
        readonly SimConfig _config;
        readonly NationNames _names;
        readonly Color32[] _palette;
        readonly MapView _map;
        readonly Camera _camera;
        readonly Action<string> _toast;

        Simulation _sim;
        World W => _sim.World;

        /// <summary>Камера едет к точке мира (клик по строке «Фронты»).</summary>
        public Action<Vector3> FlyTo;

        // HUD
        readonly Label _hudGold, _hudIncome, _hudArmy, _hudLand, _hudTimer, _tip, _plate, _assignHint, _frontsEmpty;
        readonly VisualElement _frontsList, _markers;
        readonly ScrollView _formationsList;
        readonly Button _btnNewHq, _btnNewType, _btnRear;

        // Строки фронтов по id соседа и карточки по номеру соединения k.
        FrontRow[] _rows;
        Card[] _cards;
        Label[] _markerLabels;
        byte _newType = UnitTypes.Infantry;
        int _selected = -1;     // выделенное соединение k, −1 — нет
        int _plateFront;        // сосед, чья плашка открыта; 0 — закрыта
        float _nextMarkers;

        // Состояние прошлой проверки для тостов.
        bool[] _prevAlive, _prevContact, _prevAttacked, _prevBled;
        byte[] _prevFront;

        sealed class FrontRow
        {
            public VisualElement Root, Plain, Hills, Mountains;
            public Label Name, Length, Ratio, Order, Flips;
        }

        sealed class Card
        {
            public VisualElement Root, Fill;
            public Label Title, Composition, Where;
            public Button Attack, Defend, AddInf, AddArmor;
        }

        public GameHud(VisualElement root, Rules rules, SimConfig config, NationNames names, Color32[] palette, MapView map,
            Camera camera, Action<string> toast)
        {
            _rules = rules;
            _config = config;
            _names = names;
            _palette = palette;
            _map = map;
            _camera = camera;
            _toast = toast;
            _hudGold = root.Q<Label>("hud-gold");
            _hudIncome = root.Q<Label>("hud-income");
            _hudArmy = root.Q<Label>("hud-army");
            _hudLand = root.Q<Label>("hud-land");
            _hudTimer = root.Q<Label>("hud-timer");
            _tip = root.Q<Label>("tip");
            _plate = root.Q<Label>("pixel-info");
            _plate.AddToClassList("front-plate");
            _assignHint = root.Q<Label>("assign-hint");
            _frontsEmpty = root.Q<Label>("fronts-empty");
            _frontsList = root.Q<VisualElement>("fronts-list");
            _markers = root.Q<VisualElement>("markers");
            _formationsList = root.Q<ScrollView>("formations-list");
            _btnNewHq = root.Q<Button>("btn-new-hq");
            _btnNewType = root.Q<Button>("btn-new-type");
            _btnRear = root.Q<Button>("btn-rear");

            Bind(_btnNewHq, () => RecruitReason(-1, _newType), () => GameCommands.RecruitCost(W, -1, _newType),
                () => _sim.EnqueueFromUi(CommandType.RecruitUnit, Human, -1, _newType));
            _btnNewType.clicked += () =>
            {
                _newType = _newType == UnitTypes.Infantry ? UnitTypes.Armor : UnitTypes.Infantry;
                Refresh();
            };
            Bind(_btnRear, RearReason, () => 0, () =>
            {
                _sim.EnqueueFromUi(CommandType.AssignFormation, Human, _selected, 0);
                Deselect();
            });
        }

        // ---------- партия ----------

        public void BeginMatch(Simulation sim)
        {
            _sim = sim;
            int n = W.NationCount, maxF = W.MaxFormationsPerNation;
            _frontsList.Clear();
            _formationsList.Clear();
            _markers.Clear();
            _rows = new FrontRow[n + 1];
            for (int d = 1; d <= n; d++)
                if (d != Human) _rows[d] = MakeRow((byte)d);
            _cards = new Card[maxF];
            for (int k = 0; k < maxF; k++) _cards[k] = MakeCard(k);
            _markerLabels = new Label[n + 1];
            for (int d = 1; d <= n; d++)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.AddToClassList("marker");
                l.style.display = DisplayStyle.None;
                _markers.Add(l);
                _markerLabels[d] = l;
            }
            _selected = -1;
            _plateFront = 0;
            _prevAlive = new bool[n + 1];
            _prevContact = new bool[n + 1];
            _prevAttacked = new bool[n + 1];
            _prevBled = new bool[maxF];
            _prevFront = new byte[maxF];
            for (int d = 1; d <= n; d++)
            {
                _prevAlive[d] = W.Nation((byte)d).Alive != 0;
                _prevContact[d] = Contact(d);
            }
            for (int k = 0; k < maxF; k++) _prevFront[k] = W.FormationAt(W.FormationSlot(Human, k)).Front;
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
            _hudGold.text = "Золото " + Gold(me.Gold);
            _hudIncome.text = PerSec(net) + "/с";
            _hudArmy.text = $"{me.Battalions} бат. · {PerSec(-upkeep)}/с" + (reinforce > 0 ? $" · пополнение {PerSec(-reinforce)}/с" : "");
            _hudLand.text = string.Format(CultureInfo.InvariantCulture, "{0}% / {1}%",
                w.TotalLand > 0 ? (long)me.Pixels * 100 / w.TotalLand : 0, _rules.End.VictoryShare_bp / 100);
            int left = Math.Max(0, _rules.End.TimeoutTicks - w.Tick) / _config.TicksPerSecond;
            _hudTimer.text = $"{left / 60:00}:{left % 60:00}";

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

            for (int k = 0; k < _cards.Length; k++) UpdateCard(k);
            if (_selected >= 0 && !GameCommands.OwnFormation(w, Human, _selected)) Deselect();

            _btnNewHq.text = $"Новый штаб {Gold(_rules.Formations.HqCost_c)} + {UnitLetter(_newType)} {Gold(_rules.Units[_newType].Cost_c)}";
            _btnNewHq.EnableInClassList("disabled-look", RecruitReason(-1, _newType) != RejectReason.None);
            _btnNewType.text = _newType == UnitTypes.Infantry ? "П → Б" : "Б → П";
            _btnRear.EnableInClassList("disabled-look", RearReason() != RejectReason.None);
            _assignHint.text = _selected >= 0
                ? $"{_selected + 1}-е выделено: клик по фронту справа или по соседу на карте; ПКМ или Esc — отмена"
                : "Клик по карточке выделяет соединение";

            if (_plateFront != 0)
            {
                if (Contact(_plateFront)) _plate.text = PlateText((byte)_plateFront);
                else HidePanels();
            }
            _map.SetFocusNation(_plateFront);
        }

        void UpdateRow(byte d)
        {
            var w = W;
            var r = _rows[d];
            int len = w.FrontLength(Human, d);
            r.Length.text = $"{len} px";
            r.Plain.style.flexGrow = w.FrontTerrainLength(Human, d, 0);
            r.Hills.style.flexGrow = w.FrontTerrainLength(Human, d, 1);
            r.Mountains.style.flexGrow = w.FrontTerrainLength(Human, d, 2);
            long att = w.WeightedAttack(Human, d), def = w.WeightedDefense(d, Human);
            r.Ratio.text = att > 0 ? Ratio(att, def) : "— : 1";
            var (text, cls) = OrderOnFront(d);
            r.Order.text = text;
            r.Order.EnableInClassList("order-attack", cls == 2);
            r.Order.EnableInClassList("order-hold", cls == 1);
            r.Order.EnableInClassList("order-empty", cls == 0);
            int net = w.NetFlips10s(Human, d);
            r.Flips.text = $"{(net >= 0 ? "+" : "−")}{Math.Abs(net)} px/10 с";
            r.Root.EnableInClassList("selected", _plateFront == d);
            r.Root.EnableInClassList("drop-target", _selected >= 0);
        }

        /// <summary>Наш приказ на фронте: 2 — наступаем, 1 — держим (на месте или в пути), 0 — пусто.</summary>
        (string, int) OrderOnFront(byte d)
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
            if (attack) return ("▶ наступаем", 2);
            if (hold) return ("■ держим", 1);
            if (transit) return ("… в пути", 1);
            return ("○ пусто", 0);
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
            c.Composition.text = inf + armor == 0 ? "пусто, ждёт набора" : $"П {inf} · Б {armor}";
            int avg = FormationSystem.AverageStrength(w, slot);
            c.Fill.style.width = Length.Percent(avg / 100f);
            c.Fill.EnableInClassList("low", avg < 3000);
            if (f.Front == 0) c.Where.text = "в тылу";
            else if (!w.OnFront(f))
                c.Where.text = $"в пути к {_names.Dative(f.Front)}, {(f.ArriveTick - w.Tick + _config.TicksPerSecond - 1) / _config.TicksPerSecond} с";
            else c.Where.text = _names.Nominative(f.Front);
            c.Where.text += $" · {avg / 100}%";
            c.Attack.EnableInClassList("active", f.Order == Orders.Attack);
            c.Defend.EnableInClassList("active", f.Order == Orders.Defend);
            c.AddInf.text = "+ П " + Gold(_rules.Units[UnitTypes.Infantry].Cost_c);
            c.AddArmor.text = "+ Б " + Gold(_rules.Units[UnitTypes.Armor].Cost_c);
            c.AddInf.EnableInClassList("disabled-look", RecruitReason(k, UnitTypes.Infantry) != RejectReason.None);
            c.AddArmor.EnableInClassList("disabled-look", RecruitReason(k, UnitTypes.Armor) != RejectReason.None);
            c.Root.EnableInClassList("selected", _selected == k);
        }

        // ---------- построение элементов ----------

        FrontRow MakeRow(byte d)
        {
            var r = new FrontRow { Root = new VisualElement() };
            r.Root.AddToClassList("front-row");
            var sw = new VisualElement();
            sw.AddToClassList("swatch");
            sw.style.backgroundColor = (Color)_palette[d];
            r.Root.Add(sw);
            r.Name = Cell(r.Root, "front-name", _names.Nominative(d));
            r.Length = Cell(r.Root, "front-cell", "");
            var strip = new VisualElement();
            strip.AddToClassList("terrain-strip");
            r.Plain = Part(strip, "terrain-plain");
            r.Hills = Part(strip, "terrain-hills");
            r.Mountains = Part(strip, "terrain-mountains");
            r.Root.Add(strip);
            r.Ratio = Cell(r.Root, "front-cell", "");
            r.Order = Cell(r.Root, "order-icon", "");
            r.Flips = Cell(r.Root, "front-cell", "");
            r.Root.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                OnFrontClicked(d);
                evt.StopPropagation();
            });
            _frontsList.Add(r.Root);
            return r;

            static VisualElement Part(VisualElement parent, string cls)
            {
                var e = new VisualElement();
                e.AddToClassList(cls);
                e.style.flexGrow = 1;
                e.style.flexShrink = 1;
                e.style.flexBasis = 0;
                e.style.height = 10;
                parent.Add(e);
                return e;
            }
        }

        static Label Cell(VisualElement parent, string cls, string text)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            parent.Add(l);
            return l;
        }

        Card MakeCard(int k)
        {
            var c = new Card { Root = new VisualElement() };
            c.Root.AddToClassList("card");
            c.Title = Cell(c.Root, "card-title", $"{k + 1}-е");
            c.Composition = Cell(c.Root, "card-text", "");
            var bg = new VisualElement();
            bg.AddToClassList("bar-bg");
            c.Fill = new VisualElement();
            c.Fill.AddToClassList("bar-fill");
            bg.Add(c.Fill);
            c.Root.Add(bg);
            c.Where = Cell(c.Root, "card-text", "");
            var orders = new VisualElement();
            orders.AddToClassList("card-row");
            c.Attack = Btn(orders, "Наступление");
            c.Defend = Btn(orders, "Оборона");
            c.Root.Add(orders);
            var adds = new VisualElement();
            adds.AddToClassList("card-row");
            c.AddInf = Btn(adds, "+ П");
            c.AddArmor = Btn(adds, "+ Б");
            c.Root.Add(adds);

            c.Attack.clicked += () => SetOrder(k, Orders.Attack);
            c.Defend.clicked += () => SetOrder(k, Orders.Defend);
            Bind(c.AddInf, () => RecruitReason(k, UnitTypes.Infantry), () => _rules.Units[UnitTypes.Infantry].Cost_c,
                () => _sim.EnqueueFromUi(CommandType.RecruitUnit, Human, k, UnitTypes.Infantry));
            Bind(c.AddArmor, () => RecruitReason(k, UnitTypes.Armor), () => _rules.Units[UnitTypes.Armor].Cost_c,
                () => _sim.EnqueueFromUi(CommandType.RecruitUnit, Human, k, UnitTypes.Armor));
            // Клик по телу карточки (не по кнопке) выделяет соединение; повторный — снимает.
            c.Root.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target is Button) return;
                _selected = _selected == k ? -1 : k;
                Refresh();
            });
            _formationsList.Add(c.Root);
            return c;

            static Button Btn(VisualElement parent, string text)
            {
                var b = new Button { text = text };
                b.AddToClassList("btn");
                parent.Add(b);
                return b;
            }
        }

        /// <summary>
        /// Кнопка с причиной отказа: серая, если команда не пройдёт; причина показывается при наведении и по клику.
        /// </summary>
        void Bind(Button b, Func<RejectReason> reason, Func<int> cost, Action ok)
        {
            b.clicked += () =>
            {
                if (_sim == null || W.IsOver) return;
                var r = reason();
                if (r != RejectReason.None) { ShowTip(ReasonText(r, cost())); return; }
                ok();
            };
            b.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (_sim == null) return;
                var r = reason();
                if (r != RejectReason.None) ShowTip(ReasonText(r, cost()));
            });
            b.RegisterCallback<PointerLeaveEvent>(_ => _tip.AddToClassList("hidden"));
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
            Deselect();
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
                if (_selected >= 0) { ShowTip("Назначить можно только на фронт с соседом"); return true; }
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
            ShowTip($"{_names.Nominative(o)} · с нами нет общей границы");
            return true;
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
            _plate.text = PlateText(d);
            _plate.RemoveFromClassList("hidden");
        }

        void ShowTip(string text)
        {
            _tip.text = text;
            _tip.RemoveFromClassList("hidden");
        }

        /// <summary>Плашка фронта: те же AttPow/DefPow ядра, что двигают границу. Причины: перевес, рельеф, столица, окружение.</summary>
        string PlateText(byte d)
        {
            var w = W;
            int len = w.FrontLength(Human, d);
            var sb = new StringBuilder();
            int ours = 0, theirs = 0;
            bool weAttack = false, theyAttack = false;
            for (int k = 0; k < w.MaxFormationsPerNation; k++)
            {
                var f = w.FormationAt(w.FormationSlot(Human, k));
                if (w.OnFront(f) && f.Front == d) { ours++; weAttack |= f.Order == Orders.Attack; }
                var g = w.FormationAt(w.FormationSlot(d, k));
                if (w.OnFront(g) && g.Front == Human) { theirs++; theyAttack |= g.Order == Orders.Attack; }
            }
            sb.Append($"{_names.Nominative(d)} · фронт {len} px · {(w.WeightedAttack(Human, d) > 0 ? Ratio(w.WeightedAttack(Human, d), w.WeightedDefense(d, Human)) + " мы : они" : "мы не наступаем")}\n");
            sb.Append($"Мы: {Formations(ours)}, {(ours == 0 ? "только гарнизон" : weAttack ? "наступление" : "оборона")}");
            sb.Append(weAttack
                ? ", атака " + Triple(tr => w.AttPow(Human, d, tr)) + " на ребро"
                : ", оборона " + Triple(tr => w.DefPow(Human, d, tr)) + " на ребро");
            sb.Append($"\nОни: {Formations(theirs)}, {(theirs == 0 ? "только гарнизон" : theyAttack ? "наступление" : "оборона")}");
            sb.Append(", оборона " + Triple(tr => w.DefPow(d, Human, tr)));
            if (theyAttack) sb.Append(", атака " + Triple(tr => w.AttPow(d, Human, tr)));
            sb.Append("\nрельеф их земли " + Triple(tr => w.FrontTerrainLength(Human, d, tr) * 100 / Math.Max(1, len)) + "% (равнина / холмы / горы)");
            if (CapitalNearFront(d)) sb.Append($" · их столица близко ×{_rules.Front.CapitalMult_bp / 10000}");
            int net = w.NetFlips10s(Human, d);
            sb.Append($"\nза 10 с: {(net >= 0 ? "+" : "−")}{Math.Abs(net)} px · окружённые пиксели падают быстрее");
            return sb.ToString();
        }

        static string Formations(int n) => n == 0 ? "нет соединений" : n == 1 ? "1 соединение" : n < 5 ? $"{n} соединения" : $"{n} соединений";

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
                if (w.Owner[p] == d && (w.CapitalCover[p] & bit) != 0 && TouchesHuman(p)) return true;
            }
            return false;
        }

        bool TouchesHuman(int p)
        {
            var w = W;
            byte edge = w.EdgeMask[p];
            for (int k = 0; k < 4; k++)
                if (Core.Grid.Neighbour(p, edge, k, w.Width, out int q) && w.Owner[q] == Human && w.IsLand(q)) return true;
            return false;
        }

        // ---------- значки на карте ----------

        /// <summary>
        /// Раз в секунду: на каждом своём фронте значок штаба с числом соединений (стрелка — наступаем) в точке фронта,
        /// ближайшей к среднему его пограничных пикселей человека; открытый фронт — значок «пусто». Позиция — каждый кадр.
        /// </summary>
        public void UpdateMarkers(float now)
        {
            if (_sim == null || _markerLabels == null) return;
            var w = W;
            int n = w.NationCount;
            if (now >= _nextMarkers)
            {
                _nextMarkers = now + 1f;
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
                    var l = _markerLabels[d];
                    if (d == Human || count[d] == 0 || !Contact(d) || !Alive)
                    {
                        l.style.display = DisplayStyle.None;
                        l.userData = null;
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
                    l.text = onPlace == 0 ? "○" : attack ? $"{onPlace} ▶" : $"{onPlace} ■";
                    l.EnableInClassList("attack", attack);
                    l.EnableInClassList("empty", onPlace == 0);
                    l.userData = new Vector2Int(best % width, best / width);
                    l.style.display = DisplayStyle.Flex;
                }
            }
            var panel = _markers.panel;
            if (panel == null || _camera == null) return;
            for (int d = 1; d <= n; d++)
            {
                var l = _markerLabels[d];
                if (!(l.userData is Vector2Int px)) continue;
                var world = _map.CellToWorld(px.x, px.y);
                var pos = RuntimePanelUtils.CameraTransformWorldToPanel(panel, world, _camera);
                var origin = _markers.worldBound.position;
                l.style.left = pos.x - origin.x - 10;
                l.style.top = pos.y - origin.y - 10;
            }
        }

        /// <summary>Точка фронта с d (та же, где значок штаба); пока значки не посчитаны — первый пограничный пиксель.</summary>
        bool FrontPoint(byte d, out Vector2Int cell)
        {
            cell = default;
            if (_markerLabels != null && _markerLabels[d].userData is Vector2Int v) { cell = v; return true; }
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

        // ---------- тосты (спека 01b §10: пять, больше никаких) ----------

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
                if (alive && contact && !_prevContact[d]) _toast($"Новый фронт с {_names.Instrumental(d)} — он открыт");
                _prevContact[d] = contact;
                bool attacked = alive && contact && w.WeightedAttack(d, Human) > 0;
                if (attacked && !_prevAttacked[d]) _toast($"{_names.Nominative(d)} наступают на нас");
                _prevAttacked[d] = attacked;
                bool natAlive = w.Nation((byte)d).Alive != 0;
                if (_prevAlive[d] && !natAlive) _toast($"Пала столица {_names.Genitive(d)}");
                _prevAlive[d] = natAlive;
            }
            for (int k = 0; k < maxF; k++)
            {
                int slot = w.FormationSlot(Human, k);
                var f = w.FormationAt(slot);
                byte was = _prevFront[k];
                if (alive && f.Alive != 0 && was != 0 && f.Front == 0 && (w.FrontLength(Human, was) <= 0 || w.Nation(was).Alive == 0))
                    _toast($"Фронт с {_names.Instrumental(was)} закрылся, {k + 1}-е в тылу");
                _prevFront[k] = f.Front;
                bool bled = alive && f.Alive != 0 && FormationSystem.BattalionCount(w, slot) > 0 && FormationSystem.AverageStrength(w, slot) < 3000;
                if (bled && !_prevBled[k]) _toast($"{k + 1}-е соединение обескровлено");
                _prevBled[k] = bled;
            }
        }

        // ---------- тексты ----------

        static string Gold(long c) => (c / 100).ToString(CultureInfo.InvariantCulture);
        static string PerSec(long c) => (c >= 0 ? "+" : "") + (c / 100.0).ToString("0.0", Ru);
        static string UnitLetter(byte t) => t == UnitTypes.Armor ? "Б" : "П";

        /// <summary>Тексты причин отказа (таблица раздела 10 спеки 01b).</summary>
        string ReasonText(RejectReason r, int cost)
        {
            var w = W;
            switch (r)
            {
                case RejectReason.NotEnoughGold:
                {
                    long missing = cost - w.Nation(Human).Gold;
                    long net = EconomySystem.Income(w, Human) - FormationSystem.Upkeep(w, Human) - FormationSystem.ReinforceDemand(w, Human);
                    string when = net > 0 ? $"это ~{(missing + net - 1) / net} с" : "доход не покрывает расходы";
                    return $"Не хватает {(missing + 99) / 100} золота, {when}";
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
