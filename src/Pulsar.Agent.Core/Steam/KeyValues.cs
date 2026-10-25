using System.Text;

namespace Pulsar.Agent.Core.Steam;

/// <summary>
/// Valve's KeyValues text (VDF): what SteamCMD prints for <c>app_info_print</c> and writes in a server's app manifest
/// (<c>steamapps/appmanifest_*.acf</c>). Both are text the server's account controls (its SteamCMD copy, its folder), so
/// reading is bounded in depth and in entries, and what cannot be read gives null instead of throwing.
/// </summary>
public sealed class KeyValues
{
    public const int MaxDepth = 32;
    public const int MaxEntries = 200_000;

    private readonly List<KeyValuePair<string, KeyValues>> _children = [];

    private KeyValues(string? value) => Value = value;

    /// <summary>The value of a key that has one; null for a block.</summary>
    public string? Value { get; }

    public IReadOnlyList<KeyValuePair<string, KeyValues>> Children => _children;

    /// <summary>The first child named <paramref name="key"/> (Valve's keys ignore case); null when there is none.</summary>
    public KeyValues? this[string key] =>
        _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The pairs of a whole text (an app manifest): a block holding them; null when the text is not KeyValues.</summary>
    public static KeyValues? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(text, 0);
        var root = new KeyValues(null);
        return reader.ReadPairs(root, depth: 0, untilBrace: false) ? root : null;
    }

    /// <summary>
    /// The block <c>"<paramref name="key"/>" { … }</c> starting a line of <paramref name="text"/> (what
    /// <c>app_info_print</c> prints among SteamCMD's other lines); null when there is none or it cannot be read.
    /// </summary>
    public static KeyValues? FindBlock(string text, string key)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(key);
        var quoted = $"\"{key}\"";
        for (var at = text.IndexOf(quoted, StringComparison.Ordinal); at >= 0; at = text.IndexOf(quoted, at + quoted.Length, StringComparison.Ordinal))
        {
            var lineStart = LineStart(text, at);
            if (!text.AsSpan(lineStart, at - lineStart).IsWhiteSpace())
            {
                continue;
            }

            var reader = new Reader(text, at + quoted.Length);
            if (reader.NextIsOpeningBrace())
            {
                var block = new KeyValues(null);
                reader.SkipOpeningBrace();
                if (reader.ReadPairs(block, depth: 1, untilBrace: true))
                {
                    return block;
                }
            }
        }

        return null;
    }

    private static int LineStart(string text, int at)
    {
        if (at == 0)
        {
            return 0;
        }

        var newline = text.LastIndexOfAny(['\n', '\r'], at - 1);
        return newline < 0 ? 0 : newline + 1;
    }

    private sealed class Reader(string text, int position)
    {
        private int _position = position;
        private int _entries;

        public bool NextIsOpeningBrace()
        {
            SkipSpace();
            return _position < text.Length && text[_position] == '{';
        }

        public void SkipOpeningBrace() => _position++;

        /// <summary>Reads key-value pairs into <paramref name="block"/> up to its closing brace (or the end of the text).</summary>
        public bool ReadPairs(KeyValues block, int depth, bool untilBrace)
        {
            if (depth > MaxDepth)
            {
                return false;
            }

            while (true)
            {
                SkipSpace();
                if (_position >= text.Length)
                {
                    return !untilBrace;
                }

                if (text[_position] == '}')
                {
                    _position++;
                    return untilBrace;
                }

                if (text[_position] == '{' || ++_entries > MaxEntries || ReadToken() is not { } key)
                {
                    return false;
                }

                SkipSpace();
                if (_position < text.Length && text[_position] == '{')
                {
                    _position++;
                    var child = new KeyValues(null);
                    if (!ReadPairs(child, depth + 1, untilBrace: true))
                    {
                        return false;
                    }

                    block._children.Add(new(key, child));
                }
                else if (ReadToken() is { } value)
                {
                    block._children.Add(new(key, new KeyValues(value)));
                }
                else
                {
                    return false;
                }

                SkipConditional();
            }
        }

        /// <summary>A quoted string (with Valve's escapes) or a bare word; null at a brace or the end of the text.</summary>
        private string? ReadToken()
        {
            if (_position >= text.Length || text[_position] is '{' or '}')
            {
                return null;
            }

            var token = new StringBuilder();
            if (text[_position] == '"')
            {
                _position++;
                while (_position < text.Length && text[_position] != '"')
                {
                    var c = text[_position++];
                    if (c == '\\' && _position < text.Length)
                    {
                        var escaped = text[_position++];
                        token.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            _ => escaped,
                        });
                    }
                    else
                    {
                        token.Append(c);
                    }
                }

                if (_position >= text.Length)
                {
                    return null; // never closed
                }

                _position++;
                return token.ToString();
            }

            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not ('{' or '}' or '"'))
            {
                token.Append(text[_position++]);
            }

            return token.ToString();
        }

        /// <summary>A platform condition after a value (<c>[$WIN32]</c>): ignored.</summary>
        private void SkipConditional()
        {
            var at = _position;
            while (at < text.Length && text[at] is ' ' or '\t')
            {
                at++;
            }

            if (at < text.Length && text[at] == '[')
            {
                var end = text.IndexOf(']', at);
                _position = end < 0 ? text.Length : end + 1;
            }
        }

        private void SkipSpace()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                }
                else if (text[_position] == '/' && _position + 1 < text.Length && text[_position + 1] == '/')
                {
                    var end = text.IndexOf('\n', _position);
                    _position = end < 0 ? text.Length : end + 1;
                }
                else
                {
                    return;
                }
            }
        }
    }
}
