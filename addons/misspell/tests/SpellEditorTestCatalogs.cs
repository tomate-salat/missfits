/// <summary>
/// The catalogs <c>SpellEditorSelfTest</c> writes. They live outside its <c>#if TOOLS</c>:
/// in a build without TOOLS the compiler skips that block but still reads every line starting
/// with <c>#</c> as a directive, and .po comments do.
/// </summary>
static class SpellEditorTestCatalogs {
    public const string Template = """
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

    public const string German = """
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

    public const string Polish = """
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
}
