using System.Text;

namespace Gameorder;

/// <summary>A small lossless Valve KeyValues reader. Only requested value spans are changed.</summary>
public sealed class ValveKeyValues
{
    private readonly string _source;
    public IReadOnlyList<KeyValuesEntry> Entries { get; }
    private ValveKeyValues(string source, IReadOnlyList<KeyValuesEntry> entries) { _source = source; Entries = entries; }
    public static ValveKeyValues Parse(string source) => new Parser(source).Parse();
    public KeyValuesEntry? Find(string key) => Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public string WithValues(IEnumerable<KeyValuesChange> changes)
    {
        var replacements = new List<(int Start, int Length, string Text)>();
        foreach (var group in changes.GroupBy(c => c.Entry))
        {
            if (!group.Key.IsObject) throw new FormatException("只能修改 KeyValues 对象字段。");
            var missing = new List<KeyValuesChange>();
            foreach (var change in group.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                var existing = group.Key.Children.Where(c => c.Key.Equals(change.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (existing.Any(c => c.IsObject)) throw new FormatException($"字段 {change.Key} 的类型不正确。");
                if (change.Value is null)
                {
                    foreach (var entry in existing) replacements.Add((entry.KeyStart, entry.ValueStart + entry.ValueLength - entry.KeyStart, ""));
                }
                else if (existing.Count == 0) missing.Add(change);
                else foreach (var entry in existing) replacements.Add((entry.ValueStart, entry.ValueLength, Quote(change.Value)));
            }
            if (missing.Count == 0) continue;
            var close = group.Key.ClosingBraceStart;
            var lineStart = _source.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
            var insertAt = _source.AsSpan(lineStart, close - lineStart).Trim().IsEmpty ? lineStart : close;
            var newline = _source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var baseIndent = IndentAt(group.Key.KeyStart);
            var childIndent = group.Key.Children.Count > 0 ? IndentAt(group.Key.Children[0].KeyStart) : baseIndent + "\t";
            if (childIndent.Length <= baseIndent.Length) childIndent = baseIndent + "\t";
            var added = new StringBuilder();
            if (insertAt > 0 && _source[insertAt - 1] != '\n') added.Append(newline);
            foreach (var change in missing) added.Append(childIndent).Append(Quote(change.Key)).Append("\t\t").Append(Quote(change.Value!)).Append(newline);
            if (insertAt == close) added.Append(baseIndent);
            replacements.Add((insertAt, 0, added.ToString()));
        }
        var result = new StringBuilder(_source);
        foreach (var change in replacements.OrderByDescending(r => r.Start)) result.Remove(change.Start, change.Length).Insert(change.Start, change.Text);
        return result.ToString();
    }

    private string IndentAt(int position)
    {
        var start = _source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var end = start;
        while (end < position && (_source[end] == ' ' || _source[end] == '\t')) end++;
        return _source[start..end];
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private sealed class Parser(string source)
    {
        private int _position;
        public ValveKeyValues Parse()
        {
            if (source.Length > 0 && source[0] == '\uFEFF') _position++;
            var entries = ReadEntries(false);
            SkipTrivia();
            if (_position != source.Length) throw Error("多余字符");
            return new ValveKeyValues(source, entries);
        }
        private List<KeyValuesEntry> ReadEntries(bool insideObject)
        {
            var entries = new List<KeyValuesEntry>();
            while (true)
            {
                SkipTrivia();
                if (_position == source.Length)
                {
                    if (insideObject) throw Error("缺少右花括号");
                    return entries;
                }
                if (source[_position] == '}')
                {
                    if (!insideObject) throw Error("意外的右花括号");
                    return entries;
                }
                var key = ReadToken();
                SkipTrivia();
                if (_position < source.Length && source[_position] == '{')
                {
                    _position++;
                    var children = ReadEntries(true);
                    var closing = _position++;
                    entries.Add(new KeyValuesEntry(key.Value, null, children, key.Start, -1, 0, closing));
                }
                else
                {
                    var value = ReadToken();
                    entries.Add(new KeyValuesEntry(key.Value, value.Value, [], key.Start, value.Start, value.Length, -1));
                }
            }
        }
        private (string Value, int Start, int Length) ReadToken()
        {
            SkipTrivia();
            var start = _position;
            if (_position >= source.Length || source[_position] is '{' or '}') throw Error("缺少字段值");
            var value = new StringBuilder();
            if (source[_position] == '"')
            {
                _position++;
                while (_position < source.Length)
                {
                    var c = source[_position++];
                    if (c == '"') return (value.ToString(), start, _position - start);
                    if (c == '\\')
                    {
                        if (_position >= source.Length) throw Error("转义字符串未闭合");
                        var escaped = source[_position++];
                        // Keep unknown escapes intact; KeyValues files often contain Windows paths.
                        value.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\', _ => '\\' });
                        if (escaped is not ('n' or 'r' or 't' or '"' or '\\')) value.Append(escaped);
                    }
                    else value.Append(c);
                }
                throw Error("字符串未闭合");
            }
            while (_position < source.Length && !char.IsWhiteSpace(source[_position]) && source[_position] is not ('{' or '}')) value.Append(source[_position++]);
            if (_position == start) throw Error("空字段");
            return (value.ToString(), start, _position - start);
        }
        private void SkipTrivia()
        {
            while (_position < source.Length)
            {
                if (char.IsWhiteSpace(source[_position])) { _position++; continue; }
                if (_position + 1 < source.Length && source[_position] == '/' && source[_position + 1] == '/')
                {
                    _position += 2;
                    while (_position < source.Length && source[_position] != '\n') _position++;
                    continue;
                }
                if (_position + 1 < source.Length && source[_position] == '/' && source[_position + 1] == '*')
                {
                    var end = source.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                    if (end < 0) throw Error("注释未闭合");
                    _position = end + 2;
                    continue;
                }
                break;
            }
        }
        private FormatException Error(string reason) => new($"Steam 配置格式错误：{reason}（位置 {_position}）。");
    }
}

public sealed class KeyValuesEntry
{
    internal KeyValuesEntry(string key, string? value, IReadOnlyList<KeyValuesEntry> children, int keyStart, int valueStart, int valueLength, int closingBraceStart)
    { Key = key; Value = value; Children = children; KeyStart = keyStart; ValueStart = valueStart; ValueLength = valueLength; ClosingBraceStart = closingBraceStart; }
    public string Key { get; }
    public string? Value { get; }
    public IReadOnlyList<KeyValuesEntry> Children { get; }
    public bool IsObject => ClosingBraceStart >= 0;
    internal int KeyStart { get; }
    internal int ValueStart { get; }
    internal int ValueLength { get; }
    internal int ClosingBraceStart { get; }
    public string? GetValue(string key) => Children.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
}

public sealed record KeyValuesChange(KeyValuesEntry Entry, string Key, string? Value);
