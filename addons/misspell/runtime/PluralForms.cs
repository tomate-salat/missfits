using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Misspell;

/// <summary>
/// How many plural forms a language has and which one a number takes, as said by the
/// <c>Plural-Forms</c> header of a PO file: <c>nplurals=3; plural=(n==1 ? 0 : …);</c>.
/// <para>
/// Each form can also say which numbers it is for (<see cref="Samples"/>) and what it is called
/// (<see cref="Names"/>, from the known rules in <see cref="PluralRules"/>), which is what a
/// translator needs to fill in the right text for the right row.
/// </para>
/// </summary>
public sealed class PluralForms {
    /// <summary>What gettext assumes without a header, and what English needs: one, and everything else.</summary>
    public static PluralForms Default => _default ??= Parse("nplurals=2; plural=(n != 1);");

    static PluralForms _default;

    /// <summary>How far <see cref="Samples"/> looks. Every rule in use repeats well before this.</summary>
    public const int SampleLimit = 1000;

    readonly PluralExpression _expression;
    readonly string[][] _samples;

    /// <summary>The number of plural forms, so the number of <c>msgstr[i]</c> a plural entry has.</summary>
    public int Count { get; }

    /// <summary>The header value as it was written, or as it would be written.</summary>
    public string Header { get; }

    /// <summary>The <c>plural=</c> expression.</summary>
    public string Expression => _expression.Source;

    /// <summary>
    /// The usual names of the forms, like <c>one</c>, <c>few</c>, <c>other</c>, when the rule is
    /// one of the known ones; else <c>form 0</c>, <c>form 1</c>, …
    /// </summary>
    public string[] Names { get; }

    PluralForms(int count, PluralExpression expression, string header) {
        Count = count;
        _expression = expression;
        Header = header;
        _samples = new string[count][];
        Names = PluralRules.NamesFor(this) ?? [.. Enumerable.Range(0, count).Select(i => $"form {i}")];
    }

    /// <summary>The form the number <paramref name="n"/> takes. A rule that points past the forms counts as the last one.</summary>
    public int FormFor(long n) => (int) Math.Clamp(_expression.Evaluate(n), 0, Count - 1);

    /// <summary>
    /// The numbers that take a form, short: <c>1, 21, 31, 41, …</c> or <c>0, 2+</c>. Runs of numbers
    /// are drawn as ranges, a run that does not end within <see cref="SampleLimit"/> as <c>n+</c>.
    /// </summary>
    public string SampleText(int form, int maxParts = 4) {
        var parts = Samples(form);
        if (parts.Length == 0) return "—";
        return parts.Length <= maxParts ? string.Join(", ", parts) : string.Join(", ", parts.Take(maxParts)) + ", …";
    }

    /// <summary>The runs of numbers that take a form, as <c>"5–21"</c>, <c>"1"</c> or <c>"2+"</c>.</summary>
    public string[] Samples(int form) {
        if (form < 0 || form >= Count) return [];
        if (_samples[form] != null) return _samples[form];

        var parts = new List<string>();
        var start = -1;
        for (var n = 0; n <= SampleLimit + 1; n++) {
            var takes = n <= SampleLimit && FormFor(n) == form;
            if (takes && start < 0) start = n;
            if (takes || start < 0) continue;

            var end = n - 1;
            if (end == SampleLimit) parts.Add($"{start}+");
            else if (end > start + 1) parts.Add($"{start}–{end}");
            else parts.AddRange(Enumerable.Range(start, end - start + 1).Select(i => $"{i}"));
            start = -1;
        }
        return _samples[form] = [.. parts];
    }

    /// <summary>The smallest number that takes a form, or -1 when none below <see cref="SampleLimit"/> does.</summary>
    public long FirstNumber(int form) {
        for (var n = 0; n <= SampleLimit; n++) {
            if (FormFor(n) == form) return n;
        }
        return -1;
    }

    /// <summary>Forms no number takes — a rule with such a form is most likely written wrong.</summary>
    public IEnumerable<int> UnreachableForms() => Enumerable.Range(0, Count).Where(f => FirstNumber(f) < 0);

    /// <summary>Two rules are the same when they count the same forms and agree on every number that matters.</summary>
    public bool SameAs(PluralForms other) {
        if (other == null || other.Count != Count) return false;
        for (var n = 0; n <= SampleLimit; n++) {
            if (FormFor(n) != other.FormFor(n)) return false;
        }
        return true;
    }

    static readonly Regex CountPattern = new(@"nplurals\s*=\s*(\d+)", RegexOptions.IgnoreCase);
    static readonly Regex PluralPattern = new(@"plural\s*=\s*([^;]*)", RegexOptions.IgnoreCase);

    /// <summary>Parses a header value; throws <see cref="FormatException"/> when it is no valid rule.</summary>
    public static PluralForms Parse(string header) {
        var forms = TryParse(header, out var error);
        return forms ?? throw new FormatException(error);
    }

    /// <summary>Parses a header value; on failure returns null and says why in <paramref name="error"/>.</summary>
    public static PluralForms TryParse(string header, out string error) {
        error = null;
        header ??= "";
        var count = CountPattern.Match(header);
        if (!count.Success || !int.TryParse(count.Groups[1].Value, out var n) || n < 1 || n > 10) {
            error = "needs nplurals=1 to 10";
            return null;
        }

        var plural = PluralPattern.Match(header);
        if (!plural.Success || string.IsNullOrWhiteSpace(plural.Groups[1].Value)) {
            // A single form needs no rule; anything more does.
            if (n == 1) return new PluralForms(1, PluralExpression.TryParse("0", out _), header.Trim());
            error = "needs plural=…";
            return null;
        }

        var expression = PluralExpression.TryParse(plural.Groups[1].Value.Trim(), out var parseError);
        if (expression == null) {
            error = $"plural: {parseError}";
            return null;
        }
        return new PluralForms(n, expression, header.Trim());
    }

    /// <summary>Writes a header value the way gettext does: <c>nplurals=N; plural=EXPR;</c>.</summary>
    public static string Format(int count, string expression) {
        var text = new StringBuilder($"nplurals={count}; plural=");
        text.Append(string.IsNullOrWhiteSpace(expression) ? "0" : expression.Trim());
        text.Append(';');
        return text.ToString();
    }

    public override string ToString() => Header;
}
