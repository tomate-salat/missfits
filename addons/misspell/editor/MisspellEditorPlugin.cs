#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misspell.Editor;

/// <summary>
/// Entry point of the addon: a main screen next to 2D, 3D and Script with the translation table,
/// and <i>Open in Misspell</i> in the FileSystem dock's menu. Double-clicking a <c>.po</c> file opens
/// it here as well.
/// <para>
/// A reload of the assembly does not run <see cref="_EnterTree"/> again: Godot keeps the objects
/// and rebuilds only their C# side. That is why every connection here is a native method callable,
/// and why references to the addon's own classes are stored untyped (see <see cref="ReloadSafe"/>).
/// </para>
/// </summary>
[Tool]
public partial class MisspellEditorPlugin : EditorPlugin {
    const string ScreenName = "Misspell";
    const string MetaSection = "misspell";
    const string MetaKey = "open_catalog";

    GodotObject _panel;
    GodotObject _menu;

    MisspellPanel Panel => ReloadSafe.Get<MisspellPanel>(ref _panel);

    public override void _EnterTree() {
        var panel = new MisspellPanel { Visible = false };
        _panel = panel;
        EditorInterface.Singleton.GetEditorMainScreen().AddChild(panel);
        panel.Connect(MisspellPanel.SignalName.CatalogOpened, new Callable(this, MethodName.OnCatalogOpened));

        var menu = new MisspellContextMenu();
        menu.Connect(MisspellContextMenu.SignalName.OpenRequested, new Callable(this, MethodName.OpenCatalog));
        _menu = menu;
        AddContextMenuPlugin(EditorContextMenuPlugin.ContextMenuSlot.Filesystem, menu);

        RestoreLastCatalog();
    }

    public override void _ExitTree() {
        if (ReloadSafe.Get<MisspellContextMenu>(ref _menu) is { } menu) RemoveContextMenuPlugin(menu);
        _menu = null;

        // Nothing is saved here: when the editor quits, _GetUnsavedStatus has already asked.
        Panel?.QueueFree();
        _panel = null;
    }

    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => ScreenName;

    public override Texture2D _GetPluginIcon() => EditorInterface.Singleton.GetEditorTheme().GetIcon("Translation", "EditorIcons");

    public override void _MakeVisible(bool visible) {
        if (Panel != null) Panel.Visible = visible;
    }

    /// <summary>An imported <c>.po</c> file is a Translation; double-clicking one in the FileSystem dock brings it here.</summary>
    public override bool _Handles(GodotObject @object) => @object is Translation translation && translation.ResourcePath.EndsWith(".po");

    public override void _Edit(GodotObject @object) {
        if (@object is Translation translation) OpenCatalog(translation.ResourcePath);
    }

    void OpenCatalog(string path) {
        Panel?.Open(path);
        EditorInterface.Singleton.SetMainScreenEditor(ScreenName);
    }

    void OnCatalogOpened(string path) => EditorInterface.Singleton.GetEditorSettings().SetProjectMetadata(MetaSection, MetaKey, path);

    /// <summary>Reopens whatever catalog was open when the editor was last closed.</summary>
    void RestoreLastCatalog() {
        var path = EditorInterface.Singleton.GetEditorSettings().GetProjectMetadata(MetaSection, MetaKey, "").AsString();
        if (!string.IsNullOrEmpty(path) && FileAccess.FileExists(path)) Panel?.Open(path);
    }

    /// <summary>Called before the project runs and before scenes are saved, so a run never uses stale translations.</summary>
    public override void _ApplyChanges() => Panel?.SaveAll();

    /// <summary>Ctrl+S in the editor saves the catalogs along with the scene.</summary>
    public override void _SaveExternalData() => Panel?.SaveAll();

    public override string _GetUnsavedStatus(string forScene) {
        if (!string.IsNullOrEmpty(forScene) || Panel == null) return "";
        var paths = Panel.UnsavedPaths();
        return paths.Length == 0 ? "" : $"Save changes to the following translation file(s) before closing?\n{string.Join("\n", paths)}";
    }
}

/// <summary><i>Open in Misspell</i> in the FileSystem dock's menu, for <c>.po</c> and <c>.pot</c> files.</summary>
[Tool]
public partial class MisspellContextMenu : EditorContextMenuPlugin {
    [Signal]
    public delegate void OpenRequestedEventHandler(string path);

    public override void _PopupMenu(string[] paths) {
        if (paths.Length != 1 || !(paths[0].EndsWith(".po") || paths[0].EndsWith(".pot"))) return;
        var icon = EditorInterface.Singleton.GetEditorTheme().GetIcon("Translation", "EditorIcons");
        AddContextMenuItem("Open in Misspell", new Callable(this, MethodName.OnOpen), icon);
    }

    void OnOpen(string[] paths) {
        if (paths.Length > 0) EmitSignal(SignalName.OpenRequested, paths[0]);
    }
}
#endif
