using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// An authored dialogue, saved as a single <c>.tres</c>: sections of lines, and the options that
/// lead from one section to the next. This is a definition: it is never run directly, and any
/// number of runners may play it, each on its own copy.
/// </summary>
[GlobalClass, Tool]
public partial class Dialogue : MissResource, IBlackboardSource {
    [Export]
    public Godot.Collections.Array<DialogueSection> Sections { get; set; } = [];

    /// <summary>Waypoints for the wires of a graph editor. See <see cref="Destination"/>.</summary>
    [Export]
    public Godot.Collections.Array<MissReroute> Reroutes { get; set; } = [];

    /// <summary>The section a dialogue starts with. Left empty, it is the first one.</summary>
    [Export]
    public string StartSectionId { get; set; } = "";

    [Export(PropertyHint.MultilineText)]
    public string Description { get; set; } = "";

    /// <summary>
    /// On, a line is translated with its speaker as the translation context, so the same words can
    /// be translated differently for different characters. Needs translations that carry contexts,
    /// i.e. gettext (<c>.po</c>) — a CSV translation has none, and would find nothing. Off by default.
    /// </summary>
    [Export]
    public bool SpeakerAsTranslationContext { get; set; }

    /// <summary>
    /// The values this dialogue works with: its actions and conditions link to entries by id through
    /// <see cref="BbParam{T}"/>, and its texts name them in <c>{braces}</c>.
    /// </summary>
    [Export]
    public Godot.Collections.Array<BlackboardEntry> Blackboard { get; set; } = [];

    public DialogueSection StartSection => FindSection(StartSectionId) ?? Sections.FirstOrDefault(s => s != null);

    public DialogueSection FindSection(string id) {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var section in Sections) {
            if (section?.Id == id) return section;
        }
        return null;
    }

    public MissReroute FindReroute(string id) => MissReroute.Find(Reroutes, id);

    /// <summary>
    /// The section an option's target stands for: the section of that id, or the one at the end of
    /// the reroutes that start there. Null when the id names nothing, or the reroutes end nowhere
    /// or run in a circle.
    /// </summary>
    public DialogueSection Destination(string targetId) => FindSection(MissReroute.Resolve(Reroutes, targetId));

    public DialogueSection FindSectionByName(string name) {
        foreach (var section in Sections) {
            if (section != null && section.Name == name) return section;
        }
        return null;
    }

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Blackboard) or nameof(StartSectionId) or nameof(Reroutes)) {
            property["usage"] = (int) PropertyUsageFlags.Storage;
        }
    }

    /// <summary>The context a line's text is translated in: its speaker if the dialogue says so, else none.</summary>
    public string ContextOf(DialogueLine line) => SpeakerAsTranslationContext ? line?.Speaker ?? "" : "";

    /// <summary>Every node the dialogue holds: what its lines run and check, and what its options check.</summary>
    public IEnumerable<MissNode> AllNodes() {
        foreach (var section in Sections) {
            if (section == null) continue;
            foreach (var line in section.Lines) {
                if (line == null) continue;
                foreach (var node in line.Actions.Concat(line.Conditions).SelectMany(Walk)) yield return node;
            }
            foreach (var option in section.Options) {
                if (option == null) continue;
                foreach (var node in option.Conditions.SelectMany(Walk)) yield return node;
            }
        }
    }

    static IEnumerable<MissNode> Walk(MissNode node) {
        if (node == null) yield break;
        yield return node;
        foreach (var child in node.Children) {
            foreach (var descendant in Walk(child)) yield return descendant;
        }
    }

    IEnumerable<IBbParamHost> IBlackboardSource.ParamHosts() => AllNodes();

    /// <summary>
    /// Gives every node that has none an id. The blackboard editor tells nodes apart by it, and a node
    /// created in the Inspector starts without one.
    /// </summary>
    public void EnsureNodeIds() {
        foreach (var node in AllNodes()) node.EnsureId();
    }

    public string[] Validate() {
        var problems = new List<string>();
        if (!Sections.Any(s => s != null)) {
            problems.Add("The dialogue has no sections.");
            return [.. problems];
        }
        if (!string.IsNullOrEmpty(StartSectionId) && FindSection(StartSectionId) == null) {
            problems.Add("The start section no longer exists.");
        }

        var number = 0;
        foreach (var section in Sections) {
            number++;
            if (section == null) continue;
            var name = string.IsNullOrEmpty(section.Name) ? $"section #{number}" : section.Name;

            for (var i = 0; i < section.Options.Count; i++) {
                var option = section.Options[i];
                if (option == null) problems.Add($"{name}: option #{i + 1} is empty.");
                else if (!string.IsNullOrEmpty(option.TargetSectionId) && Destination(option.TargetSectionId) == null) {
                    problems.Add($"{name}: option #{i + 1} leads nowhere — to a section that no longer exists, or a reroute that ends nowhere.");
                }
            }
            for (var i = 0; i < section.Lines.Count; i++) {
                var line = section.Lines[i];
                if (line == null) problems.Add($"{name}: line #{i + 1} is empty.");
                else if (string.IsNullOrEmpty(line.Text) && !line.Actions.Any(a => a != null)) {
                    problems.Add($"{name}: line #{i + 1} says nothing and does nothing.");
                }
            }
            if (!section.Lines.Any(l => l != null) && !section.Options.Any(o => o != null)) {
                problems.Add($"{name}: has no lines and leads nowhere.");
            }
        }

        foreach (var node in AllNodes()) {
            foreach (var warning in node.GetConfigurationWarnings()) problems.Add($"{node.GetLabel()}: {warning}");
            foreach (var problem in LinkProblems(node)) problems.Add($"{node.GetLabel()}: {problem}");
        }
        return [.. problems];
    }

    /// <summary>Parameters of <paramref name="node"/> linked to entries that are gone or of the wrong type.</summary>
    public IEnumerable<string> LinkProblems(MissNode node) {
        if (node == null) yield break;
        foreach (var (member, param) in node.BlackboardParams()) {
            if (!param.IsLinked) continue;

            var entry = this.FindEntry(param.EntryId);
            if (entry == null) {
                yield return $"{member.Name} is linked to a blackboard entry that no longer exists ({param.EntryName})";
            }
            else if (!BbTypes.Accepts(member.ValueType, entry)) {
                var expected = BbTypes.Describe(member.ValueType);
                yield return $"{member.Name} expects {BbTypes.Label(expected.VariantType, expected.ClassName)}, " +
                             $"but {entry.Name} is {entry.TypeLabel}";
            }
        }
    }

    /// <summary>
    /// Every text of the dialogue that is shown to the player, with the context it is translated in:
    /// speakers, lines and choices, each once. This is what goes into a translation template.
    /// </summary>
    public IEnumerable<(string Text, string Context)> TranslatableTexts() {
        var seen = new HashSet<(string, string)>();
        foreach (var section in Sections) {
            if (section == null) continue;
            foreach (var line in section.Lines) {
                if (line == null) continue;
                if (!string.IsNullOrEmpty(line.Speaker) && seen.Add((line.Speaker, ""))) yield return (line.Speaker, "");
                if (!string.IsNullOrEmpty(line.Text) && seen.Add((line.Text, ContextOf(line)))) yield return (line.Text, ContextOf(line));
            }
            foreach (var option in section.Options) {
                if (option != null && option.IsChoice && seen.Add((option.Text, ""))) yield return (option.Text, "");
            }
        }
    }
}
