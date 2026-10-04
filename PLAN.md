# Missfits – Plan

Missfits ist eine Sammlung eigenständiger Godot-Addons (C#). Jedes Addon funktioniert allein, sodass ein Projekt nur installiert, was es braucht. Zusammen eingesetzt können die Addons miteinander arbeiten, weil sie sich Actions, Conditions und Blackboards über einen gemeinsamen Core teilen.

## Addons

| Addon | Ordner | Aufgabe | Wortspiel |
|---|---|---|---|
| Core | `addons/misscore` | geteilte Basis, reine Bibliothek | – |
| Behavior Tree | `addons/missbehave` | existiert | misbehave |
| Statemachine | `addons/misstate` | Runtime, Graph-Editor und Live-Ansicht existieren | misstate / State |
| Dialog | `addons/misspeak` | Runtime, i18n und Beispiel-UI existieren, Graph-Editor fehlt | misspeak |
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

## Was im Core liegt

- **Blackboard:** `Blackboard`, `BlackboardEntry`, `BbParam<T>`, `BbParams`, `BbTypes`, dazu `BbParamResource` als Basisklasse für jede Resource mit Parametern (Speichern, Inspector, Revert, Reload-Sicherung).
- **Runner:** `MissRunner` als Basis der Runner-Nodes (Actor, Tick-Thread und -Rate, Stoppen, Blackboard-Overrides im Inspector) und `IMissRunner` als das, was ein Knoten vom Runner sieht.
- **Knoten:** `MissNode` als Basis von allem, was getickt wird, `ActionNode` und `ConditionNode` als Basis eigener Logik, `MissContext`, `MissStatus`, die vier Blackboard-Leaves sowie `[NodeName]` und `[NodeGroup]`.
- **Editor:** `BlackboardPanel`, `BbParamEditorProperty`, `ReloadSafe`. Das Panel arbeitet auf `IBlackboardSource` und `IBbParamHost`.

**Entscheidung:** Eine Action ist direkt ein Core-Knoten, es gibt keinen Wrapper und nur eine Art, Actions zu schreiben. `missbehave` enthält nur noch, was wirklich Behavior Tree ist: Composites, Decorators, Listen, Tree, Runner, Debugger und Editor. Der Preis war ein Breaking Change gegenüber missbehave 0.1.0:

| Vorher (`Missbehave`) | Jetzt (`Misscore`) |
|---|---|
| `ABehaviorNode` | `MissNode` |
| `BehaviorStatus` | `MissStatus` |
| `BtContext` | `MissContext` |
| `ctx.Runner` (`BehaviorTreeRunner`) | `ctx.Runner` (`IMissRunner`: `Stop()`, `Enabled`), sonst `ctx.GetRunner<BehaviorTreeRunner>()` |
| `ActionNode`, `ConditionNode`, `BbParam<T>` | gleiche Namen, anderer Namespace |

Gespeicherte Trees laden weiter, weil die Skripte ihre UIDs behalten haben.

Der Core kennt damit bewusst mehr als Daten: `Running`, `BeforeRun`/`AfterRun`/`Interrupt`, Kinder und eine Graph-Position gehören zum Knoten.

Noch im BT-Addon, mögliche Kandidaten für später:

| Heute | Anmerkung |
|---|---|
| `runtime/debug/DebugStream.cs`, `FrameThrottle.cs` | Debug-Transport, sofern nicht BT-spezifisch |
| `editor/GraphNodeStyles.cs`, `NodeTypeRegistry.cs` | zu prüfen, wie viel davon generisch ist |

## Was misstate zeigt

- Ein State hält genau einen `MissNode` und führt ihn aus wie ein Tree seine Wurzel; Transitions prüfen `MissNode`s als Bedingung. Die FSM bringt keine eigenen Knotentypen mit, Actions und Conditions sind dieselben Klassen wie im BT.
- Mit installiertem `missbehave` kann ein State einen ganzen Teilbaum ausführen, ohne dass `misstate` davon weiß.
- Der Core musste dafür nur an einer Stelle wachsen: Der BT-Runner wurde in `MissRunner` (Core) und `BehaviorTreeRunner` geteilt, `FsmRunner` nutzt dieselbe Basis.

Was der FSM-Editor gezeigt hat:

- Jedes Addon bringt sein eigenes Blackboard-Panel und Inspector-Plugin mit, und alle sehen jeden `MissNode`. Damit ein Parameter trotzdem genau einen Editor bekommt, kennen sich die Panels über eine Node-Gruppe: Das Panel, dessen Quelle den Knoten enthält, beansprucht ihn (`BlackboardPanel.Claims`). Eine Gruppe statt einer statischen Liste, weil sie einen Assembly-Reload übersteht.
- Der Graph-Editor von `misstate` ist eigenständig geblieben. Aus `missbehave` musste dafür nichts weiter in den Core; geteilt werden Blackboard-Panel, Parameter-Editor und `ReloadSafe`.

Offen:

- **Debug-Kanal doppelt:** `misstate` hat einen eigenen Debug-Kanal nach dem Muster von `missbehave` (Stream im Spiel, Router und Debugger-Plugin im Editor). Geteilt wird nur `FrameThrottle`. Die beiden Kanäle sind sich sehr ähnlich; bei einem dritten Addon lohnt es, den gemeinsamen Teil in den Core zu ziehen.
- **Teilbäume:** Eine Action eines States kann ein BT-Teilbaum sein, zusammenbauen lässt er sich aber nur im Inspector, nicht im Graph.

Entscheidungen aus dem ersten Ausprobieren im Editor:

- **Ein State ist eine Action-Liste**, kein einzelner Knoten: `Actions` mit `Mode` (Sequence oder Selector) und `Parallel`. Actions kommen über einen Picker im Graph dazu, nicht über die Knoten-Auswahl im Inspector. Transitions halten entsprechend eine Liste von `Conditions`.
- **Ein Plugin beansprucht nur die Knoten seiner eigenen offenen Resource.** Vorher holte Missbehave sein Dock nach vorn, sobald irgendein `MissNode` inspiziert wurde, auch einer in einer Statemachine.
- **Alle Missfits-Resources erben von `MissResource`**, damit sie im Dialog *New Resource* unter einem Eintrag stehen.

## Was misspeak zeigt

- Ein Dialog besteht aus Abschnitten (`DialogueSection`), im Graph je eine Box: mehrere Zeilen, die nacheinander gesprochen werden, und am Ende die Optionen als Wege weiter. Drähte braucht es nur, wo verzweigt oder gesprungen wird. Das ist die Idee der Listen aus dem BT, auf einen Graphen angewandt, der wie eine FSM an einer Stelle steht und wartet.
- Eine Zeile hat Sprecher, Text, Actions (laufen vor dem Text) und Conditions (sonst wird sie übersprungen). Eine Option mit Text ist eine Auswahl für den Spieler, eine ohne ist der Weg, den der Dialog von selbst nimmt. Ein Abschnitt, in dem nichts gesagt wird, ist eine Verzweigung. Eigene Knotentypen gibt es nicht.
- Der Runner zeichnet nichts. Er meldet per Signal, was zu zeigen ist, und wird über `Advance()` und `Choose()` weitergeschaltet. Eine Beispiel-Dialogbox liegt als eine Szene mit einem Skript bei (`ui/dialogue_box.tscn`).
- i18n über Godots eigenes System: Texte sind die Übersetzungsschlüssel, ein Parser-Plugin liefert sie an die POT-Erzeugung, und bei einem Sprachwechsel meldet der Runner die aktuelle Zeile neu. Der Sprecher als Übersetzungskontext ist abschaltbar und standardmäßig aus, weil CSV-Übersetzungen keinen Kontext kennen.
- `StartDialogueAction` und `DialogueActiveCondition` sind die Brücke zu BT und FSM. Sie finden den `DialogueRunner` beim Actor oder über eine Node-Gruppe in der Szene; der Core musste dafür nicht wachsen.

Offen:

- **Graph-Editor:** Das Modell ist dem von `misstate` so ähnlich (Box mit Zeilen, Ports pro Option, Reroutes), dass sich vor dem Bau lohnt zu prüfen, was vom FSM-Graph in den Core gehört.
- **Reroutes:** `FsmReroute` liegt in `misstate`; `misspeak` hat noch keine.

Der Core bekommt kein `plugin.cfg`. Als reine Bibliothek muss ihn niemand aktivieren; Editor-Widgets dürfen darin liegen, registriert werden sie vom jeweiligen Addon-Plugin.

## Namespaces

Jedes Addon behält seinen eigenen Namespace (`Missbehave`, `Misscore`, …). Der Assembly-Name `Missfits` gilt nur für dieses Entwicklungsprojekt und darf nirgends im Addon-Code vorausgesetzt werden.

## Veröffentlichung

Godot löst keine Abhängigkeiten zwischen Assets auf. Deshalb:

- **Core mitliefern:** Jedes Addon-Zip enthält `addons/misscore/` plus das eigene Addon. Ein zweites Addon überschreibt den Core mit derselben Version.
- **Lockstep-Versionierung:** Alle Addons werden mit derselben Versionsnummer veröffentlicht. Nutzer aktualisieren immer alle installierten Missfits-Addons zusammen.
- **Core-API nur additiv ändern:** Entfernen erst nach einer Version mit `[Obsolete]`. Ein veralteter Core zeigt sich sonst als Build-Fehler, nicht als Warnung.
- **Zips automatisch bauen:** `tools/build-zips.ps1` erzeugt pro Addon ein Zip in `dist/` (Addon plus Core, entpackt direkt nach `addons/`). Das Skript verlangt eine gemeinsame Versionsnummer in allen `plugin.cfg` (`-Version 0.2.0` setzt sie) und kompiliert jedes Zip in einem leeren Projekt, als Editor- und als Export-Build. Damit fällt auf, wenn ein Addon ein Geschwister-Addon braucht oder Runtime-Code Editor-Code benutzt. Die `tests/`-Ordner bleiben draußen (`-IncludeTests` nimmt sie mit).
- **GitHub Actions:** `.github/workflows/release.yml` lässt das Skript bei jedem Push auf master laufen. Ein Tag wie `v0.2.0` veröffentlicht die Zips zusätzlich als GitHub-Release; der Tag muss zur Version in den `plugin.cfg` passen.

Offen: Wie die Zips in den Store kommen (direkter Upload oder ein generiertes Distributions-Repo je Addon), hängt von den aktuellen Einreichungsregeln ab und ist noch nicht geprüft.

Verworfen:

- Core als Kopie in jedem Addon (eigener Namespace): zerstört die geteilten Actions und Conditions.
- Core als NuGet-Paket/DLL: verträgt sich schlecht mit `[GlobalClass]`-Resources und Script-Pfaden.

## Reihenfolge

1. **Erledigt:** `misscore` anlegen und Blackboard samt Editor-Teilen aus `missbehave` dorthin verschieben. Selbsttests von `missbehave` laufen weiter.
2. **Umgesetzt, Editor-Prüfung offen:** Knoten-Basis, Actions, Conditions, Kontext und Status in den Core ziehen; `missbehave` behält nur, was Behavior Tree ist.
3. **Runtime umgesetzt:** `misstate` auf dem Core bauen: `Fsm`, `FsmState`, `FsmTransition`, `FsmRunner` mit Selbsttests.
4. **Umgesetzt:** Graph-Editor für `misstate`. Ein Editor-Teil kam dafür in den Core: die Abgrenzung der Blackboard-Panels.
5. **Umgesetzt:** Live-Ansicht für `misstate`: aktueller State und Status seiner Actions im Graph, während das Spiel läuft.
6. **Skript umgesetzt, Veröffentlichung offen:** Build-Skript für die Addon-Zips, danach erste gemeinsame Veröffentlichung von `missbehave` und `misstate`.
7. **Runtime, i18n und Beispiel-UI umgesetzt, Graph-Editor offen:** `misspeak`, mit Actions und Conditions für BT und FSM.
8. `mission`.
