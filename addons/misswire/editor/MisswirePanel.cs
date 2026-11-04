#if TOOLS
using System.Collections.Generic;
using Godot;
using Misscore.Editor;

namespace Misswire.Editor;

/// <summary>
/// The dock: which mediator of the edited scene is shown, and its graph. It follows the edited
/// scene by itself; selecting a mediator in the scene tree shows that one.
/// </summary>
[Tool]
public partial class MisswirePanel : VBoxContainer {
    GodotObject _graph;
    OptionButton _choice;
    Label _message;
    Label _empty;
    readonly Godot.Collections.Array<Node> _mediators = [];

    public MisswireGraphEdit Graph => ReloadSafe.Get<MisswireGraphEdit>(ref _graph);

    public override void _Ready() {
        CustomMinimumSize = new Vector2(0, 260);

        var bar = new HBoxContainer { Name = "Toolbar" };
        bar.AddChild(new Label { Text = "Mediator" });
        _choice = new OptionButton { Name = "Mediator", CustomMinimumSize = new Vector2(180, 0), FitToLongestItem = false };
        _choice.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnChoice));
        bar.AddChild(_choice);
        var add = new Button { Name = "AddSelected", Text = "Add selected nodes", TooltipText = "Show the nodes selected in the scene tree; dragging them in works too" };
        add.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddSelected));
        bar.AddChild(add);
        _message = new Label { Name = "Message", SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalAlignment = HorizontalAlignment.Right };
        _message.AddThemeColorOverride("font_color", new Color("#ffd24a"));
        bar.AddChild(_message);
        AddChild(bar);

        var graph = new MisswireGraphEdit { Name = "Graph", SizeFlagsVertical = SizeFlags.ExpandFill };
        _graph = graph;
        AddChild(graph);
        graph.Connect(MisswireGraphEdit.SignalName.EditRejected, new Callable(this, MethodName.OnRejected));
        graph.Connect(MisswireGraphEdit.SignalName.PartPicked, new Callable(this, MethodName.OnPartPicked));

        _empty = new Label {
            Name = "Empty",
            Text = "No MissMediator in this scene. Add one, then drag nodes in here.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _empty.SetAnchorsPreset(LayoutPreset.FullRect);
        graph.AddChild(_empty, false, InternalMode.Front);

        ShowScene();
    }

    /// <summary>Lists the mediators of the edited scene and keeps showing the current one if it is still there.</summary>
    public void ShowScene(Node prefer = null) {
        var found = new List<Node>();
        if (EditorInterface.Singleton.GetEditedSceneRoot() is { } root) MissEditorSync.FindMediators(root, found);

        var current = prefer ?? Graph?.Mediator;
        _mediators.Clear();
        _choice.Clear();
        foreach (var mediator in found) {
            _mediators.Add(mediator);
            _choice.AddItem(mediator.Name);
        }
        var index = current == null ? -1 : _mediators.IndexOf(current);
        if (index < 0 && _mediators.Count > 0) index = 0;
        if (index >= 0) _choice.Select(index);

        _empty.Visible = _mediators.Count == 0;
        Graph?.Open(index >= 0 ? _mediators[index] : null);
    }

    public void Open(Node mediator) => ShowScene(mediator);

    public void OnEdited() {
        _message.Text = "";
        Graph?.SyncWithGraph();
    }

    public void OnSceneChanged(Node root) => ShowScene();

    /// <summary>After a reload the members of a script, or whether it is [Tool], may have changed.</summary>
    public void AfterReload() {
        ShowScene();
        Graph?.SyncWithGraph(force: true);
    }

    public bool Owns(GodotObject obj) => Graph?.Owns(obj) == true;

    void OnChoice(long index) {
        if (index >= 0 && index < _mediators.Count) Graph?.Open(_mediators[(int) index]);
    }

    void OnAddSelected() => Graph?.AddNodes(EditorInterface.Singleton.GetSelection().GetSelectedNodes(), Graph.ScrollOffset / Graph.Zoom + new Vector2(40, 40));

    void OnRejected(string reason) => _message.Text = reason;

    void OnPartPicked(Resource part) => EditorInterface.Singleton.EditResource(part);
}
#endif
