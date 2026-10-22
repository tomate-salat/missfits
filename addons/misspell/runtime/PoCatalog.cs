using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Misspell;

/// <summary>
/// A PO or POT file in memory: the header and the messages in the order of the file.
/// <para>
/// <see cref="Parse"/> and <see cref="Write"/> keep everything a file holds — comments, flags,
/// references, obsolete messages, the order of the header fields — so a file that was read and
/// written again only differs where it was edited. Plain C#, no Godot types: it works in the game
/// as well as in the editor.
/// </para>
/// </summary>
public sealed class PoCatalog {
    /// <summary>The header message, with its comments. Its <c>msgstr</c> is kept in step with <see cref="HeaderFields"/>.</summary>
    public PoEntry Header;

    /// <summary>The messages after the header, obsolete ones included.</summary>
    public List<PoEntry> Entries = [];

    /// <summary><c>Name: value</c> lines of the header, in their order.</summary>
    public List<KeyValuePair<string, string>> HeaderFields = [];

    /// <summary>What did not read as PO, with line numbers. Reading never fails; it skips such lines.</summary>
    public List<string> Problems = [];

    public const string PluralFormsField = "Plural-Forms";
    public const string LanguageField = "Language";

    public string GetHeader(string name) => HeaderFields.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Sets a header field, keeping its place; a new one goes last. Null removes it.</summary>
    public void SetHeader(string name, string value) {
        var index = HeaderFields.FindIndex(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase));
        if (value == null) {
            if (index >= 0) HeaderFields.RemoveAt(index);
        } else if (index >= 0) {
            HeaderFields[index] = new(HeaderFields[index].Key, value);
        } else {
            HeaderFields.Add(new(name, value));
        }
    }

    /// <summary>The <c>Language</c> header, like <c>de</c> or <c>pt_BR</c>; "" in a template.</summary>
    public string Language {
        get => GetHeader(LanguageField) ?? "";
        set => SetHeader(LanguageField, value);
    }

    /// <summary>
    /// The plural rule of the file. Without a header, or with one that does not parse, the rule
    /// known for its <see cref="Language"/>, else English's.
    /// </summary>
    public PluralForms Plurals => PluralForms.TryParse(GetHeader(PluralFormsField), out _) ?? PluralRules.For(Language)?.Forms ?? PluralForms.Default;

    /// <summary>Why the <c>Plural-Forms</c> header is no use, or null when it is fine or not there.</summary>
    public string PluralFormsProblem => GetHeader(PluralFormsField) is { } header && PluralForms.TryParse(header, out var error) == null ? error : null;

    /// <summary>The messages that are in use: neither the header nor obsolete.</summary>
    public IEnumerable<PoEntry> Messages => Entries.Where(e => !e.Obsolete);

    public PoEntry Find(string context, string id) => Find(PoEntry.KeyOf(context, id));

    public PoEntry Find(string key) => Entries.FirstOrDefault(e => !e.Obsolete && e.Key == key);

    public PoEntry FindObsolete(string key) => Entries.FirstOrDefault(e => e.Obsolete && e.Key == key);

    public PoCatalog Clone() => new() {
        Header = Header?.Clone(),
        Entries = [.. Entries.Select(e => e.Clone())],
        HeaderFields = [.. HeaderFields],
        Problems = [.. Problems],
    };

    /// <summary>A file with a header that says what Godot and gettext need to know, and no messages.</summary>
    public static PoCatalog Create(string language, PluralForms plurals = null) {
        var catalog = new PoCatalog { Header = new PoEntry() };
        catalog.SetHeader("MIME-Version", "1.0");
        catalog.SetHeader("Content-Type", "text/plain; charset=UTF-8");
        catalog.SetHeader("Content-Transfer-Encoding", "8bit");
        if (!string.IsNullOrEmpty(language)) catalog.Language = language;
        plurals ??= PluralRules.For(language)?.Forms;
        if (plurals != null) catalog.SetHeader(PluralFormsField, plurals.Header);
        return catalog;
    }

    // ---- reading -----------------------------------------------------------------------------

    /// <summary>Reads PO text. Never throws; what it cannot read lands in <see cref="Problems"/>.</summary>
    public static PoCatalog Parse(string text) {
        var catalog = new PoCatalog();
        new Reader(catalog).Read(text ?? "");
        catalog.Header ??= new PoEntry();
        catalog.HeaderFields = ParseHeaderFields(catalog.Header.StringAt(0));
        return catalog;
    }

    static List<KeyValuePair<string, string>> ParseHeaderFields(string text) {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var line in text.Split('\n')) {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            fields.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return fields;
    }

    sealed class Reader(PoCatalog catalog) {
        PoEntry _entry = new();
        bool _hasId;
        bool _hasString;

        /// <summary>Which string continuation lines (<c>"…"</c>) add to.</summary>
        Action<string> _continue;
        int _line;

        public void Read(string text) {
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n')) {
                _line++;
                var line = raw.Trim();
                if (_line == 1 && line.Length > 0 && line[0] == '﻿') line = line[1..];

                if (line.Length == 0) {
                    Flush();
                    continue;
                }

                var obsolete = false;
                if (line.StartsWith("#~")) {
                    obsolete = true;
                    line = line[2..].TrimStart();
                    // "#~|" is a previous message of an obsolete one; kept with the others.
                    if (line.StartsWith('|')) {
                        StartIfDone();
                        _entry.Previous.Add("~" + line[1..].TrimStart());
                        continue;
                    }
                    if (line.Length == 0) continue;
                }

                if (!obsolete && line[0] == '#') {
                    StartIfDone();
                    Comment(line);
                    continue;
                }

                if (line[0] == '"') {
                    if (_continue == null) Problem("text without a keyword before it");
                    else _continue(Unquote(line));
                    continue;
                }

                Keyword(line, obsolete);
            }
            Flush();
        }

        void Comment(string line) {
            var kind = line.Length > 1 ? line[1] : ' ';
            var rest = line.Length > 2 ? line[2..] : "";
            switch (kind) {
                case '.':
                    _entry.ExtractedComments.Add(rest.TrimStart());
                    break;
                case ':':
                    _entry.References.Add(rest.Trim());
                    break;
                case ',':
                    _entry.Flags.AddRange(rest.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0));
                    break;
                case '|':
                    _entry.Previous.Add(rest.TrimStart());
                    break;
                default:
                    // "# text" and a bare "#": the space after the hash belongs to the format.
                    _entry.TranslatorComments.Add(line.Length > 1 && line[1] == ' ' ? line[2..] : line[1..]);
                    break;
            }
        }

        void Keyword(string line, bool obsolete) {
            var space = line.IndexOfAny([' ', '\t', '"']);
            var keyword = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : Unquote(line[space..].Trim());

            switch (keyword) {
                case "msgctxt":
                    StartIfDone(alsoAfterId: true);
                    _entry.Context = value;
                    _entry.Obsolete |= obsolete;
                    _continue = s => _entry.Context += s;
                    break;
                case "msgid":
                    StartIfDone(alsoAfterId: true);
                    _entry.Id = value;
                    _entry.Obsolete |= obsolete;
                    _hasId = true;
                    _continue = s => _entry.Id += s;
                    break;
                case "msgid_plural":
                    _entry.IdPlural = value;
                    _continue = s => _entry.IdPlural += s;
                    break;
                case "msgstr":
                    if (!_hasString) _entry.Strings.Clear();
                    _entry.Strings.Add(value);
                    _hasString = true;
                    var single = _entry.Strings.Count - 1;
                    _continue = s => _entry.Strings[single] += s;
                    break;
                default:
                    if (keyword.StartsWith("msgstr[") && keyword.EndsWith(']')
                        && int.TryParse(keyword[7..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var form) && form < 64) {
                        if (!_hasString) _entry.Strings.Clear();
                        _entry.SetString(form, value);
                        _hasString = true;
                        _continue = s => _entry.Strings[form] += s;
                    } else {
                        Problem($"unknown keyword '{keyword}'");
                        _continue = null;
                    }
                    break;
            }
        }

        /// <summary>
        /// A comment, or with <paramref name="alsoAfterId"/> a <c>msgctxt</c>/<c>msgid</c>, after the
        /// strings of an entry starts the next one, blank line or not.
        /// </summary>
        void StartIfDone(bool alsoAfterId = false) {
            if (_hasString || (alsoAfterId && _hasId)) Flush();
        }

        void Flush() {
            var next = new PoEntry();
            if (_hasId) {
                if (_entry.IsHeader && catalog.Header == null && catalog.Entries.Count == 0) catalog.Header = _entry;
                else catalog.Entries.Add(_entry);
            } else {
                // Comments with no message after them, like a licence above the header with a blank
                // line between: they go to the next message rather than getting lost.
                next.TranslatorComments.AddRange(_entry.TranslatorComments);
                next.ExtractedComments.AddRange(_entry.ExtractedComments);
                next.References.AddRange(_entry.References);
                next.Flags.AddRange(_entry.Flags);
                next.Previous.AddRange(_entry.Previous);
            }
            _entry = next;
            _hasId = false;
            _hasString = false;
            _continue = null;
        }

        void Problem(string what) => catalog.Problems.Add($"line {_line}: {what}");

        string Unquote(string quoted) {
            quoted = quoted.Trim();
            if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"') {
                Problem("expected a quoted string");
                return quoted.Trim('"');
            }
            return Unescape(quoted[1..^1]);
        }
    }

    public static string Unescape(string text) {
        if (text.IndexOf('\\') < 0) return text;
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length) {
                result.Append(c);
                continue;
            }
            var next = text[++i];
            switch (next) {
                case 'n': result.Append('\n'); break;
                case 't': result.Append('\t'); break;
                case 'r': result.Append('\r'); break;
                case 'a': result.Append('\a'); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'v': result.Append('\v'); break;
                case 'x': {
                    var start = i + 1;
                    var end = start;
                    while (end < text.Length && end - start < 2 && Uri.IsHexDigit(text[end])) end++;
                    if (end > start) {
                        result.Append((char) Convert.ToInt32(text[start..end], 16));
                        i = end - 1;
                    } else {
                        result.Append('x');
                    }
                    break;
                }
                case >= '0' and <= '7': {
                    var start = i;
                    var end = start;
                    while (end < text.Length && end - start < 3 && text[end] is >= '0' and <= '7') end++;
                    result.Append((char) Convert.ToInt32(text[start..end], 8));
                    i = end - 1;
                    break;
                }
                default: result.Append(next); break;
            }
        }
        return result.ToString();
    }

    // ---- writing -----------------------------------------------------------------------------

    /// <summary>Writes the catalog as PO text: UTF-8 without BOM is what to save it as, with \n line ends.</summary>
    public string Write() {
        var text = new StringBuilder();
        var header = Header ?? new PoEntry();
        header.Strings = [string.Concat(HeaderFields.Select(f => $"{f.Key}: {f.Value}\n"))];
        WriteEntry(text, header, isHeader: true);

        foreach (var entry in Entries) {
            text.Append('\n');
            WriteEntry(text, entry, isHeader: false);
        }
        return text.ToString();
    }

    static void WriteEntry(StringBuilder text, PoEntry entry, bool isHeader) {
        foreach (var comment in entry.TranslatorComments) text.Append(comment.Length == 0 ? "#\n" : $"# {comment}\n");
        foreach (var comment in entry.ExtractedComments) text.Append($"#. {comment}\n");
        foreach (var reference in entry.References) text.Append($"#: {reference}\n");
        if (entry.Flags.Count > 0) text.Append($"#, {string.Join(", ", entry.Flags)}\n");
        foreach (var previous in entry.Previous) text.Append(previous.StartsWith('~') ? $"#~| {previous[1..]}\n" : $"#| {previous}\n");

        var prefix = entry.Obsolete ? "#~ " : "";
        if (entry.Context != null) WriteString(text, prefix, "msgctxt", entry.Context);
        WriteString(text, prefix, "msgid", entry.Id);
        if (entry.IdPlural != null) {
            WriteString(text, prefix, "msgid_plural", entry.IdPlural);
            for (var i = 0; i < Math.Max(1, entry.Strings.Count); i++) WriteString(text, prefix, $"msgstr[{i}]", entry.StringAt(i));
        } else {
            WriteString(text, prefix, "msgstr", entry.StringAt(0), split: isHeader);
        }
    }

    /// <summary>
    /// One keyword and its string. A string with line breaks inside is split after each, under an
    /// empty first line, as gettext writes it; long lines are not wrapped, which keeps diffs small.
    /// With <paramref name="split"/> — the header — even a single line is written that way.
    /// </summary>
    static void WriteString(StringBuilder text, string prefix, string keyword, string value, bool split = false) {
        var breakAt = value.IndexOf('\n');
        if (breakAt < 0 || (breakAt == value.Length - 1 && !split)) {
            text.Append($"{prefix}{keyword} \"{Escape(value)}\"\n");
            return;
        }

        text.Append($"{prefix}{keyword} \"\"\n");
        var start = 0;
        while (start < value.Length) {
            var end = value.IndexOf('\n', start);
            end = end < 0 ? value.Length : end + 1;
            text.Append($"{prefix}\"{Escape(value[start..end])}\"\n");
            start = end;
        }
    }

    public static string Escape(string text) {
        var result = new StringBuilder(text.Length + 8);
        foreach (var c in text) {
            result.Append(c switch {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\v' => "\\v",
                _ => c.ToString(),
            });
        }
        return result.ToString();
    }
}
