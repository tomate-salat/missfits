using System.Collections.Generic;
using System.Linq;

namespace Misspell;

/// <summary>
/// One message of a PO file, with everything around it that a PO file can hold, so that reading
/// and writing a file changes nothing but what was edited.
/// <code>
/// # translator comment
/// #. extracted comment
/// #: res://ui/shop.tscn
/// #, fuzzy
/// #| msgid "previous source"
/// msgctxt "context"
/// msgid "One coin"
/// msgid_plural "{n} coins"
/// msgstr[0] "Eine Münze"
/// msgstr[1] "{n} Münzen"
/// </code>
/// </summary>
public sealed class PoEntry {
    public const string FuzzyFlag = "fuzzy";

    /// <summary><c>msgctxt</c>. Null when there is none, which is not the same as an empty one.</summary>
    public string Context;

    /// <summary><c>msgid</c>, the source text and with <see cref="Context"/> the key.</summary>
    public string Id = "";

    /// <summary><c>msgid_plural</c>; null for a message without plural.</summary>
    public string IdPlural;

    /// <summary><c>msgstr</c>, or <c>msgstr[0]</c>, <c>msgstr[1]</c>, … for a plural message.</summary>
    public List<string> Strings = [""];

    /// <summary><c># …</c>, written by translators.</summary>
    public List<string> TranslatorComments = [];

    /// <summary><c>#. …</c>, written by the tool that extracted the message.</summary>
    public List<string> ExtractedComments = [];

    /// <summary><c>#: …</c>, where the message comes from — one line each, as found.</summary>
    public List<string> References = [];

    /// <summary><c>#, …</c>, like <c>fuzzy</c> or <c>c-format</c>.</summary>
    public List<string> Flags = [];

    /// <summary><c>#| …</c>, what the message was before it changed, kept as written.</summary>
    public List<string> Previous = [];

    /// <summary><c>#~</c>: no longer in the template, kept in case it comes back.</summary>
    public bool Obsolete;

    public bool IsPlural => IdPlural != null;

    /// <summary>The header is the message with an empty id and no context.</summary>
    public bool IsHeader => Id == "" && Context == null && !Obsolete;

    /// <summary>What identifies the message within a file: context and id, joined as gettext does.</summary>
    public string Key => KeyOf(Context, Id);

    public static string KeyOf(string context, string id) => context == null ? id : $"{context}\u0004{id}";

    /// <summary>A fuzzy translation is a guess to check; Godot, like gettext, does not use it.</summary>
    public bool Fuzzy {
        get => Flags.Contains(FuzzyFlag);
        set {
            if (value == Fuzzy) return;
            if (value) Flags.Insert(0, FuzzyFlag);
            else Flags.RemoveAll(f => f == FuzzyFlag);
        }
    }

    /// <summary>The translation of a form, or "" when the entry has none for it.</summary>
    public string StringAt(int form) => form >= 0 && form < Strings.Count ? Strings[form] ?? "" : "";

    /// <summary>Sets a form, adding empty forms before it as needed.</summary>
    public void SetString(int form, string text) {
        while (Strings.Count <= form) Strings.Add("");
        Strings[form] = text ?? "";
    }

    /// <summary>Whether something is written in every form it should have.</summary>
    public bool IsTranslated(int forms) {
        var needed = IsPlural ? forms : 1;
        for (var i = 0; i < needed; i++) {
            if (string.IsNullOrEmpty(StringAt(i))) return false;
        }
        return true;
    }

    /// <summary>Exactly as many forms as the language has: missing ones empty, ones too many dropped.</summary>
    public void FitForms(int forms) {
        var needed = IsPlural ? forms : 1;
        while (Strings.Count < needed) Strings.Add("");
        if (Strings.Count > needed) Strings.RemoveRange(needed, Strings.Count - needed);
    }

    public PoEntry Clone() => new() {
        Context = Context,
        Id = Id,
        IdPlural = IdPlural,
        Strings = [.. Strings],
        TranslatorComments = [.. TranslatorComments],
        ExtractedComments = [.. ExtractedComments],
        References = [.. References],
        Flags = [.. Flags],
        Previous = [.. Previous],
        Obsolete = Obsolete,
    };

    /// <summary>A copy for another language: what the source says about the message, no translation.</summary>
    public PoEntry Untranslated(int forms) {
        var copy = new PoEntry {
            Context = Context,
            Id = Id,
            IdPlural = IdPlural,
            ExtractedComments = [.. ExtractedComments],
            References = [.. References],
            Flags = [.. Flags.Where(f => f != FuzzyFlag)],
            Strings = [],
        };
        copy.FitForms(forms);
        return copy;
    }

    public override string ToString() => Context == null ? Id : $"{Context}|{Id}";
}
