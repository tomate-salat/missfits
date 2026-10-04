# Missbehave

Behavior trees for Godot 4.7 / C#, authored in a visual graph editor.

A tree is a single `.tres` resource, edited entirely in the **Missbehave** dock — create
nodes, wire them, reorder them, set their parameters, undo, save. The same panel colours the graph
live while the game runs, so you build a tree and press F5 without switching views.

---

## Enabling it

Missbehave needs the `addons/misscore` folder next to it — the shared base of the Missfits addons,
which holds the blackboard and the node classes your own actions and conditions derive from. It is
a plain library: nothing to enable, it only has to be there.

The addon compiles into the project's own assembly, so **build before enabling**:

```bash
dotnet build
```

Then *Project → Project Settings → Plugins → Missbehave → Enable*. If the very first enable reports
a script error, restart the editor once and tick it again — a known rough edge with fresh C# editor
plugins. Every later assembly rebuild reloads the plugin on its own.

## Quick start

1. *FileSystem → right-click a folder → New Resource → **BehaviorTree*** and save it.
2. Double-click the `.tres`. The Missbehave panel opens with a single **Root** box.
3. Right-click the canvas (or drag a wire into empty space) to open the node picker.
4. Drag from a node's bottom port to another node's top port to make it a child.
5. Select a node — its parameters appear in the Inspector.
6. Add a `BehaviorTreeRunner` node under your actor and assign the tree resource; fill in any
   blackboard values the tree needs in the runner's Inspector.

`addons/missbehave/demo/demo.tscn` is a working example: two enemies **sharing one tree resource**,
one close enough to chase the player, one idling out of range.

## Docking

The editor is a regular Godot dock. It opens at the bottom, but its dock menu lets you float it
into a separate window — handy on a second monitor while the game runs — or move it to a side
slot. Godot remembers the placement with the rest of the editor layout.

## Reading the graph

A box is one line: type icon, name, and its `#` position among its siblings, plus a small second
line when the node has something to summarise. The category shows in the box's shape and in the
colour of its icon:

| Category  | Shape             | Icon     |
|-----------|-------------------|----------|
| Composite | slightly rounded  | blue     |
| Decorator | chamfered corners | pink     |
| Action    | square            | teal     |
| Condition | round             | lavender |

The icon also tells the kind apart — `→` sequence, `?` selector, `⇉` parallel and so on. The boxes
themselves stay neutral: their colour is kept for live debugging, so it always means a tick status.

## Writing your own leaves

Subclass `ActionNode` or `ConditionNode`. Exported properties are edited in the Inspector and are
carried into every runtime instance for free.

```csharp
using Godot;
using Misscore;

[GlobalClass, Tool]
public partial class FollowTarget : ActionNode {
    [Export] public float Speed { get; set; } = 4f;

    protected override MissStatus Run(MissContext ctx) {
        if (ctx.Actor is not Node3D actor) return MissStatus.Failure;
        // ctx.Delta, ctx.Actor and the node's blackboard parameters are all you need.
        return MissStatus.Running;
    }
}
```

`ActionNode`, `ConditionNode`, `MissStatus` and `MissContext` live in the `Misscore` namespace, not
in `Missbehave`: an action or condition is not tied to behavior trees, and the other Missfits addons
run the very same classes.

`[Tool]` is required because the editor only runs tool scripts: without it the node turns into a
placeholder the next time its tree is loaded, and the tree is refused with a message naming the class.
`[GlobalClass]` is required too — without it Godot cannot write the node into a tree resource, and the
node picker flags the class in yellow. Rebuild and the picker lists it; no restart needed.

Two optional attributes tidy up the picker once a project has many nodes:

```csharp
[GlobalClass, Tool]
[NodeGroup("Enemies")]        // listed under Action/Enemies; nest with "Enemies/Ranged"
[NodeName("Look at player")]  // shown instead of the class name, in the picker and on the box
public partial class LookAtPlayer : ActionNode { ... }
```

The picker's filter matches the shown name, the class name and the group names. A node's own
`DisplayName` still takes precedence over `[NodeName]` on its box.

## Blackboard

A tree declares the values it works with on its **blackboard**, shown beside the graph (toggle it
from the toolbar). Each entry has a name, a type and a default, all editable right there.

Nodes use entries through `BbParam<T>`:

```csharp
using Misscore;

public partial class FollowTarget : ActionNode {
    BbParam<float> Speed { get; set; } = 4f;
    BbParam<Node3D> Target { get; set; }

    protected override MissStatus Run(MissContext ctx) {
        Target.Value.GlobalPosition += Vector3.Forward * Speed.Value * (float) ctx.Delta;
        return MissStatus.Running;
    }
}
```

No `[Export]` — Godot cannot export generic types, so the addon finds these members by type and
saves them itself. No `new()` either: a parameter without initializer is created by the node, and a
plain value (`= 4f`) becomes its fixed starting value. (For `BbParam<Variant>`, write
`= new Variant(3)`: C# will not chain two conversions.) In the Inspector every parameter gets a small menu: keep a **fixed value**, link
an **entry of a matching type**, or create a **new entry** from the parameter (its type and current
value become the entry's type and default).

`Value` is the parameter's value as of the start of the node's tick: it is read just before
`BeforeRun` and again before every tick, so a node that keeps running still sees what others wrote
in between. Assigning `Value` writes straight to the linked entry; unlinked, the runner keeps the
value for itself. `Get(ctx)` / `Set(ctx, value)` do the same on demand — only needed when something
earlier in the same tick may have just changed the entry. Reading costs about a dictionary lookup
per parameter per tick (roughly 100 ns in a debug build). Mark a parameter `[BbEntryOnly]` when a fixed value makes
no sense — node types always are, since a scene node cannot live in a tree resource.

**Entries are linked by id, not by name**, so renaming one never breaks a node: every box shows the
new name at once. A link to a removed entry, or to one whose type no longer fits, shows up as a ⚠ on
the box.

**Per runner.** The `BehaviorTreeRunner` Inspector lists the entries under *Blackboard*, so each
runner can override a default — and fill in scene nodes such as a navigation agent. The revert arrow
goes back to the tree's default. These values are stored by entry id as well.

**From code outside the tree**, look entries up by name:

```csharp
runner.Blackboard.Set("PlayerDistance", distance);
```

A name the tree does not declare is kept as an ad-hoc value, which nodes can still read by name via
`ctx.Blackboard`.

## Controlling the runner

`ctx.Runner` is whatever drives the tick, as an `IMissRunner` — here the `BehaviorTreeRunner`; use
`ctx.GetRunner<BehaviorTreeRunner>()` for what only that class offers. `ctx.Runner?.Stop()` switches the tree
off from inside a leaf: the current tick still finishes, then whatever is running is interrupted.
`Enabled = true` resumes it.

Useful overrides: `BeforeRun` / `AfterRun` (bracket a run), `Interrupt` (a higher-priority branch
took over — stop what you started), `GetSummary` (second line on the graph box),
`GetConfigurationWarnings` (⚠ badge).

## Node reference

**Composites** — `Selector`, `SelectorReactive`, `SelectorRandom`, `Sequence`, `SequenceReactive`,
`SequenceRandom`, `SequenceStar`, `SimpleParallel`.
The plain variants resume the child that was left running; the *reactive* variants re-check every
child from the front each tick and interrupt a lower-priority branch when a higher one becomes
viable. `SequenceStar` remembers its progress across failures. `SimpleParallel` runs a primary child
that decides the result alongside a background child.

**Decorators** — `Inverter`, `Failer`, `Succeeder`, `UntilFail`, `Repeater`, `Limiter`,
`TimeLimiter`, `Delayer`, `Cooldown`.

**Leaves** — your own `ActionNode` / `ConditionNode` subclasses, plus `BlackboardSet` (write a fixed
value or another entry into an entry), `BlackboardErase`, `BlackboardHas` (is the entry holding a
value) and `BlackboardCompare` (both sides fixed or linked).

**Lists** — `ConditionListNode` and `ActionListNode` fold several conditions or actions into one
box. The entries are drawn as rows inside it and run top to bottom, as a *Sequence* or a *Selector*
(`Mode`). In the node picker they head the *Condition* and *Action* groups with an icon of their own.
The box has the shape of what it holds and shows the list icon followed by the icon of the mode,
over a thin rule; summaries stay off the rows and appear on hover. While the game runs, every row is coloured with its own status.
Underneath, the entries are ordinary children, so they save, clone and link to the blackboard like
any other node.

- *Replace with…* on a selector or sequence folds its conditions (or actions) into the list, keeping
  their order and the mode; anything else stays on the canvas. Replacing a list with a composite
  unfolds the entries into boxes again.
- Drag a wire from the list's bottom port onto a condition to append it, or into empty space to
  create a new entry.
- Click a row to inspect that entry. Right-click it to move it up or down, take it out of the list,
  open its script or delete it; *Delete* removes the picked entry rather than the list.

Time-based decorators accumulate `ctx.Delta` rather than reading the engine clock, so they behave
identically on the idle and physics threads and can be driven with an artificial delta in tests.

## Definitions and instances

A tree resource is a **definition**. It is shared by every runner that references it and is never
ticked. Each `BehaviorTreeRunner` builds its own runtime clone at `_Ready`, and all mutable tick
state — the running child index, cooldown timers, your own fields — lives on that clone.

Cloning is shallow per node: exported scalars are copied, exported *resource* references stay
shared. A leaf holding `[Export] WeaponStats Stats` keeps pointing at the one asset, exactly as you
would expect; only the behavior nodes themselves are duplicated.

Ticking a definition by mistake is caught and reported rather than silently corrupting shared state.

## Live debugging

Open a tree, run the game, and the graph colours itself: green Success, red Failure, amber Running.
Nodes that were not reached this tick fade out, the wires the tick went through take the colour of
the node they lead to, and dots flow along the wires into running nodes, so the path the tree
actually took stands out. Only the tree open in the panel streams
anything, and one batched status array is sent per tick rather than a message per node. When several
actors run the same tree, pick which one to watch from the toolbar dropdown; if the watched actor is
freed, the next one running the tree takes over. A paused game keeps showing its last tick, and
switching the watched actor while paused shows that actor's last tick straight away.

## Layout and ordering

The tree grows top to bottom, the way behavior trees are usually drawn: a node's parent connects
into the port on top of its box, its children hang off the port at the bottom.

**Child order is the left-to-right order of the boxes.** Drag a child left of its sibling and it becomes
`#1`; the badge on each box shows the resulting index. *Arrange* lays the tree out automatically
(Reingold-Tilford) without ever changing that order.

**Right-clicking a box** selects it and opens a menu: *Replace with…*, *Open script* (the node's C#
file, in whatever external editor Godot is configured to use) and *Delete*.

**Replacing a node:** right-click a box → *Replace with…* and pick another type — a `Sequence`
becomes a `SequenceReactive` without rewiring anything. The new node takes over the old one's place,
children, position, name and every exported property both types share (e.g. `WaitTime` from
`Cooldown` to `Delayer`). Children beyond what the new type accepts — a composite turned into a
decorator — are kept as orphans.

Disconnecting a node does not delete it — it stays on the canvas as an orphan and is still saved, so
unplugging a branch to try something else never loses work. Deleting a node keeps its children as
orphans for the same reason. Everything, including moves and reparenting, is one undo step.

**Saving.** *Save* (or Ctrl+S in the panel) writes the open tree; the dock title carries a `*` while it
has unsaved edits. Opening another tree saves nothing — the edits stay in memory until you save,
and undoing one of them opens its tree again. Unsaved trees are saved when the editor saves its
scenes or runs the project, and quitting the editor asks about them. *Revert* discards the unsaved
edits of the open tree and goes back to its file.

## Tests

The tests are not part of the release zip; they come with the
[repository](https://github.com/tomate-salat/missfits). Both suites run headless and exit 0 when
everything passes.

```bash
godot --headless --path . res://addons/missbehave/tests/self_test.tscn
```

Runtime semantics: every composite and decorator, interrupt delivery, resource round-trip, and the
invariant that two runners sharing one tree resource never share tick state.

```bash
godot --headless --path . res://addons/missbehave/tests/editor_self_test.tscn
```

The graph editor, driven through the same signals GraphEdit emits when you click: creating,
connecting, reparenting, moving, deleting. Worth running after any change to
`BehaviorTreeGraphEdit` — the failures that matter there are lifetime bugs (rebuilding the graph
while GraphEdit is mid-signal, or freeing a box without removing it first), and they take the whole
editor down rather than printing anything.

## Notes

Missbehave is independent of the game it currently lives in — it references no project code and the
folder can be copied into another project as-is.

Author: Tomate_Salat
