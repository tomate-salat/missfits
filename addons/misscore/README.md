# Misscore

The shared base of the Missfits addons for Godot 4.7 / C#. It is a plain library: there is no plugin
to enable, the folder only has to sit in `addons/` next to the addons that use it. Every Missfits
addon ships with it.

## What is in it

**Blackboard** (`Misscore`)

- `Blackboard` — scratch memory for one running instance, by entry id or by name.
- `BlackboardEntry` — one declared value: a type, a default and a name.
- `BbParam<T>` — a parameter that is either a fixed value or a link to an entry.
- `BbParams`, `BbTypes` — finding parameters on a class, storing them, and matching types.
- `IBlackboardSource`, `IBbParamHost` — what a resource implements to have its blackboard edited:
  the source declares the entries, its hosts carry the parameters that link to them.

**Editor** (`Misscore.Editor`)

- `BlackboardPanel` — edits the entries of an `IBlackboardSource`, each edit one undo step.
- `BbParamEditorProperty` — the Inspector editor for a `BbParam<T>`.
- `ReloadSafe` — references to script objects that survive an assembly reload.

## Compatibility

All Missfits addons compile into the project's own assembly together with this folder, so they are
released with one shared version number. Update every installed Missfits addon at the same time.
