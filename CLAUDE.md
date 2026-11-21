# BattlePix

Civ-лайт в реальном времени за одну нацию на пиксельной карте. Партия 40–60 минут, 1970 → 2026. Unity 6000.6.10f1 (Unity 6.6), URP 2D. ПК (Steam) первым, мобилка портом.

Версия зафиксирована в `ProjectSettings/ProjectVersion.txt`. Это 6.6 (Supported), а не LTS: переход на 6.7 LTS — отдельной задачей, когда она выйдет.

## Пиллары — фича, которая не усиливает ни один, режется
1. Живой фронт: граница двигается в реальном времени, результат решения виден на карте.
2. Решение — выбор с ценой: 2–5 вариантов, у каждого своя плата, слайдеров нет.
3. Темповая партия до часа: 40–60 минут, всегда есть конец.

## Правила ядра (нарушение = переделка)
- `Assets/BattlePix/Core` — чистый C#. Нельзя: UnityEngine.*, MonoBehaviour, ScriptableObject, сцена, float/double/decimal, Mathf, System.Random, DateTime, Stopwatch. Можно: Unity.Collections, Unity.Jobs, Unity.Burst, Unity.Mathematics (целые типы). Тест `CoreHasNoForbiddenApis` проверяет это по тексту, включая комментарии.
- Дробные величины — целые с фиксированным масштабом (проценты в базисных пунктах, 10 000 = 100%). Деление округляет к нулю.
- Вся случайность — через `RngStreams`, у каждой системы свой поток. Номера потоков (`RngStream`) не менять, только дописывать.
- Мир меняется только командами из `CommandType`. Игрок и ИИ пользуются одним списком. Отдельный ИИ не пишем: ИИ — характер, который выбирает команды по весам из данных. Значения `CommandType` и `EndReason` только дописываются.
- Команды из UI ставятся на следующий тик (`Simulation.EnqueueFromUi`).
- Порядок обработки фиксирован, никаких итераций по Dictionary/HashSet там, где важен порядок. Порядок систем задаёт `SystemPipeline`.
- Горячие циклы — Burst-джобы (`[BurstCompile(CompileSynchronously = true)]`), пишут в непересекающиеся индексы. Фронт считается только по пограничным пикселям.
- Рендер только читает мир между тиками. Одна Texture2D владельцев + палитра в шейдере, никаких GameObject на пиксель.
- Нации, характеры, цели войны, рельеф, баланс — в `Assets/StreamingAssets/Data/*.json`, не в коде. Файлы данных хранятся с LF (`.gitattributes`), от их байтов зависит `dataHash`. Новый файл в `Data/` допиши в `GameData.KnownFiles` (нужно для Android).
- UI пока только под ПК: мышь и клавиатура, подсказки по наведению разрешены. Тач (всё по тапу, цели ≥ 44×44 pt) — при мобильном порте.

## Где что
- `Core/` — `Simulation` (тик; два конструктора: с правилами и старый для мира Фундамента), `World` (+ `FrontMath`), `CommandQueue`, `Pcg32`/`RngStreams`, `StateHash` (`HashJob` + `HashManaged`), `ReplayFile`/`ReplayPlayer` (формат 2), `SimConfig`, `Rules` (DTO `rules.json` + `ai.json` + число наций из `nations.json`), `MapGen/MapGenerator` (континент и столицы).
- Данные: `sim_config.json` (инфраструктура), `nations.json` (нации; ядро читает только id), `rules.json` (баланс), `ai.json` (профили ИИ).
- Мир Фундамента (старый конструктор `Simulation`, `DebugPaint`, `DebugSetOwner`) живёт только ради тестов этапа 0. В мире с правилами `DebugSetOwner` отклоняется (`DebugOnly`).
- `Core.Tests/` — EditMode NUnit, детекция утечек NativeArray включена с трассой.
- `Game/` — `GameBootstrap` (адаптер, `TickClock`, панель), `MapView` (квад + `OwnerPalette.shader`), `GameData`.
- `Editor/` — `HeadlessRunner`, `SceneBuilder` (сцена `Main` собирается кодом: меню BattlePix → Rebuild Main Scene), `BuildScript`.
- Спеки — `docs/specs/`, план — `docs/plan.md`.

## Как проверять
`Unity` ниже — `"C:\Program Files\Unity\Hub\Editor\6000.6.10f1\Editor\Unity.exe"`.
- Тесты: Test Runner → EditMode, или `Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode.xml`
- 100 бот-партий: `Unity -batchmode -nographics -projectPath . -executeMethod BattlePix.Editor.HeadlessRunner.Run -seeds 1-100 -out Reports/run` (`-ticks` — страховочный лимит, по умолчанию 30 000; конец партии задают правила)
- Баг = сид + реплей. Проверка: `... -executeMethod BattlePix.Editor.HeadlessRunner.Run -replay <файл>` (код 1 — расхождение, печатает первую разошедшуюся контрольную точку; код 2 — другие данные, `-ignoreDataHash` чтобы всё равно проиграть).
- Детерминизм: `... -executeMethod BattlePix.Editor.HeadlessRunner.Run -determinism 42`
- Синтетический реплей: `... -recordReplay Replays/x.json -seed 42 -commands 200 [-human 1]`. Реплей формата 1 (этап 0) отклоняется с кодом 2. Искусственный крах для проверки отчёта: `-crashSeed N`.
- Сцена: `Unity -batchmode -quit -projectPath . -executeMethod BattlePix.Editor.SceneBuilder.Build`
- ПК-сборка: `... -executeMethod BattlePix.Editor.BuildScript.BuildWindows` → `Builds/Windows/BattlePix.exe`. Замер: `BattlePix.exe -screen-fullscreen 0 -bench 15 -benchSpeed 4` пишет fps в Player.log и сохраняет реплей, который должен проходить `-replay`.
- Любое изменение ядра: тесты зелёные, `-determinism 42` проходит, бюджет тика в отчёте (≤ 0,25 мс) не вырос без причины. Цифры не подгонять.

## Как работаем
- Каждая механика: бриф → спека → реализация → 100 бот-партий → плейтест → вердикт.
- Делай ровно то, что в спеке. Новое — только с ответом «что это вытесняет», иначе в бэклог.
- Баланс меняется в данных и проверяется бот-прогоном, не на глаз.
- Спеки лежат в `docs/specs/`. Текущий этап: 1 «Прототип ядра», переделка под фронты и соединения — `docs/specs/spec-01b-fronts-formations.md` (дельта к `docs/specs/spec-01-core-prototype.md`; план — `docs/plan.md`, редакция 4).
