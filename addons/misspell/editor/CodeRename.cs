#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace Misspell.Editor;

/// <summary>
/// Renamed texts in the code: which uses a rename touches, writing the new text into them the way
/// each string is written, and putting the files back on undo. Only exact uses are changed —
/// strings with holes are listed to check by hand.
/// <para>
/// Files are written with care for the editor: a scene or a script open in it only when it has no
/// unsaved changes — the scene is then read again, the script's text replaced — and a resource is
/// read again into the copy the editor holds.
/// </para>
/// </summary>
public static class CodeRename {
    /// <summary>A text that got a new source: its old one, its context (null for none) and the new one.</summary>
    public readonly record struct Rename(string OldId, string Context, string NewId);

    /// <summary>One use to change: where it is, and the rename it follows.</summary>
    public sealed record Change(KeyUsages.Usage Usage, Rename Rename) {
        public bool Wanted = true;
    }

    /// <summary>A file before and after; what undo and redo write back.</summary>
    public sealed record FileChange(string Path, string Before, string After);

    /// <summary>The exact uses each rename touches, in file and line order.</summary>
    public static List<Change> Plan(KeyUsages usages, IEnumerable<Rename> renames) =>
        [.. renames.SelectMany(r => usages.Of(r.OldId, r.Context).Select(u => new Change(u, r)))
                   .OrderBy(c => c.Usage.Path, System.StringComparer.Ordinal).ThenBy(c => c.Usage.Line)];

    /// <summary>The strings with holes that may make one of the old texts: to check by hand.</summary>
    public static List<KeyUsages.Usage> ToCheck(KeyUsages usages, IEnumerable<Rename> renames) =>
        [.. renames.SelectMany(r => usages.GuessesOf(r.OldId)).Distinct()
                   .OrderBy(u => u.Path, System.StringComparer.Ordinal).ThenBy(u => u.Line)];

    /// <summary>
    /// The source with every string on the changes' lines that is exactly an old text — in a
    /// context that fits — written as the new one. Strings that cannot be written so (a raw
    /// string the new text has a quote for, say) are left and named in <paramref name="problems"/>.
    /// </summary>
    public static string Rewrite(string source, string extension, IEnumerable<Change> changes, List<string> problems) {
        // Only the strings change: every other character, line breaks included, stays as it was.
        var text = source;
        var lineBreak = source.Contains("\r\n") ? "\r\n" : "\n";
        var byLine = changes.GroupBy(c => c.Usage.Line).ToDictionary(g => g.Key, g => g.Select(c => c.Rename).ToList());
        var edits = new List<(int Start, int End, string Text)>();

        foreach (var literal in KeyUsages.Literals(text, extension, asWritten: true)) {
            if (literal.IsPattern || !byLine.TryGetValue(literal.Line, out var renames)) continue;
            var rename = renames.FirstOrDefault(r => r.OldId == literal.Text && literal.Fits(r.Context));
            if (rename.OldId == null) continue;
            var written = literal.End < 0 ? null : literal.Spelling?.Write(rename.NewId);
            if (written == null) {
                problems.Add($"line {literal.Line}: \"{rename.NewId}\" cannot be written into that string; change it by hand");
                continue;
            }
            // A line break the string holds as it is follows the file's.
            if (lineBreak != "\n" && (literal.Spelling.LineBreaks || literal.Spelling.Verbatim)) written = written.Replace("\n", lineBreak);
            edits.Add((literal.Start, literal.End, written));
        }

        var result = new StringBuilder(text);
        foreach (var (start, end, written) in edits.OrderByDescending(e => e.Start)) {
            result.Remove(start, end - start);
            result.Insert(start, written);
        }
        foreach (var line in byLine.Keys.Where(l => !edits.Any(e => LineAt(text, e.Start) == l)).Order()) {
            if (!problems.Any(p => p.StartsWith($"line {line}:"))) problems.Add($"line {line}: the string is no longer there");
        }
        return result.ToString();
    }

    static int LineAt(string text, int offset) => 1 + System.MemoryExtensions.Count(System.MemoryExtensions.AsSpan(text, 0, offset), '\n');

    /// <summary>
    /// Changes the wanted uses, file by file. Returns each file as it was and became, for undo;
    /// what could not be done goes into <paramref name="problems"/>, by file.
    /// </summary>
    public static List<FileChange> Apply(IEnumerable<Change> changes, List<string> problems) {
        var done = new List<FileChange>();
        foreach (var file in changes.Where(c => c.Wanted).GroupBy(c => c.Usage.Path)) {
            var path = file.Key;
            var before = Read(path);
            if (before == null) {
                problems.Add($"{path}: cannot be read");
                continue;
            }
            var fileProblems = new List<string>();
            var after = Rewrite(before, path.GetExtension().ToLowerInvariant(), file, fileProblems);
            problems.AddRange(fileProblems.Select(p => $"{path} {p}"));
            if (after == before) continue;
            if (Write(path, before, after) is { } problem) {
                problems.Add($"{path}: {problem}");
                continue;
            }
            done.Add(new FileChange(path, before, after));
        }
        return done;
    }

    /// <summary>Puts files back as they were before (undo) or after (redo) — each only while it is still as the change left it.</summary>
    public static void Replay(IEnumerable<FileChange> files, bool undo, List<string> problems) {
        foreach (var file in files) {
            var (expected, wanted) = undo ? (file.After, file.Before) : (file.Before, file.After);
            if (Write(file.Path, expected, wanted) is { } problem) problems.Add($"{file.Path}: {problem}");
        }
    }

    // ---- files -------------------------------------------------------------------------------

    /// <summary>The file as the editor has it: a script open in the script editor as shown there.</summary>
    static string Read(string path) {
        if (OpenScriptEditor(path) is { } editor) return editor.Text;
        return ReadStored(path)?.Text;
    }

    /// <summary>Writes a file, if it still reads <paramref name="expected"/>; the reason when not.</summary>
    static string Write(string path, string expected, string text) {
        // An open scene is read again after writing it; with unsaved changes that would lose them.
        var openScene = IsOpenScene(path);
        if (openScene && EditorInterface.Singleton.GetUnsavedScenes().Contains(path)) return "the scene has unsaved changes in the editor; save it first";
        var editor = OpenScriptEditor(path);
        var stored = ReadStored(path);
        if (stored == null) return "cannot be read";
        if ((editor?.Text ?? stored.Text) != expected) return "changed since; left as it is";
        if (editor != null && stored.Text.Replace("\r\n", "\n") != editor.Text.Replace("\r\n", "\n")) return "has unsaved changes in the script editor; save them first";

        if (WriteStored(path, stored, text) is { } problem) return problem;
        if (editor != null) {
            editor.Text = text;
            editor.TagSavedVersion();
        }
        Refresh(path);
        if (openScene) EditorInterface.Singleton.ReloadSceneFromPath(path);
        return null;
    }

    /// <summary>A file's text and how it is stored — encoding and byte order mark — to write it back the same way.</summary>
    internal sealed record Stored(string Text, Encoding Encoding, byte[] Mark);

    /// <summary>
    /// Reads a file as it is stored: UTF-8 with or without a byte order mark, UTF-16 by its mark,
    /// and anything else byte for byte (Latin-1), so what is not changed is written back unchanged.
    /// </summary>
    internal static Stored ReadStored(string path) {
        var full = ProjectSettings.GlobalizePath(path);
        if (!System.IO.File.Exists(full)) return null;
        var bytes = System.IO.File.ReadAllBytes(full);
        (Encoding encoding, int mark) = bytes switch {
            [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(false, true), 3),
            [0xFF, 0xFE, ..] => (new UnicodeEncoding(false, false, true), 2),
            [0xFE, 0xFF, ..] => (new UnicodeEncoding(true, false, true), 2),
            _ => ((Encoding) new UTF8Encoding(false, true), 0),
        };
        try {
            return new Stored(encoding.GetString(bytes, mark, bytes.Length - mark), encoding, bytes[..mark]);
        } catch (DecoderFallbackException) {
            return new Stored(Encoding.Latin1.GetString(bytes), Encoding.Latin1, []);
        }
    }

    static string WriteStored(string path, Stored how, string text) {
        var body = how.Encoding == Encoding.Latin1 ? Encoding.Latin1.GetBytes(text) : TryEncode(how.Encoding, text);
        if (body == null || how.Encoding == Encoding.Latin1 && Encoding.Latin1.GetString(body) != text) return "the new text has characters the file's encoding cannot hold";
        try {
            System.IO.File.WriteAllBytes(ProjectSettings.GlobalizePath(path), [.. how.Mark, .. body]);
        } catch (System.Exception e) when (e is System.IO.IOException or System.UnauthorizedAccessException) {
            return $"cannot be written ({e.Message})";
        }
        return null;
    }

    static byte[] TryEncode(Encoding encoding, string text) {
        try {
            return encoding.GetBytes(text);
        } catch (EncoderFallbackException) {
            return null;
        }
    }

    /// <summary>Tells the editor a file changed under it: the file system dock, and a resource it holds.</summary>
    static void Refresh(string path) {
        if (!Engine.IsEditorHint()) return;
        EditorInterface.Singleton.GetResourceFilesystem().UpdateFile(path);
        if (ResourceLoader.HasCached(path) && path.GetExtension() is "tres") ResourceLoader.Load(path, "", ResourceLoader.CacheMode.Replace);
        if (ResourceLoader.HasCached(path) && ResourceLoader.Load(path) is GDScript script) {
            script.SourceCode = FileAccess.GetFileAsString(path);
            script.Reload(keepState: true);
        }
    }

    static bool IsOpenScene(string path) => Engine.IsEditorHint() && EditorInterface.Singleton.GetOpenScenes().Contains(path);

    /// <summary>The text field of a script open in Godot's script editor, if it is.</summary>
    static CodeEdit OpenScriptEditor(string path) {
        if (!Engine.IsEditorHint()) return null;
        var scriptEditor = EditorInterface.Singleton.GetScriptEditor();
        var scripts = scriptEditor.GetOpenScripts();
        var editors = scriptEditor.GetOpenScriptEditors();
        for (var i = 0; i < scripts.Count && i < editors.Count; i++) {
            if (scripts[i].ResourcePath == path) return editors[i].GetBaseEditor() as CodeEdit;
        }
        return null;
    }
}
#endif
