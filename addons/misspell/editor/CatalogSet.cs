#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace Misspell.Editor;

/// <summary>
/// The files the table shows together: a template and the languages translated from it, all in one
/// folder. Every message is a <see cref="CatalogRow"/>, every language a column.
/// <para>
/// All edits go through here, and each is one undo step. A step remembers the files it touched as
/// they were before and after, whole — simple, and cheap enough for the size of a game's texts.
/// </para>
/// </summary>
public sealed class CatalogSet {
    public readonly List<CatalogFile> Files = [];

    /// <summary>The rows of the table, in the order of the template, then of the languages.</summary>
    public readonly List<CatalogRow> Rows = [];

    /// <summary>The folder of the files, and the domain they share (see <see cref="CatalogFile.NameParts"/>).</summary>
    public string Folder { get; private set; } = "";
    public string Domain { get; private set; } = "";

    public CatalogFile Template => Files.FirstOrDefault(f => f.IsTemplate);
    public IEnumerable<CatalogFile> Languages => Files.Where(f => !f.IsTemplate);

    /// <summary>What the set is called in the toolbar: its template, else its domain, else its folder.</summary>
    public string Title => Template != null ? Template.Path : $"{Folder.TrimEnd('/')}/{(Domain.Length > 0 ? Domain + ".*" : "*")}.po";

    // ---- finding the files -------------------------------------------------------------------

    /// <summary>Opens the files that belong with the one at <paramref name="path"/>: same folder, same domain.</summary>
    public static CatalogSet Open(string path) {
        var set = new CatalogSet { Folder = path.GetBaseDir() };
        var group = GroupOf(path, ListCatalogFiles(set.Folder));
        set.Domain = group.Domain;
        foreach (var member in group.Paths) set.Load(member);
        set.RebuildRows();
        return set;
    }

    /// <summary>A set made in memory, for tests and for files that are not saved yet.</summary>
    public static CatalogSet FromFiles(string folder, params CatalogFile[] files) {
        var set = new CatalogSet { Folder = folder };
        foreach (var file in files) {
            file.Index();
            file.UpdateDirty();
            set.Files.Add(file);
        }
        set.SortFiles();
        set.RebuildRows();
        return set;
    }

    void Load(string path) {
        var text = ReadText(path);
        var file = new CatalogFile {
            Path = path,
            Catalog = PoCatalog.Parse(text),
            IsTemplate = path.GetExtension().Equals("pot", StringComparison.OrdinalIgnoreCase),
            SavedText = text,
        };
        file.Index();
        file.UpdateDirty();
        foreach (var problem in file.Catalog.Problems) GD.PushWarning($"misspell: {path}, {problem}");
        Files.Add(file);
        SortFiles();
    }

    /// <summary>Template first, then the languages by name, so columns stay where they were.</summary>
    void SortFiles() {
        var sorted = Files.OrderBy(f => f.IsTemplate ? 0 : 1).ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase).ToList();
        Files.Clear();
        Files.AddRange(sorted);
    }

    public static string ReadText(string path) {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return file?.GetAsText() ?? "";
    }

    static List<string> ListCatalogFiles(string folder) {
        var files = new List<string>();
        using var dir = DirAccess.Open(folder);
        if (dir == null) return files;
        foreach (var name in dir.GetFiles()) {
            var extension = name.GetExtension().ToLowerInvariant();
            if (extension is "po" or "pot") files.Add(folder.PathJoin(name));
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>
    /// Which files of a folder go with <paramref name="path"/>. Files of the same domain do; and
    /// when the folder has exactly one template, files named only by their language (<c>de.po</c>)
    /// go with it, whatever it is called — that is how Godot's documentation lays them out.
    /// </summary>
    public static (string Domain, List<string> Paths) GroupOf(string path, List<string> folderFiles) {
        var templates = folderFiles.Where(f => f.GetExtension().Equals("pot", StringComparison.OrdinalIgnoreCase)).ToList();
        var lone = templates.Count == 1 ? CatalogFile.NameParts(templates[0]).Domain : null;

        string DomainOf(string file) {
            var domain = CatalogFile.NameParts(file).Domain;
            return domain == "" && lone != null ? lone : domain;
        }

        var wanted = DomainOf(path);
        var paths = folderFiles.Where(f => DomainOf(f) == wanted).ToList();
        if (!paths.Contains(path)) paths.Add(path);
        return (wanted, paths);
    }

    /// <summary>One entry per set of catalogs in the project, by the path of one of its files.</summary>
    public static List<(string Title, string Path)> FindAll(string root = "res://") {
        var found = new List<(string, string)>();
        Collect(root, found);
        return found;
    }

    static void Collect(string folder, List<(string, string)> found) {
        using var dir = DirAccess.Open(folder);
        if (dir == null) return;

        var files = ListCatalogFiles(folder);
        var seen = new HashSet<string>();
        foreach (var file in files) {
            var (domain, paths) = GroupOf(file, files);
            if (!seen.Add(domain)) continue;
            var template = paths.FirstOrDefault(p => p.EndsWith(".pot", StringComparison.OrdinalIgnoreCase));
            var title = template ?? $"{folder.TrimEnd('/')}/{(domain.Length > 0 ? domain + ".*" : "*")}.po";
            found.Add((title, template ?? paths[0]));
        }

        foreach (var sub in dir.GetDirectories()) {
            if (sub.StartsWith('.')) continue;
            Collect(folder.PathJoin(sub), found);
        }
    }

    // ---- rows --------------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the rows from the files: one per message, the template's first and in its order.
    /// A message only some languages have still gets its row; the others show it as missing.
    /// </summary>
    public void RebuildRows() {
        var byKey = new Dictionary<string, CatalogRow>();
        Rows.Clear();
        foreach (var file in Files) {
            file.Index();
            foreach (var entry in file.Catalog.Messages) {
                if (byKey.TryGetValue(entry.Key, out var row)) {
                    // The template decides what the source is; a language only fills in what it lacks.
                    row.IdPlural ??= entry.IdPlural;
                    continue;
                }
                row = new CatalogRow { Context = entry.Context, Id = entry.Id, IdPlural = entry.IdPlural };
                byKey[row.Key] = row;
                Rows.Add(row);
            }
        }
    }

    public PoEntry EntryOf(CatalogRow row, CatalogFile file) => row == null ? null : file?.Find(row.Key);

    /// <summary>Where the source of a message is described best: the template, else the first language that has it.</summary>
    public PoEntry SourceOf(CatalogRow row) {
        foreach (var file in Files) {
            if (EntryOf(row, file) is { } entry) return entry;
        }
        return null;
    }

    /// <summary>How many plural rows the table needs below a plural message: as many as the language with the most forms.</summary>
    public int MaxForms => Math.Max(2, Languages.Select(f => f.Catalog.Plurals.Count).DefaultIfEmpty(2).Max());

    // ---- undo --------------------------------------------------------------------------------

    sealed record Step(string Name, CatalogFile[] Files, PoCatalog[] Before, PoCatalog[] After, bool Structural);

    readonly List<Step> _undo = [];
    readonly List<Step> _redo = [];
    const int UndoLimit = 200;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string UndoName => _undo.Count > 0 ? _undo[^1].Name : "";
    public string RedoName => _redo.Count > 0 ? _redo[^1].Name : "";

    /// <summary>
    /// Runs one edit of some files as one undo step. <paramref name="structural"/> means rows may
    /// have come, gone or changed their source, so the rows are rebuilt.
    /// </summary>
    void Change(string name, IEnumerable<CatalogFile> files, Action edit, bool structural = false) {
        var touched = files.Distinct().ToArray();
        var before = touched.Select(f => f.Catalog.Clone()).ToArray();
        edit();
        var after = touched.Select(f => f.Catalog.Clone()).ToArray();

        foreach (var file in touched) file.UpdateDirty();
        if (structural) RebuildRows();

        _undo.Add(new Step(name, touched, before, after, structural));
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Undoes the last step; returns whether the rows were rebuilt by it.</summary>
    public bool Undo() => Replay(_undo, _redo, s => s.Before);

    public bool Redo() => Replay(_redo, _undo, s => s.After);

    bool Replay(List<Step> from, List<Step> to, Func<Step, PoCatalog[]> state) {
        if (from.Count == 0) return false;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(step);

        var catalogs = state(step);
        for (var i = 0; i < step.Files.Length; i++) {
            var file = step.Files[i];
            // A language added by the step comes and goes with it.
            if (catalogs[i] == null) {
                Files.Remove(file);
                continue;
            }
            file.Catalog = catalogs[i].Clone();
            file.Index();
            file.UpdateDirty();
            if (!Files.Contains(file)) Files.Add(file);
        }
        SortFiles();
        RebuildRows();
        return true;
    }

    /// <summary>Forgets every step, as after reloading from disk.</summary>
    public void ClearHistory() {
        _undo.Clear();
        _redo.Clear();
    }

    // ---- editing translations ----------------------------------------------------------------

    /// <summary>
    /// Writes the translation of one form. A file that does not have the message yet gets it.
    /// Writing clears <c>fuzzy</c>, as a translator who touched the text has checked it.
    /// </summary>
    public void SetTranslation(CatalogRow row, CatalogFile file, int form, string text) {
        if (row == null || file == null || file.IsTemplate) return;
        var current = EntryOf(row, file);
        if (current != null && current.StringAt(form) == text && !current.Fuzzy) return;

        Change($"Translate ({file.Label})", [file], () => {
            var entry = EnsureEntry(row, file);
            entry.SetString(form, text);
            entry.FitForms(file.Catalog.Plurals.Count);
            entry.Fuzzy = false;
        });
    }

    public void SetFuzzy(CatalogRow row, CatalogFile file, bool fuzzy) {
        var entry = EntryOf(row, file);
        if (entry == null || file.IsTemplate || entry.Fuzzy == fuzzy) return;
        Change(fuzzy ? "Mark fuzzy" : "Unmark fuzzy", [file], () => EntryOf(row, file).Fuzzy = fuzzy);
    }

    /// <summary>Sets the translator comment of a message in one file; line breaks make several comment lines.</summary>
    public void SetComment(CatalogRow row, CatalogFile file, string comment) {
        var lines = string.IsNullOrEmpty(comment) ? new List<string>() : [.. comment.Replace("\r", "").Split('\n')];
        var current = EntryOf(row, file);
        if (current != null && current.TranslatorComments.SequenceEqual(lines)) return;
        if (current == null && lines.Count == 0) return;

        Change($"Comment ({file.Label})", [file], () => EnsureEntry(row, file).TranslatorComments = lines);
    }

    /// <summary>Copies the source into a translation, as a start: the singular into form 0, the plural into the rest.</summary>
    public void CopySource(CatalogRow row, CatalogFile file) {
        if (row == null || file == null || file.IsTemplate) return;
        Change($"Copy source ({file.Label})", [file], () => {
            var entry = EnsureEntry(row, file);
            var forms = row.IsPlural ? file.Catalog.Plurals.Count : 1;
            for (var i = 0; i < forms; i++) entry.SetString(i, i == 0 ? row.Id : row.IdPlural);
            entry.FitForms(file.Catalog.Plurals.Count);
            entry.Fuzzy = true;
        });
    }

    PoEntry EnsureEntry(CatalogRow row, CatalogFile file) {
        var entry = file.Find(row.Key);
        if (entry != null) return entry;

        var source = SourceOf(row);
        entry = source != null ? source.Untranslated(file.Catalog.Plurals.Count) : new PoEntry { Context = row.Context, Id = row.Id, IdPlural = row.IdPlural };
        entry.FitForms(file.Catalog.Plurals.Count);
        file.Catalog.Entries.Add(entry);
        file.Index();
        return entry;
    }

    // ---- editing the source ------------------------------------------------------------------

    /// <summary>
    /// Changes what identifies a message — context and source — or its plural source, in every file.
    /// Refused, with the reason returned, when another message already has that context and source.
    /// </summary>
    public string EditSource(CatalogRow row, string context, string id, string idPlural) {
        if (row == null) return "no message";
        context = string.IsNullOrEmpty(context) ? null : context;
        if (string.IsNullOrEmpty(id)) return "A message needs a source text.";
        var key = PoEntry.KeyOf(context, id);
        if (key != row.Key && Rows.Any(r => r.Key == key)) return "Another message already has this source and context.";
        if (context == row.Context && id == row.Id && idPlural == row.IdPlural) return null;

        var oldKey = row.Key;
        var files = Files.Where(f => f.Find(oldKey) != null).ToList();
        Change("Edit source", files, () => {
            foreach (var file in files) {
                var entry = file.Find(oldKey);
                var wasPlural = entry.IsPlural;
                entry.Context = context;
                entry.Id = id;
                entry.IdPlural = idPlural;
                if (wasPlural != entry.IsPlural) entry.FitForms(file.IsTemplate ? 2 : file.Catalog.Plurals.Count);
            }
        }, structural: true);
        return null;
    }

    /// <summary>Gives a message a plural source, so every language gets a row per plural form.</summary>
    public string MakePlural(CatalogRow row) => row == null || row.IsPlural ? null : EditSource(row, row.Context, row.Id, row.Id);

    public string MakeSingular(CatalogRow row) => row == null || !row.IsPlural ? null : EditSource(row, row.Context, row.Id, null);

    /// <summary>Adds a message to every file of the set; with a template, that is where it really belongs.</summary>
    public string AddRow(string context, string id, string idPlural = null) {
        context = string.IsNullOrEmpty(context) ? null : context;
        if (string.IsNullOrEmpty(id)) return "A message needs a source text.";
        if (Rows.Any(r => r.Key == PoEntry.KeyOf(context, id))) return "This message is already there.";

        Change("Add message", Files, () => {
            foreach (var file in Files) {
                var entry = new PoEntry { Context = context, Id = id, IdPlural = idPlural, Strings = [] };
                entry.FitForms(file.IsTemplate ? 2 : file.Catalog.Plurals.Count);
                file.Catalog.Entries.Add(entry);
            }
        }, structural: true);
        return null;
    }

    public void DeleteRow(CatalogRow row) {
        if (row == null) return;
        var key = row.Key;
        var files = Files.Where(f => f.Find(key) != null).ToList();
        Change("Delete message", files, () => {
            foreach (var file in files) file.Catalog.Entries.Remove(file.Find(key));
        }, structural: true);
    }

    // ---- languages ---------------------------------------------------------------------------

    /// <summary>
    /// Sets the plural rule of a language. Every plural message gets exactly as many forms as the
    /// rule has: new ones empty, ones beyond the rule dropped — an undo brings them back.
    /// </summary>
    public string SetPluralForms(CatalogFile file, string header) {
        if (file == null || file.IsTemplate) return "A template has no plural rule.";
        var forms = PluralForms.TryParse(header, out var error);
        if (forms == null) return error;

        Change($"Plural forms ({file.Label})", [file], () => {
            file.Catalog.SetHeader(PoCatalog.PluralFormsField, forms.Header);
            foreach (var entry in file.Catalog.Entries.Where(e => e.IsPlural)) entry.FitForms(forms.Count);
        });
        return null;
    }

    /// <summary>Sets the language of a file; with <paramref name="withRule"/>, also the plural rule known for it.</summary>
    public void SetLanguage(CatalogFile file, string locale, bool withRule) {
        if (file == null || file.IsTemplate) return;
        var rule = withRule ? PluralRules.For(locale) : null;
        Change($"Language ({file.Label})", [file], () => {
            file.Catalog.Language = locale;
            if (rule == null) return;
            file.Catalog.SetHeader(PoCatalog.PluralFormsField, rule.Header);
            foreach (var entry in file.Catalog.Entries.Where(e => e.IsPlural)) entry.FitForms(rule.Forms.Count);
        });
        SortFiles();
    }

    /// <summary>
    /// The path a new language would get: the pattern of the languages already there, else
    /// <c>&lt;locale&gt;.po</c> next to a <c>messages.pot</c> and <c>&lt;domain&gt;.&lt;locale&gt;.po</c> otherwise.
    /// </summary>
    public string PathForLanguage(string locale) {
        var languages = Languages.ToList();
        bool bare;
        if (languages.Count > 0) bare = CatalogFile.NameParts(languages[0].Path).Domain == "";
        else bare = Template == null || Template.Path.GetFile().GetBaseName() == "messages";

        var domain = Template != null ? Template.Path.GetFile().GetBaseName() : Domain;
        return Folder.PathJoin(bare || string.IsNullOrEmpty(domain) ? $"{locale}.po" : $"{domain}.{locale}.po");
    }

    /// <summary>
    /// Adds a language: a new file with a header for it — its plural rule included, when known —
    /// and every message, untranslated. It is written on the next save.
    /// </summary>
    public CatalogFile AddLanguage(string locale, string pluralFormsHeader = null) {
        locale = PluralRules.Normalize(locale);
        var path = PathForLanguage(locale);
        if (Files.Any(f => f.Path == path || (!f.IsTemplate && f.Locale == locale))) return null;

        var plurals = PluralForms.TryParse(pluralFormsHeader, out _) ?? PluralRules.For(locale)?.Forms ?? PluralForms.Default;
        var file = new CatalogFile { Path = path, Catalog = PoCatalog.Create(locale, plurals) };
        var source = Template?.Catalog.Messages ?? Rows.Select(SourceOf).Where(e => e != null);
        foreach (var entry in source.ToList()) file.Catalog.Entries.Add(entry.Untranslated(plurals.Count));

        Files.Add(file);
        SortFiles();
        Change($"Add language {locale}", [file], () => { }, structural: true);
        // Before, the file was not there at all: undo takes it out of the set again.
        _undo[^1] = _undo[^1] with { Before = [null] };
        file.UpdateDirty();
        return file;
    }

    // ---- template ----------------------------------------------------------------------------

    public sealed record MergeResult(int Added, int Obsoleted, int Revived, int Updated);

    /// <summary>
    /// Brings every language in line with the template, like gettext's <c>msgmerge</c> without
    /// guessing: new messages are added untranslated, messages gone from the template become
    /// obsolete (and come back with their translation if they return), and references, extracted
    /// comments and plural sources are taken over from the template.
    /// </summary>
    public MergeResult MergeTemplate() {
        var template = Template;
        if (template == null) return new MergeResult(0, 0, 0, 0);
        int added = 0, obsoleted = 0, revived = 0, updated = 0;

        Change("Update from template", Languages, () => {
            var keys = template.Catalog.Messages.Select(e => e.Key).ToHashSet();
            foreach (var file in Languages) {
                var forms = file.Catalog.Plurals.Count;
                var merged = new List<PoEntry>();
                foreach (var source in template.Catalog.Messages) {
                    var entry = file.Find(source.Key);
                    if (entry == null && file.Catalog.FindObsolete(source.Key) is { } old) {
                        entry = old;
                        entry.Obsolete = false;
                        revived++;
                    }
                    if (entry == null) {
                        entry = source.Untranslated(forms);
                        added++;
                    } else if (!entry.ExtractedComments.SequenceEqual(source.ExtractedComments)
                               || !entry.References.SequenceEqual(source.References) || entry.IdPlural != source.IdPlural) {
                        entry.ExtractedComments = [.. source.ExtractedComments];
                        entry.References = [.. source.References];
                        entry.IdPlural = source.IdPlural;
                        entry.FitForms(forms);
                        updated++;
                    }
                    merged.Add(entry);
                }

                // What is left goes last: obsolete now, or already — or a duplicate, which stays as it is.
                var kept = merged.ToHashSet();
                foreach (var entry in file.Catalog.Entries.Where(e => !kept.Contains(e))) {
                    if (!entry.Obsolete && !keys.Contains(entry.Key)) {
                        entry.Obsolete = true;
                        obsoleted++;
                    }
                    merged.Add(entry);
                }
                file.Catalog.Entries = merged;
            }
        }, structural: true);
        return new MergeResult(added, obsoleted, revived, updated);
    }

    // ---- saving ------------------------------------------------------------------------------

    /// <summary>Writes every file with changes; returns the paths written.</summary>
    public List<string> SaveAll() {
        var saved = new List<string>();
        foreach (var file in Files.Where(f => f.Dirty)) {
            if (Save(file)) saved.Add(file.Path);
        }
        return saved;
    }

    public bool Save(CatalogFile file) {
        var text = file.Catalog.Write();
        using var stream = FileAccess.Open(file.Path, FileAccess.ModeFlags.Write);
        if (stream == null) {
            GD.PushError($"misspell: cannot write {file.Path}: {FileAccess.GetOpenError()}");
            return false;
        }
        stream.StoreString(text);
        file.SavedText = text;
        file.UpdateDirty();
        return true;
    }

    /// <summary>Files whose text on disk is no longer what was read or written last.</summary>
    public List<CatalogFile> ChangedOnDisk() => [.. Files.Where(f => f.SavedText != null && FileAccess.FileExists(f.Path) && ReadText(f.Path) != f.SavedText)];

    // ---- numbers and problems ----------------------------------------------------------------

    public sealed record Stats(int Total, int Translated, int Fuzzy);

    public Stats StatsOf(CatalogFile file) {
        var forms = file.Catalog.Plurals.Count;
        int translated = 0, fuzzy = 0;
        foreach (var row in Rows) {
            var entry = EntryOf(row, file);
            if (entry == null) continue;
            if (entry.Fuzzy) fuzzy++;
            else if (entry.IsTranslated(forms)) translated++;
        }
        return new Stats(Rows.Count, translated, fuzzy);
    }

    static readonly Regex Placeholder = new(@"\{[A-Za-z0-9_]*\}|%(?:%|[-+ 0#]*\d*(?:\.\d+)?[sdifxXocv])");

    static List<string> PlaceholdersOf(string text) => [.. Placeholder.Matches(text ?? "").Select(m => m.Value).Where(p => p != "%%")];

    /// <summary>What is wrong with a message in one language, worded for a tooltip. Empty when nothing is.</summary>
    public List<string> ProblemsOf(CatalogRow row, CatalogFile file) {
        var problems = new List<string>();
        if (file.IsTemplate) return problems;
        var entry = EntryOf(row, file);
        if (entry == null) return problems;

        var plurals = file.Catalog.Plurals;
        var forms = row.IsPlural ? plurals.Count : 1;
        var singular = PlaceholdersOf(row.Id);
        var all = row.IsPlural ? singular.Concat(PlaceholdersOf(row.IdPlural)).ToHashSet() : singular.ToHashSet();

        for (var form = 0; form < forms; form++) {
            var text = entry.StringAt(form);
            if (text.Length == 0) continue;
            var label = row.IsPlural ? $"{plurals.Names[form]}: " : "";
            var found = PlaceholdersOf(text);

            foreach (var unknown in found.Where(p => !all.Contains(p)).Distinct()) problems.Add($"{label}{unknown} is not in the source");
            // A plural form for a single thing may leave the number out ("One coin"); a plain message may not.
            if (!row.IsPlural) {
                foreach (var missing in singular.Where(p => !found.Contains(p)).Distinct()) problems.Add($"{missing} is missing");
            }

            var source = form == 0 || !row.IsPlural ? row.Id : row.IdPlural;
            if (source.EndsWith('\n') != text.EndsWith('\n')) problems.Add($"{label}the source {(source.EndsWith('\n') ? "ends" : "does not end")} with a line break");
        }

        if (row.IsPlural && entry.Strings.Count > plurals.Count) problems.Add($"has {entry.Strings.Count} plural forms, the language {plurals.Count}");
        return problems;
    }
}

/// <summary>One message of a <see cref="CatalogSet"/>, a row of the table. Identified by context and source.</summary>
public sealed class CatalogRow {
    public string Context;
    public string Id;
    public string IdPlural;

    public bool IsPlural => IdPlural != null;
    public string Key => PoEntry.KeyOf(Context, Id);

    public override string ToString() => Key;
}
#endif
