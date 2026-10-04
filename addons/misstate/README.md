# Misstate

State machines for Godot 4.7 / C#, part of Missfits.

A machine is a single `.tres` resource. Each state runs one node — the same `ActionNode` classes a
Missbehave behavior tree runs — and moves on through transitions that check the same
`ConditionNode` classes. Write an action once, use it in both.

There is no graph editor yet: a machine is put together in the Inspector, or in code.

---

## Installing it

Misstate needs the `addons/misscore` folder next to it — the shared base of the Missfits addons. It
is a plain library, and so far Misstate is too: there is no plugin to enable. Build the project and
the classes are there.

```bash
dotnet build
```

## How a machine works

- **`Fsm`** — the machine: its states, which of them is the initial one, and a blackboard.
- **`FsmState`** — a name, the node it runs, and its transitions.
- **`FsmTransition`** — where to go, on which trigger, under which condition.
- **`FsmRunner`** — the scene node that drives a machine for one actor.

A state's node runs the way a behavior tree's root does: it is started when the state is entered,
ticked while the state lasts, and interrupted if the state is left while it is still running. When
it finishes and no transition fires, it starts over on the next tick. A state without a node just
waits for a transition.

After each tick of the state, its transitions are considered top to bottom, and the first one that
fires wins. A transition fires when its trigger is met **and** its condition holds:

| Trigger | Met when |
|---|---|
| `Always` | on every tick |
| `Finished` | the state's node finished on this tick |
| `Succeeded` | it finished with Success |
| `Failed` | it finished with Failure |

The condition is any node — usually a `ConditionNode` — and holds when it returns Success. Without
one, the trigger alone decides.

The state arrived at starts on the next tick, so one tick never takes more than one transition.

## Building one in code

```csharp
using Misscore;
using Misstate;

var patrol = new FsmState { Name = "Patrol", Node = new WalkWaypoints() };
var chase = new FsmState { Name = "Chase", Node = new ChasePlayer() };

patrol.Transitions.Add(new FsmTransition { TargetStateId = chase.Id, Condition = new PlayerNear() });
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

A state's node can be any node, so with Missbehave in the project it can be a `Sequence`, a
`Selector` or a whole subtree: a state machine deciding *which* behavior runs, a behavior tree
deciding *how*.

## Tests

```bash
godot --headless --path . res://addons/misstate/tests/self_test.tscn
```

Runs headless and exits 0 when everything passes.
