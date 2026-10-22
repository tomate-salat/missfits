#if TOOLS
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Godot;

namespace Misspell.Editor;

/// <summary>One PO or POT file of a <see cref="CatalogSet"/>: where it lives, what it holds, and what is on disk.</summary>
public sealed class CatalogFile {
    public string Path;
    public PoCatalog Catalog;

    /// <summary>A POT: the source texts, no translations. It is no column of its own in the table.</summary>
    public bool IsTemplate;

    /// <summary>The text last read from or written to disk; null for a file not saved yet.</summary>
    public string SavedText;

    /// <summary>The messages by key, for the table's lookups. Rebuilt by <see cref="Index"/>.</summary>
    readonly Dictionary<string, PoEntry> _byKey = [];

    bool _dirty;

    public bool Dirty => _dirty;

    /// <summary>The language of the file: its header, else what its name says.</summary>
    public string Locale {
        get {
            if (IsTemplate) return "";
            var language = Catalog.Language;
            return !string.IsNullOrEmpty(language) ? language : NameParts(Path).Locale ?? "";
        }
    }

    /// <summary>What the column of the file is called: its language, else its name.</summary>
    public string Label => Locale is { Length: > 0 } locale ? locale : Path.GetFile();

    public PoEntry Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Rebuilds the lookup by key; needed after messages were added, removed or renamed.</summary>
    public void Index() {
        _byKey.Clear();
        foreach (var entry in Catalog.Entries) {
            if (!entry.Obsolete) _byKey.TryAdd(entry.Key, entry);
        }
    }

    /// <summary>Compares the catalog with what is on disk. Called after every change.</summary>
    public void UpdateDirty() => _dirty = SavedText == null || Catalog.Write() != SavedText;

    // ---- names -------------------------------------------------------------------------------

    static readonly Regex LocalePattern = new(@"^[a-z]{2,3}([_-][A-Za-z]{2,4})?([_-][A-Za-z0-9]{2,8})?$");

    public static bool LooksLikeLocale(string text) => LocalePattern.IsMatch(text ?? "");

    /// <summary>
    /// What a file name says: the domain (which files belong together) and the language.
    /// <c>messages.pot</c> → (messages, —), <c>de.po</c> → ("", de), <c>dialogue.pt_BR.po</c> →
    /// (dialogue, pt_BR), <c>german.po</c> → (german, —).
    /// </summary>
    public static (string Domain, string Locale) NameParts(string path) {
        var file = path.GetFile();
        var extension = file.GetExtension().ToLowerInvariant();
        var name = file.GetBaseName();
        if (extension == "pot") return (name, null);
        if (LooksLikeLocale(name)) return ("", name);

        var dot = name.LastIndexOf('.');
        if (dot > 0 && LooksLikeLocale(name[(dot + 1)..])) return (name[..dot], name[(dot + 1)..]);
        return (name, null);
    }
}
#endif
