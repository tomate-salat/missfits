using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misspeak;
using Misspeak.Tests;

/// <summary>
/// Self test of the dialogue runtime. Runs headless and exits 0 when everything passes:
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/misspeak/tests/self_test.tscn
/// </code>
/// </summary>
public partial class SpeakSelfTest : Node {
    const double Step = 0.1;

    readonly List<string> _failures = [];
    int _checks;

    public override void _Ready() {
        // lines
        TheLinesOfASectionAreShownOneAfterTheOther();
        ASectionWithoutOptionsEndsTheDialogue();
        ActionsRunBeforeTheTextIsShown();
        ALineIsSkippedUnlessItsConditionsHold();
        TextsAreFilledFromTheBlackboard();

        // options
        ChoicesAreOfferedWithTheLastLine();
        ChoicesAreOfferedWhileTheirConditionsHold();
        WithoutAChoiceTheDialogueCarriesOnByItself();
        ASectionWhereNothingIsSaidIsABranch();

        // control
        CancellingEndsTheDialogue();
        StoppingTheRunnerPausesIt();
        AnActionCanCancelTheDialogue();
        StartingAnotherDialogueReplacesTheOneRunning();

        // translation
        TextsAreTranslated();

        // from behavior trees and state machines
        AnActionStartsADialogueAndWaitsForIt();
        AConditionTellsWhetherADialogueIsRunning();

        // live debugging
        TheDebugStreamSendsWhereADialogueIs();

        // instance isolation and serialisation
        TwoRunnersOfOneDialogueAreIndependent();
        ADialogueSurvivesSavingAndLoading();
        ProblemsAreReported();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misspeak self test: {_checks - _failures.Count}/{_checks} checks passed");

        // Godot arrays the tests left behind are finalized now, not after the engine has shut down.
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    // ---- lines -------------------------------------------------------------------------------

    void TheLinesOfASectionAreShownOneAfterTheOther() {
        var gate = Section("Gate", Line("Guard", "Halt."), Line("Guard", "State your business."), Line("You", "Just passing."));
        var talk = new Talk(this, Dialogue(gate));

        Check("nothing is said before a dialogue is started", !talk.Runner.IsActive && talk.Runner.Waiting == DialogueWait.Nothing);
        Check("a dialogue starts", talk.Runner.Start() && talk.Runner.IsActive);
        Check("its first line is shown at once, with who says it",
            talk.Log.SequenceEqual(["started", "Guard: Halt."]) && talk.Runner.Speaker == "Guard" && talk.Runner.Text == "Halt.");
        Check("and it waits for the player to carry on", talk.Runner.Waiting == DialogueWait.Advance);

        talk.Runner.Tick(Step);
        Check("ticking does not move it on", talk.Log.Count == 2 && talk.Runner.Status == MissStatus.Running);

        Check("carrying on shows the section's next line", talk.Runner.Advance() && talk.Log.Last() == "Guard: State your business.");
        Check("and the one after that, without leaving the section",
            talk.Runner.Advance() && talk.Log.Last() == "You: Just passing." && talk.Runner.Instance.Current == gate);
        Check("there is nothing to choose where no choice is offered", !talk.Runner.Choose(0));
        talk.Free();
    }

    void ASectionWithoutOptionsEndsTheDialogue() {
        var talk = new Talk(this, Dialogue(Section("End", Line("", "Bye."))));
        talk.Runner.Start();

        Check("after the last line of a section that leads nowhere, the dialogue is over",
            talk.Runner.Advance() && !talk.Runner.IsActive && talk.Log.Last() == "finished");
        Check("with nothing left on show", talk.Runner.Text == "" && talk.Runner.Choices.Length == 0 && talk.Runner.Waiting == DialogueWait.Nothing);
        Check("and nothing to carry on with", !talk.Runner.Advance());
        talk.Runner.Tick(Step);
        Check("a runner with nothing left to say reports success", talk.Runner.Status == MissStatus.Success);

        Check("it can be started again", talk.Runner.Start() && talk.Runner.Text == "Bye.");
        Check("a dialogue without sections cannot be played", DialogueInstance.Create(new Dialogue()) == null);
        talk.Free();
    }

    void ActionsRunBeforeTheTextIsShown() {
        var first = new SpeakProbeAction { RunningTicks = 2 };
        var second = new SpeakProbeAction { Result = MissStatus.Failure };
        var silent = new SpeakProbeAction();
        var line = Line("", "Done.", first, second);
        var quiet = Line("", "", silent);
        var talk = new Talk(this, Dialogue(Section("Work", line, quiet, Line("", "After."))));

        talk.Runner.Start();
        var running = talk.Runner.Instance.ActionsOf(line)[0] as SpeakProbeAction;
        var after = talk.Runner.Instance.ActionsOf(line)[1] as SpeakProbeAction;
        Check("a line runs copies of its actions", running != null && !ReferenceEquals(running, first) && first.Ticks == 0);
        Check("while an action runs, the line is not shown yet",
            talk.Runner.Waiting == DialogueWait.Actions && talk.Log.SequenceEqual(["started"]) && running.Ticks == 1);
        Check("and the player cannot carry on", !talk.Runner.Advance());

        talk.Runner.Tick(Step);
        Check("a running action is resumed, not restarted", running.BeforeRuns == 1 && running.Ticks == 2 && after.Ticks == 0);
        talk.Runner.Tick(Step);
        Check("actions run one after the other, each to its end", running.AfterRuns == 1 && after.Ticks == 1 && after.AfterRuns == 1);
        Check("the text follows once they are through, whatever they returned", talk.Log.Last() == "Done." && talk.Runner.Waiting == DialogueWait.Advance);

        talk.Runner.Tick(Step);
        Check("and they do not run again while the line is shown", running.Ticks == 3 && after.Ticks == 1);

        var quietly = talk.Runner.Instance.ActionsOf(quiet)[0] as SpeakProbeAction;
        Check("the actions of a later line wait for their turn", quietly.Ticks == 0);
        talk.Runner.Advance();
        Check("a line without text just runs its actions and makes way for the next", quietly.Ticks == 1 && talk.Runner.Text == "After.");
        talk.Free();
    }

    void ALineIsSkippedUnlessItsConditionsHold() {
        var met = Entry("met", Variant.Type.Int, 0);
        var again = Line("Smith", "You again.");
        again.Conditions.Add(Needs(met, 1));
        var skipped = new SpeakProbeAction();
        var hidden = Line("Smith", "Never.", skipped);
        hidden.Conditions.Add(Needs(met, 99));
        var dialogue = Dialogue(Section("Hello", again, Line("Smith", "Welcome."), hidden));
        dialogue.Blackboard.Add(met);
        var talk = new Talk(this, dialogue);

        talk.Runner.Start();
        Check("a line whose conditions do not hold is left out", talk.Runner.Text == "Welcome.");
        talk.Runner.Advance();
        Check("and so are its actions", !talk.Runner.IsActive && (talk.Runner.Instance.ActionsOf(hidden)[0] as SpeakProbeAction)?.Ticks == 0);

        talk.Runner.Blackboard.Set("met", 1);
        talk.Runner.Start();
        Check("once they hold, it is spoken in its place", talk.Runner.Text == "You again." && talk.Runner.Advance() && talk.Runner.Text == "Welcome.");
        talk.Free();
    }

    void TextsAreFilledFromTheBlackboard() {
        var gold = Entry("gold", Variant.Type.Int, 5);
        var name = Entry("hero", Variant.Type.String, "Ada");
        var action = new SpeakProbeAction();
        Bind(action.Counter, gold);
        var section = Section("Purse", Line("{hero}", "I have {gold} coins, {stranger}.", action));
        Choice(section, "Keep all {gold}.", null);
        var dialogue = Dialogue(section);
        dialogue.Blackboard.Add(gold);
        dialogue.Blackboard.Add(name);
        var talk = new Talk(this, dialogue);

        talk.Runner.Start();
        Check("a runner's blackboard holds the dialogue's entries", talk.Runner.Blackboard.Get<string>("hero") == "Ada");
        Check("an action writes to it through its link", talk.Runner.Blackboard.Get<int>("gold") == 6);
        Check("placeholders in a text take the entries' values, unknown ones stay as written",
            talk.Runner.Text == "I have 6 coins, {stranger}." && talk.Runner.Speaker == "Ada");
        Check("and so do those in a choice", talk.Runner.Choices.SequenceEqual(["Keep all 6."]));
        talk.Free();
    }

    // ---- options -----------------------------------------------------------------------------

    void ChoicesAreOfferedWithTheLastLine() {
        var seen = Entry("seen", Variant.Type.Int, 0);
        var late = Line("Smith", "Only for regulars.");
        late.Conditions.Add(Needs(seen, 1));
        var ask = Section("Ask", Line("Smith", "Welcome."), Line("Smith", "What do you need?"), late);
        var sword = Section("Sword", Line("Smith", "A fine blade."));
        Choice(ask, "A sword.", sword);
        Choice(ask, "Nothing.", null);
        var dialogue = Dialogue(ask, sword);
        dialogue.Blackboard.Add(seen);
        var talk = new Talk(this, dialogue);

        talk.Runner.Start();
        Check("no choice is offered while lines of the section are still to come", talk.Runner.Waiting == DialogueWait.Advance && talk.Runner.Choices.Length == 0);
        talk.Runner.Advance();
        Check("the choices come with the section's last line — the last one that is going to be spoken",
            talk.Runner.Text == "What do you need?" && talk.Runner.Waiting == DialogueWait.Choice
            && talk.Runner.Choices.SequenceEqual(["A sword.", "Nothing."]));
        Check("after that line, not before it",
            talk.Log.SequenceEqual(["started", "Smith: Welcome.", "Smith: What do you need?", "choices: A sword.|Nothing."]));
        Check("the player cannot just carry on past a choice", !talk.Runner.Advance());
        Check("nor pick what is not on offer", !talk.Runner.Choose(2) && !talk.Runner.Choose(-1));

        Check("picking a choice goes to the section it leads to", talk.Runner.Choose(0) && talk.Runner.Text == "A fine blade."
                                                                    && talk.Runner.Instance.Current == sword && talk.Runner.Choices.Length == 0);

        talk.Runner.Start();
        talk.Runner.Advance();
        Check("a choice that leads nowhere ends the dialogue", talk.Runner.Choose(1) && !talk.Runner.IsActive);
        talk.Free();
    }

    void ChoicesAreOfferedWhileTheirConditionsHold() {
        var level = Entry("level", Variant.Type.Int, 3);
        var ask = Section("Ask", Line("Smith", "What do you need?"));
        var armour = Section("Armour", Line("Smith", "Sturdy plate."));
        Choice(ask, "Dragon armour.", armour, Needs(level, 10));
        Choice(ask, "Armour.", armour, Needs(level, 2));
        var dialogue = Dialogue(ask, armour);
        dialogue.Blackboard.Add(level);
        var talk = new Talk(this, dialogue);

        talk.Runner.Start();
        Check("only the choices whose conditions hold are offered", talk.Runner.Choices.SequenceEqual(["Armour."]));
        Check("and picked by their place among those offered", talk.Runner.Choose(0) && talk.Runner.Text == "Sturdy plate.");
        talk.Free();

        var either = Section("Either", Line("", "Pick."));
        var open = Choice(either, "Either will do.", null, Needs(level, 10), Needs(level, 1));
        var selective = Dialogue(either);
        selective.Blackboard.Add(level);
        talk = new Talk(this, selective);
        talk.Runner.Start();
        Check("a choice needs all of its conditions by default", talk.Runner.Choices.Length == 0);
        open.Mode = ListMode.Selector;
        talk.Runner.Rebuild();
        talk.Runner.Start();
        Check("or just one of them, as a selector", talk.Runner.Choices.SequenceEqual(["Either will do."]));
        talk.Free();
    }

    void WithoutAChoiceTheDialogueCarriesOnByItself() {
        var level = Entry("level", Variant.Type.Int, 1);
        var ask = Section("Ask", Line("Smith", "Come back when you are stronger."));
        var secret = Section("Secret", Line("Smith", "Take this."));
        var later = Section("Later", Line("Smith", "Later."));
        Choice(ask, "I am strong.", secret, Needs(level, 5));
        Link(ask, later);
        var dialogue = Dialogue(ask, secret, later);
        dialogue.Blackboard.Add(level);
        var talk = new Talk(this, dialogue);

        talk.Runner.Start();
        Check("with no choice on offer, the last line just waits to be carried on", talk.Runner.Waiting == DialogueWait.Advance && talk.Runner.Choices.Length == 0);
        Check("and then the way on that is no choice is taken", talk.Runner.Advance() && talk.Runner.Text == "Later.");

        talk.Runner.Blackboard.Set("level", 7);
        talk.Runner.Start();
        Check("once a choice is on offer, it has to be picked", talk.Runner.Choices.SequenceEqual(["I am strong."]) && !talk.Runner.Advance());
        talk.Free();
    }

    void ASectionWhereNothingIsSaidIsABranch() {
        var level = Entry("level", Variant.Type.Int, 4);
        var seen = new SpeakProbeAction();
        var check = Line("", "", seen);
        var branch = Section("Branch", check);
        var high = Section("High", Line("", "High."));
        var low = Section("Low", Line("", "Low."));
        Link(branch, high, Needs(level, 5));
        Link(branch, low);
        var dialogue = Dialogue(branch, high, low);
        dialogue.Blackboard.Add(level);
        var talk = new Talk(this, dialogue);

        talk.Runner.Start();
        Check("a section in which nothing is said shows nothing", talk.Log.SequenceEqual(["started"]) && talk.Runner.Text == "");
        Check("but still runs its actions", (talk.Runner.Instance.ActionsOf(check)[0] as SpeakProbeAction)?.Ticks == 1);
        Check("and moves on by itself, to the first option whose conditions hold", talk.Runner.Instance.Current == low);
        Check("where the next tick picks up", talk.Runner.Waiting == DialogueWait.Actions);
        talk.Runner.Tick(Step);
        Check("so the section arrived at speaks a tick later", talk.Runner.Text == "Low." && talk.Log.Last() == "Low.");

        talk.Runner.Blackboard.Set("level", 9);
        talk.Runner.Start();
        talk.Runner.Tick(Step);
        Check("the branch follows the blackboard", talk.Runner.Text == "High.");
        talk.Free();

        var loop = Section("Loop");
        Link(loop, loop);
        talk = new Talk(this, Dialogue(loop));
        talk.Runner.Start();
        talk.Runner.Tick(Step);
        Check("silent sections in a circle cannot hang a tick", talk.Runner.IsActive);
        talk.Free();

        talk = new Talk(this, Dialogue(Section("Dead end")));
        Check("a silent section with no way on ends the dialogue", talk.Runner.Start() && !talk.Runner.IsActive && talk.Log.Last() == "finished");
        talk.Free();

        var menu = Section("Menu", Line("", "", new SpeakProbeAction()));
        Choice(menu, "Leave.", null);
        talk = new Talk(this, Dialogue(menu));
        talk.Runner.Start();
        Check("choices of a section in which nothing is said are offered by themselves",
            talk.Runner.Waiting == DialogueWait.Choice && talk.Runner.Text == "" && talk.Log.SequenceEqual(["started", "choices: Leave."]));
        talk.Free();
    }

    // ---- control -----------------------------------------------------------------------------

    void CancellingEndsTheDialogue() {
        var action = new SpeakProbeAction { RunningTicks = 5 };
        var line = Line("", "Never shown.", action);
        var talk = new Talk(this, Dialogue(Section("Busy", line)));

        talk.Runner.Start();
        var running = talk.Runner.Instance.ActionsOf(line)[0] as SpeakProbeAction;
        talk.Runner.Cancel();
        Check("cancelling ends the dialogue", !talk.Runner.IsActive && talk.Log.SequenceEqual(["started", "finished"]));
        Check("and interrupts the action it left running", running.Interrupts == 1 && running.AfterRuns == 0);

        talk.Runner.Cancel();
        Check("cancelling when nothing runs does nothing", talk.Log.Count == 2);
        talk.Free();
    }

    void StoppingTheRunnerPausesIt() {
        var action = new SpeakProbeAction { RunningTicks = 1 };
        var line = Line("", "At last.", action);
        var section = Section("Busy", line);
        var talk = new Talk(this, Dialogue(section));

        talk.Runner.Start();
        var running = talk.Runner.Instance.ActionsOf(line)[0] as SpeakProbeAction;
        talk.Runner.Stop();
        Check("stopping the runner interrupts the running action", running.Interrupts == 1);
        Check("but keeps the dialogue where it is", talk.Runner.IsActive && talk.Runner.Instance.Current == section);
        talk.Runner.Tick(Step);
        Check("a stopped runner does not carry on", running.BeforeRuns == 1 && talk.Runner.Text == "");

        talk.Runner.Enabled = true;
        talk.Runner.Tick(Step);
        talk.Runner.Tick(Step);
        Check("switched back on, it starts that action over and gets to the text", running.BeforeRuns == 2 && talk.Runner.Text == "At last.");
        talk.Free();
    }

    void AnActionCanCancelTheDialogue() {
        var quit = new SpeakProbeAction { CancelOnFirstTick = true, RunningTicks = 3 };
        var after = new SpeakProbeAction();
        var line = Line("", "Never shown.", quit, after);
        var talk = new Talk(this, Dialogue(Section("Quit", line)));

        talk.Runner.Start();
        var quitting = talk.Runner.Instance.ActionsOf(line)[0] as SpeakProbeAction;
        Check("an action can cancel the dialogue it is part of", !talk.Runner.IsActive && talk.Log.SequenceEqual(["started", "finished"]));
        Check("which closes that action exactly once", quitting.Interrupts == 1 && quitting.AfterRuns == 0);
        Check("and runs nothing that came after it", (talk.Runner.Instance.ActionsOf(line)[1] as SpeakProbeAction)?.Ticks == 0);
        talk.Free();
    }

    void StartingAnotherDialogueReplacesTheOneRunning() {
        var coins = Entry("coins", Variant.Type.Int, 12);
        var first = Dialogue(Section("One", Line("", "First.")));
        var second = Dialogue(Section("Count", Line("", "{coins} coins.")), Section("Aside", Line("", "Psst.")));
        second.Blackboard.Add(coins);
        var talk = new Talk(this, first);

        talk.Runner.Start();
        Check("a runner can be handed another dialogue to play", talk.Runner.Start(second) && talk.Runner.Dialogue == second);
        Check("which ends the one that was running", talk.Log.SequenceEqual(["started", "First.", "finished", "started", "12 coins."]));

        talk.Runner.Start();
        Check("starting again while a dialogue runs begins it anew", talk.Log.Skip(5).SequenceEqual(["finished", "started", "12 coins."]));

        Check("a dialogue can be started at a section, by its name", talk.Runner.Start(section: "Aside") && talk.Runner.Text == "Psst.");
        Check("but not at one it does not have", !talk.Runner.Start(section: "Nowhere") && talk.Runner.Text == "Psst.");
        talk.Free();
    }

    // ---- translation -------------------------------------------------------------------------

    void TextsAreTranslated() {
        var gold = Entry("gold", Variant.Type.Int, 3);
        var section = Section("Shop", Line("Smith", "That makes {gold} coins."), Line("Guard", "Yes."), Line("Smith", "Yes."));
        Choice(section, "Pay.", null);
        var dialogue = Dialogue(section);
        dialogue.Blackboard.Add(gold);

        var texts = dialogue.TranslatableTexts().ToList();
        Check("a dialogue lists its texts for translation: speakers, lines and choices, each once",
            texts.SequenceEqual([("Smith", ""), ("That makes {gold} coins.", ""), ("Guard", ""), ("Yes.", ""), ("Pay.", "")]));
        dialogue.SpeakerAsTranslationContext = true;
        texts = [.. dialogue.TranslatableTexts()];
        Check("with the speaker as context where the dialogue asks for it",
            texts.Contains(("Yes.", "Guard")) && texts.Contains(("Yes.", "Smith")) && texts.Contains(("Smith", "")) && texts.Contains(("Pay.", "")));

        var german = new Translation { Locale = "de" };
        german.AddMessage("Smith", "Schmied");
        german.AddMessage("That makes {gold} coins.", "Das macht {gold} Münzen.", "Smith");
        german.AddMessage("Yes.", "Jawohl.", "Guard");
        german.AddMessage("Yes.", "Ja.", "Smith");
        german.AddMessage("Pay.", "Bezahlen.");
        // The machine the test runs on may well be set to German itself.
        var before = TranslationServer.GetLocale();
        TranslationServer.SetLocale("en");
        TranslationServer.AddTranslation(german);

        var talk = new Talk(this, dialogue);
        talk.Runner.Start();
        Check("without a translation for the language, a text is shown as written", talk.Runner.Text == "That makes 3 coins." && talk.Runner.Speaker == "Smith");
        TranslationServer.SetLocale("de");
        Check("a change of language says the line on show again, in the new one",
            talk.Log.Last() == "Schmied: Das macht 3 Münzen." && talk.Runner.Text == "Das macht 3 Münzen." && talk.Runner.Speaker == "Schmied");
        talk.Runner.Advance();
        Check("the same words are translated by who says them", talk.Runner.Text == "Jawohl.");
        talk.Runner.Advance();
        Check("each in their own way, choices included", talk.Runner.Text == "Ja." && talk.Runner.Choices.SequenceEqual(["Bezahlen."]));

        TranslationServer.SetLocale("en");
        Check("and back", talk.Runner.Text == "Yes." && talk.Runner.Choices.SequenceEqual(["Pay."]) && talk.Log.Last() == "choices: Pay.");
        TranslationServer.RemoveTranslation(german);
        TranslationServer.SetLocale(before);
        talk.Free();
    }

    // ---- from behavior trees and state machines ----------------------------------------------

    void AnActionStartsADialogueAndWaitsForIt() {
        var dialogue = Dialogue(Section("Hello", Line("", "Hello.")), Section("Aside", Line("", "Psst.")));
        var talk = new Talk(this, null);
        var ctx = new MissContext { Actor = talk.Actor, Blackboard = new Blackboard(), Delta = Step };

        var start = (StartDialogueAction) new StartDialogueAction { Dialogue = dialogue }.CloneRuntime();
        start.Begin(ctx);
        Check("the action starts its dialogue on the actor's runner", start.Execute(ctx) == MissStatus.Running && talk.Runner.Text == "Hello.");
        Check("and keeps running while it lasts", start.Execute(ctx) == MissStatus.Running);
        talk.Runner.Advance();
        Check("until it is over", start.Execute(ctx) == MissStatus.Success);
        start.AfterRun(ctx);

        start.Begin(ctx);
        start.Execute(ctx);
        start.Interrupt(ctx);
        Check("interrupted, it cancels the dialogue it started", !talk.Runner.IsActive);

        var fire = (StartDialogueAction) new StartDialogueAction { Dialogue = dialogue, WaitUntilFinished = false }.CloneRuntime();
        fire.Begin(ctx);
        Check("it can also just start it and be done", fire.Execute(ctx) == MissStatus.Success && talk.Runner.IsActive);
        talk.Runner.Cancel();

        var own = (StartDialogueAction) new StartDialogueAction { Section = "Aside" }.CloneRuntime();
        own.Begin(ctx);
        Check("without a dialogue of its own it plays the runner's, at the section it names", own.Execute(ctx) == MissStatus.Running && talk.Runner.Text == "Psst.");
        talk.Runner.Cancel();

        var byPath = (StartDialogueAction) new StartDialogueAction { Dialogue = dialogue, Runner = new NodePath("Nowhere") }.CloneRuntime();
        byPath.Begin(ctx);
        Check("a path to no runner makes it fail", byPath.Execute(ctx) == MissStatus.Failure && !talk.Runner.IsActive);

        // An actor without a runner of its own falls back on any runner in the scene.
        var bystander = new Node { Name = "Bystander" };
        AddChild(bystander);
        var elsewhere = (StartDialogueAction) new StartDialogueAction { Dialogue = dialogue }.CloneRuntime();
        var other = ctx with { Actor = bystander };
        elsewhere.Begin(other);
        Check("an actor without a runner of its own uses one from the scene", elsewhere.Execute(other) == MissStatus.Running && talk.Runner.IsActive);

        bystander.Free();
        talk.Free();
    }

    void AConditionTellsWhetherADialogueIsRunning() {
        var talk = new Talk(this, Dialogue(Section("Hello", Line("", "Hello."))));
        var ctx = new MissContext { Actor = talk.Actor, Blackboard = new Blackboard(), Delta = Step };
        var active = (DialogueActiveCondition) new DialogueActiveCondition().CloneRuntime();
        var over = (DialogueActiveCondition) new DialogueActiveCondition { Negate = true }.CloneRuntime();

        Check("the condition fails while nothing is said", Holds(active, ctx) == false && Holds(over, ctx));
        talk.Runner.Start();
        Check("and holds while a dialogue runs", Holds(active, ctx) && !Holds(over, ctx));
        talk.Free();
    }

    static bool Holds(MissNode condition, MissContext ctx) {
        condition.Begin(ctx);
        var status = condition.Execute(ctx);
        condition.AfterRun(ctx);
        return status == MissStatus.Success;
    }

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>
    /// The game half of the debug channel, with a recorder in place of the debugger: a runner sends
    /// where its dialogue is, to an editor that watches that dialogue, and only when it changed.
    /// </summary>
    void TheDebugStreamSendsWhereADialogueIs() {
        var sent = new List<(string Message, Godot.Collections.Array Data)>();
        ulong now = 1000;
        var stream = MisspeakDebug.NewStream((message, data) => sent.Add((message, data)), () => now);

        const string path = "user://misspeak_debug_dialogue.tres";
        var busy = Line("", "Second.", new SpeakProbeAction { RunningTicks = 1 });
        var first = Line("", "First.");
        var ask = Section("Ask", first, busy);
        var answer = Section("Answer", Line("", "Good."));
        var choice = Choice(ask, "Go on.", answer);
        var dialogue = Dialogue(ask, answer);
        dialogue.TakeOverPath(path);
        var talk = new Talk(this, dialogue);
        var id = (long) talk.Runner.GetInstanceId();

        stream.Register(talk.Runner);
        Check("a runner announces itself with its dialogue and its actor",
            sent.Count == 1 && sent[0].Message == "register" && sent[0].Data[0].AsInt64() == id
            && sent[0].Data[1].AsString() == path && sent[0].Data[2].AsString() == "Speaker");

        talk.Runner.Start();
        talk.Runner.Tick(Step);
        stream.SendState(talk.Runner);
        Check("nothing is streamed while the editor watches no dialogue", sent.Count == 1);

        stream.OnEditorMessage("watch_path", [path]);
        var state = sent.LastOrDefault(m => m.Message == "state").Data;
        Check("watching a dialogue brings where its runner is, right away: the section and the line on show",
            state != null && state[0].AsInt64() == id && state[1].AsString() == ask.Id && state[2].AsString() == first.Id
            && state[3].AsInt32() == (int) DialogueWait.Advance && state[5].AsString() == "");

        var before = sent.Count;
        now += 100;
        talk.Runner.Tick(Step);
        stream.SendState(talk.Runner);
        Check("an unchanged picture is not sent again", sent.Count == before);

        now += 100;
        talk.Runner.Advance();
        stream.SendState(talk.Runner);
        Check("a line whose actions are still running is the line the dialogue is at",
            sent.Count == before + 1 && sent[^1].Data[2].AsString() == busy.Id && sent[^1].Data[3].AsInt32() == (int) DialogueWait.Actions);

        now += 100;
        talk.Runner.Tick(Step);
        talk.Runner.Choose(0);
        stream.SendState(talk.Runner);
        Check("a change of section is sent with the option that led there",
            sent[^1].Data[1].AsString() == answer.Id && sent[^1].Data[5].AsString() == choice.Id && talk.Runner.Instance.EnteredBy == choice);

        now += 100;
        talk.Runner.Advance();
        stream.SendState(talk.Runner);
        Check("the end of the talk is sent as being in no section", sent[^1].Message == "state" && sent[^1].Data[1].AsString() == "");

        stream.Unregister(talk.Runner);
        Check("a runner that goes says so", sent[^1].Message == "unregister" && sent[^1].Data[0].AsInt64() == id);
        talk.Free();
    }

    // ---- instance isolation and serialisation ------------------------------------------------

    void TwoRunnersOfOneDialogueAreIndependent() {
        var dialogue = Dialogue(Section("Count", Line("", "One."), Line("", "Two.")));
        var a = new Talk(this, dialogue);
        var b = new Talk(this, dialogue);

        a.Runner.Start();
        b.Runner.Start();
        a.Runner.Advance();
        Check("two runners of one dialogue each keep their own place", a.Runner.Text == "Two." && b.Runner.Text == "One.");
        a.Free();
        b.Free();
    }

    void ADialogueSurvivesSavingAndLoading() {
        var count = Entry("count", Variant.Type.Int, 0);
        var action = new SpeakProbeAction { RunningTicks = 1 };
        Bind(action.Counter, count);
        var question = Line("Smith", "Well?", action);
        question.Conditions.Add(Needs(count, 0));
        var ask = Section("Ask", question);
        var answer = Section("Answer", Line("Smith", "Good."));
        var choice = Choice(ask, "Yes.", answer, Needs(count, 2));
        var dialogue = Dialogue(answer, ask);
        dialogue.StartSectionId = ask.Id;
        dialogue.Blackboard.Add(count);

        const string path = "user://misspeak_dialogue.tres";
        Check("a dialogue saves", ResourceSaver.Save(dialogue, path) == Error.Ok);
        var loaded = ResourceLoader.Load<Dialogue>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        var loadedAsk = loaded?.FindSection(ask.Id);
        var loadedLine = loadedAsk?.Lines.FirstOrDefault();
        Check("sections come back with their ids and names", loadedAsk is { Name: "Ask" } && loaded.StartSection == loadedAsk);
        Check("a line comes back with its speaker, text and condition",
            loadedLine is { Speaker: "Smith", Text: "Well?" } && loadedLine.Id == question.Id && loadedLine.Conditions.FirstOrDefault() is SpeakProbeCondition);
        Check("its action comes back with its link", loadedLine?.Actions.FirstOrDefault() is SpeakProbeAction { RunningTicks: 1 } node && node.Counter.EntryId == count.Id);
        Check("an option comes back with its text, target and condition",
            loadedAsk?.Options.FirstOrDefault() is { Text: "Yes." } option && option.Id == choice.Id
            && option.TargetSectionId == answer.Id && option.Conditions.FirstOrDefault() is SpeakProbeCondition { AtLeast: 2 });

        var talk = new Talk(this, loaded);
        talk.Runner.Start();
        talk.Runner.Tick(Step);
        Check("a loaded dialogue plays", talk.Runner.Text == "Well?" && talk.Runner.Choices.SequenceEqual(["Yes."]));
        talk.Free();
    }

    void ProblemsAreReported() {
        Check("a dialogue without sections says so", new Dialogue().Validate().Any(p => p.Contains("no sections")));

        var section = Section("Odd", Line("", "Hm."), Line("", ""));
        section.Options.Add(new DialogueOption { TargetSectionId = "gone" });
        var dialogue = Dialogue(section, Section("Empty"));
        dialogue.StartSectionId = "gone";
        var problems = dialogue.Validate();

        Check("a missing start section is reported", problems.Any(p => p.Contains("start section")));
        Check("an option to a section that is gone is reported", problems.Any(p => p.Contains("no longer exists") && p.Contains("option")));
        Check("a line that says and does nothing is reported", problems.Any(p => p.Contains("Odd: line #2")));
        Check("a section that does nothing at all is reported, by its name", problems.Any(p => p.Contains("Empty: has no lines")));

        Check("a sound dialogue has no problems", Dialogue(Section("Fine", Line("", "Fine."))).Validate().Length == 0);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>An actor with a runner of its own, driven by hand, and a record of what the runner announced.</summary>
    sealed class Talk {
        public readonly Node Actor = new() { Name = "Speaker" };
        public readonly DialogueRunner Runner;
        public readonly List<string> Log = [];

        public Talk(Node parent, Dialogue dialogue) {
            Runner = new DialogueRunner { Dialogue = dialogue, Thread = MissRunner.ProcessThread.Manual };
            Runner.DialogueStarted += () => Log.Add("started");
            Runner.LineShown += (speaker, text) => Log.Add(speaker == "" ? text : $"{speaker}: {text}");
            Runner.ChoicesOffered += choices => Log.Add($"choices: {string.Join("|", choices)}");
            Runner.DialogueFinished += () => Log.Add("finished");
            Actor.AddChild(Runner);
            parent.AddChild(Actor);
        }

        /// <summary>At once, not queued: a runner left in the tree would be found by the next test's actions.</summary>
        public void Free() => Actor.Free();
    }

    static Dialogue Dialogue(params DialogueSection[] sections) {
        var dialogue = new Dialogue();
        foreach (var section in sections) dialogue.Sections.Add(section);
        return dialogue;
    }

    static DialogueSection Section(string name, params DialogueLine[] lines) {
        var section = new DialogueSection { Name = name };
        foreach (var line in lines) section.Lines.Add(line);
        return section;
    }

    static DialogueLine Line(string speaker, string text, params MissNode[] actions) {
        var line = new DialogueLine { Speaker = speaker, Text = text };
        foreach (var action in actions) line.Actions.Add(action);
        return line;
    }

    /// <summary>A way on that is no choice. A null target ends the dialogue.</summary>
    static DialogueOption Link(DialogueSection from, DialogueSection to, params MissNode[] conditions) => Choice(from, "", to, conditions);

    static DialogueOption Choice(DialogueSection from, string text, DialogueSection to, params MissNode[] conditions) {
        var option = new DialogueOption { Text = text, TargetSectionId = to?.Id ?? "" };
        foreach (var condition in conditions) option.Conditions.Add(condition);
        from.Options.Add(option);
        return option;
    }

    static SpeakProbeCondition Needs(BlackboardEntry entry, int atLeast) {
        var condition = new SpeakProbeCondition { AtLeast = atLeast };
        Bind(condition.Value, entry);
        return condition;
    }

    static BlackboardEntry Entry(string name, Variant.Type type, Variant value)
        => new() { Name = name, VariantType = type, Default = value };

    static void Bind(IBbParam param, BlackboardEntry entry) {
        param.EntryId = entry.Id;
        param.EntryName = entry.Name;
    }

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}
