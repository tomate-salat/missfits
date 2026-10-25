#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misspell;
using Misspell.Editor;

/// <summary>
/// Self test of the catalog sets and the table, run outside the editor: the panel works without
/// one, only importing and the project's translation list are skipped. Writes its files to
/// <c>user://misspell_test</c>.
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/misspell/tests/editor_self_test.tscn
/// </code>
/// </summary>
public partial class SpellEditorSelfTest : Node {
    const string Folder = "user://misspell_test";

    readonly List<string> _failures = [];
    int _checks;

    const string Template = """
        msgid ""
        msgstr ""
        "Content-Type: text/plain; charset=UTF-8\n"

        #. On the shop's button.
        #: res://ui/shop.tscn
        msgid "Buy"
        msgstr ""

        msgctxt "verb"
        msgid "Sell"
        msgstr ""

        msgid "You have {n} coin."
        msgid_plural "You have {n} coins."
        msgstr[0] ""
        msgstr[1] ""

        msgid "Hello, {name}!"
        msgstr ""
        """;

    const string German = """
        msgid ""
        msgstr ""
        "Language: de\n"
        "Content-Type: text/plain; charset=UTF-8\n"
        "Plural-Forms: nplurals=2; plural=(n != 1);\n"

        #. On the shop's button.
        #: res://ui/shop.tscn
        msgid "Buy"
        msgstr "Kaufen"

        #, fuzzy
        msgctxt "verb"
        msgid "Sell"
        msgstr "Verkaufen"

        msgid "You have {n} coin."
        msgid_plural "You have {n} coins."
        msgstr[0] "Du hast {n} Münze."
        msgstr[1] "Du hast {n} Münzen."

        msgid "Hello, {name}!"
        msgstr "Hallo!"

        #~ msgid "Gone"
        #~ msgstr "Weg"
        """;

    const string Polish = """
        msgid ""
        msgstr ""
        "Language: pl\n"
        "Plural-Forms: nplurals=3; plural=(n==1 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);\n"

        msgid "You have {n} coin."
        msgid_plural "You have {n} coins."
        msgstr[0] "Masz {n} monetę."
        msgstr[1] ""
        msgstr[2] "Masz {n} monet."
        """;

    public override async void _Ready() {
        try {
            NamesAndGroups();
            WriteFiles();
            var set = CatalogSet.Open($"{Folder}/de.po");
            SetsOpenTogether(set);
            TranslationsAreUndoable(set);
            SourcesChangeEverywhere(set);
            PluralRulesResizeForms(set);
            LanguagesAreAdded(set);
            TemplateUpdatesTheLanguages();
            ProblemsAreFound();
            SavingWritesTheFiles();
            await TheTableShowsPluralsAsRows();
            await KeysLaidOutAsPathsShowAsATree();
        } catch (System.Exception e) {
            // Without this, a crash would leave the headless run waiting forever.
            _failures.Add($"crashed: {e}");
        }

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misspell editor self test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    // ---- files -------------------------------------------------------------------------------

    void NamesAndGroups() {
        Check("a template names its domain", CatalogFile.NameParts("res://l/messages.pot") == ("messages", null));
        Check("a file named by its language has no domain", CatalogFile.NameParts("res://l/pt_BR.po") == ("", "pt_BR"));
        Check("domain and language", CatalogFile.NameParts("res://l/dialogue.de.po") == ("dialogue", "de"));
        Check("a name that is no language is the domain", CatalogFile.NameParts("res://l/german.po") == ("german", null));

        List<string> folder = ["res://l/messages.pot", "res://l/de.po", "res://l/pl.po", "res://l/dialogue.de.po"];
        Check("with one template, files named by language go with it", CatalogSet.GroupOf("res://l/de.po", folder).Paths.SequenceEqual(["res://l/messages.pot", "res://l/de.po", "res://l/pl.po"]));
        Check("a domain keeps to itself", CatalogSet.GroupOf("res://l/dialogue.de.po", folder).Paths.SequenceEqual(["res://l/dialogue.de.po"]));

        List<string> two = ["res://l/a.pot", "res://l/b.pot", "res://l/de.po", "res://l/a.de.po"];
        Check("with two templates, a bare language file is on its own", CatalogSet.GroupOf("res://l/de.po", two).Paths.SequenceEqual(["res://l/de.po"])
                                                                        && CatalogSet.GroupOf("res://l/a.pot", two).Paths.SequenceEqual(["res://l/a.pot", "res://l/a.de.po"]));
    }

    void WriteFiles() {
        DirAccess.MakeDirRecursiveAbsolute(Folder);
        using (var dir = DirAccess.Open(Folder)) {
            foreach (var file in dir.GetFiles()) dir.Remove(file);
        }
        Write("messages.pot", Template);
        Write("de.po", German);
        Write("pl.po", Polish);
        Write("other.fr.po", "msgid \"\"\nmsgstr \"Language: fr\\n\"\n");
    }

    static void Write(string name, string text) {
        using var file = FileAccess.Open($"{Folder}/{name}", FileAccess.ModeFlags.Write);
        file.StoreString(text.Replace("\r\n", "\n") + "\n");
    }

    void SetsOpenTogether(CatalogSet set) {
        Check("the files of a set are opened together, template first", set.Files.Select(f => f.Path.GetFile()).SequenceEqual(["messages.pot", "de.po", "pl.po"]));
        Check("a file of another domain is left out", set.Files.All(f => !f.Path.EndsWith("other.fr.po")));
        Check("one row per message of the template, in its order", set.Rows.Select(r => r.Id).SequenceEqual(["Buy", "Sell", "You have {n} coin.", "Hello, {name}!"]));
        Check("a context is part of the row", set.Rows[1].Context == "verb" && set.Rows[1].Key == "verb\u0004Sell");
        Check("obsolete messages get no row", set.Rows.All(r => r.Id != "Gone"));
        Check("the table needs rows for as many forms as Polish has", set.MaxForms == 3);
        Check("nothing is unsaved after opening", set.Files.All(f => !f.Dirty));

        var de = Language(set, "de");
        var stats = set.StatsOf(de);
        Check("German: three translated, one fuzzy", stats.Translated == 3 && stats.Fuzzy == 1 && stats.Total == 4);
        Check("Polish: a plural with an empty form is not translated", set.StatsOf(Language(set, "pl")).Translated == 0);
    }

    void TranslationsAreUndoable(CatalogSet set) {
        var pl = Language(set, "pl");
        var buy = Row(set, "Buy");
        Check("Polish has no Buy yet", set.EntryOf(buy, pl) == null);

        set.SetTranslation(buy, pl, 0, "Kup");
        var entry = set.EntryOf(buy, pl);
        Check("writing a translation adds the message to the file", entry?.StringAt(0) == "Kup");
        Check("with what the template says about it", entry.ExtractedComments.SequenceEqual(["On the shop's button."]) && entry.References.SequenceEqual(["res://ui/shop.tscn"]));
        Check("the file is unsaved now, the others not", pl.Dirty && !Language(set, "de").Dirty && !set.Template.Dirty);

        var de = Language(set, "de");
        var sell = Row(set, "Sell");
        set.SetTranslation(sell, de, 0, "Verkaufen");
        Check("writing clears fuzzy, even with the same text", set.EntryOf(sell, de).Fuzzy == false && de.Dirty);

        Check("each edit is one undo step", set.UndoName == "Translate (de)");
        set.Undo();
        Check("undo brings fuzzy back", set.EntryOf(Row(set, "Sell"), Language(set, "de")).Fuzzy && !Language(set, "de").Dirty);
        set.Undo();
        Check("undo takes the added message out again", set.EntryOf(Row(set, "Buy"), Language(set, "pl")) == null && !Language(set, "pl").Dirty);
        set.Redo();
        Check("redo puts it back", set.EntryOf(Row(set, "Buy"), Language(set, "pl"))?.StringAt(0) == "Kup");

        set.SetFuzzy(Row(set, "Buy"), Language(set, "pl"), true);
        Check("fuzzy can be set by hand", set.EntryOf(Row(set, "Buy"), Language(set, "pl")).Fuzzy);
        set.SetComment(Row(set, "Buy"), Language(set, "pl"), "Short!\nIt is a button.");
        Check("a comment becomes comment lines", set.EntryOf(Row(set, "Buy"), Language(set, "pl")).TranslatorComments.SequenceEqual(["Short!", "It is a button."]));
    }

    void SourcesChangeEverywhere(CatalogSet set) {
        var hello = Row(set, "Hello, {name}!");
        Check("a source that another message has is refused", set.EditSource(hello, null, "Buy", null) != null);

        Check("a source can be changed", set.EditSource(hello, null, "Hi, {name}!", null) == null);
        Check("in the template and in every language", set.Template.Find("Hi, {name}!") != null && Language(set, "de").Find("Hi, {name}!")?.StringAt(0) == "Hallo!"
                                                     && Row(set, "Hi, {name}!") != null && Row(set, "Hello, {name}!") == null);
        set.Undo();
        Check("undo renames it back", Row(set, "Hello, {name}!") != null && Language(set, "de").Find("Hello, {name}!") != null);

        Check("a message can become plural", set.MakePlural(Row(set, "Buy")) == null && Row(set, "Buy").IsPlural);
        Check("and gets as many forms as each language has", set.EntryOf(Row(set, "Buy"), Language(set, "pl")).Strings.Count == 3
                                                             && set.EntryOf(Row(set, "Buy"), Language(set, "de")).Strings.Count == 2
                                                             && set.EntryOf(Row(set, "Buy"), Language(set, "pl")).StringAt(0) == "Kup");
        set.Undo();
        Check("undo makes it singular again", !Row(set, "Buy").IsPlural && set.EntryOf(Row(set, "Buy"), Language(set, "pl")).Strings.Count == 1);

        Check("a message is added to every file", set.AddRow(null, "New text") == null && set.Files.All(f => f.Find("New text") != null));
        Check("not twice", set.AddRow(null, "New text") != null);
        set.DeleteRow(Row(set, "New text"));
        Check("and deleted from every file", set.Files.All(f => f.Find("New text") == null) && Row(set, "New text") == null);
    }

    void PluralRulesResizeForms(CatalogSet set) {
        var de = Language(set, "de");
        var coins = Row(set, "You have {n} coin.");
        Check("a bad rule is refused", set.SetPluralForms(de, "nplurals=two") != null);

        set.SetPluralForms(de, "nplurals=1; plural=0;");
        Check("one form: the plural messages have one string", set.EntryOf(coins, Language(set, "de")).Strings.SequenceEqual(["Du hast {n} Münze."]));
        Check("and the table needs no more rows than Polish", set.MaxForms == 3);
        set.Undo();
        Check("undo brings the dropped form back", set.EntryOf(coins, Language(set, "de")).Strings.Count == 2
                                                   && set.EntryOf(coins, Language(set, "de")).StringAt(1) == "Du hast {n} Münzen.");
    }

    void LanguagesAreAdded(CatalogSet set) {
        Check("a new language follows the pattern of the others", set.PathForLanguage("ar") == $"{Folder}/ar.po");

        var ar = set.AddLanguage("ar");
        Check("Arabic is added as a file of the set", ar != null && set.Files.Contains(ar) && ar.Dirty && ar.SavedText == null);
        Check("with its plural rule", ar.Catalog.Plurals.Count == 6 && ar.Catalog.Language == "ar");
        Check("and every message of the template, untranslated", ar.Catalog.Messages.Count() == 4 && set.EntryOf(Row(set, "You have {n} coin."), ar).Strings.Count == 6);
        Check("the table grows to six plural rows", set.MaxForms == 6);
        Check("a language that is there already is refused", set.AddLanguage("de") == null);

        set.Undo();
        Check("undo takes the language out again", !set.Files.Contains(ar) && set.MaxForms == 3);
        set.Redo();
        Check("redo brings it back", set.Files.Contains(ar));
        set.Undo();
    }

    void TemplateUpdatesTheLanguages() {
        WriteFiles();
        Write("messages.pot", Template.Replace("msgid \"Hello, {name}!\"\nmsgstr \"\"", "msgid \"Gone\"\nmsgstr \"\"\n\nmsgid \"Brand new\"\nmsgstr \"\""));
        var set = CatalogSet.Open($"{Folder}/messages.pot");
        var result = set.MergeTemplate();
        var de = Language(set, "de");

        // German lacks "Brand new"; Polish lacks that, "Buy", "Sell" and "Gone".
        Check("new messages are added", result.Added == 5 && de.Find("Brand new") != null && Language(set, "pl").Catalog.Messages.Count() == 5);
        Check("a message gone from the template becomes obsolete, with its translation", de.Catalog.FindObsolete("Hello, {name}!")?.StringAt(0) == "Hallo!" && de.Find("Hello, {name}!") == null);
        Check("an obsolete message that is back comes back translated", result.Revived == 1 && de.Find("Gone")?.StringAt(0) == "Weg");
        Check("the order follows the template, obsolete ones last", de.Catalog.Entries.Select(e => e.Id).SequenceEqual(["Buy", "Sell", "You have {n} coin.", "Gone", "Brand new", "Hello, {name}!"]));
        Check("the rows follow too", set.Rows.Select(r => r.Id).SequenceEqual(["Buy", "Sell", "You have {n} coin.", "Gone", "Brand new"]));
        set.Undo();
        Check("the whole update is one undo step", Language(set, "de").Find("Hello, {name}!") != null && Language(set, "de").Find("Brand new") == null);
    }

    void ProblemsAreFound() {
        WriteFiles();
        var set = CatalogSet.Open($"{Folder}/de.po");
        var de = Language(set, "de");
        Check("a placeholder left out is a problem", set.ProblemsOf(Row(set, "Hello, {name}!"), de).Any(p => p.Contains("{name} is missing")));
        Check("a good translation has none", set.ProblemsOf(Row(set, "You have {n} coin."), de).Count == 0);

        set.SetTranslation(Row(set, "You have {n} coin."), de, 1, "Du hast {m} Münzen.");
        Check("a placeholder not in the source is one, named with its form", set.ProblemsOf(Row(set, "You have {n} coin."), de).Any(p => p == "other: {m} is not in the source"));

        set.SetTranslation(Row(set, "You have {n} coin."), de, 0, "Eine Münze.");
        Check("a plural form may leave the number out", set.ProblemsOf(Row(set, "You have {n} coin."), de).All(p => !p.StartsWith("one:")));
    }

    void SavingWritesTheFiles() {
        WriteFiles();
        var set = CatalogSet.Open($"{Folder}/de.po");
        set.SetTranslation(Row(set, "Hello, {name}!"), Language(set, "de"), 0, "Hallo, {name}!");
        set.AddLanguage("cs");
        var saved = set.SaveAll();

        Check("only changed files are written, new ones included", saved.Select(p => p.GetFile()).OrderBy(p => p).SequenceEqual(["cs.po", "de.po"]));
        Check("nothing is unsaved after saving", set.Files.All(f => !f.Dirty));
        var written = CatalogSet.ReadText($"{Folder}/de.po");
        Check("the edit is on disk, and nothing else changed", written == (German.Replace("\r\n", "\n") + "\n").Replace("msgstr \"Hallo!\"", "msgstr \"Hallo, {name}!\""));
        Check("the new file reads as Czech with three forms", PoCatalog.Parse(CatalogSet.ReadText($"{Folder}/cs.po")) is { Language: "cs" } cs && cs.Plurals.Count == 3
                                                               && cs.Find(null, "You have {n} coin.")?.Strings.Count == 3);

        Write("pl.po", Polish.Replace("Masz {n} monet.", "Masz {n} monet!"));
        Check("a file changed on disk is noticed", set.ChangedOnDisk().Select(f => f.Path.GetFile()).SequenceEqual(["pl.po"]));
    }

    // ---- the table ---------------------------------------------------------------------------

    async System.Threading.Tasks.Task TheTableShowsPluralsAsRows() {
        WriteFiles();
        var panel = new MisspellPanel();
        AddChild(panel);
        panel.Size = new Vector2(1400, 700);
        await Settle();

        panel.Open($"{Folder}/de.po");
        await Settle();
        var table = panel.Table;
        var set = panel.Catalogs;

        Check("columns: status, context, source, then one per language", table.Columns == 5
                                                                         && table.KindOf(2) == TranslationTable.ColumnKind.Source
                                                                         && table.FileOf(3)?.Label == "de" && table.FileOf(4)?.Label == "pl");
        Check("a language's title says how far it is", table.GetColumnTitle(3) == "de  75%" && table.GetColumnTitle(4) == "pl  0%");

        var coins = Row(set, "You have {n} coin.");
        var item = table.ItemOf(coins);
        Check("a plural message has a row per further form, as many as Polish needs", item != null && item.GetChildCount() == 2);
        Check("the second row shows the plural source", item.GetChild(0).GetText(2) == "You have {n} coins.");
        Check("filled forms show their text", item.GetText(4) == "Masz {n} monetę." && item.GetChild(1).GetText(4) == "Masz {n} monet.");
        Check("an empty form says which numbers it is for", item.GetChild(0).GetText(4) == "few · n = 2–4, 22–24, 32–34, 42–44, …");
        Check("a form German does not have stays empty", item.GetChild(1).GetText(3) == "" && !table.CanEdit(item.GetChild(1), 3) && table.CanEdit(item.GetChild(1), 4));
        Check("its tooltip names the language, the form and its numbers", item.GetChild(0).GetTooltipText(4).StartsWith("pl · few · n = 2–4"));
        Check("a singular message has no further rows", table.ItemOf(Row(set, "Buy")).GetChildCount() == 0);
        Check("the status column marks problems", table.ItemOf(Row(set, "Hello, {name}!")).GetText(0) == "⚠"
                                                   && table.ItemOf(Row(set, "Hello, {name}!")).GetTooltipText(0).Contains("de: {name} is missing"));
        Check("and fuzzy ones", table.ItemOf(Row(set, "Sell")).GetText(0) == "~");

        // Typing into a selected cell replaces it, Enter keeps it and goes down.
        table.SelectCell(coins, 1, 4);
        table._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.M, Unicode = 'M' });
        Check("typing opens the cell editor with what was typed", table.IsEditing && panel.Table.GetNode<TextEdit>("CellEditor").Text == "M");
        panel.Table.GetNode<TextEdit>("CellEditor").Text = "Masz {n} monety.";
        table.CommitEdit();
        await Settle();
        Check("committing writes the form into the file", set.EntryOf(coins, Language(set, "pl")).StringAt(1) == "Masz {n} monety.");
        Check("the cell and the column title show it", table.ItemOf(coins, 1).GetText(4) == "Masz {n} monety." && table.GetColumnTitle(4) == "pl*  25%");
        Check("the panel knows there is something to save", panel.IsDirty && panel.UnsavedPaths().SequenceEqual([$"{Folder}/pl.po"]));

        table.SelectCell(Row(set, "Buy"), 0, 3);
        Check("Enter opens the editor on the text as it is", table.BeginEdit() && panel.Table.GetNode<TextEdit>("CellEditor").Text == "Kaufen");
        table.CancelEdit();
        Check("Escape leaves it as it was", !table.IsEditing && set.EntryOf(Row(set, "Buy"), Language(set, "de")).StringAt(0) == "Kaufen");

        table.SelectCell(Row(set, "Hello, {name}!"), 0, 3);
        Check("a line break is kept in the file and shown as ↵", WriteAndRead(table, "Hallo,\n{name}!") == "Hallo,↵{name}!"
                                                                 && set.EntryOf(Row(set, "Hello, {name}!"), Language(set, "de")).StringAt(0) == "Hallo,\n{name}!");

        // Sources are edited in place too, and the row follows its new key.
        table.SelectCell(Row(set, "Buy"), 0, 2);
        table.WriteCell(table.GetSelected(), 2, "Purchase");
        Check("editing a source renames the message everywhere", Row(set, "Purchase") != null && set.Template.Find("Purchase") != null
                                                                 && table.SelectedRow?.Id == "Purchase");
        panel.Undo();
        Check("panel undo takes it back and rebuilds the table", Row(set, "Buy") != null && table.ItemOf(Row(set, "Buy")) != null);

        // Filters and search.
        table.ShowOnly = TranslationTable.Filter.Plural;
        table.Rebuild();
        Check("the plural filter shows only plural messages", RootRows(table).SequenceEqual(["You have {n} coin."]));
        table.ShowOnly = TranslationTable.Filter.Fuzzy;
        table.Rebuild();
        Check("the fuzzy filter", RootRows(table).SequenceEqual(["Sell"]));
        table.ShowOnly = TranslationTable.Filter.All;
        table.Search = "münze";
        table.Rebuild();
        Check("search looks into translations too, ignoring case", RootRows(table).SequenceEqual(["You have {n} coin."]));
        table.Search = "";
        table.ShowContext = false;
        table.Rebuild();
        Check("the context column can be hidden", table.Columns == 4 && table.ColumnOf(TranslationTable.ColumnKind.Context) < 0);
        table.ShowContext = true;
        table.Rebuild();

        // The details explain the plural forms.
        table.SelectCell(coins, 0, 4);
        panel.Details.ShowRow(table.SelectedRow, table.SelectedFile);
        await Settle();
        var labels = panel.Details.FindChildren("*", nameof(Label), true, false).OfType<Label>().Select(l => l.Text).ToList();
        Check("the details list each language's forms with their numbers", labels.Contains("   few · n = 2–4, 22–24, 32–34, …"));
        Check("and mark the form the tried number takes", labels.Contains("▶ one · n = 1") && labels.Contains("Masz 1 monetę."));

        // An assembly reload takes the set; unsaved edits must come back.
        panel.ForgetSetAsAReloadWould();
        var restored = panel.Catalogs;
        Check("after a reload the set is read again with the unsaved edits on top", restored != null && !ReferenceEquals(restored, set)
                                                                                    && restored.EntryOf(Row(restored, "You have {n} coin."), Language(restored, "pl")).StringAt(1) == "Masz {n} monety."
                                                                                    && Language(restored, "pl").Dirty && !restored.Template.Dirty);
        await Settle();

        // As the editor does with the project's translations: loaded once, before the edit was saved.
        var loaded = ResourceLoader.Load<Translation>($"{Folder}/pl.po");
        TranslationServer.AddTranslation(loaded);
        Check("the loaded translation still has the old text", loaded.GetPluralMessage("You have {n} coin.", "You have {n} coins.", 3) == "");

        var saved = panel.SaveAll();
        Check("saving puts the new text into the translation already loaded", ReferenceEquals(ResourceLoader.Load<Translation>($"{Folder}/pl.po"), loaded)
                                                                              && loaded.GetPluralMessage("You have {n} coin.", "You have {n} coins.", 3) == "Masz {n} monety."
                                                                              && TranslationServer.HasTranslation(loaded));
        TranslationServer.RemoveTranslation(loaded);
        Check("saving writes what was edited", saved.Count == 2 && !panel.IsDirty
                                               && CatalogSet.ReadText($"{Folder}/pl.po").Contains("msgstr[1] \"Masz {n} monety.\""));

        // What counts in the end: Godot reads the written file and picks the same forms.
        var translation = ResourceLoader.Load<Translation>($"{Folder}/pl.po", "", ResourceLoader.CacheMode.Ignore);
        string Pick(int n) => translation?.GetPluralMessage("You have {n} coin.", "You have {n} coins.", n).ToString();
        Check("Godot reads the saved file with its plural rule", Pick(1) == "Masz {n} monetę." && Pick(3) == "Masz {n} monety."
                                                                && Pick(5) == "Masz {n} monet." && Pick(22) == "Masz {n} monety." && Pick(12) == "Masz {n} monet.");
        panel.QueueFree();
    }

    async System.Threading.Tasks.Task KeysLaidOutAsPathsShowAsATree() {
        const string keys = Folder + "/keys";
        DirAccess.MakeDirRecursiveAbsolute(keys);
        string[] ids = ["%welcome/welcome", "%welcome/option/enter", "%welcome/option/good bye", "%quest missing/missing text", "plain"];
        string Po(string language, Dictionary<string, string> texts) {
            var po = PoCatalog.Create(language);
            foreach (var id in ids) po.Entries.Add(new PoEntry { Id = id, Strings = [texts.GetValueOrDefault(id, "")] });
            return po.Write();
        }
        void Save(string name, string text) {
            using var file = FileAccess.Open($"{keys}/{name}", FileAccess.ModeFlags.Write);
            file.StoreString(text);
        }
        Save("arena.pot", Po(null, []));
        // Written as another tool might: Windows line ends, a string split over two lines.
        Save("arena.de.po", Po("de", new() { ["%welcome/welcome"] = "Willkommen", ["%welcome/option/enter"] = "Eintreten", ["%quest missing/missing text"] = "Quest fehlt", ["plain"] = "Schlicht" })
            .Replace("msgstr \"Willkommen\"", "msgstr \"Will\"\n\"kommen\"").Replace("\n", "\r\n"));

        var panel = new MisspellPanel();
        AddChild(panel);
        panel.Size = new Vector2(1400, 700);
        await Settle();
        panel.Open($"{keys}/arena.pot");
        await Settle();
        var table = panel.Table;
        var set = panel.Catalogs;

        Check("a file formatted differently is not unsaved just by being opened", !panel.IsDirty && Language(set, "de").Catalog.Find("%welcome/welcome")?.StringAt(0) == "Willkommen");
        Check("keys laid out as paths open as a tree", table.TreeView);
        Check("the source comes first, where the tree indents", table.KindOf(0) == TranslationTable.ColumnKind.Source && table.KindOf(1) == TranslationTable.ColumnKind.Status);

        var top = Children(table.GetRoot());
        Check("folders and loose keys at the top, in the order of the file", top.Select(i => i.GetText(0)).SequenceEqual(["%welcome/", "%quest missing/", "plain"]));
        var welcome = table.GroupItem("%welcome");
        Check("a folder holds its texts and the folders below it", Children(welcome).Select(i => i.GetText(0)).SequenceEqual(["welcome", "option/"])
                                                                   && Children(table.GroupItem("%welcome/option")).Select(i => i.GetText(0)).SequenceEqual(["enter", "good bye"]));
        var enter = table.ItemOf(Row(set, "%welcome/option/enter"));
        Check("a text shows the last part of its key, the whole key on hover", enter.GetText(0) == "enter" && enter.GetTooltipText(0) == "%welcome/option/enter");
        var de = table.ColumnOf(Language(set, "de"));
        Check("a folder counts what is translated inside it", welcome.GetText(de) == "2/3" && table.GroupItem("%welcome/option").GetText(de) == "1/2"
                                                              && table.GroupItem("%quest missing").GetText(de) == "1/1");
        Check("a folder is no text: only its name can be edited", !table.CanEdit(welcome, de) && table.CanEdit(welcome, 0) && table.RowOf(welcome) == null);

        table.SelectCell(Row(set, "%welcome/option/good bye"), 0, de);
        table.WriteCell(table.GetSelected(), de, "Tschüss");
        Check("translating a text updates the folders above it", table.GroupItem("%welcome/option").GetText(de) == "2/2" && table.GroupItem("%welcome").GetText(de) == "3/3");

        Check("a new text goes in the folder of the selected one", table.SelectedFolder() == "%welcome/option/");
        table.SelectItem(table.GroupItem("%quest missing"), de);
        Check("or in the selected folder", table.SelectedFolder() == "%quest missing/");
        table._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.X, Unicode = 'x' });
        Check("typing on a folder opens no editor", !table.IsEditing);
        table._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.Enter });
        Check("Enter folds a folder", table.GroupItem("%quest missing").Collapsed);

        table.GroupItem("%welcome").Collapsed = true;
        table.Rebuild();
        Check("folded folders stay folded when the table is rebuilt", table.GroupItem("%welcome").Collapsed && table.GroupItem("%quest missing").Collapsed);
        table.SelectCell(Row(set, "%welcome/option/enter"), 0, de);
        Check("selecting a text inside unfolds the folders it is in", !table.GroupItem("%welcome").Collapsed && !table.GroupItem("%welcome/option").Collapsed);

        table.Search = "tschüss";
        table.Rebuild();
        Check("the search keeps only the folders with something found", Children(table.GetRoot()).Select(i => i.GetText(0)).SequenceEqual(["%welcome/"])
                                                                         && table.GroupItem("%welcome/option").GetText(0) == "option/");
        table.Search = "";

        table.Rebuild();
        await Settle();

        // Drag and drop moves keys between folders.
        var questRect = table.GetItemAreaRect(table.GroupItem("%quest missing"), 0);
        Check("a drop on a folder lands in it", table.FolderAt(questRect.GetCenter()) == "%quest missing");
        var enterRect = table.GetItemAreaRect(table.ItemOf(Row(set, "%welcome/option/enter")), 0);
        Check("a drop on a text lands beside it", table.FolderAt(enterRect.GetCenter()) == "%welcome/option");
        Check("a drop below the rows lands at the top", table.FolderAt(new Vector2(20, table.Size.Y - 5)) == "");

        var dragEnter = table.DragDataFor(table.ItemOf(Row(set, "%welcome/option/enter")));
        Check("a text can be dropped in another folder", table._CanDropData(questRect.GetCenter(), dragEnter));
        Check("not where it already is", !table._CanDropData(enterRect.GetCenter(), dragEnter));
        table._DropData(questRect.GetCenter(), dragEnter);
        Check("dropping renames the key in every file, translation and all", Row(set, "%quest missing/enter") != null && Row(set, "%welcome/option/enter") == null
                                                                             && set.Template.Find("%quest missing/enter") != null
                                                                             && Language(set, "de").Find("%quest missing/enter")?.StringAt(0) == "Eintreten");
        Check("the moved text is selected in its new folder", table.SelectedRow?.Id == "%quest missing/enter"
                                                              && table.GetSelected().GetParent() == table.GroupItem("%quest missing"));
        panel.Undo();
        Check("one undo puts it back", Row(set, "%welcome/option/enter") != null && Row(set, "%quest missing/enter") == null);

        var dragOption = table.DragDataFor(table.GroupItem("%welcome/option"));
        Check("a folder cannot go into itself or below itself", table.MovesFor(dragOption, "%welcome/option") == null
                                                                && table.MovesFor(table.DragDataFor(table.GroupItem("%welcome")), "%welcome/option") == null);
        table.Move(dragOption, "%quest missing");
        Check("a folder moves with everything in it", Row(set, "%quest missing/option/enter") != null && Row(set, "%quest missing/option/good bye") != null
                                                      && table.GroupItem("%welcome/option") == null && table.GroupItem("%quest missing/option") != null
                                                      && table.GetSelected() == table.GroupItem("%quest missing/option"));
        table.Move(table.DragDataFor(table.GroupItem("%quest missing/option")), "");
        Check("and to the top level", Row(set, "option/enter") != null && table.GroupItem("option") != null);
        panel.Undo();
        panel.Undo();

        set.AddRow(null, "%quest missing/welcome");
        table.Rebuild();
        table.Move(table.DragDataFor(table.ItemOf(Row(set, "%welcome/welcome"))), "%quest missing");
        Check("a move onto a key that is there is refused, and changes nothing", Row(set, "%welcome/welcome") != null
                                                                                 && panel.StatusText.Contains("already there"));
        panel.Undo();

        // Renaming folders.
        var source = table.ColumnOf(TranslationTable.ColumnKind.Source);
        table.SelectItem(table.GroupItem("%welcome/option"), de);
        table._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.F2 });
        Check("F2 on a folder edits its whole path", table.IsEditing && table.GetNode<TextEdit>("CellEditor").Text == "%welcome/option");
        table.CancelEdit();

        table.GroupItem("%welcome/option").Collapsed = true;
        table.WriteCell(table.GroupItem("%welcome/option"), source, "%welcome/choices");
        Check("renaming a folder renames every key inside, in every file", Row(set, "%welcome/choices/enter") != null && Row(set, "%welcome/choices/good bye") != null
                                                                          && Row(set, "%welcome/option/enter") == null
                                                                          && Language(set, "de").Find("%welcome/choices/enter")?.StringAt(0) == "Eintreten"
                                                                          && set.Template.Find("%welcome/choices/good bye") != null);
        Check("the renamed folder is selected and stays folded", table.GetSelected() == table.GroupItem("%welcome/choices") && table.GroupItem("%welcome/choices").Collapsed);
        Check("keys outside it stay", Row(set, "%welcome/welcome") != null);

        table.WriteCell(table.GroupItem("%quest missing"), source, "/%quest/");
        Check("slashes around the name are dropped", table.GroupItem("%quest") != null && Row(set, "%quest/missing text") != null);
        table.WriteCell(table.GroupItem("%quest"), source, "%welcome/quest");
        Check("changing the rest of the path moves the folder", Row(set, "%welcome/quest/missing text") != null && table.GroupItem("%welcome/quest") != null);
        table.WriteCell(table.GroupItem("%welcome"), source, " ");
        Check("an empty name is refused", Row(set, "%welcome/welcome") != null && panel.StatusText == "A folder needs a name.");

        panel.Undo();
        panel.Undo();
        panel.Undo();
        Check("each rename is one undo step", Row(set, "%welcome/option/enter") != null && Row(set, "%quest missing/missing text") != null && table.GroupItem("%welcome/choices") == null);

        panel.ShowAsTree(false);
        Check("without the tree nothing can be dragged", table.DragDataFor(table.ItemOf(Row(set, "plain"))) == null);
        Check("without the tree the keys are rows of their own again", table.KindOf(0) == TranslationTable.ColumnKind.Status && table.GroupItem("%welcome") == null
                                                                       && Children(table.GetRoot()).Count == 5 && table.ItemOf(Row(set, "%welcome/option/enter")).GetText(table.ColumnOf(TranslationTable.ColumnKind.Source)) == "%welcome/option/enter");
        panel.QueueFree();
    }

    static List<TreeItem> Children(TreeItem parent) {
        var children = new List<TreeItem>();
        for (var item = parent?.GetFirstChild(); item != null; item = item.GetNext()) children.Add(item);
        return children;
    }

    static string WriteAndRead(TranslationTable table, string text) {
        var item = table.GetSelected();
        var column = table.GetSelectedColumn();
        table.WriteCell(item, column, text);
        return table.GetSelected().GetText(column);
    }

    static List<string> RootRows(TranslationTable table) {
        var rows = new List<string>();
        for (var item = table.GetRoot()?.GetFirstChild(); item != null; item = item.GetNext()) rows.Add(table.RowOf(item).Id);
        return rows;
    }

    // ---- helpers -----------------------------------------------------------------------------

    static CatalogFile Language(CatalogSet set, string locale) => set.Languages.First(f => f.Locale == locale);

    static CatalogRow Row(CatalogSet set, string id) => set.Rows.FirstOrDefault(r => r.Id == id);

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
