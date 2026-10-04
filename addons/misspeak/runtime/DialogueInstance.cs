using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Misscore;

namespace Misspeak;

/// <summary>What a running dialogue is waiting for.</summary>
public enum DialogueWait {
    /// <summary>Nothing: no dialogue is running.</summary>
    Nothing,

    /// <summary>The actions of the line that is next, which run before its text is shown.</summary>
    Actions,

    /// <summary>The player, to carry on after the line shown.</summary>
    Advance,

    /// <summary>The player, to pick one of the choices offered.</summary>
    Choice,
}

/// <summary>
/// One runner's private copy of a <see cref="Dialogue"/>: the cloned actions and conditions of every
/// line and option, and where the dialogue is.
/// </summary>
public sealed partial class DialogueInstance {
    sealed class RuntimeLine {
        public DialogueLine Definition;
        public MissNode[] Actions;
        public MissNode[] Conditions;
    }

    sealed class RuntimeOption {
        public DialogueOption Definition;
        public MissNode[] Conditions;
    }

    sealed class RuntimeSection {
        public DialogueSection Definition;
        public RuntimeLine[] Lines;
        public RuntimeOption[] Options;
    }

    public Dialogue Definition { get; }

    /// <summary>A dialogue began.</summary>
    public event Action Started;

    /// <summary>A line with text is to be shown; <see cref="Speaker"/> and <see cref="Text"/> hold it.</summary>
    public event Action<DialogueLine> LineShown;

    /// <summary>The player has to pick; <see cref="Choices"/> holds the texts.</summary>
    public event Action<IReadOnlyList<string>> ChoicesOffered;

    /// <summary>The dialogue is over: it ran out of sections, or was cancelled.</summary>
    public event Action Finished;

    /// <summary>
    /// Applied to every speaker and text before the placeholders are filled in, with the text and
    /// its translation context — a runner hooks translation in here.
    /// </summary>
    public Func<string, string, string> Translate { get; set; } = (text, _) => text;

    public bool Active => _current != null;

    /// <summary>The section the dialogue is in, as authored — or null while none is running.</summary>
    public DialogueSection Current => _current?.Definition;

    /// <summary>The line being shown, as authored — or null while none is.</summary>
    public DialogueLine CurrentLine => _shown?.Definition;

    public DialogueWait Waiting { get; private set; }

    /// <summary>Speaker of the line being shown, or empty.</summary>
    public string Speaker { get; private set; } = "";

    /// <summary>Text of the line being shown, or empty.</summary>
    public string Text { get; private set; } = "";

    /// <summary>The texts of the choices being offered, in order; empty unless waiting for a choice.</summary>
    public IReadOnlyList<string> Choices { get; private set; } = [];

    /// <summary>This instance's copies of the actions of <paramref name="line"/>, in order.</summary>
    public IReadOnlyList<MissNode> ActionsOf(DialogueLine line) {
        if (line == null) return [];
        foreach (var section in _sections.Values) {
            foreach (var candidate in section.Lines) {
                if (ReferenceEquals(candidate.Definition, line)) return candidate.Actions;
            }
        }
        return [];
    }

    readonly Dictionary<string, RuntimeSection> _sections = [];
    RuntimeSection _current;

    /// <summary>The line whose turn it is, and whether its conditions were already found to hold.</summary>
    int _line;
    bool _lineAdmitted;

    /// <summary>The action to run next, and whether it was left Running by the tick before.</summary>
    int _action;
    bool _actionRunning;

    /// <summary>The line on show, and the one that follows it — already admitted — or the end of the section.</summary>
    RuntimeLine _shown;
    int _next;

    List<RuntimeOption> _offered = [];

    DialogueInstance(Dialogue definition) {
        Definition = definition;
        foreach (var section in definition.Sections) {
            if (section == null || _sections.ContainsKey(section.Id)) continue;

            _sections[section.Id] = new RuntimeSection {
                Definition = section,
                Lines = [.. section.Lines.Where(l => l != null).Select(line => new RuntimeLine {
                    Definition = line,
                    Actions = Clones(line.Actions),
                    Conditions = Clones(line.Conditions),
                })],
                Options = [.. section.Options.Where(o => o != null).Select(option => new RuntimeOption {
                    Definition = option,
                    Conditions = Clones(option.Conditions),
                })],
            };
        }
    }

    static MissNode[] Clones(IEnumerable<MissNode> nodes) => [.. nodes.Where(n => n != null).Select(n => n.CloneRuntime())];

    public static DialogueInstance Create(Dialogue definition) => definition?.StartSection == null ? null : new DialogueInstance(definition);

    /// <summary>
    /// Begins the dialogue at its start section — or at the given one — and works on it right away,
    /// so that the first line is there when this returns unless an action is still running. A
    /// dialogue already under way is cancelled first. False when there is no such section.
    /// </summary>
    public bool Start(MissContext ctx, string sectionId = null) {
        var start = string.IsNullOrEmpty(sectionId) ? Definition.StartSection : Definition.FindSection(sectionId);
        if (start == null || !_sections.TryGetValue(start.Id, out var section)) return false;

        Cancel(ctx);
        Enter(section);
        Started?.Invoke();
        if (ReferenceEquals(_current, section)) Work(ctx);
        return true;
    }

    /// <summary>One tick: carries on with the actions of the line that is next, if the dialogue is waiting for them.</summary>
    public void Tick(MissContext ctx) {
        if (Active && Waiting == DialogueWait.Actions) Work(ctx);
    }

    void Enter(RuntimeSection section) {
        _current = section;
        TurnTo(0, admitted: false);
        Hide();
    }

    void TurnTo(int line, bool admitted) {
        _line = line;
        _lineAdmitted = admitted;
        _action = 0;
        _actionRunning = false;
        Waiting = DialogueWait.Actions;
    }

    void Hide() {
        _shown = null;
        _offered = [];
        Speaker = "";
        Text = "";
        Choices = [];
    }

    /// <summary>
    /// Works through the section's lines from the one whose turn it is: skips those whose conditions
    /// do not hold, runs their actions, and stops at the first one that has something to say — or at
    /// an action that is still running.
    /// </summary>
    void Work(MissContext ctx) {
        var section = _current;
        while (_line < section.Lines.Length) {
            var line = section.Lines[_line];
            if (!_lineAdmitted && !Holds(line.Conditions, line.Definition.Mode, ctx)) {
                TurnTo(_line + 1, admitted: false);
                continue;
            }
            _lineAdmitted = true;

            if (!RunActions(line, section, ctx)) return;

            if (!string.IsNullOrEmpty(line.Definition.Text)) {
                Show(line, ctx);
                return;
            }
            TurnTo(_line + 1, admitted: false);
        }
        Conclude(ctx);
    }

    /// <summary>Runs the line's actions as far as they go. True once they are all through.</summary>
    bool RunActions(RuntimeLine line, RuntimeSection section, MissContext ctx) {
        while (_action < line.Actions.Length) {
            var action = line.Actions[_action];
            var resumed = _actionRunning;
            if (!resumed) action.Begin(ctx);
            var status = action.Execute(ctx);

            // The action itself cancelled or restarted the dialogue. That interrupted it if it was
            // being resumed; one begun on this very tick is closed here.
            if (!ReferenceEquals(_current, section) || !ReferenceEquals(section.Lines[_line], line) || Waiting != DialogueWait.Actions) {
                if (!resumed) {
                    if (status == MissStatus.Running) action.Interrupt(ctx);
                    else action.AfterRun(ctx);
                }
                return false;
            }
            if (status == MissStatus.Running) {
                _actionRunning = true;
                return false;
            }

            action.AfterRun(ctx);
            _actionRunning = false;
            _action++;
        }
        return true;
    }

    /// <summary>
    /// Shows a line. If no further line of the section is going to follow, it is the last one, and
    /// the choices are offered along with it.
    /// </summary>
    void Show(RuntimeLine line, MissContext ctx) {
        var section = _current;
        _next = _line + 1;
        while (_next < section.Lines.Length && !Holds(section.Lines[_next].Conditions, section.Lines[_next].Definition.Mode, ctx)) _next++;

        // Everything is in place before anyone is told: a listener may answer on the spot.
        _shown = line;
        _offered = _next < section.Lines.Length ? [] : Offerable(section, ctx);
        Waiting = _offered.Count > 0 ? DialogueWait.Choice : DialogueWait.Advance;
        Fill(ctx);
        Announce(section, line);
    }

    /// <summary>
    /// The lines are through and none is on show: offers the choices by themselves, or — with none
    /// to offer — moves on. The section arrived at that way is worked on by the next tick, so
    /// sections in which nothing is said can never spin within one.
    /// </summary>
    void Conclude(MissContext ctx) {
        var section = _current;
        var choices = Offerable(section, ctx);
        if (choices.Count == 0) {
            Follow(section.Options.FirstOrDefault(option => !option.Definition.IsChoice && Holds(option.Conditions, option.Definition.Mode, ctx)), ctx, atOnce: false);
            return;
        }

        Hide();
        _offered = choices;
        Waiting = DialogueWait.Choice;
        Fill(ctx);
        Announce(section, null);
    }

    List<RuntimeOption> Offerable(RuntimeSection section, MissContext ctx)
        => [.. section.Options.Where(option => option.Definition.IsChoice && Holds(option.Conditions, option.Definition.Mode, ctx))];

    /// <summary>Puts what is on show into words: translated, and with the placeholders filled in.</summary>
    void Fill(MissContext ctx) {
        Speaker = _shown == null ? "" : Words(_shown.Definition.Speaker, "", ctx);
        Text = _shown == null ? "" : Words(_shown.Definition.Text, Definition.ContextOf(_shown.Definition), ctx);
        Choices = [.. _offered.Select(option => Words(option.Definition.Text, "", ctx))];
    }

    void Announce(RuntimeSection section, RuntimeLine line) {
        if (line != null) LineShown?.Invoke(line.Definition);
        if (ReferenceEquals(_current, section) && ReferenceEquals(_shown, line) && Waiting == DialogueWait.Choice) ChoicesOffered?.Invoke(Choices);
    }

    /// <summary>
    /// Says what is on show once more, in words made afresh — after the language changed, say. Does
    /// nothing while nothing is on show.
    /// </summary>
    public void Reshow(MissContext ctx) {
        if (!Active || Waiting is not (DialogueWait.Advance or DialogueWait.Choice)) return;

        Fill(ctx);
        Announce(_current, _shown);
    }

    /// <summary>
    /// Carries on after a line that offered no choice: to the section's next line, or past its last
    /// one to the first option whose conditions hold now — or to the end if there is none. False
    /// unless the dialogue is waiting for exactly that.
    /// </summary>
    public bool Advance(MissContext ctx) {
        if (!Active || Waiting != DialogueWait.Advance) return false;

        if (_next < _current.Lines.Length) {
            Hide();
            TurnTo(_next, admitted: true);
            Work(ctx);
        }
        else {
            Follow(_current.Options.FirstOrDefault(option => !option.Definition.IsChoice && Holds(option.Conditions, option.Definition.Mode, ctx)), ctx, atOnce: true);
        }
        return true;
    }

    /// <summary>Picks one of the <see cref="Choices"/>, by its place among them. False when there is no such choice to pick.</summary>
    public bool Choose(int index, MissContext ctx) {
        if (!Active || Waiting != DialogueWait.Choice || index < 0 || index >= _offered.Count) return false;

        Follow(_offered[index], ctx, atOnce: true);
        return true;
    }

    void Follow(RuntimeOption option, MissContext ctx, bool atOnce) {
        // Through any reroutes, which are only there for the graph.
        var targetId = Definition.Destination(option?.Definition.TargetSectionId)?.Id;
        if (string.IsNullOrEmpty(targetId) || !_sections.TryGetValue(targetId, out var target)) {
            End();
            return;
        }

        Enter(target);
        if (atOnce) Work(ctx);
    }

    void End() {
        _current = null;
        _actionRunning = false;
        Waiting = DialogueWait.Nothing;
        Hide();
        Finished?.Invoke();
    }

    static bool Holds(MissNode[] conditions, ListMode mode, MissContext ctx) {
        if (conditions.Length == 0) return true;

        var all = mode == ListMode.Sequence;
        foreach (var condition in conditions) {
            var holds = Holds(condition, ctx);
            if (holds != all) return holds;
        }
        return all;
    }

    /// <summary>Checked afresh every time: a condition is never resumed, so one still running is dropped.</summary>
    static bool Holds(MissNode condition, MissContext ctx) {
        condition.Begin(ctx);
        var status = condition.Execute(ctx);
        if (status == MissStatus.Running) condition.Interrupt(ctx);
        else condition.AfterRun(ctx);
        return status == MissStatus.Success;
    }

    /// <summary>
    /// Abandons the action a line left running. The dialogue stays where it is: ticked again, it
    /// starts that action over.
    /// </summary>
    public void Interrupt(MissContext ctx) {
        if (_current == null || !_actionRunning) return;

        _actionRunning = false;
        _current.Lines[_line].Actions[_action].Interrupt(ctx);
    }

    /// <summary>Ends the dialogue where it is, interrupting what it left running.</summary>
    public void Cancel(MissContext ctx) {
        if (!Active) return;

        Interrupt(ctx);
        End();
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    /// <summary>Translates a text and puts blackboard values in place of its <c>{entry}</c> placeholders.</summary>
    string Words(string text, string context, MissContext ctx) {
        if (string.IsNullOrEmpty(text)) return "";

        var translated = Translate?.Invoke(text, context ?? "") ?? text;
        var board = ctx.Blackboard;
        if (board == null || !translated.Contains('{')) return translated;

        // A name the blackboard does not know is left as written, braces and all.
        return Placeholder().Replace(translated, match => {
            var name = match.Groups[1].Value;
            return board.TryGetVariant(name, out var value) ? value.ToString() : match.Value;
        });
    }
}
