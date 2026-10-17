using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TwitchDropsBot.Web.Games;

/// <summary>
/// Replaces one top-level list of strings in a JSON file's text and leaves every other byte as it was, so editing
/// favourites never re-serializes the tokens, the webhook or the file's formatting.
/// </summary>
public static class ConfigJsonArray
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // As written by hand: accents and apostrophes stay readable
    private static readonly JsonSerializerOptions StringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The list's strings, or an empty list when the property is missing.</summary>
    public static List<string> Read(byte[] file, string property)
    {
        using var document = JsonDocument.Parse(Body(file), DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("config.json doesn't hold a JSON object.");
        }

        foreach (var item in document.RootElement.EnumerateObject())
        {
            if (string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase))
            {
                if (item.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"{property} in config.json isn't a list.");
                }

                return item.Value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .ToList();
            }
        }

        return new List<string>();
    }

    /// <summary>
    /// The file with the list replaced, or added at the end of the top-level object when it is missing. Checked
    /// before it is returned: the new list reads back exactly and every other top-level value is unchanged.
    /// </summary>
    public static byte[] Replace(byte[] file, string property, IReadOnlyList<string> values)
    {
        var bomLength = HasBom(file) ? 3 : 0;
        var body = Body(file);
        var newline = Contains(body, "\r\n"u8) ? "\r\n" : "\n";

        var (propertyStart, valueStart, valueEnd, rootEnd) = Locate(body, property);
        byte[] result;

        if (propertyStart >= 0)
        {
            var indent = LineIndent(body, propertyStart);
            var list = Format(values, indent, newline);
            result = Concat(file.AsSpan(0, bomLength + valueStart), list, file.AsSpan(bomLength + valueEnd));
        }
        else
        {
            // After the last value of the top-level object, indented like its first property
            var last = rootEnd - 1;
            while (last >= 0 && IsWhitespace(body[last]))
            {
                last--;
            }

            var empty = body[last] == (byte)'{';
            var indent = FirstPropertyIndent(body) ?? "  ";
            var name = JsonSerializer.Serialize(property, StringOptions);
            var list = Encoding.UTF8.GetString(Format(values, indent, newline));
            var insertion = $"{(empty ? "" : ",")}{newline}{indent}{name}: {list}{newline}";
            result = Concat(file.AsSpan(0, bomLength + last + 1), Encoding.UTF8.GetBytes(insertion),
                file.AsSpan(bomLength + rootEnd));
        }

        Verify(file, result, property, values);
        return result;
    }

    private static (int PropertyStart, int ValueStart, int ValueEnd, int RootEnd) Locate(ReadOnlySpan<byte> body,
        string property)
    {
        var reader = new Utf8JsonReader(body, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidOperationException("config.json doesn't hold a JSON object.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
            {
                return (-1, -1, -1, (int)reader.TokenStartIndex);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var isTarget = string.Equals(reader.GetString(), property, StringComparison.OrdinalIgnoreCase);
            var propertyStart = (int)reader.TokenStartIndex;
            reader.Read();

            if (!isTarget)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new InvalidOperationException($"{property} in config.json isn't a list.");
            }

            var valueStart = (int)reader.TokenStartIndex;
            reader.Skip();
            return (propertyStart, valueStart, (int)reader.TokenStartIndex + 1, -1);
        }

        throw new InvalidOperationException("config.json ends before its top-level object does.");
    }

    private static byte[] Format(IReadOnlyList<string> values, string indent, string newline)
    {
        if (values.Count == 0)
        {
            return "[]"u8.ToArray();
        }

        // Minified files stay on one line
        if (indent.Length == 0)
        {
            return Encoding.UTF8.GetBytes("[" + string.Join(",", values.Select(Quote)) + "]");
        }

        var item = indent + indent;
        return Encoding.UTF8.GetBytes("[" + newline +
                                      string.Join("," + newline, values.Select(x => item + Quote(x))) +
                                      newline + indent + "]");
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, StringOptions);

    private static void Verify(byte[] before, byte[] after, string property, IReadOnlyList<string> values)
    {
        using var old = JsonDocument.Parse(Body(before), DocumentOptions);
        using var updated = JsonDocument.Parse(Body(after), DocumentOptions);

        if (!Read(after, property).SequenceEqual(values))
        {
            throw new InvalidOperationException($"The new {property} list didn't read back as written.");
        }

        var others = old.RootElement.EnumerateObject()
            .Where(x => !string.Equals(x.Name, property, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Name, x.Value.GetRawText()))
            .ToList();
        var othersAfter = updated.RootElement.EnumerateObject()
            .Where(x => !string.Equals(x.Name, property, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Name, x.Value.GetRawText()))
            .ToList();

        if (!others.SequenceEqual(othersAfter))
        {
            throw new InvalidOperationException("Editing the list would have changed other settings; nothing was written.");
        }
    }

    private static string LineIndent(ReadOnlySpan<byte> body, int index)
    {
        var lineStart = index;
        while (lineStart > 0 && body[lineStart - 1] != (byte)'\n')
        {
            lineStart--;
        }

        var end = lineStart;
        while (end < index && (body[end] == (byte)' ' || body[end] == (byte)'\t'))
        {
            end++;
        }

        // Something other than indentation before the property: the file is on one line
        return end == index ? Encoding.UTF8.GetString(body[lineStart..end]) : "";
    }

    private static string? FirstPropertyIndent(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                return LineIndent(body, (int)reader.TokenStartIndex);
            }
        }

        return null;
    }

    private static bool HasBom(byte[] file) =>
        file.Length >= 3 && file[0] == 0xEF && file[1] == 0xBB && file[2] == 0xBF;

    private static byte[] Body(byte[] file) => HasBom(file) ? file[3..] : file;

    private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool Contains(ReadOnlySpan<byte> body, ReadOnlySpan<byte> value) => body.IndexOf(value) >= 0;

    private static byte[] Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> middle, ReadOnlySpan<byte> last)
    {
        var result = new byte[first.Length + middle.Length + last.Length];
        first.CopyTo(result);
        middle.CopyTo(result.AsSpan(first.Length));
        last.CopyTo(result.AsSpan(first.Length + middle.Length));
        return result;
    }
}
