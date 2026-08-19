namespace DiSkyAtlas.Services.Docs;

/// <summary>
/// Per-page-render interactive state for dynamic doc components (toggles today; inputs later
/// for the code-playground follow-up). One instance is created per rendered doc page and
/// cascaded through the markdown component tree — never shared between visitors or pages.
/// </summary>
public sealed class DocPageState
{
    private readonly Dictionary<string, bool> _toggles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a value changes; conditional blocks re-render on it.</summary>
    public event Action? Changed;

    public bool Get(string id) => _toggles.TryGetValue(id, out var v) && v;

    public void Set(string id, bool value)
    {
        _toggles[id] = value;
        Changed?.Invoke();
    }

    /// <summary>Seeds a default without firing <see cref="Changed"/> (used before first render).</summary>
    public void Seed(string id, bool value) => _toggles.TryAdd(id, value);
}

/// <summary>
/// Tiny boolean expression evaluator over toggle ids for <c>::: when</c> blocks.
/// Grammar: <c>expr := and (('||'|'or') and)*; and := unary (('&amp;&amp;'|'and') unary)*;
/// unary := ('!'|'not') unary | '(' expr ')' | id</c>. Ids match <c>[A-Za-z0-9_-]+</c>;
/// <c>true</c>/<c>false</c> are literals.
/// </summary>
public static class DocCondition
{
    public static bool TryParse(string expression, out Func<DocPageState, bool> evaluate, out string? error)
    {
        try
        {
            var parser = new Parser(expression);
            var node = parser.ParseOr();
            parser.ExpectEnd();
            evaluate = node;
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            evaluate = _ => false;
            error = e.Message;
            return false;
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public Func<DocPageState, bool> ParseOr()
        {
            var left = ParseAnd();
            while (TryConsume("||") || TryConsumeWord("or"))
            {
                var l = left;
                var r = ParseAnd();
                left = s => l(s) || r(s);
            }
            return left;
        }

        private Func<DocPageState, bool> ParseAnd()
        {
            var left = ParseUnary();
            while (TryConsume("&&") || TryConsumeWord("and"))
            {
                var l = left;
                var r = ParseUnary();
                left = s => l(s) && r(s);
            }
            return left;
        }

        private Func<DocPageState, bool> ParseUnary()
        {
            SkipSpaces();
            if (TryConsume("!") || TryConsumeWord("not"))
            {
                var inner = ParseUnary();
                return s => !inner(s);
            }
            if (TryConsume("("))
            {
                var inner = ParseOr();
                SkipSpaces();
                if (!TryConsume(")"))
                    throw new FormatException("missing “)”");
                return inner;
            }

            var id = ReadIdentifier();
            return id.ToLowerInvariant() switch
            {
                "true" => _ => true,
                "false" => _ => false,
                _ => s => s.Get(id)
            };
        }

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_pos < text.Length)
                throw new FormatException($"unexpected “{text[_pos..]}”");
        }

        private string ReadIdentifier()
        {
            SkipSpaces();
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '-'))
                _pos++;
            if (_pos == start)
                throw new FormatException(_pos >= text.Length ? "unexpected end of expression" : $"unexpected “{text[_pos]}”");
            return text[start.._pos];
        }

        private bool TryConsume(string token)
        {
            SkipSpaces();
            if (!text.AsSpan(_pos).StartsWith(token, StringComparison.Ordinal))
                return false;
            _pos += token.Length;
            return true;
        }

        private bool TryConsumeWord(string word)
        {
            SkipSpaces();
            if (!text.AsSpan(_pos).StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return false;
            var after = _pos + word.Length;
            if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] is '_' or '-'))
                return false; // part of a longer identifier
            _pos = after;
            return true;
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }
    }
}
