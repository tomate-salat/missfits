# Misscore

The shared base of the Missfits addons for Godot 4.7 / C#. It is a plain library: there is no plugin
to enable, the folder only has to sit in `addons/` next to the addons that use it. Every Missfits
addon ships with it.

## What is in it

**Blackboard** (`Misscore`)

- `Blackboard` — scratch memory for one running instance, by entry id or by name.
- `BlackboardEntry` — one declared value: a type, a default and a name.
- `BbParam<T>` — a parameter that is either a fixed value or a link to an entry.
- `BbParamResource` — base class of a resource with `BbParam<T>` members: it saves, loads and shows
  them, and carries them across an assembly reload.
- `BbParams`, `BbTypes` — finding parameters on a class, storing them, and matching types.
- `IBlackboardSource`, `IBbParamHost` — what a resource implements to have its blackboard edited:
  the source declares the entries, its hosts carry the parameters that link to them.

**Nodes** (`Misscore`)

- `MissNode` — base class of everything a Missfits addon ticks, with its identity, its children and
  the tick contract: `Begin`, `Execute`, `AfterRun`, `Interrupt`.
- `ActionNode`, `ConditionNode` — what you subclass for your own logic: override `Run(MissContext)`
  or `Check(MissContext)`. Written once, run by every Missfits addon.
- `MissContext` — the actor, the blackboard, the delta and, if there is one, the runner.
- `IMissRunner` — what every runner offers a node: `Stop()` and `Enabled`.
- `MissRunner` — base class of the runner nodes: actor, tick thread and rate, stopping, and a
  blackboard the Inspector fills from the source's entries.
- `MissStatus` — `Success`, `Failure` or `Running`.
- `BlackboardSetNode`, `BlackboardEraseNode`, `BlackboardHasNode`, `BlackboardCompareNode` — ready-made
  leaves working on the blackboard.
- `[NodeName]`, `[NodeGroup]` — how a node type is named and filed in a node picker.

A node loaded from a file is a definition and is never ticked; whatever runs it works on its own
copy from `CloneRuntime()`.

**Editor** (`Misscore.Editor`)

- `BlackboardPanel` — edits the entries of an `IBlackboardSource`, each edit one undo step.
- `BbParamEditorProperty` — the Inspector editor for a `BbParam<T>`.
- `ReloadSafe` — references to script objects that survive an assembly reload.

## Compatibility

All Missfits addons compile into the project's own assembly together with this folder, so they are
released with one shared version number. Update every installed Missfits addon at the same time.
