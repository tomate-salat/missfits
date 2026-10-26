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
            LiteralsAreRead();
            await UsagesAreFound();
            StringsAreWrittenAnew();
            await RenamesChangeTheCode();
            FilesKeepTheirBytes();
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
        // Not the project: its scripts, this test among them, would be offered for renames.
        panel.Details.Usages = new KeyUsages(NoCode);
        panel.Size = new Vector2(1400, 700);
        await Settle();

        panel.Open($"{Folder}/de.po");
        await Settle();
        var table = panel.Table;
        var set = panel.Catalogs;

        Check("columns: context first, as the set has contexts, then source, then one per language", table.Columns == 4
                                                                         && table.KindOf(0) == TranslationTable.ColumnKind.Context
                                                                         && table.KindOf(1) == TranslationTable.ColumnKind.Source
                                                                         && table.FileOf(2)?.Label == "de" && table.FileOf(3)?.Label == "pl");
        var src = table.ColumnOf(TranslationTable.ColumnKind.Source);
        var de = table.ColumnOf(Language(set, "de"));
        var pl = table.ColumnOf(Language(set, "pl"));
        Check("a language's title says which it is and how far", table.GetColumnTitle(de).StartsWith("de") && table.GetColumnTitle(de).EndsWith("  75%")
                                                                 && table.GetColumnTitle(pl).StartsWith("pl") && table.GetColumnTitle(pl).EndsWith("  0%"));

        var coins = Row(set, "You have {n} coin.");
        var item = table.ItemOf(coins);
        Check("a plural message has a row per further form, as many as Polish needs", item != null && item.GetChildCount() == 2);
        Check("the second row shows the plural source", item.GetChild(0).GetText(src) == "You have {n} coins.");
        Check("filled forms show their text", item.GetText(pl) == "Masz {n} monetę." && item.GetChild(1).GetText(pl) == "Masz {n} monet.");
        Check("an empty form says which numbers it is for", item.GetChild(0).GetText(pl) == "few · n = 2–4, 22–24, 32–34, 42–44, …");
        Check("a form German does not have stays empty", item.GetChild(1).GetText(de) == "" && !table.CanEdit(item.GetChild(1), de) && table.CanEdit(item.GetChild(1), pl));
        Check("its tooltip names the language, the form and its numbers", item.GetChild(0).GetTooltipText(pl).StartsWith("pl · few · n = 2–4"));
        Check("a singular message has no further rows", table.ItemOf(Row(set, "Buy")).GetChildCount() == 0);
        Check("a text with a problem says so on hover", table.HasProblems(Row(set, "Hello, {name}!")) && !table.HasProblems(Row(set, "Buy"))
                                                         && table.ItemOf(Row(set, "Hello, {name}!")).GetTooltipText(src).Contains("de: {name} is missing"));
        Check("and where it is fuzzy", table.ItemOf(Row(set, "Sell")).GetTooltipText(src).Contains("Fuzzy in de"));
        Check("a further plural form names itself in the source column", item.GetChild(1).GetText(src) == "form 2");

        // Typing into a selected cell replaces it, Enter keeps it and goes down.
        table.SelectCell(coins, 1, pl);
        table._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.M, Unicode = 'M' });
        Check("typing opens the cell editor with what was typed", table.IsEditing && panel.Table.GetNode<TextEdit>("CellEditor").Text == "M");
        panel.Table.GetNode<TextEdit>("CellEditor").Text = "Masz {n} monety.";
        table.CommitEdit();
        await Settle();
        Check("committing writes the form into the file", set.EntryOf(coins, Language(set, "pl")).StringAt(1) == "Masz {n} monety.");
        Check("the cell and the column title show it", table.ItemOf(coins, 1).GetText(pl) == "Masz {n} monety." && table.GetColumnTitle(pl).StartsWith("● pl") && table.GetColumnTitle(pl).EndsWith("  25%"));
        Check("the panel knows there is something to save", panel.IsDirty && panel.UnsavedPaths().SequenceEqual([$"{Folder}/pl.po"]));

        table.SelectCell(Row(set, "Buy"), 0, de);
        Check("Enter opens the editor on the text as it is", table.BeginEdit() && panel.Table.GetNode<TextEdit>("CellEditor").Text == "Kaufen");
        table.CancelEdit();
        Check("Escape leaves it as it was", !table.IsEditing && set.EntryOf(Row(set, "Buy"), Language(set, "de")).StringAt(0) == "Kaufen");

        table.SelectCell(Row(set, "Hello, {name}!"), 0, de);
        Check("a line break is kept in the file and shown as ↵", WriteAndRead(table, "Hallo,\n{name}!") == "Hallo,↵{name}!"
                                                                 && set.EntryOf(Row(set, "Hello, {name}!"), Language(set, "de")).StringAt(0) == "Hallo,\n{name}!");

        // Sources are edited in place too, and the row follows its new key.
        table.SelectCell(Row(set, "Buy"), 0, src);
        table.WriteCell(table.GetSelected(), src, "Purchase");
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
        Check("the context column can be hidden", table.Columns == 3 && table.ColumnOf(TranslationTable.ColumnKind.Context) < 0);
        table.ShowContext = true;
        table.Rebuild();

        // The details explain the plural forms.
        table.SelectCell(coins, 0, pl);
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
        // Not the project: its scripts, this test among them, would be offered for renames.
        panel.Details.Usages = new KeyUsages(NoCode);
        panel.Size = new Vector2(1400, 700);
        await Settle();
        panel.Open($"{keys}/arena.pot");
        await Settle();
        var table = panel.Table;
        var set = panel.Catalogs;

        Check("a file formatted differently is not unsaved just by being opened", !panel.IsDirty && Language(set, "de").Catalog.Find("%welcome/welcome")?.StringAt(0) == "Willkommen");
        Check("keys laid out as paths open as a tree", table.TreeView);
        Check("the source comes first, where the tree indents", table.KindOf(0) == TranslationTable.ColumnKind.Source && table.ColumnOf(TranslationTable.ColumnKind.Context) < 0);

        var top = Children(table.GetRoot());
        Check("folders and loose keys at the top, in the order of the file", top.Select(i => i.GetText(0)).SequenceEqual(["%welcome", "%quest missing", "plain"]));
        var welcome = table.GroupItem("%welcome");
        Check("a folder holds the folders below it first, then its texts", Children(welcome).Select(i => i.GetText(0)).SequenceEqual(["option", "welcome"])
                                                                   && Children(table.GroupItem("%welcome/option")).Select(i => i.GetText(0)).SequenceEqual(["enter", "good bye"]));
        var enter = table.ItemOf(Row(set, "%welcome/option/enter"));
        Check("a text shows the last part of its key, the whole key on hover", enter.GetText(0) == "enter" && enter.GetTooltipText(0).Split('\n')[0] == "%welcome/option/enter");
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
        Check("the search keeps only the folders with something found", Children(table.GetRoot()).Select(i => i.GetText(0)).SequenceEqual(["%welcome"])
                                                                         && table.GroupItem("%welcome/option").GetText(0) == "option");
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
        Check("F2 on a folder edits just its name", table.IsEditing && table.GetNode<TextEdit>("CellEditor").Text == "option");
        table.CancelEdit();

        table.GroupItem("%welcome/option").Collapsed = true;
        table.WriteCell(table.GroupItem("%welcome/option"), source, "choices");
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

        // Deleting folders.
        table.SelectItem(table.GroupItem("%welcome/option"), de);
        table._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.Delete });
        var dialog = table.GetNode<ConfirmationDialog>("DeleteFolderDialog");
        Check("Delete on a folder asks first, saying how much goes", dialog.Visible && dialog.DialogText.StartsWith("Delete %welcome/option/ and the 2 texts")
                                                                     && Row(set, "%welcome/option/enter") != null);
        dialog.Hide();
        dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
        Check("confirming deletes every text inside from every file", Row(set, "%welcome/option/enter") == null && Row(set, "%welcome/option/good bye") == null
                                                                      && set.Files.All(f => f.Find("%welcome/option/enter") == null)
                                                                      && table.GroupItem("%welcome/option") == null);
        Check("and nothing outside it", Row(set, "%welcome/welcome") != null && Row(set, "%quest missing/missing text") != null);
        Check("the folder it was in is selected", table.GetSelected() == table.GroupItem("%welcome"));
        table.DeleteFolder("%welcome");
        Check("a folder goes with the folders inside it", set.Rows.All(r => !r.Id.StartsWith("%welcome/")) && table.GroupItem("%welcome") == null);
        panel.Undo();
        panel.Undo();
        Check("each delete is one undo step, translations and all", Row(set, "%welcome/option/enter") != null
                                                                    && Language(set, "de").Find("%welcome/option/enter")?.StringAt(0) == "Eintreten"
                                                                    && table.GroupItem("%welcome/option") != null);

        // Keys are renamed like folders in the tree: just the name.
        table.SelectCell(Row(set, "%welcome/option/enter"), 0, source);
        table.BeginEdit();
        Check("editing a key in the tree shows just its name", table.GetNode<TextEdit>("CellEditor").Text == "enter");
        table.CancelEdit();
        table.WriteCell(table.ItemOf(Row(set, "%welcome/option/enter")), source, "go in");
        Check("and the new name stays in the key's folder", Row(set, "%welcome/option/go in") != null && Row(set, "go in") == null
                                                            && table.SelectedRow?.Id == "%welcome/option/go in");
        panel.Undo();

        // Copying gives the whole path, though the tree shows only the name.
        table.SelectCell(Row(set, "%welcome/option/enter"), 0, source);
        Check("Ctrl+C on a key copies it whole", table.CopyText(table.GetSelected(), source) == "%welcome/option/enter");
        Check("on a folder too", table.CopyText(table.GroupItem("%welcome/option"), source) == "%welcome/option");
        Check("on a translation it copies the translation", table.CopyText(table.GetSelected(), de) == "Eintreten");

        // Right-click below the rows: a new folder at the top.
        table.SelectCell(Row(set, "%welcome/option/enter"), 0, source);
        table.EmitSignal(Tree.SignalName.EmptyClicked, new Vector2(20, table.Size.Y - 5), (long) MouseButton.Right);
        var menu = table.GetNode<PopupMenu>("CellMenu");
        Check("a right-click below the rows lets go of the selection and offers a new text and folder", table.GetSelected() == null && menu.Visible
                                                                                                         && menu.ItemCount == 2 && menu.GetItemText(0) == "New text" && menu.GetItemText(1) == "New folder");
        menu.Hide();
        table.RunMenu(TranslationTable.MenuId.NewText);
        var messageDialog = panel.GetNode<ConfirmationDialog>("MessageDialog");
        Check("a new text asks for its key", messageDialog.Visible);
        messageDialog.Hide();
        table.SelectCell(Row(set, "%welcome/option/enter"), 0, source);
        table.EmitSignal(Tree.SignalName.ItemMouseSelected, Vector2.Zero, (long) MouseButton.Right);
        Check("on a text, new text and folder come first", menu.GetItemText(0) == "New text here" && menu.GetItemText(1) == "New folder here");
        menu.Hide();
        table.SelectItem(table.GroupItem("%welcome"), source);
        table.EmitSignal(Tree.SignalName.ItemMouseSelected, Vector2.Zero, (long) MouseButton.Right);
        Check("on a folder too", menu.GetItemText(0) == "New text inside" && menu.GetItemText(1) == "New folder inside");
        menu.Hide();
        table.DeselectAll();
        table.RunMenu(TranslationTable.MenuId.NewTopFolder);
        Check("which is made at the top", table.GroupItem("new folder") != null && table.GroupItem("new folder").GetParent() == table.GetRoot() && table.IsEditing);
        table.CancelEdit();
        table.DeleteFolder("new folder");
        Check("with nothing selected, the folder of a new text is the top", table.SelectedFolder() == null);

        // New folders: empty until a text goes in, and never saved as such.
        table.SelectItem(table.GroupItem("%welcome"), de);
        var made = table.NewFolder("%welcome");
        Check("a new folder is made in the selected one, its name open for typing", made == "%welcome/new folder"
                                                                                    && table.IsEditing && table.GetNode<TextEdit>("CellEditor").Text == "new folder");
        table.GetNode<TextEdit>("CellEditor").Text = "rewards";
        table.CommitEdit();
        Check("typing names it", table.GroupItem("%welcome/rewards") != null && table.GroupItem("%welcome/new folder") == null
                                 && table.GetSelected() == table.GroupItem("%welcome/rewards"));
        Check("an empty folder is shown greyed, with no counts, and changes no file", table.IsEmptyFolder("%welcome/rewards")
                                                                                     && table.GroupItem("%welcome/rewards").GetText(de) == "" && set.Files.All(f => f.Catalog.Entries.All(e => !e.Id.Contains("rewards"))));
        Check("it sits with the folders, before the texts", Children(table.GroupItem("%welcome")).Select(i => i.GetText(0)).SequenceEqual(["option", "rewards", "welcome"]));
        Check("a second new folder is named apart", table.NewFolder("%welcome") == "%welcome/new folder" && table.NewFolder("%welcome") == "%welcome/new folder 2");
        table.CancelEdit();
        table.DeleteFolder("%welcome/new folder");
        table.AskDeleteFolder("%welcome/new folder 2");
        Check("an empty folder is deleted without asking", table.GroupItem("%welcome/new folder") == null && table.GroupItem("%welcome/new folder 2") == null
                                                           && !table.GetNode<ConfirmationDialog>("DeleteFolderDialog").Visible);

        table.Rebuild();
        Check("it lasts while the table is rebuilt", table.GroupItem("%welcome/rewards") != null);
        table.Move(table.DragDataFor(table.ItemOf(Row(set, "%welcome/welcome"))), "%welcome/rewards");
        Check("a text dropped in makes it a folder like any other", Row(set, "%welcome/rewards/welcome") != null && !table.IsEmptyFolder("%welcome/rewards")
                                                                   && table.GroupItem("%welcome/rewards").GetText(de) == "1/1");
        panel.Undo();
        Check("once it had a text, an undo that takes the text out takes the folder too", Row(set, "%welcome/welcome") != null && table.GroupItem("%welcome/rewards") == null);

        var empty = table.NewFolder("");
        table.CancelEdit();
        table.Move(table.DragDataFor(table.GroupItem(empty)), "%quest missing");
        Check("an empty folder can be dragged into another", table.GroupItem("%quest missing/new folder") != null && table.GroupItem(empty) == null);
        table.WriteCell(table.GroupItem("%quest missing/new folder"), source, "later");
        Check("and renamed", table.GroupItem("%quest missing/later") != null && table.IsEmptyFolder("%quest missing/later"));
        table.Search = "Willkommen";
        table.Rebuild();
        Check("a search hides empty folders", table.GroupItem("%quest missing/later") == null);
        table.Search = "";
        table.Rebuild();
        table.DeleteFolder("%quest missing/later");

        panel.ShowAsTree(false);
        Check("without the tree nothing can be dragged", table.DragDataFor(table.ItemOf(Row(set, "plain"))) == null);
        Check("without the tree the keys are rows of their own again", table.KindOf(0) == TranslationTable.ColumnKind.Source && table.GroupItem("%welcome") == null
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

    // ---- usages ------------------------------------------------------------------------------

    const string UsageFolder = "user://misspell_usages";
    /// <summary>A folder with no code at all (it need not even be there).</summary>
    const string NoCode = "user://misspell_no_code";

    void LiteralsAreRead() {
        List<string> Texts(string source, string extension) => KeyUsages.Literals(source, extension).Select(l => l.Text).ToList();
        List<string> Plain(string source, string extension) => KeyUsages.Literals(source, extension).Where(l => !l.IsPattern).Select(l => l.Text).ToList();

        Check("C#: plain, escaped and verbatim strings", Texts("""Tr("Buy"); Tr("a\"b\n"); Tr(@"c""d");""", "cs").SequenceEqual(["Buy", "a\"b\n", "c\"d"]));
        Check("C#: comments and chars are no strings", Texts("// \"no\"\n/* \"no\" */ var c = '\"'; Tr(\"yes\");", "cs").SequenceEqual(["yes"]));
        Check("C#: an interpolated string with holes is no plain string, the strings in its holes are",
            Plain("""var s = $"{Tr("Close", "menu")} · {n}"; var t = $"{{plain}}";""", "cs").SequenceEqual(["Close", "menu", "{plain}"]));
        Check("C#: raw strings, over several lines without their indentation",
            Texts("var a = \"\"\"raw \"q\" \"\"\";\nvar b = \"\"\"\n    one\n      two\n    \"\"\";", "cs").SequenceEqual(["raw \"q\" ", "one\n  two"]));
        Check("C#: the line a string starts on", KeyUsages.Literals("a();\n\nTr(\"x\");", "cs").Single().Line == 3);

        Check("GDScript: both quotes, raw and triple, no comments",
            Texts("tr(\"a\") # \"no\"\ntr('b\\'c')\nvar r = r\"d\\e\"\nvar t = \"\"\"multi\nline\"\"\"", "gd").SequenceEqual(["a", "b'c", "d\\e", "multi\nline"]));

        string ContextOf(string source, string extension, string text) {
            var literal = KeyUsages.Literals(source, extension).First(l => l.Text == text);
            return literal.AnyContext ? "any" : literal.Context ?? "none";
        }
        Check("C#: a translation call gives its message the context", ContextOf("""Tr("Close", "menu");""", "cs", "Close") == "menu");
        Check("C#: without one, the message has none", ContextOf("""Tr("Close");""", "cs", "Close") == "none" && ContextOf("""Tr("Close", "");""", "cs", "Close") == "none");
        Check("C#: TrN's context is its fourth argument, or named",
            ContextOf("""TrN("a coin", "{n} coins", n, "shop");""", "cs", "a coin") == "shop" && ContextOf("""TrN("a coin", "{n} coins", n, context: "bank");""", "cs", "a coin") == "bank");
        Check("C#: a context that is no string may be any", ContextOf("""Tr("Close", where);""", "cs", "Close") == "any");
        Check("C#: a string outside a call may be any", ContextOf("""var key = "Close";""", "cs", "Close") == "any" && ContextOf("""Tr("Close" + x);""", "cs", "Close") == "any");
        Check("C#: calls inside interpolation holes and other calls count",
            ContextOf("""Show($"{Tr("Close", "menu"),10} · {n}");""", "cs", "Close") == "menu" && ContextOf("""Show(Tr("Close", F(a, b)), "x");""", "cs", "Close") == "any");
        Check("GDScript: tr and tr_n with contexts, StringNames too",
            ContextOf("tr(\"Close\", \"door\")", "gd", "Close") == "door" && ContextOf("tr_n(&\"a coin\", \"coins\", n, &\"shop\")", "gd", "a coin") == "shop"
            && ContextOf("tr(r\"Close\")", "gd", "Close") == "none");
        Check("scenes: any context", ContextOf("text = \"Close\"", "tscn", "Close") == "any");

        List<string> PartsOf(string source, string extension) => KeyUsages.Literals(source, extension).FirstOrDefault(l => l.IsPattern)?.Parts;
        Check("C#: an interpolated string is a pattern of its parts", PartsOf("""Tr($"shop/{item}/name");""", "cs")?.SequenceEqual(["shop/", "/name"]) == true);
        Check("C#: string.Format's holes", PartsOf("""string.Format("x/{0:N2} {{y}}", v);""", "cs")?.SequenceEqual(["x/", " {y}"]) == true);
        Check("GDScript: % and its escape", PartsOf("tr(\"a %s b %-3d%%\" % [x, y])", "gd")?.SequenceEqual(["a ", " b ", "%"]) == true);
        Check("GDScript: format's holes", PartsOf("\"x/{name}\".format({\"name\": n})", "gd")?.SequenceEqual(["x/", ""]) == true);
        Check("a string with a % but no format is plain", PartsOf("tr(\"100%\")", "gd") == null && PartsOf("""Tr("{0}");""", "cs") == null);

        Check("scenes: property strings, not the headers",
            Texts("[node name=\"Buy\" type=\"Button\"]\ntext = \"Buy\"\ntooltip_text = \"Say \\\"hi\\\"\nthere\"", "tscn").SequenceEqual(["Buy", "Say \"hi\"\nthere"]));
    }

    async System.Threading.Tasks.Task UsagesAreFound() {
        DirAccess.MakeDirRecursiveAbsolute($"{UsageFolder}/ui");
        DirAccess.MakeDirRecursiveAbsolute($"{UsageFolder}/ignored");
        WriteAt($"{UsageFolder}/ui/shop.tscn", "[gd_scene format=3]\n\n[node name=\"Buy\" type=\"Button\"]\ntext = \"Buy\"\n");
        WriteAt($"{UsageFolder}/Shop.cs", "class Shop {\n    // Tr(\"Buy\") in a comment\n    string Label => Tr(\"Buy\");\n}");
        WriteAt($"{UsageFolder}/shop.gd", "func _ready():\n\tprint(tr(\"Sell\", \"verb\"))\n");
        WriteAt($"{UsageFolder}/ignored/.gdignore", "");
        WriteAt($"{UsageFolder}/ignored/old.gd", "tr(\"Buy\")");

        var usages = new KeyUsages(UsageFolder);
        var buy = usages.Of("Buy", null);
        Check("a text is found in scenes and scripts, by file and line", buy.Select(u => u.Reference).SequenceEqual([$"{UsageFolder}/Shop.cs:3", $"{UsageFolder}/ui/shop.tscn:4"]));
        Check("with its line of code", buy[0].Code == "string Label => Tr(\"Buy\");");
        Check("a folder with .gdignore is left out", buy.All(u => !u.Path.Contains("ignored")));
        Check("an unused text has no usages", usages.Of("Hello, {name}!", null).Count == 0);
        Check("a call's context picks the text", usages.Of("Sell", "verb").Count == 1 && usages.Of("Sell", null).Count == 0 && usages.Of("Sell", "noun").Count == 0);

        WriteAt($"{UsageFolder}/shop.gd", "func _ready():\n\tpass\n\tprint(tr(\"Buy\"))\n");
        // In the same second as the first read: the modification time alone would not tell.
        DirAccess.RemoveAbsolute($"{UsageFolder}/Shop.cs");
        usages.Refresh();
        Check("after changes the files are read again", usages.Of("Buy", null).Select(u => u.Reference).SequenceEqual([$"{UsageFolder}/shop.gd:3", $"{UsageFolder}/ui/shop.tscn:4"])
                                                        && usages.Of("Sell", "verb").Count == 0);

        // Guesses: strings with holes, and folder names put before something.
        WriteAt($"{UsageFolder}/Items.cs", "class Items {\n    string Name(string item) => Tr($\"shop/{item}\");\n    string Any(string a, string b) => $\"{a}/{b}\";\n}");
        WriteAt($"{UsageFolder}/items.gd", "const MENU = \"menu/\"\nfunc price(item):\n\treturn tr(\"price/%s\" % item)\n");
        usages.Refresh();
        Check("a string with holes may make a text", usages.GuessesOf("shop/sword").Select(u => u.Reference).SequenceEqual([$"{UsageFolder}/Items.cs:2"]));
        Check("one with marks only between its holes guesses nothing", usages.GuessesOf("x/y").Count == 0);
        Check("% makes a pattern", usages.GuessesOf("price/sword").Select(u => u.Line).SequenceEqual([3]));
        Check("a folder's guesses: what may make its texts", usages.GuessesIn("weapons", ["weapons/sword"]).Count == 0 && usages.GuessesIn("all", ["all/shop/sword"]).Count == 0
                                                             && usages.GuessesIn("shop", ["shop/sword"]).Count == 1 && usages.GuessesIn("other", ["other/x"]).Count == 0);
        Check("and what starts inside it, with or without texts", usages.GuessesIn("menu", []).Select(u => u.Line).SequenceEqual([1])
                                                                 && usages.GuessesIn("menu", ["menu/close"]).Count == 1);
        Check("a folder name with its separator is still a plain use too", usages.Of("menu/", null).Count == 1);

        var keyFolder = "user://misspell_usage_keys";
        DirAccess.MakeDirRecursiveAbsolute(keyFolder);
        WriteAt($"{keyFolder}/keys.pot", "msgid \"\"\nmsgstr \"\"\n\nmsgid \"shop/sword\"\nmsgstr \"\"\n\nmsgid \"shop/shield\"\nmsgstr \"\"\n\nmsgid \"menu/close\"\nmsgstr \"\"\n\nmsgid \"gone/old\"\nmsgstr \"\"\n");
        WriteAt($"{UsageFolder}/Shield.cs", "class Shield {\n    string Name => Tr(\"shop/shield\");\n    string Other => Tr(\"shop/sword\");\n}");

        // Probably unused: not even a part of the key found.
        WriteAt($"{UsageFolder}/Sections.cs", "class Sections {\n    const string Section = \"quests/main\";\n    string Deep(string x) => Tr($\"deep/a/{x}/end\");\n}");
        usages.Refresh();
        Check("a used text is not unused", !usages.ProbablyUnused("shop/sword", null));
        Check("nor one a string with holes may make", !usages.ProbablyUnused("shop/axe", null) && !usages.ProbablyUnused("menu/sub/x", null));
        Check("nor one whose folder is named as a string", !usages.ProbablyUnused("quests/main/intro", null));
        Check("nor one whose folder a string with holes starts in", !usages.ProbablyUnused("deep/a/b/other", null) && !usages.ProbablyUnused("deep/elsewhere", null));
        Check("one with nothing of its key found is", usages.ProbablyUnused("gone/old", null) && usages.ProbablyUnused("lonely", null) && usages.ProbablyUnused("questsx/main/intro", null));
        WriteFiles();
        var panel = new MisspellPanel();
        AddChild(panel);
        panel.Size = new Vector2(1400, 700);
        await Settle();
        panel.Open($"{keyFolder}/keys.pot");
        panel.Usages = new KeyUsages(UsageFolder);
        panel.ShowAsTree(true);
        await Settle();
        panel.Table.GroupItem("shop")?.Select(0);
        await Settle();
        var folderTexts = panel.Details.FindChildren("*", nameof(RichTextLabel), true, false).OfType<RichTextLabel>().Where(l => l.IsVisibleInTree()).Select(l => l.Text).ToList();
        var folderLabels = panel.Details.FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.IsVisibleInTree()).Select(l => l.Text).ToList();
        Check("a selected folder shows in the details", folderLabels.Contains("shop/") && folderLabels.Contains("2 texts"));

        var table = panel.Table;
        Check("the tree marks a folder of only unused texts", table.GroupItem("gone")?.GetTooltipText(table.ColumnOf(TranslationTable.ColumnKind.Source)).Contains("Probably unused") == true
                                                              && table.GroupItem("shop")?.GetTooltipText(table.ColumnOf(TranslationTable.ColumnKind.Source)).Contains("Probably unused") == false);
        var gone = Row(panel.Catalogs, "gone/old");
        Check("and the unused text, in its tooltip", table.ItemOf(gone)?.GetTooltipText(table.ColumnOf(TranslationTable.ColumnKind.Source)).Contains(TranslationTable.UnusedTip) == true);
        table.ShowOnly = TranslationTable.Filter.Unused;
        table.Rebuild();
        Check("the filter shows only the probably unused", table.ItemOf(gone) != null && table.ItemOf(Row(panel.Catalogs, "shop/sword")) == null && table.ItemOf(Row(panel.Catalogs, "menu/close")) == null);
        table.ShowOnly = TranslationTable.Filter.All;
        table.Rebuild();
        panel.Details.ShowRow(gone, null);
        Check("the details say it plainly", panel.Details.FindChildren("*", nameof(RichTextLabel), true, false).OfType<RichTextLabel>().Any(l => l.Text.Contains("Probably unused")));
        table.GroupItem("shop")?.Select(0);
        await Settle();
        Check("with where its texts are used, by name", folderTexts.Any(t => t.Contains("[b]shield[/b]") && t.IndexOf("[b]shield[/b]") < t.IndexOf("[b]sword[/b]") && t.Contains($"[url={UsageFolder}/Shield.cs:2]")));
        Check("grouped by file: its name once, then a row per use", folderTexts.Any(t => t.Split("[b]Shield.cs[/b]").Length == 2 && t.Contains($"[url={UsageFolder}/Shield.cs:3]")));
        Check("and what may make them", folderTexts.Any(t => t.Contains($"[url={UsageFolder}/Items.cs:2]")));
        panel.QueueFree();
        await Settle();

        panel = new MisspellPanel();
        AddChild(panel);
        panel.Size = new Vector2(1400, 700);
        await Settle();
        panel.Open($"{Folder}/de.po");
        panel.Usages = new KeyUsages(UsageFolder);
        panel.Details.ShowRow(Row(panel.Catalogs, "Buy"), null);
        await Settle();
        var shown = panel.Details.FindChildren("*", nameof(RichTextLabel), true, false).OfType<RichTextLabel>().Select(l => l.Text).ToList();
        Check("the details link where the text is used", shown.Any(t => t.Contains($"[url={UsageFolder}/ui/shop.tscn:4]") && t.Contains("text = \"[/color]")));
        Check("with the string marked in its line of code", shown.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"\[color=#[0-9a-f]+\]Buy\[/color\]")));
        panel.Details.ShowRow(Row(panel.Catalogs, "Hello, {name}!"), null);
        shown = panel.Details.FindChildren("*", nameof(RichTextLabel), true, false).OfType<RichTextLabel>().Select(l => l.Text).ToList();
        Check("and say when it is used nowhere", shown.Any(t => t.Contains(TranslationTable.UnusedTip)));
        panel.QueueFree();
        await Settle();
    }

    // ---- renaming in the code ----------------------------------------------------------------

    void StringsAreWrittenAnew() {
        string Rewrite(string source, string extension, int line, string old, string @new, string context = null) {
            var usage = new KeyUsages.Usage("x", line, "", null);
            return CodeRename.Rewrite(source, extension, [new CodeRename.Change(usage, new CodeRename.Rename(old, context, @new))], []);
        }

        Check("C#: escapes kept", Rewrite("Tr(\"a\");", "cs", 1, "a", "say \"hi\"\\now") == "Tr(\"say \\\"hi\\\"\\\\now\");");
        Check("C#: verbatim doubles its quotes", Rewrite("Tr(@\"a\");", "cs", 1, "a", "b\"c") == "Tr(@\"b\"\"c\");");
        Check("C#: an interpolated string without holes doubles braces", Rewrite("Tr($\"a\");", "cs", 1, "a", "{b}") == "Tr($\"{{b}}\");");
        Check("C#: raw stays raw", Rewrite("Tr(\"\"\"a\"\"\");", "cs", 1, "a", "b/c") == "Tr(\"\"\"b/c\"\"\");");
        var problems = new List<string>();
        var unchanged = CodeRename.Rewrite("Tr(\"\"\"a\"\"\");", "cs", [new CodeRename.Change(new KeyUsages.Usage("x", 1, "", null), new CodeRename.Rename("a", null, "\"\"\"x"))], problems);
        Check("C#: a text a raw string cannot hold is left, and said why", unchanged == "Tr(\"\"\"a\"\"\");" && problems.Count == 1 && problems[0].Contains("by hand"));
        Check("C#: only the string on that line, exactly that text, not in comments",
            Rewrite("Tr(\"a\"); // \"a\"\nTr(\"a\");\nTr(\"ab\");", "cs", 1, "a", "b") == "Tr(\"b\"); // \"a\"\nTr(\"a\");\nTr(\"ab\");");
        Check("C#: only the call in the renamed context", Rewrite("Tr(\"Close\", \"menu\"); Tr(\"Close\", \"door\");", "cs", 1, "Close", "Shut", "menu")
                                                          == "Tr(\"Shut\", \"menu\"); Tr(\"Close\", \"door\");");
        Check("Windows line breaks stay", Rewrite("a();\r\nTr(\"a\");\r\n", "cs", 2, "a", "b") == "a();\r\nTr(\"b\");\r\n");
        Check("GDScript: its own quote is escaped", Rewrite("tr('a')", "gd", 1, "a", "it's") == "tr('it\\'s')");
        Check("GDScript: StringNames too", Rewrite("tr(&\"a\")", "gd", 1, "a", "b") == "tr(&\"b\")");
        Check("scenes: the property, never the header", Rewrite("[node name=\"a\"]\ntext = \"a\"", "tscn", 2, "a", "b\nc") == "[node name=\"a\"]\ntext = \"b\nc\"");
    }

    async System.Threading.Tasks.Task RenamesChangeTheCode() {
        const string root = "user://misspell_refactor";
        DirAccess.MakeDirRecursiveAbsolute(root);
        using (var dir = DirAccess.Open(root)) {
            foreach (var file in dir.GetFiles()) dir.Remove(file);
        }
        WriteAt($"{root}/keys.pot", "msgid \"\"\nmsgstr \"\"\n\nmsgid \"shop/sword\"\nmsgstr \"\"\n\nmsgid \"shop/shield\"\nmsgstr \"\"\n\n"
                                    + "msgctxt \"menu\"\nmsgid \"Close\"\nmsgstr \"\"\n\nmsgctxt \"door\"\nmsgid \"Close\"\nmsgstr \"\"\n\nmsgid \"unused\"\nmsgstr \"\"\n");
        WriteAt($"{root}/Code.cs", "class Code {\n    string A => Tr(\"shop/sword\");\n    string B => Tr(@\"shop/shield\");\n    string C => Tr(\"Close\", \"menu\");\n"
                                   + "    string D => Tr(\"Close\", \"door\");\n    string E(string x) => Tr($\"shop/{x}\");\n}\n");
        WriteAt($"{root}/code.gd", "func f():\n\treturn tr('shop/sword')\n");
        WriteAt($"{root}/scene.tscn", "[node name=\"shop/sword\" type=\"Label\"]\ntext = \"shop/sword\"\n");
        string Read(string name) => FileAccess.GetFileAsString($"{root}/{name}");

        var panel = new MisspellPanel();
        AddChild(panel);
        panel.Size = new Vector2(1400, 700);
        await Settle();
        panel.Open($"{root}/keys.pot");
        panel.Usages = new KeyUsages(root);
        panel.ShowAsTree(true);
        var table = panel.Table;
        var source = table.ColumnOf(TranslationTable.ColumnKind.Source);

        // A key renamed in its cell: every exact use is offered, ticked.
        table.WriteCell(table.ItemOf(Row(panel.Catalogs, "shop/sword")), source, "blade");
        var pending = panel.PendingCodeChanges;
        Check("a rename offers to change its uses in the code", pending?.Count == 3 && pending.All(c => c.Wanted && c.Rename.NewId == "shop/blade"));
        pending.First(c => c.Usage.Path.EndsWith(".tscn")).Wanted = false;
        panel.ConfirmCodeRename();
        Check("the ticked uses are written anew", Read("Code.cs").Contains("Tr(\"shop/blade\")") && Read("code.gd").Contains("tr('shop/blade')"));
        Check("an unticked one is left", Read("scene.tscn") == "[node name=\"shop/sword\" type=\"Label\"]\ntext = \"shop/sword\"\n");

        panel.Undo();
        Check("undo puts the code back with the text", Read("Code.cs").Contains("Tr(\"shop/sword\")") && Read("code.gd").Contains("tr('shop/sword')") && Row(panel.Catalogs, "shop/sword") != null);
        panel.Redo();
        Check("and redo writes it again", Read("Code.cs").Contains("Tr(\"shop/blade\")") && Row(panel.Catalogs, "shop/blade") != null);

        // A folder renamed: all its keys, each written its own way; strings with holes only to check.
        table.WriteCell(table.GroupItem("shop"), source, "store");
        Check("a folder rename offers every key in it", panel.PendingCodeChanges?.Count == 3);
        Check("strings with holes are listed to check by hand", CodeRename.ToCheck(panel.Usages, [new CodeRename.Rename("shop/blade", null, "store/blade")]).Count == 1);
        panel.ConfirmCodeRename();
        Check("and changes them", Read("Code.cs").Contains("Tr(\"store/blade\")") && Read("Code.cs").Contains("Tr(@\"store/shield\")") && Read("Code.cs").Contains("Tr($\"shop/{x}\")"));

        // A text with a context: only the calls asking for that context.
        var menu = panel.Catalogs.Rows.First(r => r.Id == "Close" && r.Context == "menu");
        table.WriteCell(table.ItemOf(menu), source, "Shut");
        Check("a rename in a context offers only that context's uses", panel.PendingCodeChanges?.Count == 1);
        panel.ConfirmCodeRename();
        Check("and leaves the other context", Read("Code.cs").Contains("Tr(\"Shut\", \"menu\")") && Read("Code.cs").Contains("Tr(\"Close\", \"door\")"));

        // The third way out: cancel the rename itself.
        var door = panel.Catalogs.Rows.First(r => r.Id == "Close" && r.Context == "door");
        table.WriteCell(table.ItemOf(door), source, "Open");
        Check("cancelling is offered too", panel.PendingCodeChanges?.Count == 1);
        panel.CancelRename();
        Check("and takes the rename back, the code untouched", panel.Catalogs.Rows.Any(r => r.Id == "Close" && r.Context == "door") && Row(panel.Catalogs, "Open") == null
                                                                && Read("Code.cs").Contains("Tr(\"Close\", \"door\")") && panel.StatusText.StartsWith("Cancelled") && panel.PendingCodeChanges == null);

        // Nothing to offer for a text the code does not use.
        table.WriteCell(table.ItemOf(Row(panel.Catalogs, "unused")), source, "still unused");
        Check("an unused text is renamed without asking", panel.PendingCodeChanges == null);

        // Deleting warns while a text is used; an unused one just goes.
        await Settle();
        var used = panel.Catalogs.Rows.First(r => r.Id == "Close" && r.Context == "door");
        Check("deleting a used text warns, naming where", table.UseWarning([used])?.Contains($"{root.TrimPrefix("res://")}/Code.cs:5") == true);
        table.AskDeleteRow(used);
        Check("and asks before", panel.Catalogs.Rows.Contains(used) || Row(panel.Catalogs, "Close") != null);
        table.AskDeleteRow(Row(panel.Catalogs, "still unused"));
        Check("an unused one is deleted at once", Row(panel.Catalogs, "still unused") == null);
        Check("a folder's delete names the uses inside", table.UseWarning([.. panel.Catalogs.Rows.Where(r => r.Id.StartsWith("store/"))])?.Contains("Still used") == true);

        panel.QueueFree();
        await Settle();
    }

    /// <summary>A rename changes the strings and nothing else: line breaks, byte order marks and encodings stay.</summary>
    void FilesKeepTheirBytes() {
        const string root = "user://misspell_bytes";
        DirAccess.MakeDirRecursiveAbsolute(root);
        var latin1 = System.Text.Encoding.Latin1;
        var utf8 = new System.Text.UTF8Encoding(false);
        var utf16 = new System.Text.UnicodeEncoding(false, false);
        byte[] Bom(byte[] mark, byte[] body) => [.. mark, .. body];
        var files = new Dictionary<string, (System.Text.Encoding Encoding, byte[] Mark, string Text)> {
            ["bom.cs"] = (utf8, [0xEF, 0xBB, 0xBF], "class A {\r\n    string X => Tr(\"old\");\r\n}\r\n"),
            ["mixed.cs"] = (utf8, [], "a();\nTr(\"old\");\r\nb(); // ü\n"),
            ["latin.gd"] = (latin1, [], "# Grüße\nfunc f():\n\treturn tr(\"old\")\n"),
            ["wide.gd"] = (utf16, [0xFF, 0xFE], "tr(\"old\")\r\n"),
        };
        foreach (var (name, (encoding, mark, text)) in files) System.IO.File.WriteAllBytes(ProjectSettings.GlobalizePath($"{root}/{name}"), Bom(mark, encoding.GetBytes(text)));

        var usages = new KeyUsages(root);
        var problems = new List<string>();
        var done = CodeRename.Apply(CodeRename.Plan(usages, [new CodeRename.Rename("old", null, "neu")]), problems);
        Check("every file is changed", done.Count == 4 && problems.Count == 0);
        foreach (var (name, (encoding, mark, text)) in files) {
            var expected = Bom(mark, encoding.GetBytes(text.Replace("\"old\"", "\"neu\"")));
            Check($"{name}: only the string changes, line breaks and encoding stay",
                System.IO.File.ReadAllBytes(ProjectSettings.GlobalizePath($"{root}/{name}")).SequenceEqual(expected));
        }

        usages.Refresh();
        problems.Clear();
        done = CodeRename.Apply(CodeRename.Plan(usages, [new CodeRename.Rename("neu", null, "€uro")]), problems);
        Check("a text the file's encoding cannot hold is left there, and said why",
            done.Count == 3 && problems.Count == 1 && problems[0].Contains("latin.gd") && problems[0].Contains("encoding")
            && System.IO.File.ReadAllBytes(ProjectSettings.GlobalizePath($"{root}/latin.gd")).SequenceEqual(latin1.GetBytes(files["latin.gd"].Text.Replace("\"old\"", "\"neu\""))));
        CodeRename.Replay(done, undo: true, problems);
        Check("undo writes the old bytes back", System.IO.File.ReadAllBytes(ProjectSettings.GlobalizePath($"{root}/bom.cs"))
                                                    .SequenceEqual(Bom([0xEF, 0xBB, 0xBF], utf8.GetBytes(files["bom.cs"].Text.Replace("\"old\"", "\"neu\"")))));
    }

    static void WriteAt(string path, string text) {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        file.StoreString(text);
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
