using System.Collections.Generic;
using System.Linq;

namespace Misspell;

/// <summary>
/// The plural rules of the languages a game is commonly translated into, as gettext writes them,
/// with the names of their forms. A new language file gets its <c>Plural-Forms</c> header from
/// here, and a file whose header disagrees with its language is pointed out.
/// </summary>
public static class PluralRules {
    /// <summary>One rule and the languages that use it.</summary>
    public sealed record Rule(string Header, string[] Names, string[] Languages) {
        PluralForms _forms;
        public PluralForms Forms => _forms ??= PluralForms.Parse(Header);
    }

    public static readonly Rule[] All = [
        new("nplurals=1; plural=0;", ["other"],
            ["ja", "zh", "ko", "vi", "th", "id", "ms", "lo", "km", "my", "jv", "su"]),
        new("nplurals=2; plural=(n != 1);", ["one", "other"],
            ["en", "de", "nl", "sv", "da", "no", "nb", "nn", "fi", "et", "it", "es", "pt", "el", "hu", "bg", "eo", "ca",
             "eu", "gl", "he", "af", "sq", "az", "ka", "kk", "ky", "tr", "hi", "bn", "ta", "te", "ur", "sw", "fy", "lb",
             "fo", "ml", "mr", "gu", "kn", "ne", "pa", "uz", "mn", "tk", "fil", "tl"]),
        new("nplurals=2; plural=(n > 1);", ["one", "other"],
            ["fr", "pt_BR", "oc", "ln", "mg", "ti", "wa"]),
        new("nplurals=2; plural=(n%10 != 1 || n%100 == 11);", ["one", "other"],
            ["is"]),
        new("nplurals=2; plural=(n%10 == 1 && n%100 != 11 ? 0 : 1);", ["one", "other"],
            ["mk"]),
        new("nplurals=3; plural=(n%10 == 1 && n%100 != 11 ? 0 : n%10 >= 2 && n%10 <= 4 && (n%100 < 10 || n%100 >= 20) ? 1 : 2);",
            ["one", "few", "many"],
            ["ru", "uk", "be", "sr", "hr", "bs"]),
        new("nplurals=3; plural=(n == 1 ? 0 : n%10 >= 2 && n%10 <= 4 && (n%100 < 10 || n%100 >= 20) ? 1 : 2);",
            ["one", "few", "many"],
            ["pl"]),
        new("nplurals=3; plural=(n == 1 ? 0 : n >= 2 && n <= 4 ? 1 : 2);", ["one", "few", "other"],
            ["cs", "sk"]),
        new("nplurals=3; plural=(n%10 == 1 && n%100 != 11 ? 0 : n%10 >= 2 && (n%100 < 10 || n%100 >= 20) ? 1 : 2);",
            ["one", "few", "other"],
            ["lt"]),
        new("nplurals=3; plural=(n%10 == 1 && n%100 != 11 ? 0 : n != 0 ? 1 : 2);", ["one", "other", "zero"],
            ["lv"]),
        new("nplurals=3; plural=(n == 1 ? 0 : (n == 0 || (n%100 > 0 && n%100 < 20)) ? 1 : 2);", ["one", "few", "other"],
            ["ro"]),
        new("nplurals=4; plural=(n%100 == 1 ? 0 : n%100 == 2 ? 1 : n%100 == 3 || n%100 == 4 ? 2 : 3);", ["one", "two", "few", "other"],
            ["sl"]),
        new("nplurals=4; plural=(n == 1 ? 0 : n == 0 || (n%100 > 1 && n%100 < 11) ? 1 : n%100 > 10 && n%100 < 20 ? 2 : 3);",
            ["one", "few", "many", "other"],
            ["mt"]),
        new("nplurals=4; plural=(n == 1 ? 0 : n == 2 ? 1 : n != 8 && n != 11 ? 2 : 3);", ["one", "two", "few", "other"],
            ["cy"]),
        new("nplurals=4; plural=(n == 1 || n == 11 ? 0 : n == 2 || n == 12 ? 1 : n > 2 && n < 20 ? 2 : 3);", ["one", "two", "few", "other"],
            ["gd"]),
        new("nplurals=5; plural=(n == 1 ? 0 : n == 2 ? 1 : n < 7 ? 2 : n < 11 ? 3 : 4);", ["one", "two", "few", "many", "other"],
            ["ga"]),
        new("nplurals=6; plural=(n == 0 ? 0 : n == 1 ? 1 : n == 2 ? 2 : n%100 >= 3 && n%100 <= 10 ? 3 : n%100 >= 11 ? 4 : 5);",
            ["zero", "one", "two", "few", "many", "other"],
            ["ar"]),
    ];

    static readonly Dictionary<string, Rule> ByLanguage = All
        .SelectMany(rule => rule.Languages.Select(language => (language, rule)))
        .ToDictionary(p => p.language, p => p.rule);

    /// <summary>
    /// The rule of a locale such as <c>pt_BR</c>, <c>de-AT</c> or <c>pl</c>: the whole locale if it
    /// has one of its own, else its language. Null for a language not in the table.
    /// </summary>
    public static Rule For(string locale) {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        var normal = Normalize(locale);
        if (ByLanguage.TryGetValue(normal, out var exact)) return exact;
        var language = normal.Split('_')[0];
        return ByLanguage.GetValueOrDefault(language);
    }

    /// <summary><c>de-AT</c>, <c>de_at</c> and <c>de_AT.UTF-8</c> all become <c>de_AT</c>.</summary>
    public static string Normalize(string locale) {
        var trimmed = locale.Trim().Split('.', '@')[0].Replace('-', '_');
        var parts = trimmed.Split('_');
        parts[0] = parts[0].ToLowerInvariant();
        for (var i = 1; i < parts.Length; i++) parts[i] = parts[i].Length == 2 ? parts[i].ToUpperInvariant() : parts[i];
        return string.Join("_", parts);
    }

    /// <summary>The names of the forms of a rule, if it is one of the known ones in effect.</summary>
    internal static string[] NamesFor(PluralForms forms) {
        // By the text first: that is how the rules of the table get their own names while being built.
        if (All.FirstOrDefault(rule => rule.Header == forms.Header) is { } same) return same.Names;
        // Then by what the rules do, so another way of writing one still gets its names.
        return All.FirstOrDefault(rule => rule.Names.Length == forms.Count && rule.Forms.SameAs(forms))?.Names;
    }
}
