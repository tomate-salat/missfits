using System;
using System.Collections.Generic;

namespace Misspell;

/// <summary>
/// The <c>plural=</c> part of a <c>Plural-Forms</c> header: a C expression in <c>n</c> that picks
/// the form for a number. Parsed once into a small tree and evaluated as often as needed.
/// <para>
/// Understands what gettext allows: integers, <c>n</c>, parentheses, <c>! - * / % + - &lt; &gt;
/// &lt;= &gt;= == != &amp;&amp; ||</c> and <c>?:</c>, with C's precedence. Comparisons and logic
/// yield 1 or 0; division by zero yields 0 instead of crashing.
/// </para>
/// </summary>
public sealed class PluralExpression {
    readonly Func<long, long> _evaluate;

    /// <summary>The expression as it was written.</summary>
    public string Source { get; }

    PluralExpression(string source, Func<long, long> evaluate) {
        Source = source;
        _evaluate = evaluate;
    }

    public long Evaluate(long n) => _evaluate(n);

    /// <summary>Parses an expression; on failure returns null and says why in <paramref name="error"/>.</summary>
    public static PluralExpression TryParse(string source, out string error) {
        error = null;
        try {
            var parser = new Parser(source ?? "");
            var root = parser.Ternary();
            parser.ExpectEnd();
            return new PluralExpression(source, root);
        } catch (FormatException e) {
            error = e.Message;
            return null;
        }
    }

    sealed class Parser {
        readonly List<string> _tokens = [];
        int _at;

        public Parser(string source) {
            for (var i = 0; i < source.Length;) {
                var c = source[i];
                if (char.IsWhiteSpace(c)) {
                    i++;
                } else if (char.IsDigit(c)) {
                    var start = i;
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                    _tokens.Add(source[start..i]);
                } else if (i + 1 < source.Length && source.Substring(i, 2) is "&&" or "||" or "==" or "!=" or "<=" or ">=") {
                    _tokens.Add(source.Substring(i, 2));
                    i += 2;
                } else if ("n!*/%+-<>?:()".IndexOf(c) >= 0) {
                    _tokens.Add(c.ToString());
                    i++;
                } else {
                    throw new FormatException($"unexpected '{c}' at {i + 1}");
                }
            }
        }

        string Peek => _at < _tokens.Count ? _tokens[_at] : null;

        bool Take(string token) {
            if (Peek != token) return false;
            _at++;
            return true;
        }

        public void ExpectEnd() {
            if (Peek != null) throw new FormatException($"unexpected '{Peek}'");
        }

        public Func<long, long> Ternary() {
            var condition = Or();
            if (!Take("?")) return condition;
            var then = Ternary();
            if (!Take(":")) throw new FormatException("'?' without ':'");
            var otherwise = Ternary();
            return n => condition(n) != 0 ? then(n) : otherwise(n);
        }

        Func<long, long> Or() {
            var left = And();
            while (Take("||")) {
                var l = left;
                var right = And();
                left = n => l(n) != 0 || right(n) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<long, long> And() {
            var left = Equality();
            while (Take("&&")) {
                var l = left;
                var right = Equality();
                left = n => l(n) != 0 && right(n) != 0 ? 1 : 0;
            }
            return left;
        }

        Func<long, long> Equality() {
            var left = Relation();
            while (Peek is "==" or "!=") {
                var op = _tokens[_at++];
                var l = left;
                var right = Relation();
                left = op == "==" ? n => l(n) == right(n) ? 1 : 0 : n => l(n) != right(n) ? 1 : 0;
            }
            return left;
        }

        Func<long, long> Relation() {
            var left = Sum();
            while (Peek is "<" or ">" or "<=" or ">=") {
                var op = _tokens[_at++];
                var l = left;
                var right = Sum();
                left = op switch {
                    "<" => n => l(n) < right(n) ? 1 : 0,
                    ">" => n => l(n) > right(n) ? 1 : 0,
                    "<=" => n => l(n) <= right(n) ? 1 : 0,
                    _ => n => l(n) >= right(n) ? 1 : 0,
                };
            }
            return left;
        }

        Func<long, long> Sum() {
            var left = Product();
            while (Peek is "+" or "-") {
                var op = _tokens[_at++];
                var l = left;
                var right = Product();
                left = op == "+" ? n => l(n) + right(n) : n => l(n) - right(n);
            }
            return left;
        }

        Func<long, long> Product() {
            var left = Unary();
            while (Peek is "*" or "/" or "%") {
                var op = _tokens[_at++];
                var l = left;
                var right = Unary();
                left = op switch {
                    "*" => n => l(n) * right(n),
                    "/" => n => right(n) is var d && d != 0 ? l(n) / d : 0,
                    _ => n => right(n) is var d && d != 0 ? l(n) % d : 0,
                };
            }
            return left;
        }

        Func<long, long> Unary() {
            if (Take("!")) {
                var operand = Unary();
                return n => operand(n) == 0 ? 1 : 0;
            }
            if (Take("-")) {
                var operand = Unary();
                return n => -operand(n);
            }
            return Primary();
        }

        Func<long, long> Primary() {
            var token = Peek ?? throw new FormatException("expression ends too early");
            _at++;
            if (token == "n") return n => n;
            if (token == "(") {
                var inner = Ternary();
                if (!Take(")")) throw new FormatException("missing ')'");
                return inner;
            }
            if (long.TryParse(token, out var value)) return _ => value;
            throw new FormatException($"unexpected '{token}'");
        }
    }
}
