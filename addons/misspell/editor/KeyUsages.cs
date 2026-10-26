#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace Misspell.Editor;

/// <summary>
/// Where in the project a text is used: every string literal in the scenes, resources and scripts
/// that is exactly that text. Comments are skipped, and so are the headers of scenes and resources
/// (<c>[node name="Buy"]</c> is a node, not a text). An interpolated C# string with holes is no
/// exact use of anything; the strings inside its holes are.
/// <para>
/// Plain C#. Files are read again only when they changed, and the project is looked through again
/// at most every <see cref="RefreshAfterMs"/>, so asking for every selected row stays cheap.
/// </para>
/// </summary>
public sealed class KeyUsages {
    public static readonly string[] Extensions = ["tscn", "tres", "gd", "cs"];
    const ulong RefreshAfterMs = 2000;
    const int CodeLength = 80;

    /// <summary>One use: the file, the line the literal starts on (from 1), and that line of code.</summary>
    public readonly record struct Usage(string Path, int Line, string Code) {
        public string Reference => $"{Path}:{Line}";
    }

    sealed class ScannedFile {
        public ulong Modified;
        public ulong ReadAt;
        public List<Usage> Usages;
        public List<string> Texts;
    }

    readonly string _root;
    readonly Dictionary<string, ScannedFile> _files = [];
    Dictionary<string, List<Usage>> _byText;
    ulong _lastWalk;
    bool _walked;

    public KeyUsages(string root = "res://") => _root = root;

    /// <summary>Every use of exactly this text, by file and line.</summary>
    public IReadOnlyList<Usage> Of(string text) {
        if (!_walked || Time.GetTicksMsec() - _lastWalk > RefreshAfterMs) Refresh();
        return text != null && _byText.TryGetValue(text, out var usages) ? usages : [];
    }

    /// <summary>Looks through the project now, reading only the files that changed.</summary>
    public void Refresh() {
        var seen = new HashSet<string>();
        var changed = false;
        Walk(_root, seen, ref changed);
        foreach (var gone in _files.Keys.Where(p => !seen.Contains(p)).ToList()) {
            _files.Remove(gone);
            changed = true;
        }
        if (changed || _byText == null) {
            _byText = [];
            foreach (var file in _files.OrderBy(f => f.Key, System.StringComparer.Ordinal).Select(f => f.Value)) {
                for (var i = 0; i < file.Texts.Count; i++) {
                    if (!_byText.TryGetValue(file.Texts[i], out var list)) _byText[file.Texts[i]] = list = [];
                    list.Add(file.Usages[i]);
                }
            }
        }
        _lastWalk = Time.GetTicksMsec();
        _walked = true;
    }

    void Walk(string folder, HashSet<string> seen, ref bool changed) {
        using var dir = DirAccess.Open(folder);
        if (dir == null || FileAccess.FileExists(folder.PathJoin(".gdignore"))) return;

        foreach (var name in dir.GetFiles()) {
            var extension = name.GetExtension().ToLowerInvariant();
            if (!Extensions.Contains(extension)) continue;
            var path = folder.PathJoin(name);
            seen.Add(path);
            var modified = FileAccess.GetModifiedTime(path);
            // Times are whole seconds: a file changed in the second it was read is read again.
            if (_files.TryGetValue(path, out var known) && known.Modified == modified && modified < known.ReadAt) continue;
            _files[path] = Scan(path, extension, modified);
            changed = true;
        }
        foreach (var sub in dir.GetDirectories()) {
            if (sub.StartsWith('.')) continue;
            Walk(folder.PathJoin(sub), seen, ref changed);
        }
    }

    static ScannedFile Scan(string path, string extension, ulong modified) {
        var source = FileAccess.GetFileAsString(path);
        var lines = source.Split('\n');
        var literals = Literals(source, extension);
        return new ScannedFile {
            Modified = modified,
            ReadAt = (ulong) Time.GetUnixTimeFromSystem(),
            Texts = literals.Select(l => l.Text).ToList(),
            Usages = literals.Select(l => new Usage(path, l.Line, CodeAt(lines, l.Line))).ToList(),
        };
    }

    static string CodeAt(string[] lines, int line) {
        var code = line - 1 < lines.Length ? lines[line - 1].Trim() : "";
        return code.Length > CodeLength ? code[..(CodeLength - 1)] + "…" : code;
    }

    // ---- literals ----------------------------------------------------------------------------

    /// <summary>The string literals of a file's source, each with the line it starts on.</summary>
    public static List<(string Text, int Line)> Literals(string source, string extension) {
        var found = new List<(string, int)>();
        var lexer = new Lexer(source.Replace("\r\n", "\n"), found);
        switch (extension) {
            case "cs": lexer.CSharp(insideHole: false); break;
            case "gd": lexer.GdScript(); break;
            default: lexer.Resource(); break;
        }
        return found;
    }

    sealed class Lexer(string s, List<(string, int)> found) {
        int _i;
        int _line = 1;

        char At(int offset = 0) => _i + offset < s.Length ? s[_i + offset] : '\0';
        bool StartsHere(string text) => string.CompareOrdinal(s, _i, text, 0, text.Length) == 0;

        void Step() {
            if (s[_i] == '\n') _line++;
            _i++;
        }

        void SkipPast(string end) {
            while (_i < s.Length && !StartsHere(end)) Step();
            for (var k = 0; k < end.Length && _i < s.Length; k++) Step();
        }

        /// <summary>Up to the closing quote, with backslash escapes. Ends at a line break when <paramref name="oneLine"/>.</summary>
        string Escaped(char quote, bool oneLine) {
            var text = new StringBuilder();
            while (_i < s.Length && s[_i] != quote && !(oneLine && s[_i] == '\n')) {
                if (s[_i] == '\\' && _i + 1 < s.Length) {
                    _i++;
                    text.Append(Unescape());
                    continue;
                }
                text.Append(s[_i]);
                Step();
            }
            if (_i < s.Length && s[_i] == quote) _i++;
            return text.ToString();
        }

        /// <summary>The escape after a backslash; <see cref="_i"/> is on its letter.</summary>
        string Unescape() {
            var c = s[_i];
            Step();
            switch (c) {
                case 'n': return "\n";
                case 't': return "\t";
                case 'r': return "\r";
                case '0': return "\0";
                case 'u' or 'U' or 'x': {
                    var length = c == 'U' ? 8 : 4;
                    var start = _i;
                    while (_i < s.Length && _i - start < length && System.Uri.IsHexDigit(s[_i])) _i++;
                    return int.TryParse(s[start.._i], System.Globalization.NumberStyles.HexNumber, null, out var code) && code <= 0x10FFFF
                        ? char.ConvertFromUtf32(code) : "";
                }
                default: return c.ToString();
            }
        }

        // ---- scenes and resources ----

        /// <summary><c>.tscn</c>/<c>.tres</c>: every string outside the <c>[…]</c> headers.</summary>
        public void Resource() {
            var header = false;
            var lineStart = true;
            while (_i < s.Length) {
                var c = s[_i];
                if (c == '\n') {
                    lineStart = true;
                    header = false;
                    Step();
                } else if (lineStart && c is ' ' or '\t') {
                    _i++;
                } else {
                    if (lineStart) header = c == '[';
                    lineStart = false;
                    if (c == '"') {
                        var line = _line;
                        _i++;
                        var text = Escaped('"', oneLine: false);
                        if (!header) found.Add((text, line));
                    } else {
                        _i++;
                    }
                }
            }
        }

        // ---- GDScript ----

        public void GdScript() {
            while (_i < s.Length) {
                var c = s[_i];
                if (c == '#') {
                    while (_i < s.Length && s[_i] != '\n') _i++;
                } else if (c is '"' or '\'') {
                    var raw = _i > 0 && s[_i - 1] == 'r' && (_i < 2 || !IsWordChar(s[_i - 2]));
                    var line = _line;
                    var triple = new string(c, 3);
                    string text;
                    if (StartsHere(triple)) {
                        _i += 3;
                        text = raw ? Until(triple) : EscapedUntil(triple);
                    } else {
                        _i++;
                        text = raw ? Until(c.ToString(), oneLine: true) : Escaped(c, oneLine: true);
                    }
                    found.Add((text, line));
                } else {
                    Step();
                }
            }
        }

        string EscapedUntil(string end) {
            var text = new StringBuilder();
            while (_i < s.Length && !StartsHere(end)) {
                if (s[_i] == '\\' && _i + 1 < s.Length) {
                    _i++;
                    text.Append(Unescape());
                    continue;
                }
                text.Append(s[_i]);
                Step();
            }
            _i = System.Math.Min(s.Length, _i + end.Length);
            return text.ToString();
        }

        /// <summary>Up to <paramref name="end"/> as written. A raw GDScript string still lets a backslash keep its quote.</summary>
        string Until(string end, bool oneLine = false) {
            var start = _i;
            while (_i < s.Length && !StartsHere(end) && !(oneLine && s[_i] == '\n')) {
                if (s[_i] == '\\' && _i + 1 < s.Length) Step();
                Step();
            }
            var text = s[start.._i];
            if (StartsHere(end)) _i += end.Length;
            return text;
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        // ---- C# ----

        /// <summary>Code up to its end — or, inside an interpolation hole, up to the brace that closes it.</summary>
        public void CSharp(bool insideHole) {
            var depth = 0;
            while (_i < s.Length) {
                var c = s[_i];
                if (c == '/' && At(1) == '/') {
                    while (_i < s.Length && s[_i] != '\n') _i++;
                } else if (c == '/' && At(1) == '*') {
                    _i += 2;
                    SkipPast("*/");
                } else if (c == '\'') {
                    _i++;
                    if (At() == '\\') _i++;
                    if (_i < s.Length) _i++;
                    while (_i < s.Length && s[_i] != '\'' && s[_i] != '\n') _i++;
                    if (At() == '\'') _i++;
                } else if (c is '"' or '$' or '@' && StringStart() is { } start) {
                    CSharpString(start.Dollars, start.Verbatim, start.Quotes);
                } else if (insideHole && c == '{') {
                    depth++;
                    _i++;
                } else if (insideHole && c == '}') {
                    if (depth-- == 0) return;
                    _i++;
                } else {
                    Step();
                }
            }
        }

        /// <summary>When a string starts here: its <c>$</c>s, whether it has an <c>@</c>, and how many quotes open it.</summary>
        (int Dollars, bool Verbatim, int Quotes)? StringStart() {
            if (_i > 0 && IsWordChar(s[_i - 1]) && s[_i] != '"') return null;
            var j = _i;
            int dollars = 0, ats = 0;
            while (j < s.Length && s[j] is '$' or '@') {
                if (s[j] == '$') dollars++;
                else ats++;
                j++;
            }
            if (j >= s.Length || s[j] != '"' || ats > 1) return null;
            var quotes = 0;
            while (j + quotes < s.Length && s[j + quotes] == '"') quotes++;
            // "" is an empty string, not the start of a raw one.
            if (quotes == 2 || ats == 1) quotes = 1;
            _i = j;
            return (dollars, ats == 1, quotes);
        }

        void CSharpString(int dollars, bool verbatim, int quotes) {
            var line = _line;
            _i += quotes;
            var raw = quotes >= 3;
            var close = new string('"', quotes);
            var text = new StringBuilder();
            var holes = false;

            while (_i < s.Length) {
                if (raw ? StartsHere(close) : s[_i] == '"') {
                    if (verbatim && At(1) == '"') {
                        text.Append('"');
                        _i += 2;
                        continue;
                    }
                    _i += quotes;
                    break;
                }
                if (!raw && !verbatim && s[_i] == '\n') break;
                if (dollars > 0 && s[_i] is '{' or '}') {
                    var run = 0;
                    while (_i + run < s.Length && s[_i + run] == s[_i]) run++;
                    var opens = raw ? run >= dollars : run % 2 == 1;
                    if (s[_i] == '{' && opens) {
                        // The braces before the hole's own are text (raw), or escaped pairs.
                        var literal = raw ? run - dollars : run / 2;
                        text.Append('{', literal);
                        _i += run;
                        holes = true;
                        CSharp(insideHole: true);
                        _i = System.Math.Min(s.Length, _i + (raw ? dollars : 1));
                        continue;
                    }
                    text.Append(s[_i], raw ? run : run / 2 + run % 2);
                    _i += run;
                    continue;
                }
                if (!raw && !verbatim && s[_i] == '\\' && _i + 1 < s.Length) {
                    _i++;
                    text.Append(Unescape());
                    continue;
                }
                text.Append(s[_i]);
                Step();
            }

            if (holes) return;
            found.Add((raw ? Dedent(text.ToString()) : text.ToString(), line));
        }

        /// <summary>A raw string over several lines loses its first and last line and the closing line's indentation.</summary>
        static string Dedent(string text) {
            if (!text.Contains('\n')) return text;
            var lines = text.Split('\n').ToList();
            var indent = lines[^1];
            if (indent.Trim().Length > 0) return text;
            lines.RemoveAt(lines.Count - 1);
            lines.RemoveAt(0);
            return string.Join("\n", lines.Select(l => l.StartsWith(indent) ? l[indent.Length..] : l.TrimStart()));
        }
    }
}
#endif
