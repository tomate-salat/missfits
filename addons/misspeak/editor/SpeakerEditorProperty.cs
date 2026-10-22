#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misspeak.Editor;

/// <summary>
/// The Inspector's editor for a line's speaker: a dropdown of every speaker file in the project,
/// plus those the open dialogue keeps inside itself, and one to make a new speaker file. The button
/// beside it shows the speaker in the Inspector, to rename them or give them more.
/// </summary>
[Tool]
public partial class SpeakerEditorProperty : EditorProperty {
    /// <summary>What the dropdown shows for a line nobody speaks.</summary>
    public const string Nobody = "(nobody)";

    /// <summary>The entry that makes a new speaker file.</summary>
    public const string NewSpeaker = "New speaker…";

    /// <summary>What it appends to a speaker kept inside the dialogue: one that no other dialogue can use until it is saved to a file.</summary>
    public const string OnlyHere = " (only in this dialogue)";

    const int NewId = 100000;

    OptionButton _picker;
    Button _edit;
    EditorFileDialog _newDialog;

    /// <summary>The speakers on offer, as a Godot array so an assembly reload keeps them.</summary>
    Godot.Collections.Array<DialogueSpeaker> _speakers = [];

    bool _updating;

    public void Offer(IEnumerable<DialogueSpeaker> speakers) => _speakers = [.. speakers.Where(s => s != null).Distinct()];

    /// <summary>The entries of the dropdown, top to bottom.</summary>
    public List<string> Entries => [Nobody, .. _speakers.Select(Label), NewSpeaker];

    static bool HasOwnFile(Resource resource) => !string.IsNullOrEmpty(resource.ResourcePath) && !resource.ResourcePath.Contains("::");

    static string Label(DialogueSpeaker speaker) {
        var name = speaker.Name != "" ? speaker.Name : HasOwnFile(speaker) ? speaker.ResourcePath.GetFile().GetBaseName() : "(unnamed)";
        return HasOwnFile(speaker) ? name : name + OnlyHere;
    }

    /// <summary>Every <see cref="DialogueSpeaker"/> saved in a file of its own anywhere in the project, by name.</summary>
    public static List<DialogueSpeaker> ProjectSpeakers() {
        var paths = new List<string>();
        void Scan(EditorFileSystemDirectory directory) {
            if (directory == null) return;
            for (var i = 0; i < directory.GetFileCount(); i++) {
                if (directory.GetFileType(i) == "Resource" && IsSpeakerFile(directory.GetFilePath(i))) paths.Add(directory.GetFilePath(i));
            }
            for (var i = 0; i < directory.GetSubdirCount(); i++) Scan(directory.GetSubdir(i));
        }

        if (Engine.IsEditorHint()) Scan(EditorInterface.Singleton?.GetResourceFilesystem()?.GetFilesystem());
        // "as", not Load<T>: a file that is not what it claims must not take the whole dropdown down.
        return [.. paths.Select(path => ResourceLoader.Load(path) as DialogueSpeaker).Where(s => s != null).OrderBy(s => s.Name)];
    }

    /// <summary>
    /// Whether a <c>.tres</c> holds a speaker, by its first line, where Godot names the script class
    /// — without loading every resource of the project. The editor's file system knows the class
    /// name of a script file only, not of a resource that uses the script.
    /// </summary>
    static bool IsSpeakerFile(string path) {
        if (!path.EndsWith(".tres")) return false;
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return file != null && file.GetLine().Contains($"script_class=\"{nameof(DialogueSpeaker)}\"");
    }

    public override void _Ready() {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(row);

        _picker = new OptionButton { ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _picker.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnPicked));
        row.AddChild(_picker);
        AddFocusable(_picker);

        _edit = new Button { Flat = true, TooltipText = "Show the speaker in the Inspector" };
        _edit.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnEditPressed));
        row.AddChild(_edit);
        AddFocusable(_edit);

        Fill();
    }

    public override void _Notification(int what) {
        if (what == NotificationThemeChanged && _edit != null) _edit.Icon = GetThemeIcon("Edit", "EditorIcons");
    }

    void Fill() {
        if (_picker == null) return;

        _updating = true;
        _picker.Clear();
        _picker.AddItem(Nobody, 0);
        for (var i = 0; i < _speakers.Count; i++) _picker.AddItem(Label(_speakers[i]), i + 1);
        _picker.AddSeparator();
        _picker.AddItem(NewSpeaker, NewId);
        _updating = false;
    }

    DialogueSpeaker Current => GetEditedObject()?.Get(GetEditedProperty()).As<DialogueSpeaker>();

    public override void _UpdateProperty() {
        if (_picker == null || GetEditedObject() == null) return;

        // One from elsewhere — another dialogue's own, say — is offered too, so opening a line never changes it.
        var current = Current;
        if (current != null && !_speakers.Contains(current)) {
            _speakers.Add(current);
            Fill();
        }

        _updating = true;
        _picker.Selected = _picker.GetItemIndex(current == null ? 0 : _speakers.IndexOf(current) + 1);
        _updating = false;
        _edit.Disabled = current == null;
    }

    void OnPicked(long index) {
        if (_updating) return;

        var id = _picker.GetItemId((int) index);
        if (id == NewId) {
            // Back to what it was until a file is chosen.
            _UpdateProperty();
            AskForNewFile();
            return;
        }
        EmitChanged(GetEditedProperty(), id <= 0 ? new Variant() : _speakers[id - 1]);
    }

    void OnEditPressed() {
        if (Current is { } speaker) EditorInterface.Singleton.EditResource(speaker);
    }

    // ---- a new speaker ------------------------------------------------------------------------

    void AskForNewFile() {
        if (_newDialog == null) {
            _newDialog = new EditorFileDialog {
                Title = "New speaker",
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Resources,
                Filters = ["*.tres ; Speaker"],
            };
            _newDialog.Connect(FileDialog.SignalName.FileSelected, new Callable(this, MethodName.OnNewFileChosen));
            AddChild(_newDialog);
        }

        // Next to the speakers there are, so they gather in one place.
        var beside = _speakers.FirstOrDefault(HasOwnFile);
        _newDialog.CurrentDir = beside != null ? beside.ResourcePath.GetBaseDir() : "res://";
        _newDialog.CurrentFile = "speaker.tres";
        _newDialog.PopupFileDialog();
    }

    void OnNewFileChosen(string path) {
        var speaker = CreateSpeakerFile(path);
        if (speaker == null) return;

        _speakers.Add(speaker);
        Fill();
        EmitChanged(GetEditedProperty(), speaker);
    }

    /// <summary>Saves a new speaker, named after the file, at <paramref name="path"/> — and returns it as loaded from there, so it is the shared one.</summary>
    public static DialogueSpeaker CreateSpeakerFile(string path) {
        var name = path.GetFile().GetBaseName();
        var error = ResourceSaver.Save(new DialogueSpeaker { Name = name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name }, path);
        if (error != Error.Ok) {
            GD.PushError($"misspeak: saving the new speaker to {path} failed: {error}");
            return null;
        }
        if (Engine.IsEditorHint()) EditorInterface.Singleton?.GetResourceFilesystem()?.UpdateFile(path);
        return ResourceLoader.Load<DialogueSpeaker>(path);
    }
}
#endif
