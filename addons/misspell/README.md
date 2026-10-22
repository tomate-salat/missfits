# Misspell

A spreadsheet for gettext translations inside the Godot editor: one row per text, one column per
language, edited in place. Plural forms are first-class: every form gets a row of its own, and an
empty one tells you which numbers it is for.

> **State of things:** reading and writing `.po`/`.pot` files, the table with plural rows, the
> details panel, languages and plural rules, updating from the template, and undo are here.

Needs `addons/misscore`. Does not need, and does not know, any other Missfits addon.

## Enabling it

Enable **Misspell** under *Project → Project Settings → Plugins*. That adds a **Misspell** screen
next to 2D, 3D and Script.

## Opening translations

- Pick a catalog from the list at the top left: Misspell finds every `.po`/`.pot` in the project.
- Or double-click a `.po` file in the FileSystem dock, right-click it and choose *Open in Misspell*,
  or use *Open…*.

Misspell opens the file **together with the files that belong to it** — same folder, same domain:

| Layout | Opened together |
|---|---|
| `messages.pot`, `de.po`, `pl.po` | all three (files named only by their language go with the folder's single template) |
| `dialogue.pot`, `dialogue.de.po`, `dialogue.pl.po` | all three |
| `dialogue.de.po` without a template | the `dialogue.*.po` files; the rows are what they contain |

The template (`.pot`) is the source of the rows; it is not a column. Godot's FileSystem dock does
not list `.pot` files, which is why the list at the top finds them for you.

## The table

| Column | |
|---|---|
| status | `⚠` a problem (hover for which), `~` fuzzy somewhere, a number for a plural form's row |
| Context | `msgctxt` — tells the same words apart, e.g. *Close* in a menu and *Close* a door |
| Source | `msgid`, and in the row below a plural message `msgid_plural` |
| one per language | the translation, titled with the language and how much is translated, `*` while unsaved |

Red cells are untranslated, amber ones fuzzy (a guess Godot does not use). Click a column title to
sort by it — again to sort the other way, a third time for the order of the file. Right-click a
language's title for its language and plural rule.

**Editing works like a spreadsheet:**

- Type on a selected cell to replace it; **Enter**, **F2** or a double-click to change it.
- While editing: **Enter** keeps the text and goes down, **Tab** / **Shift+Tab** go right / left,
  **Shift+Enter** breaks the line, **Escape** cancels. Clicking elsewhere keeps what you typed.
- **Delete** empties a cell, **Ctrl+C** / **Ctrl+X** / **Ctrl+V** copy, cut and paste one.
- **Ctrl+Z** / **Ctrl+Y** undo and redo, **Ctrl+F** searches, **Ctrl+S** saves.
- Writing a translation clears its *fuzzy* flag: you have looked at it.
- Line breaks show as `↵` in the table and are real line breaks in the file.
- Sources and contexts can be edited too; the change is made in every file of the set.

Right-click a cell for more: copy the source into the translation (marked fuzzy, as a start), toggle
fuzzy, clear, make a text plural or singular, delete it.

The search looks into contexts, sources and every translation. The filter shows only what is
untranslated, fuzzy, has problems, or is plural.

## Plural forms

Languages count differently. English and German have two forms, Polish three (*1 moneta, 2 monety,
5 monet*), Arabic six, Japanese one. A `.po` file says how its language counts in its
`Plural-Forms` header, and a plural text has exactly that many translations, `msgstr[0]` to
`msgstr[n-1]`.

Misspell gives each form a row of its own: a plural text has as many rows as the language with the
most forms needs. In each language's column:

- a filled form shows its text;
- **an empty form shows its name and the numbers it is for** — `few · n = 2–4, 22–24, 32–34, …` —
  so you know what to write there without knowing the rule by heart;
- a row past the language's forms is greyed out (Japanese has only the first).

The **details** beside the table show, for the selected text, every language's forms with their
numbers and texts. *Try n* picks a number and marks the form each language uses for it, with the
number filled into `{n}`, `{count}` or `%d` — what the player will read.

**Language and plural rule:** *+ Language* and right-clicking a language's title open the same
dialog. Type the language (`de`, `pt_BR`, `pl` …) and its usual rule is filled in; the forms are
listed with their numbers as you type, and a rule that is broken, leaves a form unused, or differs
from the usual one for the language is pointed out. The rules of the languages games are commonly
translated into are built in (`PluralRules`), in the form gettext uses; for any other, pick a rule
from the list or type one. Changing a rule gives every plural text exactly as many forms as the rule
has; undo brings dropped ones back.

**Checks** (the `⚠` column): a placeholder (`{name}`, `%s`, `%d` …) missing from a translation or
not in the source, a line break at the end of one but not the other, more forms than the language
has. A plural form may leave the number out — *Eine Münze* for *{n} coins* is fine.

In the game, use `TrN` (C#) or `tr_n` (GDScript); Godot picks the form by the same rule:

```csharp
label.Text = TrN("You have {n} coin.", "You have {n} coins.", n).Replace("{n}", n.ToString());
```

## Template, languages, saving

- **Update from template** brings every language in line with the `.pot`, like gettext's
  `msgmerge` without guessing: new texts are added untranslated, texts gone from the template become
  obsolete (kept at the end of the file, and back with their translation if they return), and
  references, comments for translators and plural sources are taken over. Generate the template
  with Godot under *Project Settings → Localization → Template Generation* first.
- **+ Language** creates a file for a new language with every text of the template, named like the
  files already there (`fr.po` beside `de.po`, `dialogue.fr.po` beside `dialogue.de.po`). It can add
  the file to *Project Settings → Localization → Translations* for you.
- **+ Text** adds a text by hand to every file — for projects without a template.
- **Save** writes every changed file, nothing else. Files are written as gettext writes them;
  comments, flags, references, obsolete texts and the order of everything are kept, so a diff shows
  only what you changed. The editor reloads the translations after saving, and running the game
  saves first.
- Files changed outside the editor — a new template, a `git pull` — are read again when you come
  back to the editor; with unsaved changes, the status line asks you to choose.

Unsaved edits survive the assembly reload of pressing play or building.

## In code

The model is plain C# and works in the game too, for tools or mods that read and write PO files:

```csharp
var po = PoCatalog.Parse(FileAccess.GetFileAsString("res://locale/pl.po"));
var entry = po.Find(context: null, id: "You have {n} coin.");
var form = po.Plurals.FormFor(5);          // 2 — "many" in Polish
GD.Print(po.Plurals.SampleText(form));     // 0, 5–21, 25–31, 35–41, …
entry.SetString(form, "Masz {n} monet.");
var text = po.Write();
```

| Type | |
|---|---|
| `PoCatalog` | a file: `Header`, `HeaderFields`, `Entries`, `Language`, `Plurals`; `Parse`, `Write`, `Create(language)` |
| `PoEntry` | a text: `Context`, `Id`, `IdPlural`, `Strings`, comments, `References`, `Flags`, `Fuzzy`, `Obsolete` |
| `PluralForms` | a rule: `Count`, `FormFor(n)`, `Names`, `Samples(form)`, `SampleText(form)` |
| `PluralRules` | the built-in rules: `For("pt_BR")` |

## Tests

The tests are not part of the release zip; they come with the
[repository](https://github.com/tomate-salat/missfits). Both suites run headless and exit 0 when
everything passes.

```bash
godot --headless --path . res://addons/misspell/tests/self_test.tscn
godot --headless --path . res://addons/misspell/tests/editor_self_test.tscn
```
