# Missfits – Plan

Missfits ist eine Sammlung eigenständiger Godot-Addons (C#). Jedes Addon funktioniert allein, sodass ein Projekt nur installiert, was es braucht. Zusammen eingesetzt können die Addons miteinander arbeiten, weil sie sich Actions, Conditions und Blackboards über einen gemeinsamen Core teilen.

## Addons

| Addon | Ordner | Aufgabe | Wortspiel |
|---|---|---|---|
| Core | `addons/misscore` | geteilte Basis, reine Bibliothek | – |
| Behavior Tree | `addons/missbehave` | existiert | misbehave |
| Statemachine | `addons/misstate` | geplant | misstate / State |
| Dialog | `addons/misspeak` | geplant | misspeak |
| Quests | `addons/mission` | geplant | miss-ion |

Alle Addons liegen in diesem einen Godot-Projekt (Monorepo). Demos liegen außerhalb von `addons/`.

## Abhängigkeitsregel

```
misscore  ←  missbehave
misscore  ←  misstate
misscore  ←  misspeak
misscore  ←  mission
```

Geschwister-Addons kennen sich nie gegenseitig, alle kennen nur den Core.

Der Grund: Die Addons haben kein eigenes `.csproj` und werden in die Assembly des jeweiligen Projekts kompiliert. Eine Referenz auf einen Typ aus einem nicht installierten Addon wäre ein Compile-Fehler im ganzen Projekt. Optionale Abhängigkeiten zwischen Addons gibt es deshalb nicht.

## Wie die Addons zusammenspielen

Actions und Conditions sind im Core definiert. Jedes Addon kann eigene mitliefern, und jedes ausführende Addon kann sie benutzen, ohne den Lieferanten zu kennen:

- `misspeak` liefert z. B. `StartDialogueAction` und `DialogueFinishedCondition`.
- `mission` liefert z. B. `QuestCompletedCondition` und `AdvanceQuestAction`.
- `missbehave` und `misstate` führen sie aus.

So lassen sich Dialoge und Quests aus Behavior Trees und Statemachines steuern, ohne dass diese Addons voneinander wissen.

## Was in den Core wandert

Heute ist alles BT-spezifisch:

- `ActionNode` und `ConditionNode` sind BT-Knoten (`ALeafNode`) und liefern `BehaviorStatus`.
- `BtContext` enthält neben `Actor`, `Blackboard` und `Delta` auch `BehaviorTreeInstance` und `BehaviorTreeRunner`.

Für die Wiederverwendung muss getrennt werden, *was* getan wird und *wie* es eingebettet ist:

- **Core:** Action und Condition als eigenständige Resources mit einem neutralen Kontext (`Actor`, `Blackboard`, `Delta`) und einem neutralen Ergebnis.
- **Addon:** dünne Wrapper, die eine Core-Action einbetten – im BT als Leaf, in der FSM als State-Verhalten bzw. Transition-Bedingung.

Offen ist, wie BT-spezifische Zugriffe (z. B. `Runner.Stop()` aus einer Action) über den neutralen Kontext erreichbar bleiben.

Weitere Kandidaten aus `addons/missbehave`:

| Heute | Anmerkung |
|---|---|
| `runtime/Blackboard.cs`, `runtime/blackboard/*` | Blackboard, `BbParam`, `BbTypes` |
| `runtime/debug/DebugStream.cs`, `FrameThrottle.cs` | Debug-Transport, sofern nicht BT-spezifisch |
| `editor/blackboard/*` | Blackboard-Panel und Parameter-Editor |
| `editor/ReloadSafe.cs` | Reload-sichere Referenzen |
| `editor/GraphNodeStyles.cs`, `NodeTypeRegistry.cs` | zu prüfen, wie viel davon generisch ist |

Der Core bekommt kein `plugin.cfg`. Als reine Bibliothek muss ihn niemand aktivieren; Editor-Widgets dürfen darin liegen, registriert werden sie vom jeweiligen Addon-Plugin.

## Namespaces

Jedes Addon behält seinen eigenen Namespace (`Missbehave`, `Misscore`, …). Der Assembly-Name `Missfits` gilt nur für dieses Entwicklungsprojekt und darf nirgends im Addon-Code vorausgesetzt werden.

## Veröffentlichung

Godot löst keine Abhängigkeiten zwischen Assets auf. Deshalb:

- **Core mitliefern:** Jedes Addon-Zip enthält `addons/misscore/` plus das eigene Addon. Ein zweites Addon überschreibt den Core mit derselben Version.
- **Lockstep-Versionierung:** Alle Addons werden mit derselben Versionsnummer veröffentlicht. Nutzer aktualisieren immer alle installierten Missfits-Addons zusammen.
- **Core-API nur additiv ändern:** Entfernen erst nach einer Version mit `[Obsolete]`. Ein veralteter Core zeigt sich sonst als Build-Fehler, nicht als Warnung.
- **Zips automatisch bauen:** Ein Skript bzw. CI erzeugt pro Addon ein Zip aus dem Monorepo.

Offen: Wie die Zips in den Store kommen (direkter Upload oder ein generiertes Distributions-Repo je Addon), hängt von den aktuellen Einreichungsregeln ab und ist noch nicht geprüft.

Verworfen:

- Core als Kopie in jedem Addon (eigener Namespace): zerstört die geteilten Actions und Conditions.
- Core als NuGet-Paket/DLL: verträgt sich schlecht mit `[GlobalClass]`-Resources und Script-Pfaden.

## Reihenfolge

1. `misscore` anlegen und Blackboard samt Editor-Teilen aus `missbehave` dorthin verschieben. Selbsttests von `missbehave` laufen weiter.
2. Action/Condition und Kontext im Core neutral definieren, `missbehave` auf Wrapper umstellen. Bestehende Trees und Nutzer-Actions müssen ladbar bleiben oder einen dokumentierten Migrationsweg bekommen.
3. `misstate` auf dem Core bauen. Erst hier zeigt sich, ob die Core-API wirklich neutral ist; Korrekturen am Core sind an dieser Stelle noch billig.
4. Build-Skript für die Addon-Zips, danach erste gemeinsame Veröffentlichung von `missbehave` und `misstate`.
5. `misspeak`, mit Actions und Conditions für BT und FSM.
6. `mission`.
