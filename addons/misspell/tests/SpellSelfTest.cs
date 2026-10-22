using System.Collections.Generic;
using System.Linq;
using Godot;
using Misspell;

/// <summary>
/// Self test of the PO model: reading, writing and plural rules. Runs headless and exits 0 when
/// everything passes:
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/misspell/tests/self_test.tscn
/// </code>
/// </summary>
public partial class SpellSelfTest : Node {
    readonly List<string> _failures = [];
    int _checks;

    const string Sample = """
        # German translation.
        # Second line.
        #, fuzzy
        msgid ""
        msgstr ""
        "Project-Id-Version: Demo\n"
        "Language: de\n"
        "Content-Type: text/plain; charset=UTF-8\n"
        "Plural-Forms: nplurals=2; plural=(n != 1);\n"

        #. Shown on the shop's button.
        #: res://ui/shop.tscn res://ui/shop.gd:12
        msgid "Buy"
        msgstr "Kaufen"

        # Checked by Anna.
        #, fuzzy, c-format
        #| msgid "Sell it"
        msgctxt "verb"
        msgid "Sell"
        msgstr "Verkaufen"

        msgid "One coin"
        msgid_plural "{n} coins"
        msgstr[0] "Eine Münze"
        msgstr[1] "{n} Münzen"

        msgid ""
        "First line\n"
        "Second line with a \"quote\" and a \\ backslash\n"
        msgstr ""
        "Erste Zeile\n"
        "Zweite Zeile mit \"Zitat\" und \\ Backslash\n"

        msgid "Tab\there"
        msgstr "Tab\thier"

        #~ msgid "Old text"
        #~ msgstr "Alter Text"
        """;

    public override void _Ready() {
        try {
            Reading();
            WritingKeepsTheFile();
            HeaderFields();
            Escapes();
            BrokenInput();
            PluralExpressions();
            PluralFormsOfLanguages();
            SamplesSayWhichNumbers();
            KnownRules();
            EntriesFitTheirForms();
        } catch (System.Exception e) {
            // Without this, a crash would leave the headless run waiting forever.
            _failures.Add($"crashed: {e}");
        }

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misspell self test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    void Reading() {
        var po = PoCatalog.Parse(Sample);
        Check("no problems in a good file", po.Problems.Count == 0);
        Check("the header is not a message", po.Entries.Count == 6 && po.Header != null);
        Check("the header keeps its comments and flags", po.Header.TranslatorComments.SequenceEqual(["German translation.", "Second line."]) && po.Header.Fuzzy);

        var buy = po.Find(null, "Buy");
        Check("a message is found by its source", buy?.StringAt(0) == "Kaufen");
        Check("extracted comments and references are read", buy.ExtractedComments.SequenceEqual(["Shown on the shop's button."])
                                                            && buy.References.SequenceEqual(["res://ui/shop.tscn res://ui/shop.gd:12"]));

        var sell = po.Find("verb", "Sell");
        Check("a message with context is found by both", sell != null && po.Find(null, "Sell") == null);
        Check("flags are split", sell.Flags.SequenceEqual(["fuzzy", "c-format"]) && sell.Fuzzy);
        Check("previous messages are kept", sell.Previous.SequenceEqual(["msgid \"Sell it\""]));
        Check("translator comments belong to their message", sell.TranslatorComments.SequenceEqual(["Checked by Anna."]));

        var coin = po.Find(null, "One coin");
        Check("a plural message has its plural source and forms", coin.IsPlural && coin.IdPlural == "{n} coins" && coin.Strings.SequenceEqual(["Eine Münze", "{n} Münzen"]));

        var multi = po.Entries[3];
        Check("strings over several lines are joined", multi.Id == "First line\nSecond line with a \"quote\" and a \\ backslash\n");
        Check("escapes are read", po.Entries[4].Id == "Tab\there");

        var old = po.Entries[5];
        Check("obsolete messages are read and kept out of the messages", old.Obsolete && old.Id == "Old text" && old.StringAt(0) == "Alter Text"
                                                                           && po.Find(null, "Old text") == null && po.Messages.Count() == 5);
    }

    void WritingKeepsTheFile() {
        var text = Sample.Replace("\r\n", "\n") + "\n";
        var written = PoCatalog.Parse(text).Write();
        Check("reading and writing changes nothing", written == text);
        if (written != text) GD.PrintErr(written);

        var po = PoCatalog.Parse(text);
        po.Find(null, "Buy").SetString(0, "Erwerben");
        var edited = po.Write();
        Check("an edit changes only its line", edited == text.Replace("msgstr \"Kaufen\"", "msgstr \"Erwerben\""));

        Check("a written file reads the same again", PoCatalog.Parse(edited).Write() == edited);
        Check("CRLF files read too", PoCatalog.Parse(text.Replace("\n", "\r\n")).Find(null, "Buy")?.StringAt(0) == "Kaufen");
        Check("a BOM is ignored", PoCatalog.Parse("﻿" + text).Header.TranslatorComments[0] == "German translation.");
    }

    void HeaderFields() {
        var po = PoCatalog.Parse(Sample);
        Check("header fields are read in order", po.HeaderFields.Select(f => f.Key).SequenceEqual(["Project-Id-Version", "Language", "Content-Type", "Plural-Forms"]));
        Check("the language comes from the header", po.Language == "de");
        Check("lookup ignores case", po.GetHeader("language") == "de");

        po.SetHeader("Language", "pl");
        po.SetHeader("X-Generator", "Misspell");
        var written = po.Write();
        Check("a changed field keeps its place, a new one goes last",
            written.Contains("\"Language: pl\\n\"\n\"Content-Type") && written.Contains("\"X-Generator: Misspell\\n\"\n\n#. Shown"));

        var created = PoCatalog.Create("pl");
        Check("a new file gets the plural rule of its language", created.Plurals.Count == 3 && created.Language == "pl");
        Check("and says it is UTF-8", created.GetHeader("Content-Type") == "text/plain; charset=UTF-8");
    }

    void Escapes() {
        const string tricky = "a\"b\\c\nd\te\r";
        Check("escaping and unescaping are each other's inverse", PoCatalog.Unescape(PoCatalog.Escape(tricky)) == tricky);
        Check("octal and hex escapes read", PoCatalog.Unescape("\\101\\x42") == "AB");

        var po = PoCatalog.Create("de");
        po.Entries.Add(new PoEntry { Id = "Line\nbreak", Strings = ["Zeilen\numbruch"] });
        po.Entries.Add(new PoEntry { Id = "Ends with break\n", Strings = ["Endet mit Umbruch\n"] });
        var written = po.Write();
        Check("a break inside splits the string after it", written.Contains("msgid \"\"\n\"Line\\n\"\n\"break\"\n"));
        Check("a break only at the end stays on one line", written.Contains("msgid \"Ends with break\\n\"\n"));
        Check("both read back", PoCatalog.Parse(written).Find(null, "Line\nbreak")?.StringAt(0) == "Zeilen\numbruch");
    }

    void BrokenInput() {
        var po = PoCatalog.Parse("msgid \"a\"\nmsgstr \"b\"\nnonsense here\n\"stray\"\nmsgid \"c\"\nmsgstr \"d\"\n");
        Check("reading never fails, and says what it skipped", po.Problems.Count >= 1 && po.Find(null, "c")?.StringAt(0) == "d");
        Check("a message starts without a blank line before it", po.Find(null, "a")?.StringAt(0) == "b");
        Check("an empty file is an empty catalog", PoCatalog.Parse("").Entries.Count == 0);

        var lead = PoCatalog.Parse("# Licence\n\n# Header comment\nmsgid \"\"\nmsgstr \"Language: de\\n\"\n");
        Check("comments above the header with a blank line between are kept", lead.Header.TranslatorComments.SequenceEqual(["Licence", "Header comment"]) && lead.Language == "de");
    }

    void PluralExpressions() {
        long Eval(string source, long n) => PluralExpression.TryParse(source, out _).Evaluate(n);
        Check("comparisons yield 1 or 0", Eval("n != 1", 1) == 0 && Eval("n != 1", 5) == 1);
        Check("modulo and precedence", Eval("n%10==1 && n%100!=11", 21) == 1 && Eval("n%10==1 && n%100!=11", 11) == 0);
        Check("ternaries nest to the right", Eval("n==1 ? 0 : n==2 ? 1 : 2", 1) == 0 && Eval("n==1 ? 0 : n==2 ? 1 : 2", 2) == 1 && Eval("n==1 ? 0 : n==2 ? 1 : 2", 7) == 2);
        Check("not, unary minus and parentheses", Eval("!(n > 1)", 0) == 1 && Eval("-(n - 3) * 2", 1) == 4);
        Check("division by zero does not crash", Eval("n / 0", 4) == 0 && Eval("n % 0", 4) == 0);
        Check("a broken expression says why", PluralExpression.TryParse("n ==", out var error) == null && !string.IsNullOrEmpty(error));
        Check("so does an unknown letter", PluralExpression.TryParse("x > 1", out _) == null && PluralExpression.TryParse("(n > 1", out _) == null);
    }

    void PluralFormsOfLanguages() {
        var pl = PluralRules.For("pl").Forms;
        Check("Polish: 1 is one", pl.FormFor(1) == 0);
        Check("Polish: 2–4, 22–24 are few, 12–14 are not", pl.FormFor(2) == 1 && pl.FormFor(24) == 1 && pl.FormFor(12) == 2 && pl.FormFor(14) == 2);
        Check("Polish: 0, 5, 21 are many", pl.FormFor(0) == 2 && pl.FormFor(5) == 2 && pl.FormFor(21) == 2);

        var ru = PluralRules.For("ru").Forms;
        Check("Russian: 21 is one, 11 is many", ru.FormFor(21) == 0 && ru.FormFor(11) == 2);

        var ar = PluralRules.For("ar").Forms;
        Check("Arabic has six forms", ar.Count == 6 && ar.FormFor(0) == 0 && ar.FormFor(2) == 1 + 1 && ar.FormFor(105) == 3 && ar.FormFor(111) == 4 && ar.FormFor(100) == 5);

        var fr = PluralRules.For("fr").Forms;
        Check("French: 0 is singular, unlike English", fr.FormFor(0) == 0 && PluralForms.Default.FormFor(0) == 1);

        Check("a form index past the count counts as the last form", PluralForms.Parse("nplurals=2; plural=n;").FormFor(7) == 1);
        Check("one form needs no rule", PluralForms.TryParse("nplurals=1;", out _)?.Count == 1);
        Check("a bad header says why", PluralForms.TryParse("nplurals=2; plural=n ==", out var error) == null && error.StartsWith("plural"));
        Check("nplurals is required", PluralForms.TryParse("plural=n != 1;", out _) == null);
        Check("a file without the header uses its language's rule", PoCatalog.Create("cs", PluralForms.Default) is var cs
                                                                    && cs.SetHeaderAndReturn(PoCatalog.PluralFormsField, null).Plurals.Count == 3);
        Check("a file with a broken header names the problem", PoCatalog.Parse("msgid \"\"\nmsgstr \"Plural-Forms: nplurals=x\\n\"\n").PluralFormsProblem != null);
    }

    void SamplesSayWhichNumbers() {
        var de = PluralForms.Default;
        Check("German one is 1", de.SampleText(0) == "1");
        Check("German other is 0 and from 2 on", de.SampleText(1) == "0, 2+");

        var pl = PluralRules.For("pl").Forms;
        Check("Polish few", pl.SampleText(1) == "2–4, 22–24, 32–34, 42–44, …");
        Check("Polish many", pl.SampleText(2) == "0, 5–21, 25–31, 35–41, …");

        var ru = PluralRules.For("ru").Forms;
        Check("Russian one lists single numbers", ru.SampleText(0) == "1, 21, 31, 41, …");
        Check("the first number of a form", ru.FirstNumber(1) == 2 && ru.FirstNumber(2) == 0);

        var broken = PluralForms.Parse("nplurals=3; plural=(n != 1);");
        Check("a form no number takes is found", broken.UnreachableForms().SequenceEqual([2]) && broken.SampleText(2) == "—");
    }

    void KnownRules() {
        Check("locales are normalised", PluralRules.Normalize("pt-br") == "pt_BR" && PluralRules.Normalize("de_DE.UTF-8") == "de_DE");
        Check("a region with a rule of its own gets it", PluralRules.For("pt_BR").Forms.FormFor(0) == 0 && PluralRules.For("pt").Forms.FormFor(0) == 1);
        Check("a region without one gets its language's", PluralRules.For("de_AT") == PluralRules.For("de"));
        Check("an unknown language has none", PluralRules.For("xx") == null && PluralRules.For("") == null);
        Check("forms of known rules have names", PluralRules.For("pl").Forms.Names.SequenceEqual(["one", "few", "many"]));
        Check("a rule written differently still gets the names", PluralForms.Parse("nplurals=2; plural=n>1;").Names.SequenceEqual(["one", "other"]));
        Check("an unknown rule numbers its forms", PluralForms.Parse("nplurals=2; plural=n>5;").Names.SequenceEqual(["form 0", "form 1"]));
        Check("every rule of the table parses and reaches all its forms", PluralRules.All.All(r => r.Forms.Count == r.Names.Length && !r.Forms.UnreachableForms().Any()));
    }

    void EntriesFitTheirForms() {
        var entry = new PoEntry { Id = "One coin", IdPlural = "{n} coins", Strings = ["a", "b"] };
        entry.FitForms(3);
        Check("missing forms are added empty", entry.Strings.SequenceEqual(["a", "b", ""]) && !entry.IsTranslated(3));
        entry.FitForms(1);
        Check("forms too many are dropped", entry.Strings.SequenceEqual(["a"]) && entry.IsTranslated(1));

        var single = new PoEntry { Id = "x", Strings = ["y"] };
        single.FitForms(6);
        Check("a singular message keeps one string", single.Strings.Count == 1);

        var copy = new PoEntry { Id = "x", Flags = ["fuzzy", "c-format"], References = ["a.gd"], TranslatorComments = ["mine"] }.Untranslated(2);
        Check("an untranslated copy keeps what the source says, not what a translator said",
            copy.Flags.SequenceEqual(["c-format"]) && copy.References.SequenceEqual(["a.gd"]) && copy.TranslatorComments.Count == 0 && copy.Strings.SequenceEqual([""]));
    }

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}

static class SpellTestExtensions {
    public static PoCatalog SetHeaderAndReturn(this PoCatalog catalog, string name, string value) {
        catalog.SetHeader(name, value);
        return catalog;
    }
}
