# Misspeak

Resource-based dialogues for Godot 4 and C#. A dialogue is one `.tres`; what happens in it are the
same actions and conditions the other [Missfits](https://github.com/tomate-salat/missfits) addons
run, so a behavior tree or a state machine can start a dialogue, and a dialogue can do anything an
action can do.

> **State of things:** runtime, graph editor with live view, translation support and an example
> dialogue box are here.

Needs `addons/misscore`. Does not need, and does not know, any other Missfits addon.

## Enabling it

Enable **Misspeak** under *Project → Project Settings → Plugins*. That adds the *Misspeak* dock
with the graph editor and lets Godot's translation template read dialogues. The runtime works
without it.

## Quick start

1. In the FileSystem dock, *New Resource… → Dialogue*, and double-click it: it opens in the
   *Misspeak* dock.
2. *Add section*. It comes with one empty line: click the line and write its speaker and text in
   the Inspector. *+ line* adds the next one.
3. Drag from the port beside *new option* onto another section — or onto empty canvas, which makes
   one. Click the new row and give it a text to turn it into a choice for the player.
4. Add a `DialogueRunner` to your scene, assign the dialogue, tick *Autostart*, and instance
   `addons/misspeak/ui/dialogue_box.tscn` next to it.

## How a dialogue works

A `Dialogue` is made of **sections**. A `DialogueSection` is a stretch that runs straight through —
in a graph, one box — and consists of:

- **lines**, spoken one after the other;
- **options**, the ways on to other sections once the lines are through.

Wires are only needed where a dialogue branches or jumps. A talk without choices is one section.

A `DialogueLine` has:

- a **speaker** and a **text** — either may be empty;
- **actions**, which run on reaching the line, *before* its text is shown: top to bottom, each
  until it no longer returns Running;
- **conditions**: a line whose conditions do not hold is skipped, actions and all.

A `DialogueOption` with a text is a **choice** for the player; one without is just where the
dialogue goes next. It may have **conditions** and only counts while they hold.

What happens in a section:

1. Each line in turn: its actions run, then its text is shown and the dialogue waits for the
   player to carry on. A line without text just runs its actions.
2. With the last line, the choices whose conditions hold are offered, and the dialogue waits for
   the player to pick one.
3. With no choice on offer, the player carries on past the last line and the dialogue takes the
   first option without text whose conditions hold.
4. An option that leads nowhere — or no option at all — ends the dialogue.

An option can also **lead back** (`Back`): to the section in which the player last made a choice,
which starts over. Sections passed through without a choice are skipped on the way back. With no
choice made yet, there is nowhere to go back to and the dialogue ends.

A section in which nothing is said moves on by itself. That makes it a branch (the first option
whose conditions hold is taken) or simply a place to run actions.

Texts may name blackboard entries in braces: `You have {gold} coins.`

## The editor

- **Sections** are boxes. The one marked ▶ is where the dialogue starts; right-click a section to
  start there instead, to add a line or an option, or to delete it.
- **Lines** are the upper rows, as *Speaker: text*. Right-click one to move it, delete it, or add
  an **action** or a **condition** — which opens the node picker shared with the other Missfits
  editors. They show below their line, set in: *if …* for conditions, *▸ …* for actions.
- **Options** are the lower rows, each with a port on the right and a wire to where it leads:
  *“text” → Section* for a choice, *→ Section* for the way on by itself, *→ end* for one that ends
  the dialogue. Right-click one to move it, delete it, or add a condition.
- **Wires.** Drag from the last, faint port to add an option; drag from an option's own port to
  lead it somewhere else. Dropping a wire on empty canvas makes a new section there.
- **Reroutes** lead a wire around the boxes: double-click a wire, or right-click it. They work as
  in Misstate, turning round when they lead back to the left.
- **Ports** are reroutes without the wire that leaves them, drawn as an arrow running into a bar.
  Right-click a wire and choose *Add port to this wire*, or right-click a reroute to hide its
  wire. Put one next to the option, and a long wire across the graph becomes a short one; the
  option's row still says where it leads. Double-click a port to go there; drag from its right end
  to lead it elsewhere.
- **Where it leads.** Picking an option — or selecting a port or reroute — puts a thin outline
  around the section it leads to.
- **Leading back.** Right-click an option and tick *Lead back*: instead of following a wire it
  returns to the section in which the player last made a choice — the menu this branch was picked
  from — which then starts over. Its row reads *↩ back* and has no wire. This is what keeps a hub
  with many branches from growing a wire back for each. It is never assumed: an option without a
  target that does not lead back ends the dialogue.
- **Selecting** a section or a row shows exactly that in the Inspector, which is where texts,
  speakers and parameters are edited.
- **Speakers.** Click the empty canvas to get the dialogue itself into the Inspector, and list who
  speaks under *Speakers*. From then on a line's speaker is a dropdown of those instead of a text
  field, and a line spoken by anyone else is flagged. Renaming a speaker there renames it in every
  line. With no speakers listed, a line's speaker is free text.
- **Blackboard.** The dialogue's entries sit beside the graph; parameters of actions and
  conditions link to them in the Inspector, and texts name them in `{braces}`.
- **⚠** names what is wrong: an option that leads to a section that is gone, a line that says and
  does nothing, a link to an entry that no longer exists.

Every edit in the graph is one undo step. *Save* writes the dialogue; the dock title carries a `*`
while it has unsaved edits, and *Revert* goes back to the file.

## Live debugging

Open a dialogue, run the game, and the graph shows where the dialogue is: the section it is in gets
an amber outline, the others fade, and the line it is at — on show, or still running its actions —
is tinted. The way the dialogue came is green, from where the player last did something: through
any sections it passed by itself, so a choice that went through a branch is drawn all the way. The
options out of the current section are amber with dots travelling along them. While a game runs,
the wire behind a port is drawn as well. An option that leads back draws no wire: while the
dialogue is in its section, its row reads *↩ back to Hub* in amber and that section gets a thin
amber outline; once it was taken, the row is green. Between talks the graph looks as it does while
editing.

Only the dialogue open in the dock sends anything, and only when something changed. With nothing
open, the dock opens the dialogue the game is playing. When several runners play the same dialogue,
pick which one to watch from the toolbar dropdown.

## Translation

Speakers, lines and choices go through Godot's translation before the placeholders are filled in,
so the texts you write are the translation keys:

1. Add the dialogue's `.tres` under *Project Settings → Localization → Template Generation* and
   generate the template. The plugin puts the dialogue's texts into it.
2. Translate, and add the translations under *Localization → Translations* as usual.

Placeholders stay as they are in a translation: `Du hast {gold} Münzen.`

When the language changes mid-dialogue, the runner says what is on show again, in the new one.

`Dialogue.SpeakerAsTranslationContext` translates each line with its speaker as the context, so
the same words can be translated differently for different characters. That needs gettext (`.po`)
translations — a CSV translation carries no contexts — and is off by default.

## Showing it

A `DialogueRunner` node plays the dialogue and tells whoever shows it what to show. It draws
nothing itself.

**The quick way:** instance `addons/misspeak/ui/dialogue_box.tscn` in your scene. It shows the
first runner it finds — or the one you assign — along the bottom of the screen, writes lines out
letter by letter, offers choices as buttons, and carries on with `ui_accept` or a click. It is one
script, `DialogueBox.cs`, meant to be copied and made yours.

**Your own:** connect to the runner's signals.

```csharp
public partial class MyDialogueBox : Control {
    [Export] public DialogueRunner Runner { get; set; }

    public override void _Ready() {
        Runner.LineShown += (speaker, text) => { /* show them */ };
        Runner.ChoicesOffered += choices => { /* one button each; on press: Runner.Choose(index) */ };
        Runner.DialogueFinished += Hide;
    }

    public override void _UnhandledInput(InputEvent @event) {
        if (@event.IsActionPressed("ui_accept")) Runner.Advance();
    }
}
```

| Member | |
|---|---|
| `Start(dialogue = null, section = "")` | plays the given dialogue, or the runner's own, from its start or from the section of that name; one under way is cancelled first |
| `Advance()` | carries on after a line without choices |
| `Choose(index)` | picks one of the choices offered |
| `Cancel()` | ends the dialogue where it is |
| `IsActive`, `Waiting`, `Speaker`, `Text`, `Choices` | where the dialogue is, for a box that polls instead |
| `Autostart` | starts the runner's dialogue when the scene is ready |

`Stop()` and `Enabled`, which every Missfits runner has, pause the dialogue where it is.

Like the other runners, it has a blackboard filled from the dialogue's entries, and the Inspector
lets each runner override them. The blackboard lasts as long as the runner, so what one talk
changed is still so in the next.

## From a behavior tree or a state machine

Two nodes come with the addon and show up in the node pickers of Missbehave and Misstate under
*Dialogue*:

- **Start dialogue** (`StartDialogueAction`) plays a dialogue and, by default, keeps running until
  it is over — so a state lasts as long as the talk, and a sequence waits for it. Interrupted, it
  cancels the dialogue.
- **Dialogue active** (`DialogueActiveCondition`) holds while a dialogue is under way; negate it
  for "the talking is over".

Both look for the `DialogueRunner` among the actor's children first and fall back on the first one
in the scene, so one runner next to your dialogue box serves every NPC. Set their `Runner` path to
name a particular one.

## Building one in code

```csharp
var ask = new DialogueSection { Name = "Ask" };
ask.Lines.Add(new DialogueLine { Speaker = "Smith", Text = "Welcome." });
ask.Lines.Add(new DialogueLine { Speaker = "Smith", Text = "What do you need?" });

var sword = new DialogueSection { Name = "Sword" };
sword.Lines.Add(new DialogueLine { Speaker = "Smith", Text = "A fine blade. {gold} coins." });

ask.Options.Add(new DialogueOption { Text = "A sword.", TargetSectionId = sword.Id });
ask.Options.Add(new DialogueOption { Text = "Nothing." });

var dialogue = new Dialogue();
dialogue.Sections.Add(ask);
dialogue.Sections.Add(sword);
runner.Start(dialogue);
```

## Tests

The tests are not part of the release zip; they come with the
[repository](https://github.com/tomate-salat/missfits). Both suites run headless and exit 0 when
everything passes.

```bash
godot --headless --path . res://addons/misspeak/tests/self_test.tscn
godot --headless --path . res://addons/misspeak/tests/editor_self_test.tscn
```
