# Misstate

State machines for Godot 4.7 / C#, part of Missfits.

A machine is a single `.tres` resource. Each state runs a list of actions — the same `ActionNode`
classes a Missbehave behavior tree runs — and moves on through transitions that check the same
`ConditionNode` classes. Write an action once, use it in both.

A machine is edited in the **Misstate** dock: states as boxes, transitions as wires.

---

## Enabling it

Misstate needs the `addons/misscore` folder next to it — the shared base of the Missfits addons,
which is a plain library with nothing to enable. Misstate itself compiles into the project's own
assembly, so **build before enabling**:

```bash
dotnet build
```

Then *Project → Project Settings → Plugins → Misstate → Enable*.

## Quick start

1. *FileSystem → right-click a folder → New Resource → **Fsm*** and save it.
2. Double-click the `.tres`. The Misstate dock opens.
3. *Add state* in the toolbar, or right-click the canvas.
4. Select a state to set its *Name* in the Inspector, and press *+ action* in its box to pick what
   it does.
5. Drag from the port beside *new transition* onto another state. Right-click the new row to add
   conditions; click a row to edit what it shows in the Inspector.
6. Add an `FsmRunner` node under your actor and assign the machine.

## How a machine works

- **`Fsm`** — the machine: its states, which of them is the initial one, and a blackboard.
- **`FsmState`** — a name, the actions it runs, and its transitions.
- **`FsmTransition`** — where to go, on which trigger, under which conditions.
- **`FsmRunner`** — the scene node that drives a machine for one actor.

A state's actions are started when the state is entered, ticked while they are running, and
interrupted if the state is left before they are through. Once they have finished — with Success or
with Failure — the state is through: nothing runs again until the machine enters it anew. A state
without actions just waits for a transition.

After each tick of the state, its transitions are considered top to bottom, and the first one that
fires wins. A transition fires when its trigger is met **and** its condition holds:

| Trigger | Met when |
|---|---|
| `Always` | on every tick |
| `Finished` | the state's actions have finished |
| `Succeeded` | they finished with Success |
| `Failed` | they finished with Failure |

A finished state stays finished, so `Finished`, `Succeeded` and `Failed` are met on every tick
from then on: a transition that also has conditions fires as soon as those hold. In a state that
repeats they are met only on the tick a run ended.

A condition is any node — usually a `ConditionNode` — and holds when it returns Success. Without
conditions, the trigger alone decides.

The state arrived at starts on the next tick, so one tick never takes more than one transition.

## Writing actions and conditions

Subclass `ActionNode` or `ConditionNode` from `Misscore` — the very classes a Missbehave behavior
tree uses, so one written for either works in both:

```csharp
using Godot;
using Misscore;

[GlobalClass, Tool]
public partial class OpenDoor : ActionNode {
    protected override MissStatus Run(MissContext ctx) {
        ctx.GetActor<Node3D>()?.Call("open");
        return MissStatus.Success;
    }
}
```

Both attributes are needed. Without `[GlobalClass]` Godot cannot write the node into the machine's
file. Without `[Tool]` the editor cannot work with it: it only runs tool scripts, so the node turns
into a placeholder the next time the file is loaded. The picker does not offer such a class, and a
machine that already uses one is refused with a message naming it.

## What a state runs

A state holds a list of actions, worked through like an action list of a behavior tree. Two
settings on the state decide how:

- **Mode** — as a *Sequence* (the default) a run fails with the first action that fails and
  succeeds once all have succeeded. As a *Selector* it succeeds with the first action that succeeds
  and fails once all have failed.
- **Parallel** — off (the default), the actions run one after the other, each waiting for the one
  before it. On, all of them are ticked on every tick, and the action that decides the run
  interrupts the others.
- **Repeat** — off (the default), the actions run once per visit: when the run has ended with
  Success or Failure the state is through and just waits for a transition, until the machine enters
  it again. On, a finished run starts over on the next tick for as long as the machine stays.

A transition's conditions work the same way: by its *Mode*, all of them have to hold, or one is
enough.

## The editor

- **States** are boxes. The one marked ▶ is where the machine starts; right-click a state to make
  it the initial one, or to delete it.
- **Actions** are the upper rows of a state. *+ action* opens the node picker — the same one
  Missbehave uses, with filter, groups and icons — showing only the project's `ActionNode` classes.
  Right-click a row to move it up or down, or to delete it. They run top to bottom.
- **Transitions** are the lower rows of the state they leave, each with a port on the right and a
  wire to its target. They are considered top to bottom, so the order of the rows matters:
  right-click a row to move it, to delete it, or to add a condition — which opens the picker
  again, showing only `ConditionNode` classes.
- **Conditions** are rows of their own, set in below their transition: *if* the first, *and* or
  *or* the others, by the transition's mode. Right-click one to move or delete it.
- **Wires.** Drag from the last, faint port to add a transition; drag from a transition's own port
  to lead it somewhere else. Dropping a wire on empty canvas makes a new state there.
- **Reroutes** are small pills that lead a wire around the boxes. Double-click a wire to put one
  into it — or right-click the wire and choose *Add reroute to this wire*; right-click the canvas
  for a loose one. A reroute faces what it leads to: if that lies to its left, it turns round, so
  wires arrive at its right end and leave from the left. The chevron shows which way. Any
  number of wires may end at a reroute; the one wire that leaves it says where they all go, so
  dragging that wire elsewhere takes every transition through it along. Drag a reroute by its
  middle. Deleting one leaves the wires whole. A reroute does nothing at runtime: a transition
  through it goes straight to the state at the end.
- **Selecting** a state or a row shows exactly that in the Inspector: a state's name, mode and
  parallel switch, an action's parameters, a transition's trigger and mode, a condition's
  parameters. Nothing has to be unfolded there to get at it.
- **Blackboard.** The machine's entries sit beside the graph. The parameters of actions and
  conditions link to them in the Inspector, exactly as in a behavior tree.
- **⚠** on a state or a row names what is wrong with it: a transition that leads nowhere, a link to
  an entry that no longer exists.

Deleting a state keeps the transitions that led to it, flagged as leading nowhere, so their
conditions are not lost. Every edit in the graph is one undo step. *Save* writes the machine; the
dock title carries a `*` while it has unsaved edits, and *Revert* goes back to the file.

## Live debugging

Open a machine, run the game, and the graph shows where the machine is: the state it is in gets an
amber outline, the others fade, and the actions of the current state take the colour of what they
last returned — green Success, red Failure, amber Running. An action that is through keeps its
colour until the state's next run starts.

The wires tell the rest. The transition the machine came in by is green for as long as it stays in
the state — so you see where it came from. The transitions out of the current state are amber with
dots travelling along them: they are being checked on every tick, and none has fired yet. Both are
followed through their reroutes. A state entered as the initial one, or by `GoTo`, has no green wire.

Only the machine open in the dock sends anything, and only when something changed. When several
actors run the same machine, pick which one to watch from the toolbar dropdown; if the watched actor
is freed, the next one running the machine takes over. With nothing open, the dock opens the machine
the game is running. Stopping the game puts the graph back to how it looks while editing.

The editor brings its *Output* panel forward when a game starts — click the *Misstate* tab to watch.
## Building one in code

```csharp
using Misscore;
using Misstate;

var patrol = new FsmState { Name = "Patrol" };
patrol.Actions.Add(new WalkWaypoints());

var chase = new FsmState { Name = "Chase", Parallel = true };
chase.Actions.Add(new ChasePlayer());
chase.Actions.Add(new PlayAlarm());

var spotted = new FsmTransition { TargetStateId = chase.Id };
spotted.Conditions.Add(new PlayerNear());
patrol.Transitions.Add(spotted);
chase.Transitions.Add(new FsmTransition { TargetStateId = patrol.Id, On = FsmTrigger.Failed });

var machine = new Fsm();
machine.States.Add(patrol);
machine.States.Add(chase);
```

Transitions refer to their target by id, so renaming a state never breaks one.

## Running it

Add an `FsmRunner` under your actor and assign the machine. Like a behavior tree runner it ticks on
the physics step by default, can skip steps (`TickRate`), and lists the machine's blackboard entries
in the Inspector so each runner can fill in its own values.

```csharp
runner.StateChanged += (from, to) => GD.Print($"{from} -> {to}");
runner.GoTo("Chase");     // force a state by name
runner.Restart();         // back to the initial state
runner.Stop();            // interrupts the current node, keeps the state
runner.Enabled = true;    // carries on where it was
```

The same machine resource can drive any number of runners: each works on its own copy of the nodes.

Inside a node, `ctx.Runner?.Stop()` stops whatever is running it — a machine here, a tree elsewhere.
`ctx.GetRunner<FsmRunner>()?.GoTo("Flee")` is for what only a state machine can do.

## With Missbehave installed

An action of a state can be any node, so with Missbehave in the project it can be a `Sequence`, a
`Selector` or a whole subtree. Such a node is not in the action picker; add it to the state's
*Actions* in the Inspector.

## Tests

The tests are not part of the release zip; they come with the
[repository](https://github.com/tomate-salat/missfits). Both suites run headless and exit 0 when
everything passes.

```bash
godot --headless --path . res://addons/misstate/tests/self_test.tscn
godot --headless --path . res://addons/misstate/tests/editor_self_test.tscn
```
