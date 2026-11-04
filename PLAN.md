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
| Übersetzungen | `addons/misspell` | PO-Editor als Tabelle mit Plural-Zeilen existiert | misspell |
| Verdrahtung | `addons/misswire` | Runtime, Abgleich im Editor und Graph existieren, Praxistest fehlt | miswire |

Alle Addons liegen in diesem einen Godot-Projekt (Monorepo). Demos liegen außerhalb von `addons/`.

## Abhängigkeitsregel

```
misscore  ←  missbehave
misscore  ←  misstate
misscore  ←  misspeak
misscore  ←  mission
misscore  ←  misspell
misscore  ←  misswire
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
- **Graph (links nach rechts):** `MissReroute` als Datenklasse, dazu im Editor `MissGraphEdit` (eckige Drähte in Spuren, Reroutes samt Umdrehen, Greifbereiche der Ports, Doppelklick auf einen Draht, Live-Drähte), `RerouteBox`, `MissGraphBox` (Box ohne Titelleiste), `GraphRow` (Zeile mit Warnung, Auswahl und Live-Status) und `LiveWireOverlay`. `misstate` baut darauf auf; der Dialog-Editor von `misspeak` wird es auch. Der BT-Graph von `missbehave` läuft von oben nach unten und nutzt das nicht.

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
| `runtime/debug/DebugStream.cs` | Debug-Transport des BT; die gemeinsame Basis liegt inzwischen im Core, siehe unten |
| `editor/GraphNodeStyles.cs`, `NodeTypeRegistry.cs` | zu prüfen, wie viel davon generisch ist |

## Was misstate zeigt

- Ein State hält genau einen `MissNode` und führt ihn aus wie ein Tree seine Wurzel; Transitions prüfen `MissNode`s als Bedingung. Die FSM bringt keine eigenen Knotentypen mit, Actions und Conditions sind dieselben Klassen wie im BT.
- Mit installiertem `missbehave` kann ein State einen ganzen Teilbaum ausführen, ohne dass `misstate` davon weiß.
- Der Core musste dafür nur an einer Stelle wachsen: Der BT-Runner wurde in `MissRunner` (Core) und `BehaviorTreeRunner` geteilt, `FsmRunner` nutzt dieselbe Basis.

Was der FSM-Editor gezeigt hat:

- Jedes Addon bringt sein eigenes Blackboard-Panel und Inspector-Plugin mit, und alle sehen jeden `MissNode`. Damit ein Parameter trotzdem genau einen Editor bekommt, kennen sich die Panels über eine Node-Gruppe: Das Panel, dessen Quelle den Knoten enthält, beansprucht ihn (`BlackboardPanel.Claims`). Eine Gruppe statt einer statischen Liste, weil sie einen Assembly-Reload übersteht.
- Der Graph-Editor von `misstate` ist eigenständig geblieben. Aus `missbehave` musste dafür nichts weiter in den Core; geteilt werden Blackboard-Panel, Parameter-Editor und `ReloadSafe`.

Offen:

- **Debug-Kanal von `missbehave`:** `misstate` und `misspeak` teilen sich den Kanal aus dem Core (`RunnerDebugChannel` und `RunnerDebugStream` im Spiel, `RunnerDebugRouter` und `RunnerDebuggerPlugin` im Editor); ein Addon liefert nur noch seinen Präfix und was der Zustand eines Runners ist. `missbehave` hat weiter seinen eigenen Kanal nach demselben Muster, weil er pro Tick die Status aller Knoten schickt. Er ließe sich auf dieselbe Basis stellen.
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

- **Panel doppelt:** `DialogueEditorPanel` ist bis auf die Typen `FsmEditorPanel` (Toolbar, Speichern, Zurücksetzen, ungespeicherte Änderungen, Abgleich mit dem Inspector, Instanz-Auswahl). Graph und Debug-Kanal sind geteilt, das Panel noch nicht.
- **Entwirren:** Umgesetzt sind Ports (Reroute ohne ausgehenden Draht, im Core, also auch in `misstate`) und Optionen, die ausdrücklich zurückführen (`DialogueOption.Back`). Weitere Ideen, noch nicht gebaut: nur die Drähte der ausgewählten Box hervorheben; automatisch anordnen. Verworfen: ein zweites "sonst"-Ziel an der Option.
- **Text nur im Inspector:** Sprecher und Text einer Zeile lassen sich im Graph nicht direkt tippen.

## Was misspell zeigt

- Ein Werkzeug ohne Runtime-Knoten: ein eigener Hauptbildschirm im Editor, der `.po`/`.pot`-Dateien als Tabelle zeigt. Zeilen sind Texte, Spalten Sprachen; ein Template und die Sprachen daneben werden zusammen geöffnet (gleicher Ordner, gleiche Domain, `de.po` gehört zum einzigen `.pot` im Ordner).
- Plural steht im Mittelpunkt: jede Form ist eine eigene Zeile, so viele wie die Sprache mit den meisten Formen braucht. Eine leere Form zeigt ihren Namen und die Zahlen, für die sie gilt (`few · n = 2–4, 22–24, …`). Die `Plural-Forms`-Formel wird selbst ausgewertet, Regeln der üblichen Sprachen liegen bei (`PluralRules`).
- Das PO-Modell (`PoCatalog`, `PoEntry`, `PluralForms`) ist reines C# in `runtime/` und verlustfrei: Lesen und Schreiben ändert nur, was bearbeitet wurde. Ein Test prüft, dass Godot die geschriebenen Dateien mit denselben Formen liest.
- Vom Core nutzt es nur `ReloadSafe`. Weil das Modell kein GodotObject ist, geht es beim Assembly-Reload verloren; das Panel hält Pfad und Text ungespeicherter Dateien in Godot-Typen und baut den Stand danach wieder auf.
- Undo ist eigenes: jeder Schritt merkt sich die betroffenen Dateien als Kopie davor und danach.

Offen:

- **Unscharfe Übernahme** beim Abgleich mit dem Template (wie `msgmerge` ohne `--no-fuzzy-matching`) gibt es nicht; neue Texte kommen leer dazu.
- **Mehrere Zellen** markieren, kopieren und einfügen (z. B. aus Excel) geht noch nicht, nur einzelne Zellen.
- **Template erzeugen** bleibt bei Godot (*Template Generation*); ein Knopf dafür fehlt.

## Was misswire werden soll

Verdrahtung von Nodes per Klicken: Eine Quelle (Property oder Signal) löst eine Folge von Schritten aus (Methode aufrufen oder Property setzen), im Spiel und auf Wunsch schon im Editor. Kein Visual Scripting: keine Bedingungen, keine Schleifen, keine eigene Logik, nur Weitergabe.

Der Anlass sind C#-Spiele, deren Nodes kein `[Tool]` haben sollen, weil `[Tool]`-Code beim Assembly-Reload bricht. Die Regel dahinter:

- Nodes sind dumm: Sie emittieren Signale und bieten Methoden an, verbinden aber nichts selbst.
- Nur der Mediator hält Verbindungen, ausschließlich name-basiert (`new Callable(obj, MethodName.X)` mit `IsConnected`-Check). Zustand nur in Variant-Feldern.
- Ein Ziel, das im Editor etwas tun soll (z. B. Meshes setzen), braucht weiter `[Tool]`, bleibt aber zustandslos und ohne eigene Verbindungen.

Erster Anwendungsfall (Projekt Gladiator): `CharacterModel` (ohne `[Tool]`) mit `Outfit` und `OutfitChanged`, `CharacterOutfitController` (`[Tool]`) mit `Outfit` und `ApplyOutfit()`. Zwei Drähte: `CharacterModel.Outfit` → `Controller.Outfit` gibt die Referenz weiter, `Outfit.changed` → `ApplyOutfit()` wendet innere Änderungen an.

### Bausteine

| Teil | Aufgabe |
|---|---|
| `MissBindingGraph` (`MissResource`) | eingebettet in den Mediator, also Teil der `.tscn`; Liste der Bindungen und Positionen der Boxen |
| `MissBinding` | Quelle (NodePath, Property oder Signal, optional Pfad in eine ausgeklappte Resource), im Editor aktiv, Auslöser im Editor, Startwert übertragen, Schritte |
| `MissBindingStep` | Ziel (NodePath, Methode oder Property), Argumente (Wert der Quelle, Signal-Argumente, feste Werte) |
| `MissMediator` (Node, ohne `[Tool]`) | verdrahtet im Spiel |
| `MisswireEditorPlugin` | verdrahtet im Editor, Graph im unteren Panel |

Die Klassen tragen das Präfix `Miss`, weil `[GlobalClass]`-Namen im ganzen Projekt gelten und ein bloßes `Mediator` leicht mit Klassen des Spiels kollidiert.

### Im Spiel

- `_Ready` verbindet. Dann ist die ganze Szene im Baum, auch wenn spätere Geschwister noch nicht fertig sind.
- Der Startwert geht per `CallDeferred` raus, damit jedes Ziel fertig ist.
- `_ExitTree` trennt symmetrisch. Kommt der Mediator wieder in den Baum, verbindet `_EnterTree` neu, aber nur wenn `IsNodeReady()` gilt, weil `_Ready` dann nicht noch einmal läuft.
- Pro Quelle genau eine Verbindung, vom Signal zu einer Methode des Mediators. Der ruft die Schritte nacheinander auf und baut die Argumente selbst. So ist die Reihenfolge fest.
- C# kennt kein `Callable.Bind`, und eine per Name verbundene Methode muss genau die Argumente des Signals nehmen. Deshalb hängt jede Verbindung an einem `MissSignalRelay` (`RefCounted`, nur Godot-Felder), das einen Handler pro Argumentzahl hat (0 bis 6) und `receiver.method(binding, arguments)` aufruft. Das Plugin wird dieselben Relays benutzen.
- Ein Schritt ohne eigene Argumente gibt weiter, was die Bindung bekam, aber höchstens so viel, wie die Methode nimmt (wie `unbind`). So geht `Outfit` → `ApplyOutfit()` ohne Anpassung. Bekommt sie zu wenig, gibt es eine Warnung.
- Die Logik der Schritte liegt zustandslos in `MissWiring`, damit Mediator und Plugin dasselbe tun.
- Warnungen gehen per `push_warning` und zusätzlich als Signal `Warned` raus, jede nur einmal.
- Eine ausgeklappte Resource (z. B. `CharacterModel:Outfit`) wird neu verbunden, wenn die Property eine andere Resource bekommt.
- Fehlt ein Node, eine Methode oder ein Signal: einmal `push_warning`, die Bindung wird übersprungen.

### Im Editor

`MissMediator` hat kein `[Tool]` und ist im Editor nur ein Platzhalter; alles macht das Plugin. Es liest dieselben Bindungen.

- **Quelle mit `[Tool]`** (echte Instanz): Das Plugin hört auf ihr Signal, wie im Spiel.
- **Quelle ohne `[Tool]`** (Platzhalter, sendet nie): Fallback über die Undo-Historie des Editors, danach `Get()`. Dafür braucht es zwei Signale von `EditorUndoRedoManager`: `HistoryChanged` meldet jede neue Aktion (Inspector, Gizmos, Sub-Resources), `VersionChanged` nur Undo und Redo. Auslöser pro Bindung: *Wert geändert* (Vergleich mit dem letzten Wert) oder *jede Editor-Aktion* (für Resources ohne `[Tool]`, deren Inneres sich ändert, ohne dass sich die Referenz ändert).
- Eine Quelle läuft immer nur über einen der beiden Wege, sonst liefen die Schritte doppelt.
- **Nichts merken, immer abgleichen:** Bei `scene_changed`, nach jedem Reload (`ISerializationListener.OnAfterDeserialize`) und bei jeder Änderung der Historie prüft das Plugin pro Quelle, ob sie gerade Tool ist, und verbindet oder trennt entsprechend (`IsConnected`-Check). Damit sind Reload, Szenenwechsel und ein Wechsel zwischen Tool und Nicht-Tool derselbe Fall.
- Verbindungen im Editor ohne `ConnectFlags.Persist`, sonst landen sie in der `.tscn`.
- Das Plugin setzt Werte direkt, nicht über UndoRedo, also meldet die Historie danach nichts. Ketten (A → B → C) laufen deshalb über einen erneuten Vergleich, bis sich nichts mehr ändert, mit Obergrenze gegen Zyklen.
- Vergleichswerte in einem `Godot.Collections.Dictionary` (Instanz-ID des Mediators + Index der Bindung).
- Meldungen der Historie werden pro Frame zu einem deferred Abgleich zusammengefasst: Fünf Edits einer Property im selben Frame ergeben fünf `HistoryChanged`.
- Vor einem Aufruf prüft das Plugin, ob das Ziel ein Platzhalter ist. Ein `Call` auf einen Platzhalter tut nichts und schreibt einen Fehler ins Log.
- Ziel ohne `[Tool]` bei einer im Editor aktiven Bindung: Der Aufruf geht ins Leere, der Graph warnt.
- Was Ziele im Editor setzen (z. B. Meshes), wird mit der Szene gespeichert. Gewollt: Es ist abgeleitet, und Undo der Quelle stellt es über den nächsten Abgleich wieder her.
- Änderungen am Graph laufen über `EditorUndoRedoManager`; die Szene wird dadurch ungespeichert, einen eigenen Speichern-Knopf gibt es nicht.

### Graph

Auf `MissGraphEdit` (links nach rechts):

- Eine Box pro Node, links Properties und Signale als Ausgänge, rechts Methoden und Properties als Eingänge. Die Listen kommen aus `get_script_property_list`, `get_script_signal_list` und `get_script_method_list`; das geht auch bei Scripts ohne `[Tool]`. Die Kopfzeile zeigt, ob der Node im Editor läuft.
- Eine Resource-Property lässt sich ausklappen und wird eine eigene Quell-Box mit den Signalen der Resource, die gerade darin steckt.
- Mehrere Schritte stehen als Zeilen einer Sequenz-Box (wie die Action-Liste eines States), sortierbar per Drag. Ein einfacher Draht ist eine Sequenz mit einem Schritt.
- Nur Nodes der eigenen Szene, keine Pfade in instanzierte Teilszenen.
- Passt eine Signatur nicht, warnt die Zeile (`GraphRow`); verhindert wird nichts.

### Phasen

0. **Erledigt: Spike im echten Editor** (`tests/spike/`, `godot --headless -e --path . -- --misswire-spike`). Ergebnisse:
   - Platzhalter erkennen: `Script.IsTool()` ist `false`, und die C#-Instanz ist der native Typ (`Godot.Node`) statt der Script-Klasse. Ein Tool-Node ist die echte Klasse.
   - `Get()` auf Platzhaltern liefert gespeicherte Werte und Defaults aus dem Script (`Health` = 100, ohne Eintrag in der Szene). `Set()` speichert. Eine Node-Export liefert den `NodePath`, nicht den Node. Eine Tool-Resource darin ist die echte C#-Klasse, eine Resource ohne Tool nur `Godot.Resource`, aber `Get()` geht auch dort.
   - Script-Listen (Signale, Methoden, Properties) gehen bei Platzhaltern. `HasMethod`/`HasSignal` sind `true`.
   - `Call` auf einen Platzhalter tut nichts, gibt einen Fehlertext zurück und schreibt einen Fehler ins Log. `Connect` geht, `EmitSignal` von außen erreicht den Handler auch.
   - **`VersionChanged` feuert nicht bei neuen Aktionen**, auch nicht bei Edits im Inspector, nur bei Undo/Redo über den Editor (Strg+Z, einen Frame verzögert). Neue Aktionen melden sich über `HistoryChanged`, auch an Sub-Resources und an per `_Set` erzeugten Properties. Ein `Undo()` direkt auf dem `UndoRedo` einer Historie meldet nichts.
   - **`changed` sendet Godot nie von selbst**, weder bei Aktionen noch bei Edits im Inspector, auch nicht an einem normalen Export. Für den Draht `Outfit.changed` → `ApplyOutfit()` muss die Resource selbst `EmitChanged()` rufen.
   - Offen bleibt, was passiert, wenn ein Script zwischen Tool und Nicht-Tool wechselt. Das braucht einen Rebuild und bleibt der Handtest aus Phase 4.
1. **Erledigt: Runtime.** `MissBindingGraph`, `MissBinding`, `MissBindingStep`, `MissStepArgument`, `MissMediator`, `MissSignalRelay`, `MissWiring`. Selbsttest `tests/self_test.tscn` (30 Checks): Startwert erst nach dem Bereitmachen, Reihenfolge der Schritte, Signal-Argumente auswählen oder fest setzen, Resource-Signal folgt der Property, Verlassen und Wiederkommen ohne doppelte Verbindungen, Probleme einmal gemeldet, Speichern und Laden. Das Zip kompiliert allein mit dem Core.
2. **Erledigt: Abgleich im Editor.** `MissEditorSync` (gehalten vom `MisswireEditorPlugin`) hört auf `HistoryChanged`, `VersionChanged`, `scene_changed` und den Reload (`OnAfterDeserialize`), fasst pro Frame zu einem Abgleich zusammen und benutzt dieselben `MissSignalRelay` wie der Mediator, mit dem Mediator als Kontext. Bearbeitet wird vorerst im Inspector am Mediator. Entschieden beim Bau:
   - Ein Objekt läuft im Editor, wenn es kein Script hat oder sein Script `[Tool]` ist. Nur dann wird es gehört und gerufen; `Set` auf einen Platzhalter ist erlaubt und landet in der Szene.
   - Verglichen wird bei einer Property die Referenz (bei Objekten) bzw. der Wert, bei einem Resource-Signal ohne Tool der Inhalt der Resource (`var_to_str`).
   - Beim Öffnen einer Szene, beim Zurückwechseln auf sie und wenn der Graph neu ist, laufen Bindungen mit *Startwert übertragen* einmal.
   - Auch Mediatoren in instanzierten Teilszenen laufen mit.
   - Editor-Selbsttest `godot --headless -e --path . -- --misswire-editor-test` (30 Checks), unter anderem: Platzhalter-Edits werden verglichen und weitergegeben, gleicher Wert läuft nicht, fünf Edits in einem Frame sind ein Abgleich, Resource-Signal folgt der Property, Strg+Z wird weitergegeben, Tool-Quellen laufen genau einmal über ihr Signal, Ketten laufen im selben Abgleich, nichts davon wird in der Szene gespeichert, eine andere Szene vorn trennt alles.
   - Nicht automatisch prüfbar und offen für Phase 4: ein echter Reload mit offener Szene.
3. **Erledigt: Graph** im Dock „Misswire“ (unten), auf `MissGraphEdit`. Wie gebaut:
   - Eine Box pro Node (`WireNodeBox`): Properties und Signale als Ausgänge, Methoden und Properties als Eingänge. Sie zeigt, was das Script deklariert, dazu was benutzt wird; alles Weitere (auch Engine-Members) per Rechtsklick „Add a member...“. Die Kopfzeile sagt, ob der Node `[Tool]` hat.
   - Eine Box pro Bindung (`WireBindingBox`), immer, auch bei einem Schritt: Quelle links in die Kopfzeile, rechts ein Ausgang pro Schritt und „+ step“. Reihenfolge und Löschen per Rechtsklick, ebenso „im Editor aktiv“ und „Startwert“; alles andere im Inspector, der sich beim Klick auf die Bindung oder einen Schritt öffnet.
   - Draht von Property/Signal auf Methode/Property macht Bindung plus Schritt, Draht ins Leere eine Bindung ohne Schritte. Das Änderungs-Signal bleibt leer und wird bei jedem Verbinden über den Namen gesucht (`OutfitChanged`, `outfit_changed`), zur Laufzeit wie im Editor; ein später ins Script geschriebenes Signal greift so ohne Neuverdrahten. Ein eingetragenes Signal geht vor.
   - Drähte löschen: Ende am Eingang greifen und ins Leere ziehen, oder Rechtsklick auf den Draht „Delete wire“. Ein Schritt-Draht nimmt den Schritt mit, ein Quellen-Draht lässt die Bindung ohne Quelle (Warnung). Auf einen anderen Eingang gezogen, wird der Draht umgehängt. Property- und Resource-Bindungen sind sofort im Editor aktiv, Signal-Bindungen nicht.
   - Resource-Property per Rechtsklick „Show the signals of …“ als eigene Box mit `changed` und den Signalen ihres Scripts.
   - Nodes kommen per Drag aus dem Szenenbaum oder „Add selected nodes“; Nodes aus instanzierten Teilszenen werden abgelehnt.
   - Jede Änderung ersetzt den `Graph` des Mediators durch eine bearbeitete Kopie über die Undo-Historie: Szene wird ungespeichert, Strg+Z geht, gespeichert wird mit der Szene. Positionen liegen in `MissBinding.GraphPosition` und `MissBindingGraph.Boxes` (`MissGraphBoxInfo`).
   - Warnungen an Bindung und Schritt: fehlender Node, Member oder Signal, kein Änderungs-Signal, Aufruf auf Node ohne `[Tool]` bei aktiver Bindung, zu wenige Argumente.
   - Graph-Selbsttest `godot --headless -e --path . -- --misswire-graph-test` (36 Checks); mit Fenster und `--misswire-screenshot=<datei.png>` speichert er ein Bild des Docks.
   - **In Resources hinein:** Eine Resource-Box zeigt wie eine Node-Box Properties (lesen und setzen), Signale und Methoden ihres Scripts, dazu `changed`; „Open … as a box“ geht auch auf Resources in Resources. Gespeichert wird das in Godots Pfadschreibweise: eine Quelle `Property = "Outfit:Hair"`, ein Ziel `Target = "../Model:Outfit"`. Im Spiel hört eine solche Quelle auf das Änderungs-Signal des Nodes (neue Resource) und auf `changed` jeder Resource auf dem Weg (innere Änderung), im Editor wird sie verglichen. Methoden einer Resource laufen im Editor nur mit `[Tool]`, Setzen geht immer.
   - **Mehrere Boxen pro Node** („Another box of this“): eine weitere Box startet leer und bekommt Zeilen per „Add a member...“ oder durch Drähte. Welche Box ein Draht meint, steht in `MissBinding.SourceBox` und `MissBindingStep.TargetBox` (nur Editor); fehlt die Box, springt die Hauptbox ein.
   - **Gruppen** als `GraphFrame` (`MissGraphGroupInfo`: Titel, Farbe, Mitglieder): Boxen markieren, „Group the selected boxes“; Boxen auf den Rahmen ziehen nimmt sie auf, „Out of the group“ und „Ungroup“ per Rechtsklick; Titel und Farbe im Inspector nach Klick auf den Rahmen. Zuklappen gibt es nicht.
   - Neue Bindungen ohne Abwurfstelle suchen rechts neben ihrer Quelle den nächsten freien Platz.
   - Ein Node, der schon eine Box hat, bekommt per Drag & Drop aus dem Szenenbaum eine weitere (der Knopf „Add selected nodes“ legt nur fehlende an).
   - „Add a member...“ öffnet ein Fenster (`WireMemberDialog`): Suchfeld, darüber die Members nach der Klasse gruppiert, die sie deklariert, vom eigenen Script bis `Object`; nur die oberste Gruppe ist offen, beim Suchen alle; Mehrfachauswahl, Doppelklick nimmt einen; schon gezeigte stehen ausgegraut dabei.
   - **Ein Wert pro Schritt** (verworfen: eine Zeile pro Parameter; nahm zu viel Platz und brauchte für alles außer Text und Zahlen den Inspector). Angeboten werden nur Methoden, die höchstens einen Wert brauchen; ein Mediator ist keine Programmiersprache. Weitere Parameter mit Default zählen nicht mit und stehen mit ihrem Wert in der Zeile (`Greet(name: String, times: int = 1)`). Godot kennt die Defaults von C#-Methoden nicht und verlangt beim Aufruf per Name alle Argumente: `MissDefaults` liest sie per Reflection aus der C#-Klasse (über `ScriptPathAttribute` auch für Platzhalter) und füllt sie beim Aufruf auf.
     - Der Schritt nimmt den Wert der Bindung (`Note(← Health)`). Passt der Typ nicht oder liefert die Bindung nichts, warnt der Schritt; im Spiel und im Editor wird er dann übersprungen statt mit falschem Typ gerufen.
     - Rechtsklick „Value via port“ gibt der Schritt-Zeile links einen eigenen Eingang (`MissBindingStep.ValueFrom = Port`). Hinein geht eine Property irgendeiner Box, auch aus einer Resource (`ValueSource = "../Model:Outfit:Hair"`), oder eine **Konstanten-Box** (`MissGraphConstant` in `MissBindingGraph.Constants`, `ValueConstant`): Typ (Text, ganze Zahl, Zahl, Ja/Nein, Farbe) und Wert direkt in der Box, mehrfach nutzbar.
     - **Ein Wert-Draht liefert nur den Wert und löst nie etwas aus.** Gelesen wird, wenn die Bindung feuert. Sonst würde ein Schritt unberechenbar, sobald mehrere Felder den Ablauf anstoßen können, und ein Schritt, der so ein Feld ändert, könnte sich selbst wieder anstoßen.
     - `Arguments` (eins pro Parameter) gibt es nur noch für alte Szenen; die Laufzeit liest es, der Graph legt es nicht mehr an.
     - Verworfen: Bindungen verketten („nach A läuft B“). Schleifen, unklar wann B läuft, und es reicht trotzdem keine Daten weiter; der Weg zum Visual Scripting.
   - **„Follow … in the game“** (Rechtsklick auf eine Property-Bindung, `MissBinding.FollowChanges`): aus, bekommt das Spiel nur den ersten Wert; im Editor läuft die Bindung weiter bei jeder Änderung.
   - Property-Zeilen zeigen ihren Typ, blasser als der Name (`Text  String`, `Res  MedRes ▸`); bei Resources ohne `[GlobalClass]` den Script-Namen der Resource, die gerade darin steckt. Werte in Schritt-Zeilen sind fett (`MedUser.Res = **Res**`), Konstanten nicht. Dafür kann `GraphRow` (Core, additiv) seinen Text mit BBCode zeigen (`ShowRich`).
   - Konstanten können auch ein Node der Szene sein: Typ „Node“, Auswahl über Godots Node-Auswahl, gespeichert als Pfad vom Mediator aus; der Schritt bekommt den Node selbst.
   - Konstanten können auch eine Resource sein: Typ „Resource“ mit Godots `EditorResourcePicker` (neu anlegen, laden, aus dem Dateisystem ziehen; Klick öffnet sie im Inspector). `MissGraphConstant.HoldsResource` hält den Typ fest, solange noch keine gewählt ist; ohne Resource warnt der Schritt und wird übersprungen.
   - **Neue Resource in einer Property, signalgebunden.** Eine Resource-Box hat in der Kopfzeile den Eingang „replaced by“: Signale, die sagen, dass die Property jetzt eine andere Resource hält. Bindungen auf diese Resource (Resource-Signal, Property darin) trennen sich dann von der alten und verbinden sich mit der neuen. misswire findet sie selbst (gestrichelt): per Namenskonvention, ein eingetragenes Change Signal, und die Trigger von Property-Bindungen auf genau diese Property (bei `Outfit` also z. B. `UpdateOutfit` der Bindung, die das Outfit weiterreicht). Von Hand verbundene (durchgezogen) liegen in `MissBindingGraph.ReplaceWatches`. Gestrichelte Drähte gehen von der nächstgelegenen Box des Nodes aus, die das Signal zeigt (auch bei Triggern); von Hand verbundene von der Hauptbox. Findet sich keins, warnt die Box. Kein Polling. Unabhängig davon löst ein `changed` einer Resource, die nicht mehr in der Property steckt, nie etwas aus; die Bindung hängt dann sofort um.
   - Strg+D dupliziert die markierten Boxen (Node-Box als weitere Box mit denselben Zeilen, Bindung samt Schritten, Konstante). Strg+A öffnet für die eine markierte Box „Add a member...“.
   - Entf auf einem markierten Schritt löscht den Schritt (ein Klick auf eine Zeile markiert ihre Box allein). Jede weitere Box eines Nodes zeigt wie die erste alle Members; Rechtsklick „Hide unused members“ blendet aus, was kein Draht nutzt (auch von Hand hinzugefügte), „Show all members“ holt es zurück.
   - **Trigger-Eingang an Property-Bindungen (gebaut; das automatische Signal ist ein gestrichelter Draht, gezeichnet von `WireDashOverlay`, und steht zusätzlich als „(auto)“ in der Trigger-Zeile).** Die Kopfzeile bekommt einen zweiten Eingang für Signale, bei denen die Bindung die Property neu liest und ihre Schritte ausführt (`MissBinding.Triggers`, Pfade wie `../CharacterModel:UpdateOutfit`; Argumente der Signale werden ignoriert). Das per Konvention gefundene Änderungs-Signal ist ein gestrichelter Draht (implizit, abschaltbar über „Follow … in the game“), weitere Signale auch anderer Nodes durchgezogene Drähte. GraphEdit kann keine einzelnen Drähte stricheln: den impliziten selbst zeichnen, über das Overlay der Live-Drähte. Mit mindestens einem Trigger entfällt die Warnung „no change signal“. Signal-Bindungen bekommen keinen Trigger-Eingang, dort sind die Argumente der Wert.
   - Offen: Reroutes, automatisch anordnen.
4. **Gladiator:** nur das Addon ins Projekt bringen (wie die anderen Missfits-Addons, samt `misscore`) und das Plugin aktivieren. Verdrahten macht der Nutzer selbst, dabei auch die Handtests: Szene offen lassen und mehrfach bauen (keine doppelten Aufrufe, keine Unload-Fehler); `[Tool]` an einer Quelle setzen und wieder entfernen (feuert genau einmal, danach greift der Fallback).

Später, nicht jetzt: Live-Ansicht im Spiel über den Debug-Kanal des Core. Core-Actions als Schritt würden misswire mit BT und FSM verbinden, ziehen das Addon aber Richtung Visual Scripting.

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
7. **Umgesetzt:** `misspeak`, mit Actions und Conditions für BT und FSM.
8. **Umgesetzt:** `misspell`, PO-Editor mit Plural-Unterstützung.
9. `mission`.
10. `misswire`, Phasen siehe oben.
