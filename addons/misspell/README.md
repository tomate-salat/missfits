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
| Context | `msgctxt` — tells the same words apart, e.g. *Close* in a menu and *Close* a door. First, as it is half of what identifies a text; shown only when the files have contexts (the *Context* button switches it — without it, a context shows after its text as `‹menu›`) |
| Source (*Key* in the tree) | `msgid`. A warning icon means a problem in some language; hover for which, and for where it is fuzzy. The rows below a plural message hold `msgid_plural` and `form 2`, `form 3` … |
| one per language | the translation, titled like `de · German  75%` — a tick once complete, a dot while unsaved |

Red cells are untranslated, amber ones fuzzy (a guess Godot does not use). Click a column title to
sort by it — again to sort the other way, a third time for the order of the file. Right-click a
language's title for its language and plural rule.

**Editing works like a spreadsheet:**

- Type on a selected cell to replace it; **Enter**, **F2** or a double-click to change it.
- While editing: **Enter** keeps the text and goes down, **Tab** / **Shift+Tab** go right / left,
  **Shift+Enter** breaks the line, **Escape** cancels. Clicking elsewhere keeps what you typed.
- **Delete** empties a cell, **Ctrl+C** / **Ctrl+X** / **Ctrl+V** copy, cut and paste one. On a key or folder, Ctrl+C copies its whole path, even where the tree shows only its name.
- **Ctrl+Z** / **Ctrl+Y** undo and redo, **Ctrl+F** searches, **Ctrl+S** saves.
- Writing a translation clears its *fuzzy* flag: you have looked at it.
- Line breaks show as `↵` in the table and are real line breaks in the file.
- Sources and contexts can be edited too; the change is made in every file of the set.

Right-click a cell for more: copy the source into the translation (marked fuzzy, as a start), toggle
fuzzy, clear, make a text plural or singular, delete it.

**Keys as paths — the tree.** When the sources are keys laid out like paths rather than English
sentences (`%welcome/option/enter`), the table shows them as a tree: one row per folder, which folds
and unfolds, with per language how many of the texts inside are translated (`2/3` — green when
all are, amber while some are, red while none is). A text shows only the last part of its key; hover for the whole key, and
edit the key cell to rename it — the field holds just that last part too, and the key stays in its folder. Sets whose sources mostly contain a `/` open as a tree; the
*Tree* button switches. In each folder the folders come first, then the texts. Enter or a
double-click folds a folder, folded folders stay folded, and *Text* starts a new key in the folder
at hand.

**New folders:** *Folder* in the toolbar, right-click → *New folder inside* / *New folder here*, or right-click below the rows for one at the top (a click there lets go of the selection, so *Folder* and *Text* then work at the top as well).
Its name opens for typing. A PO file only knows keys, so a folder without a text in it cannot be
saved: it is shown with a grey icon and lasts until a text is put in it — drag one in, or start a
*Text* there — or until the editor closes.

**Renaming a folder:** F2 on it, typing on its name, or right-click → *Rename folder*. The field
holds only the folder's name (`b` for `a/b`); a slash in what you type makes folders inside. To move
a folder elsewhere, drag it. Every key inside is renamed in every file, as one undo step; a rename
that would give a key one that is already there is refused.

**Deleting a folder:** Delete on it, or right-click → *Delete folder…*. After asking — with how
many texts go — every text in it and in the folders inside is removed from every file, as one undo
step. An empty folder goes without asking.

**Drag and drop** moves keys in the tree: drag a text or a whole folder onto a folder to move it
in, onto a text to put it beside that one, or below the rows to move it to the top level. Every key
is renamed in every file, translations and comments included — `%welcome/option/enter` dropped on
`%quest missing/` becomes `%quest missing/enter` — and the whole move is one undo step. A move that
would give a key one that is already there is refused.

Why keys: with the English text as the key, changing the English changes the key — every
translation of the old text no longer matches, and the game quietly shows the new English instead.
With keys, the English is a translation like any other (add an `en` language), and a missing one
shows the key, which is hard to miss.

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

**Used in** lists where the selected text is used: every string in the project's `.tscn`, `.tres`,
`.gd` and `.cs` files that is exactly its source, by file and line with the line of code below.
A click opens it — a script at that line, a scene, a resource in the inspector. Comments, the
`[node name="…"]` headers of scenes and folders with a `.gdignore` are left out. What is found is
the string, not the call: the context of `Tr("Close", "menu")` is not compared yet, and an
interpolated C# string like `$"shop/{item}"` is no use of anything (the strings in its holes are).
Files are read again when they change.

**Language and plural rule:** *Language* in the toolbar and right-clicking a language's title open the same
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

- **Update** (from the template; only shown when there is one) brings every language in line with the `.pot`, like gettext's
  `msgmerge` without guessing: new texts are added untranslated, texts gone from the template become
  obsolete (kept at the end of the file, and back with their translation if they return), and
  references, comments for translators and plural sources are taken over. Generate the template
  with Godot under *Project Settings → Localization → Template Generation* first.
- **Language** creates a file for a new language with every text of the template, named like the
  files already there (`fr.po` beside `de.po`, `dialogue.fr.po` beside `dialogue.de.po`). It can add
  the file to *Project Settings → Localization → Translations* for you.
- **Text** adds a text by hand to every file — for projects without a template.
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
