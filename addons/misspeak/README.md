# Misspeak

Resource-based dialogues for Godot 4 and C#. A dialogue is one `.tres`; what happens in it are the
same actions and conditions the other [Missfits](https://github.com/tomate-salat/missfits) addons
run, so a behavior tree or a state machine can start a dialogue, and a dialogue can do anything an
action can do.

> **State of things:** the runtime, translation support and an example dialogue box are here; the
> graph editor is not yet. Until it is, a dialogue is built in the Inspector or in code.

Needs `addons/misscore`. Does not need, and does not know, any other Missfits addon.

## Enabling it

Enable **Misspeak** under *Project → Project Settings → Plugins*. The runtime works without that;
the plugin is what lets Godot's translation template read dialogues.

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

A section in which nothing is said moves on by itself. That makes it a branch (the first option
whose conditions hold is taken) or simply a place to run actions.

Texts may name blackboard entries in braces: `You have {gold} coins.`

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
[repository](https://github.com/tomate-salat/missfits). The suite runs headless and exits 0 when
everything passes.

```bash
godot --headless --path . res://addons/misspeak/tests/self_test.tscn
```
