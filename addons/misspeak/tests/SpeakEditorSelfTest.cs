#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;
using Misspeak;
using Misspeak.Editor;
using Misspeak.Tests;

/// <summary>
/// Self test of the dialogue graph editor, run outside the editor: the panel and its graph work
/// without one, only undo and the Inspector are missing.
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/misspeak/tests/editor_self_test.tscn
/// </code>
/// </summary>
public partial class SpeakEditorSelfTest : Node {
    readonly List<string> _failures = [];
    int _checks;

    DialogueEditorPanel _panel;
    DialogueGraphEdit _graph;
    Dialogue _dialogue;
    DialogueSection _ask;
    DialogueSection _sword;
    DialogueLine _question;
    DialogueOption _buy;

    Resource _lastEdited;
    int _inspectorCalls;

    public override async void _Ready() {
        // The probes are hidden from the pickers in the editor; the tests create them through one.
        NodeTypeRegistry.IncludeTestTypes = true;
        BuildDialogue();

        _panel = new DialogueEditorPanel();
        AddChild(_panel);
        _panel.EditInInspector = resource => {
            _inspectorCalls++;
            _lastEdited = resource;
            _panel.Highlight(resource);
        };
        _panel.OpenDialogue(_dialogue);
        _graph = _panel.Graph;
        await Settle();

        EditorSourcesDoNotWireDelegates();
        BoxesRowsAndWiresExist();
        TheHeaderSitsInsideTheBox();
        await SectionsAreAddedAndNamedApart();
        await LinesTakeActionsAndConditions();
        await RowsAreOrderedAndDeleted();
        await WiresMakeAndLeadOptions();
        await TheStartSectionIsMarked();
        await SelectingGoesToTheInspectorWithoutLooping();
        await InspectorEditsReachTheGraph();
        await UndoRestoresTheStructure();
        await DeletingASectionLeavesItsOptionsFlagged();
        await ReroutesLeadWiresAround();
        await NodesLinkToTheBlackboardBesideTheGraph();
        await SavingAndRevertingFollowTheFile();
        await TheGraphShowsWhereARunningDialogueIs();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misspeak editor self test: {_checks - _failures.Count}/{_checks} checks passed");
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    void BuildDialogue() {
        _question = new DialogueLine { Speaker = "Smith", Text = "What do you need?" };
        _ask = new DialogueSection { Name = "Ask", GraphPosition = new Vector2(40, 40) };
        _ask.Lines.Add(new DialogueLine { Speaker = "Smith", Text = "Welcome." });
        _ask.Lines.Add(_question);

        _sword = new DialogueSection { Name = "Sword", GraphPosition = new Vector2(460, 40) };
        _sword.Lines.Add(new DialogueLine { Speaker = "Smith", Text = "A fine blade, forged in the old way, and worth every single coin of its price." });

        _buy = new DialogueOption { Text = "A sword.", TargetSectionId = _sword.Id };
        _buy.Conditions.Add(new SpeakProbeCondition { AtLeast = 1 });
        _ask.Options.Add(_buy);
        _ask.Options.Add(new DialogueOption { Text = "Nothing." });
        _sword.Options.Add(new DialogueOption { TargetSectionId = _ask.Id });

        _dialogue = new Dialogue();
        _dialogue.Sections.Add(_ask);
        _dialogue.Sections.Add(_sword);
    }

    // ---- structure ---------------------------------------------------------------------------

    void BoxesRowsAndWiresExist() {
        Check("every section has a box", _graph.BoxFor(_ask.Id) != null && _graph.BoxFor(_sword.Id) != null);
        Check("a box shows the section's name", Text(_ask, "Header/SectionName") == "Ask");
        Check("every line has a row, saying who says what", Rows(_ask, SpeakRow.Line).Count == 2 && RowText(_ask, SpeakRow.Line, 1) == "Smith: What do you need?");
        Check("a long line is cut short", RowText(_sword, SpeakRow.Line, 0).EndsWith("…") && RowText(_sword, SpeakRow.Line, 0).Length <= 44);
        Check("every option has a row", Rows(_ask, SpeakRow.Option).Count == 2 && Rows(_sword, SpeakRow.Option).Count == 1);
        Check("a choice shows its text and where it leads", RowText(_ask, SpeakRow.Option, 0) == "“A sword.” → Sword");
        Check("one that leads nowhere says that it ends the dialogue", RowText(_ask, SpeakRow.Option, 1) == "“Nothing.” → end"
                                                                      && Rows(_ask, SpeakRow.Option)[1].Warning == "");
        Check("the way on by itself shows just where it leads", RowText(_sword, SpeakRow.Option, 0) == "→ Ask");
        Check("a condition has a row below its option", Rows(_ask, SpeakRow.OptionCondition).Count == 1
                                                         && RowText(_ask, SpeakRow.OptionCondition, 0) == $"if {nameof(SpeakProbeCondition)}"
                                                         && Rows(_ask, SpeakRow.OptionCondition)[0].Indent > 0);
        Check("every option that leads somewhere has a wire", Wires().SequenceEqual([$"{_ask.Id}:0>{_sword.Id}", $"{_sword.Id}:0>{_ask.Id}"]));
        Check("lines carry no ports, options and the spare do", _graph.BoxFor(_ask.Id).GetOutputPortCount() == 3 && _graph.BoxFor(_ask.Id).GetInputPortCount() == 1);
        Check("a node created in code got an id on opening", !string.IsNullOrEmpty(_buy.Conditions[0].Id));
    }

    void TheHeaderSitsInsideTheBox() {
        var box = _graph.BoxFor(_ask.Id);
        var header = box.GetNode<Control>("Header");
        Check("the unused title bar takes no room", box.GetTitlebarHBox().Size.Y == 0);
        Check("so the header sits inside the box's outline", header.Position.Y >= 0 && header.Position.Y < 12);
    }

    async System.Threading.Tasks.Task SectionsAreAddedAndNamedApart() {
        var first = _graph.AddSection(new Vector2(40, 400));
        var second = _graph.AddSection(new Vector2(40, 600));
        await Settle();
        Check("a new section gets a name of its own", first.Name == "Section" && second.Name == "Section2");
        Check("and a first line to fill in", first.Lines.Count == 1 && Rows(first, SpeakRow.Line).Count == 1 && RowText(first, SpeakRow.Line, 0) == "(silent)");
        Check("an empty line warns that it does nothing", Rows(first, SpeakRow.Line)[0].Warning.Contains("says nothing"));
        Check("and a box where it was put", _graph.BoxFor(first.Id)?.PositionOffset == new Vector2(40, 400));

        _graph.DeleteBoxes([first.Id, second.Id]);
        await Settle();
        Check("sections can be deleted again", _dialogue.Sections.Count == 2 && _graph.BoxFor(first.Id) == null);
    }

    async System.Threading.Tasks.Task LinesTakeActionsAndConditions() {
        _graph.BoxFor(_ask.Id).GetNode<Button>("AddLine").EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        var added = _ask.Lines.Last();
        Check("the button below the lines adds one", _ask.Lines.Count == 3 && Rows(_ask, SpeakRow.Line).Count == 3);
        Check("with the speaker of the line before it", added.Speaker == "Smith");

        var action = _graph.AddAction(_ask.Id, _question.Id, typeof(SpeakProbeAction));
        var gate = _graph.AddLineCondition(_ask.Id, _question.Id, typeof(SpeakProbeCondition));
        await Settle();
        Check("a line takes actions and conditions", _question.Actions.SequenceEqual([action]) && _question.Conditions.SequenceEqual([gate]));
        var kinds = _graph.BoxFor(_ask.Id).GetChildren().OfType<GraphRow>().Select(r => r.Kind).ToList();
        Check("which show below it: its conditions, then its actions",
            kinds.Take(5).SequenceEqual([SpeakRow.Line, SpeakRow.Line, SpeakRow.LineCondition, SpeakRow.Action, SpeakRow.Line]));
        Check("and say what they are", RowText(_ask, SpeakRow.Action, 0) == $"▸ {nameof(SpeakProbeAction)}"
                                       && RowText(_ask, SpeakRow.LineCondition, 0) == $"if {nameof(SpeakProbeCondition)}");

        // Through the menu and the picker, as a user would.
        _graph.EmitSignal(GraphEdit.SignalName.NodeSelected, _graph.BoxFor(_ask.Id));
        _graph.BoxFor(_ask.Id).EmitSignal(SectionBox.SignalName.RowMenuRequested, _ask.Id, SpeakRow.Line, _question.Id, Vector2.Zero);
        var menu = _graph.GetChildren(true).OfType<PopupMenu>().First();
        var items = Enumerable.Range(0, menu.ItemCount).Select(i => menu.GetItemText(i)).Where(text => text != "").ToList();
        Check("a line's menu offers actions and conditions", items.SequenceEqual(["Move up", "Move down", "Add action…", "Add condition…", "Delete"]));
        menu.Hide();
        _graph.OnMenuIdPressed(DialogueGraphEdit.MenuAddAction);
        Check("the picker opens for actions only", _graph.Picker.Visible && Offered(_graph.Picker).Contains(typeof(SpeakProbeAction).FullName)
                                                 && !Offered(_graph.Picker).Contains(typeof(SpeakProbeCondition).FullName));
        _graph.Picker.Hide();
        _graph.OnTypeChosen(typeof(SpeakProbeAction).FullName);
        await Settle();
        Check("and what is chosen there is added to that line", _question.Actions.Count == 2 && _question.Actions[1] is SpeakProbeAction);

        _graph.BoxFor(_ask.Id).EmitSignal(SectionBox.SignalName.RowMenuRequested, _ask.Id, SpeakRow.Option, _buy.Id, Vector2.Zero);
        items = [.. Enumerable.Range(0, menu.ItemCount).Select(i => menu.GetItemText(i)).Where(text => text != "")];
        Check("an option's menu offers conditions but no actions", items.Contains("Add condition…") && !items.Contains("Add action…"));
        menu.Hide();
        _graph.OnMenuIdPressed(DialogueGraphEdit.MenuAddCondition);
        _graph.Picker.Hide();
        _graph.OnTypeChosen(typeof(SpeakProbeCondition).FullName);
        await Settle();
        Check("a condition chosen for an option lands on that option", _buy.Conditions.Count == 2
                                                                      && RowText(_ask, SpeakRow.OptionCondition, 1) == $"and {nameof(SpeakProbeCondition)}");
    }

    async System.Threading.Tasks.Task RowsAreOrderedAndDeleted() {
        var first = _question.Actions[0];
        _graph.MoveRow(_ask.Id, SpeakRow.Action, first.Id, 1);
        await Settle();
        Check("an action can be moved within its line", _question.Actions[1] == first);
        _graph.DeleteRow(_ask.Id, SpeakRow.Action, first.Id);
        _graph.DeleteRow(_ask.Id, SpeakRow.Action, _question.Actions[0].Id);
        _graph.DeleteRow(_ask.Id, SpeakRow.LineCondition, _question.Conditions[0].Id);
        _graph.DeleteRow(_ask.Id, SpeakRow.OptionCondition, _buy.Conditions[1].Id);
        await Settle();
        Check("actions and conditions can be deleted again", _question.Actions.Count == 0 && _question.Conditions.Count == 0 && _buy.Conditions.Count == 1);

        var extra = _ask.Lines[2];
        _graph.MoveRow(_ask.Id, SpeakRow.Line, extra.Id, -1);
        await Settle();
        Check("a line can be moved up", _ask.Lines[1] == extra && _ask.Lines[2] == _question);
        _graph.MoveRow(_ask.Id, SpeakRow.Line, _ask.Lines[0].Id, -1);
        Check("but not past the top", _ask.Lines[1] == extra);
        _graph.DeleteRow(_ask.Id, SpeakRow.Line, extra.Id);
        await Settle();
        Check("and deleted", _ask.Lines.Count == 2 && Rows(_ask, SpeakRow.Line).Count == 2);

        _graph.MoveRow(_ask.Id, SpeakRow.Option, _buy.Id, 1);
        await Settle();
        Check("moving an option moves its port and wire along", _ask.Options[1] == _buy && Wires().Contains($"{_ask.Id}:1>{_sword.Id}"));
        _graph.MoveRow(_ask.Id, SpeakRow.Option, _buy.Id, -1);
        await Settle();
    }

    async System.Threading.Tasks.Task WiresMakeAndLeadOptions() {
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _ask.Id, 2, _ask.Id, 0);
        await Settle();
        Check("a wire from the spare port adds an option", _ask.Options.Count == 3 && _ask.Options[2].TargetSectionId == _ask.Id && !_ask.Options[2].IsChoice);

        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _ask.Id, 1, _sword.Id, 0);
        await Settle();
        Check("a wire from an option's own port leads it somewhere", _ask.Options[1].TargetSectionId == _sword.Id
                                                                     && RowText(_ask, SpeakRow.Option, 1) == "“Nothing.” → Sword");

        _graph.EmitSignal(GraphEdit.SignalName.ConnectionToEmpty, _sword.Id, 1, new Vector2(900, 300));
        await Settle();
        var made = _dialogue.Sections.Last();
        Check("a wire dropped on the canvas makes a section there", _dialogue.Sections.Count == 3 && made.Name == "Section");
        Check("and a new option that leads to it", _sword.Options.Count == 2 && _sword.Options[1].TargetSectionId == made.Id);

        var before = _ask.Options.Count;
        var option = _graph.AddOption(_ask.Id);
        await Settle();
        Check("an option can also be added without a wire, leading nowhere yet", _ask.Options.Count == before + 1 && option.TargetSectionId == "");

        _graph.DeleteRow(_ask.Id, SpeakRow.Option, option.Id);
        _graph.DeleteRow(_ask.Id, SpeakRow.Option, _ask.Options[2].Id);
        _graph.DeleteRow(_sword.Id, SpeakRow.Option, _sword.Options[1].Id);
        _graph.DeleteBoxes([made.Id]);
        _ask.Options[1].TargetSectionId = "";
        _graph.SyncWithDialogue();
        await Settle();
        Check("and all of it taken back", Wires().SequenceEqual([$"{_ask.Id}:0>{_sword.Id}", $"{_sword.Id}:0>{_ask.Id}"]));
    }

    async System.Threading.Tasks.Task TheStartSectionIsMarked() {
        Check("the first section is where the dialogue starts", Text(_ask, "Header/Start") == "▶" && Text(_sword, "Header/Start") == "");
        _graph.SetStart(_sword.Id);
        await Settle();
        Check("another section can be made the start", _dialogue.StartSection == _sword && Text(_sword, "Header/Start") == "▶" && Text(_ask, "Header/Start") == "");
        _graph.SetStart(_ask.Id);
        await Settle();
    }

    // ---- inspector ---------------------------------------------------------------------------

    async System.Threading.Tasks.Task SelectingGoesToTheInspectorWithoutLooping() {
        _inspectorCalls = 0;
        _graph.Pick(_ask.Id);
        await Settle();
        Check("selecting a box inspects its section", ReferenceEquals(_lastEdited, _ask) && _inspectorCalls == 1);

        _graph.Pick(_ask.Id, SpeakRow.Line, _question.Id);
        await Settle();
        Check("picking a line inspects that line", ReferenceEquals(_lastEdited, _question) && _inspectorCalls == 2);
        Check("and marks its row", _graph.BoxFor(_ask.Id).PickedKind == SpeakRow.Line && _graph.BoxFor(_ask.Id).PickedId == _question.Id);

        _graph.Pick(_ask.Id, SpeakRow.Option, _buy.Id);
        await Settle();
        Check("picking an option inspects that option", ReferenceEquals(_lastEdited, _buy));
        _graph.Pick(_ask.Id, SpeakRow.OptionCondition, _buy.Conditions[0].Id);
        await Settle();
        Check("picking a condition inspects that condition", ReferenceEquals(_lastEdited, _buy.Conditions[0]));

        var calls = _inspectorCalls;
        _panel.Highlight(_sword.Lines[0]);
        await Settle();
        Check("inspecting a line elsewhere picks its row", _graph.BoxFor(_sword.Id).Selected && _graph.BoxFor(_sword.Id).PickedId == _sword.Lines[0].Id);
        Check("without going round in circles", _inspectorCalls <= calls + 1);
        Check("the panel owns the parts of its dialogue, and nothing else",
            _panel.Owns(_question) && _panel.Owns(_buy) && _panel.Owns(_buy.Conditions[0]) && _panel.Owns(_ask) && !_panel.Owns(new DialogueLine()));
    }

    async System.Threading.Tasks.Task InspectorEditsReachTheGraph() {
        _question.Text = "Well?";
        _buy.Text = "A blade.";
        _ask.Name = "Question";
        _panel.OnInspectorEdited();
        await Settle();
        Check("a text typed in the Inspector shows on the line's row", RowText(_ask, SpeakRow.Line, 1) == "Smith: Well?");
        Check("and on the option's", RowText(_ask, SpeakRow.Option, 0) == "“A blade.” → Sword");
        Check("a renamed section shows on its box and on the options that lead to it", Text(_ask, "Header/SectionName") == "Question"
                                                                                       && RowText(_sword, SpeakRow.Option, 0) == "→ Question");
        Check("and the dialogue counts as unsaved", _panel.HasUnsavedChanges(_dialogue));

        _ask.Lines.Add(new DialogueLine { Text = "Added elsewhere." });
        _panel.OnInspectorEdited();
        await Settle();
        Check("a line added in the Inspector gets a row", Rows(_ask, SpeakRow.Line).Count == 3);
        _ask.Lines.RemoveAt(2);
        _question.Text = "What do you need?";
        _buy.Text = "A sword.";
        _ask.Name = "Ask";
        _panel.OnInspectorEdited();
        await Settle();
    }

    // ---- undo --------------------------------------------------------------------------------

    async System.Threading.Tasks.Task UndoRestoresTheStructure() {
        var before = _graph.TakeSnapshot();
        var condition = _buy.Conditions[0];

        var added = _graph.AddSection(new Vector2(40, 400));
        _graph.LeadTo(_sword, 1, added);
        _graph.SetStart(added.Id);
        _graph.AddAction(_ask.Id, _question.Id, typeof(SpeakProbeAction));
        _graph.DeleteRow(_ask.Id, SpeakRow.OptionCondition, condition.Id);
        _graph.DeleteRow(_ask.Id, SpeakRow.Option, _buy.Id);
        _graph.DeleteRow(_ask.Id, SpeakRow.Line, _question.Id);
        await Settle();

        _graph.RestoreSnapshot(before);
        await Settle();
        Check("undo takes an added section away again", _dialogue.Sections.SequenceEqual([_ask, _sword]) && _graph.BoxFor(added.Id) == null);
        Check("and brings a deleted line back as the same object, without what was added to it", _ask.Lines[1] == _question && _question.Actions.Count == 0);
        Check("a deleted option comes back with the condition it had", _ask.Options[0] == _buy && _buy.Conditions.SequenceEqual([condition]));
        Check("with every wire where it was", Wires().SequenceEqual([$"{_ask.Id}:0>{_sword.Id}", $"{_sword.Id}:0>{_ask.Id}"]));
        Check("and the start it had", _dialogue.StartSection == _ask);
    }

    async System.Threading.Tasks.Task DeletingASectionLeavesItsOptionsFlagged() {
        var before = _graph.TakeSnapshot();
        _graph.DeleteBoxes([_sword.Id]);
        await Settle();

        var row = Rows(_ask, SpeakRow.Option)[0];
        Check("an option to a deleted section stays", _ask.Options.Count == 2 && row.Text.EndsWith("→ ?"));
        Check("and warns that it leads nowhere", row.Warning.Contains("leads nowhere"));
        Check("with no wire left to draw", Wires().Count == 0);

        _graph.RestoreSnapshot(before);
        await Settle();
    }

    async System.Threading.Tasks.Task ReroutesLeadWiresAround() {
        var before = _graph.TakeSnapshot();
        var rejected = new List<string>();
        _graph.EditRejected += rejected.Add;

        var reroute = _graph.AddReroute(new Vector2(300, 60), _ask.Id, 0);
        await Settle();
        Check("a reroute put into a wire is where that wire now ends", _dialogue.Reroutes.SequenceEqual([reroute]) && _buy.TargetSectionId == reroute.Id
                                                                      && Wires().Contains($"{_ask.Id}:0>{reroute.Id}"));
        Check("and leads on to where the wire went", reroute.TargetId == _sword.Id && Wires().Contains($"{reroute.Id}:0>{_sword.Id}"));
        Check("the option's row still names the section at the end", RowText(_ask, SpeakRow.Option, 0) == "“A sword.” → Sword" && Rows(_ask, SpeakRow.Option)[0].Warning == "");
        Check("and the dialogue still goes there", _dialogue.Destination(_buy.TargetSectionId) == _sword && _dialogue.Validate().Length == 0);

        _graph.LeadOn(reroute, reroute.Id);
        Check("a reroute cannot lead back to itself", reroute.TargetId == _sword.Id && rejected.Count == 1);

        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, reroute.Id, 0, _ask.Id, 0);
        await Settle();
        Check("leading it elsewhere takes the option along, and turns it round to face left",
            RowText(_ask, SpeakRow.Option, 0) == "“A sword.” → Ask" && _graph.RerouteBoxFor(reroute.Id).Flipped);

        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { reroute.Id });
        await Settle();
        Check("deleting a reroute leaves the wire whole", _dialogue.Reroutes.Count == 0 && _buy.TargetSectionId == _ask.Id);

        _graph.EditRejected -= rejected.Add;
        _graph.RestoreSnapshot(before);
        await Settle();
        Check("undo puts it all back", _buy.TargetSectionId == _sword.Id && Wires().SequenceEqual([$"{_ask.Id}:0>{_sword.Id}", $"{_sword.Id}:0>{_ask.Id}"]));
    }

    // ---- blackboard --------------------------------------------------------------------------

    async System.Threading.Tasks.Task NodesLinkToTheBlackboardBesideTheGraph() {
        var blackboard = _panel.Blackboard;
        var condition = (SpeakProbeCondition) _buy.Conditions[0];
        Check("the panel has a blackboard showing the dialogue", blackboard != null && ReferenceEquals(blackboard.Source, _dialogue));

        var level = blackboard.CreateEntryForParam(condition, nameof(SpeakProbeCondition.Value), "level");
        await Settle();
        Check("an entry made for a condition's parameter lands on the dialogue", level != null && _dialogue.Blackboard.Contains(level));
        Check("and the parameter is linked to it", condition.Value.EntryId == level?.Id);

        blackboard.SetEntryType(level.Id, Variant.Type.String);
        await Settle();
        Check("a link that no longer fits warns on the condition's row", Rows(_ask, SpeakRow.OptionCondition)[0].Warning.Contains("expects int"));

        blackboard.RemoveEntry(level.Id);
        blackboard.UnlinkParam(condition, nameof(SpeakProbeCondition.Value));
        await Settle();
        Check("and the warning goes once the link is gone", Rows(_ask, SpeakRow.OptionCondition)[0].Warning == "");
    }

    // ---- saving ------------------------------------------------------------------------------

    async System.Threading.Tasks.Task SavingAndRevertingFollowTheFile() {
        const string path = "user://misspeak_editor_dialogue.tres";
        ResourceSaver.Save(_dialogue, path);
        _dialogue.TakeOverPath(path);
        _panel.SaveUnsaved();
        Check("saving clears the unsaved mark", !_panel.HasUnsavedChanges(_dialogue) && _panel.UnsavedPaths().Length == 0);

        var added = _graph.AddSection(new Vector2(40, 400), "Extra");
        _graph.AddReroute(new Vector2(300, 60), _ask.Id, 0);
        _question.Text = "Changed";
        _panel.OnInspectorEdited();
        await Settle();
        Check("an edit after saving marks it unsaved again", _panel.UnsavedPaths().SequenceEqual([path]));

        _panel.RevertDialogue();
        await Settle();
        var ask = _dialogue.FindSectionByName("Ask");
        Check("reverting goes back to what the file holds", _dialogue.Sections.Count == 2 && _dialogue.FindSectionByName("Extra") == null
                                                            && _dialogue.Reroutes.Count == 0 && ask?.Lines[1].Text == "What do you need?");
        Check("keeps the dialogue resource itself", ReferenceEquals(_panel.Dialogue, _dialogue) && _graph.BoxFor(added.Id) == null);
        Check("and leaves nothing unsaved", !_panel.HasUnsavedChanges(_dialogue));
    }

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>
    /// The editor half of the debug channel, fed the messages a running game sends: the section the
    /// watched runner is in stands out, the line it is at is tinted, and the wires say how it got
    /// there and where it may go.
    /// </summary>
    async System.Threading.Tasks.Task TheGraphShowsWhereARunningDialogueIs() {
        var router = new RunnerDebugRouter(MisspeakDebug.Prefix);
        var path = _dialogue.ResourcePath;
        var ask = _dialogue.FindSectionByName("Ask");
        var sword = _dialogue.FindSectionByName("Sword");
        var overlay = _graph.WireOverlay;

        Check("a runner announcing itself is accepted, in the form the editor is handed",
            router.Handle("misspeak:register", [42L, path, "Smith"], _panel) && router.Selected == 42);

        router.Handle("misspeak:state", [42L, ask.Id, ask.Lines[1].Id, (int) DialogueWait.Choice, 1, ""], _panel);
        var askBox = _graph.BoxFor(ask.Id);
        Check("the section the dialogue is in stands out", askBox.IsCurrent && askBox.Modulate.A == 1f
                                                         && askBox.GetThemeStylebox("panel") is StyleBoxFlat outline && outline.BorderColor == GraphRow.Running);
        Check("the others fade", !_graph.BoxFor(sword.Id).IsCurrent && _graph.BoxFor(sword.Id).Modulate.A == SectionBox.DimmedAlpha);
        Check("the line it is at is tinted, the others are not",
            askBox.Rows(SpeakRow.Line).Select(r => r.LiveStatus).SequenceEqual([null, MissStatus.Running]));
        Check("the ways out that lead somewhere are highlighted as not taken yet",
            overlay.Wires.SequenceEqual([new LiveWire(ask.Id, 0, sword.Id, Taken: false)]));

        router.Handle("state", [42L, sword.Id, sword.Lines[0].Id, (int) DialogueWait.Advance, 2, ask.Options[0].Id], _panel);
        Check("a change of section moves the highlight, in the bare form too", _graph.BoxFor(sword.Id).IsCurrent && !_graph.BoxFor(ask.Id).IsCurrent
                                                                              && _graph.BoxFor(ask.Id).Rows(SpeakRow.Line).All(r => r.LiveStatus == null));
        Check("the option the dialogue came in by is highlighted as taken",
            overlay.Wires.Where(w => w.Taken).SequenceEqual([new LiveWire(ask.Id, 0, sword.Id, true)])
            && overlay.Wires.Where(w => !w.Taken).SequenceEqual([new LiveWire(sword.Id, 0, ask.Id, false)]));

        _graph.AddSection(new Vector2(40, 500), "Later");
        await Settle();
        Check("an edit that rebuilds the graph keeps the live picture", _graph.BoxFor(sword.Id).IsCurrent && overlay.Wires.Count == 2);

        router.Handle("misspeak:state", [42L, "", "", (int) DialogueWait.Nothing, 3, ""], _panel);
        Check("between talks nothing stands out and nothing fades",
            !_graph.BoxFor(sword.Id).IsCurrent && _graph.BoxFor(ask.Id).Modulate.A == 1f && overlay.Wires.Count == 0);

        router.Handle("misspeak:state", [7L, ask.Id, "", 0, 1, ""], _panel);
        Check("a runner the router was never told about makes it ask again", router.MissesRunners && !_graph.BoxFor(ask.Id).IsCurrent);

        router.Handle("misspeak:state", [42L, ask.Id, ask.Lines[0].Id, (int) DialogueWait.Advance, 4, ""], _panel);
        router.Reset(_panel);
        Check("when the game stops, the graph looks as it does while editing",
            !_graph.BoxFor(ask.Id).IsCurrent && _graph.BoxFor(sword.Id).Modulate.A == 1f && overlay.Wires.Count == 0
            && _graph.BoxFor(ask.Id).Rows(SpeakRow.Line).All(r => r.LiveStatus == null));
    }

    // ---- sources -----------------------------------------------------------------------------

    /// <summary>
    /// A delegate-backed signal connection dies with the assembly reload that pressing play causes.
    /// The rule is held at the source level: no <c>+=</c> in the plugin classes, and no
    /// <c>Callable.From</c> anywhere under editor/.
    /// </summary>
    void EditorSourcesDoNotWireDelegates() {
        const string editor = "res://addons/misspeak/editor";
        string[] plugins = [$"{editor}/MisspeakEditorPlugin.cs", $"{editor}/DialogueInspectorPlugin.cs", $"{editor}/DialogueTranslationParser.cs"];

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var path in DirAccess.GetFilesAt(editor).Where(f => f.EndsWith(".cs")).Select(f => $"{editor}/{f}")) {
            using var source = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (source == null) continue;
            scanned++;

            var lines = source.GetAsText().Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                var line = lines[i].Trim();
                if (line.StartsWith("//")) continue;
                if (line.Contains("Callable.From") || (plugins.Contains(path) && line.Contains("+="))) offenders.Add($"{path}:{i + 1}");
            }
        }
        foreach (var offender in offenders) GD.PrintErr($"      delegate wiring at {offender}");

        Check("the source check read the editor sources", scanned >= 6);
        Check("no editor source wires a signal through a delegate", offenders.Count == 0);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Full names of the types a picker lists, top to bottom.</summary>
    static List<string> Offered(CreateNodeDialog picker) => [.. Items(picker.FindChildren("*", nameof(Tree), true, false).OfType<Tree>().First().GetRoot())
        .Select(item => item.GetMetadata(0).AsString()).Where(name => !string.IsNullOrEmpty(name))];

    static IEnumerable<TreeItem> Items(TreeItem parent) {
        for (var item = parent?.GetFirstChild(); item != null; item = item.GetNext()) {
            yield return item;
            foreach (var nested in Items(item)) yield return nested;
        }
    }

    string Text(DialogueSection section, string path) => _graph.BoxFor(section.Id)?.GetNodeOrNull<Label>(path)?.Text;

    List<GraphRow> Rows(DialogueSection section, string kind)
        => [.. _graph.BoxFor(section.Id).Rows(kind).Where(r => !r.IsQueuedForDeletion())];

    string RowText(DialogueSection section, string kind, int index) => Rows(section, kind)[index].Text;

    List<string> Wires() => [.. _graph.GetConnectionList()
        .Select(c => $"{c["from_node"].AsStringName()}:{c["from_port"].AsInt32()}>{c["to_node"].AsStringName()}")
        .OrderBy(w => w.StartsWith($"{_ask.Id}:") ? 0 : 1).ThenBy(w => w)];

    async System.Threading.Tasks.Task Settle() {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}
#endif
