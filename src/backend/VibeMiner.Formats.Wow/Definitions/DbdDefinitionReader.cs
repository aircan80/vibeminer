using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VibeMiner.Domain;

namespace VibeMiner.Formats.Wow;

public sealed record DbdLimits(int MaxBytes = 1 << 20, int MaxLines = 16_384, int MaxLineCharacters = 16_384,
    int MaxColumns = 512, int MaxBlocks = 4096, int MaxFieldsPerBlock = 256, int MaxSelectors = 100_000,
    int MaxArrayLength = 4096, int MaxCommentCharacters = 2000);
public sealed record DbdReference(string Table, string Column);
public sealed record DbdColumn(string Name, string Family, bool VerifiedName, string? Comment, DbdReference? Reference);
public sealed record DbdField(DbdColumn Column, int? IntegerBits, bool? Unsigned, int ArrayLength,
    bool IsId, bool NonInline, bool RelationStorage, string? Comment);
public readonly record struct DbdBuildVersion(uint Major, uint Minor, uint Patch, uint Build) : IComparable<DbdBuildVersion>
{
    public int CompareTo(DbdBuildVersion other)
    {
        var comparison = Major.CompareTo(other.Major); if (comparison != 0) return comparison;
        comparison = Minor.CompareTo(other.Minor); if (comparison != 0) return comparison;
        comparison = Patch.CompareTo(other.Patch); return comparison != 0 ? comparison : Build.CompareTo(other.Build);
    }
    public static DbdBuildVersion Parse(string value)
    {
        if (value is null || value.Length is < 7 or > 43) throw DbdDefinitionReader.Error("DBD_INVALID", "An exact four-component numeric build is required.");
        var parts = value.Split('.');
        if (parts.Length != 4 || parts.Any(part => part.Length == 0 || part.Any(ch => ch is < '0' or > '9') ||
            part.Length > 1 && part[0] == '0')) throw DbdDefinitionReader.Error("DBD_INVALID", "An exact four-component numeric build is required.");
        var values = new uint[4];
        for (var i = 0; i < values.Length; i++)
            if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
                throw DbdDefinitionReader.Error("DBD_INVALID", "A build version component overflows unsigned32.");
        return new(values[0], values[1], values[2], values[3]);
    }
}
public sealed record DbdBuildRange(DbdBuildVersion First, DbdBuildVersion Last)
{
    public bool Contains(DbdBuildVersion value) => First.CompareTo(value) <= 0 && Last.CompareTo(value) >= 0;
}
public sealed record DbdBlock(IReadOnlyList<string> LayoutHashes, IReadOnlyList<DbdBuildRange> Builds,
    IReadOnlyList<DbdField> Fields, string? Comment);
/// <summary>Match is "build" when a block lists the exact build, or "layout" when no block lists it and
/// the file's layout hash chose the block (D080). A layout match never verifies the build.</summary>
public sealed record DbdSelection(string Status, IReadOnlyList<DbdBlock> Blocks, string Match = "build");
public sealed record DbdDocument(IReadOnlyList<DbdColumn> Columns, IReadOnlyList<DbdBlock> Blocks)
{
    public bool ListsBuild(string buildVersion, CancellationToken token = default) =>
        SelectBuild(buildVersion, token: token).Blocks.Count > 0;

    // Selection for an actual file (D080). A listed build keeps exact build+layout selection, so a listed build
    // whose file has another layout still fails. Only an unlisted build falls back to the file's layout hash,
    // and only to blocks on the same version line (major version). If several such blocks carry the layout,
    // the one with a listed build number nearest the file's build wins; a tie between different field sets
    // is ambiguous. Field count, widths and arrays are still checked against the file by the reader.
    public DbdSelection SelectFile(string buildVersion, string actualLayoutHash, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(actualLayoutHash);
        if (ListsBuild(buildVersion, token)) return SelectBuild(buildVersion, actualLayoutHash, token);
        if (!DbdDefinitionReader.IsLayout(actualLayoutHash)) throw DbdDefinitionReader.Error("DBD_INVALID", "A complete eight-hex layout is required.");
        return SelectByLayout(DbdBuildVersion.Parse(buildVersion), actualLayoutHash, token);
    }

    private DbdSelection SelectByLayout(DbdBuildVersion build, string actualLayoutHash, CancellationToken token)
    {
        var ranked = new List<(DbdBlock Block, ulong Distance)>();
        foreach (var block in Blocks)
        {
            token.ThrowIfCancellationRequested();
            if (block.LayoutHashes.Contains(actualLayoutHash, StringComparer.OrdinalIgnoreCase) && LineDistance(block, build) is { } distance)
                ranked.Add((block, distance));
        }
        return Choose(ranked);
    }

    /// <summary>For a build no block lists: every layout on its version line that selects exactly one block,
    /// ordered by layout hash. Empty for a listed build. These are the only layouts a file can open by.</summary>
    public IReadOnlyList<(string LayoutHash, DbdBlock Block)> LayoutCandidates(string buildVersion, CancellationToken token = default)
    {
        if (ListsBuild(buildVersion, token)) return [];
        var build = DbdBuildVersion.Parse(buildVersion);
        // One pass builds a layout-to-blocks index, so the work stays linear in the definition size,
        // including untrusted fetched definitions (#55).
        var index = new Dictionary<string, List<(DbdBlock Block, ulong Distance)>>(StringComparer.Ordinal);
        foreach (var block in Blocks)
        {
            token.ThrowIfCancellationRequested();
            if (LineDistance(block, build) is not { } distance) continue;
            foreach (var layout in block.LayoutHashes)
            {
                if (!index.TryGetValue(layout, out var ranked)) index[layout] = ranked = [];
                ranked.Add((block, distance));
            }
        }
        var result = new List<(string, DbdBlock)>();
        foreach (var (layout, ranked) in index.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var selected = Choose(ranked);
            if (selected.Blocks.Count == 1) result.Add((layout, selected.Blocks[0]));
        }
        return result.AsReadOnly();
    }

    // The nearest block wins; equally near blocks must have identical fields, else the layout is ambiguous.
    private static DbdSelection Choose(List<(DbdBlock Block, ulong Distance)> ranked)
    {
        if (ranked.Count == 0) return new("missing", Array.Empty<DbdBlock>(), "layout");
        var nearest = ranked.Min(value => value.Distance);
        var tied = ranked.Where(value => value.Distance == nearest).Select(value => value.Block).ToArray();
        return tied.All(block => block.Fields.SequenceEqual(tied[0].Fields))
            ? new("candidate", Array.AsReadOnly(new[] { tied[0] }), "layout")
            : new("ambiguous", Array.AsReadOnly(tied), "layout");
    }

    // Smallest build-number gap between the file's build and a listed build of this block with the same major
    // version; null when the block lists nothing on that line.
    private static ulong? LineDistance(DbdBlock block, DbdBuildVersion build)
    {
        ulong? best = null;
        foreach (var range in block.Builds)
            foreach (var bound in new[] { range.First, range.Last })
                if (bound.Major == build.Major)
                {
                    var gap = bound.Build > build.Build ? (ulong)bound.Build - build.Build : (ulong)build.Build - bound.Build;
                    if (best is null || gap < best) best = gap;
                }
        return best;
    }

    // A build-only result is candidate metadata. An actual layout still requires independently
    // acquired/header-verified source bytes; this selector makes no readability claim.
    public DbdSelection SelectBuild(string buildVersion, string? actualLayoutHash = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var build = DbdBuildVersion.Parse(buildVersion);
        if (actualLayoutHash is not null && !DbdDefinitionReader.IsLayout(actualLayoutHash))
            throw DbdDefinitionReader.Error("DBD_INVALID", "A complete eight-hex layout is required.");
        var result = new List<DbdBlock>();
        foreach (var block in Blocks)
        {
            token.ThrowIfCancellationRequested();
            var matches = false;
            foreach (var range in block.Builds) { token.ThrowIfCancellationRequested(); if (range.Contains(build)) { matches = true; break; } }
            if (matches &&
                (actualLayoutHash is null || block.LayoutHashes.Contains(actualLayoutHash, StringComparer.OrdinalIgnoreCase))) result.Add(block);
        }
        return new(result.Count switch { 0 => "missing", 1 => "candidate", _ => "ambiguous" }, result.AsReadOnly());
    }
}

/// <summary>Original bounded DBD metadata grammar reader. No game data, source acquisition or storage.</summary>
public static class DbdDefinitionReader
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex ColumnSyntax = new("^(int|float|string|locstring)(?:<([A-Za-z_][A-Za-z0-9_]*)::([A-Za-z_][A-Za-z0-9_]*)>)?[ \\t]+([A-Za-z_][A-Za-z0-9_]*)(\\?)?$", Options);
    private static readonly Regex FieldSyntax = new("^(?:\\$([^$]+)\\$)?([A-Za-z_][A-Za-z0-9_]*)(?:<(u?)([0-9]+)>)?(?:\\[([0-9]+)\\])?$", Options);

    public static DbdDocument Parse(ReadOnlySpan<byte> bytes, DbdLimits? limits = null, CancellationToken token = default)
    {
        limits ??= new();
        if (limits.MaxBytes < 1 || limits.MaxLines < 1 || limits.MaxLineCharacters < 1 || limits.MaxColumns < 1 || limits.MaxBlocks < 1 ||
            limits.MaxFieldsPerBlock < 1 || limits.MaxSelectors < 1 || limits.MaxArrayLength < 1 || limits.MaxCommentCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
        token.ThrowIfCancellationRequested();
        if (bytes.Length > limits.MaxBytes) throw Error("DBD_LIMIT", "The definition exceeds its byte limit.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Error("DBD_INVALID", "The definition is not strict UTF-8."); }
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (text[i] == '\ufeff' || char.IsControl(text[i]) && text[i] is not ('\t' or '\n' or '\r') ||
                text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))
                throw Error("DBD_INVALID", "The definition contains an unsupported control character or line ending.");
        }
        var groups = new List<List<(string Text, string? Comment)>>();
        var group = new List<(string, string?)>();
        var lineCount = 0;
        using (var lines = new StringReader(text))
            while (lines.ReadLine() is { } raw)
            {
                token.ThrowIfCancellationRequested();
                if (++lineCount > limits.MaxLines || raw.Length > limits.MaxLineCharacters)
                    throw Error("DBD_LIMIT", "The definition exceeds its line bounds.");
                if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                var commentAt = raw.IndexOf("//", StringComparison.Ordinal);
                var body = (commentAt < 0 ? raw : raw[..commentAt]).Trim();
                var comment = commentAt < 0 ? null : raw[(commentAt + 2)..].Trim();
                if (comment?.Length > limits.MaxCommentCharacters) throw Error("DBD_LIMIT", "A definition comment exceeds its bound.");
                if (body.Length == 0)
                {
                    if (group.Count > 0) { groups.Add(group); group = []; }
                }
                else group.Add((body, string.IsNullOrEmpty(comment) ? null : comment));
            }
        if (group.Count > 0) groups.Add(group);
        if (groups.Count == 0 || groups[0].Count == 0 || groups[0][0].Text != "COLUMNS")
            throw Error("DBD_INVALID", "A definition requires its initial COLUMNS section.");
        if (groups.Count - 1 > limits.MaxBlocks) throw Error("DBD_LIMIT", "The definition exceeds its block bound.");
        var columns = new Dictionary<string, DbdColumn>(StringComparer.Ordinal);
        foreach (var (body, comment) in groups[0].Skip(1))
        {
            token.ThrowIfCancellationRequested();
            var match = ColumnSyntax.Match(body);
            if (!match.Success) throw Error("DBD_UNSUPPORTED", "A column declaration uses unsupported DBD grammar.");
            if (match.Groups[4].Length > 128 || match.Groups[2].Length > 128 || match.Groups[3].Length > 128)
                throw Error("DBD_LIMIT", "A definition column or reference name exceeds its identifier bound.");
            var reference = match.Groups[2].Success ? new DbdReference(match.Groups[2].Value, match.Groups[3].Value) : null;
            var column = new DbdColumn(match.Groups[4].Value, match.Groups[1].Value, !match.Groups[5].Success, comment, reference);
            if (!columns.TryAdd(column.Name, column)) throw Error("DBD_INVALID", "A definition declares the same column twice.");
            if (columns.Count > limits.MaxColumns) throw Error("DBD_LIMIT", "The definition exceeds its column bound.");
        }
        if (columns.Count == 0) throw Error("DBD_INVALID", "The COLUMNS section is empty.");
        var blocks = new List<DbdBlock>(); var selectors = 0;
        foreach (var lines in groups.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            var layouts = new List<string>(); var builds = new List<DbdBuildRange>(); var fields = new List<DbdField>();
            var seen = new HashSet<string>(StringComparer.Ordinal); var hasLayoutLine = false; string? blockComment = null;
            foreach (var (body, lineComment) in lines)
            {
                token.ThrowIfCancellationRequested();
                if (body.StartsWith("LAYOUT ", StringComparison.Ordinal) || body.StartsWith("BUILD ", StringComparison.Ordinal) || body.StartsWith("COMMENT ", StringComparison.Ordinal))
                {
                    if (fields.Count > 0) throw Error("DBD_INVALID", "Selectors must precede their block's fields.");
                    if (body.StartsWith("COMMENT ", StringComparison.Ordinal))
                    {
                        if (blockComment is not null) throw Error("DBD_INVALID", "A definition repeats a block comment.");
                        blockComment = body[8..];
                        if (blockComment.Length > limits.MaxCommentCharacters) throw Error("DBD_LIMIT", "A block comment exceeds its bound.");
                        continue;
                    }
                    var isLayout = body.StartsWith("LAYOUT ", StringComparison.Ordinal);
                    if (isLayout && hasLayoutLine) throw Error("DBD_INVALID", "A definition repeats a LAYOUT line.");
                    hasLayoutLine |= isLayout;
                    foreach (var raw in body[(isLayout ? 7 : 6)..].Split(','))
                    {
                        if (++selectors > limits.MaxSelectors) throw Error("DBD_LIMIT", "The definition exceeds its selector work bound.");
                        var value = raw.Trim();
                        if (isLayout)
                        {
                            if (!IsLayout(value) || layouts.Contains(value, StringComparer.OrdinalIgnoreCase))
                                throw Error("DBD_INVALID", "A layout selector is malformed or repeated.");
                            layouts.Add(value.ToUpperInvariant());
                        }
                        else
                        {
                            var range = value.Split('-');
                            if (range.Length is < 1 or > 2) throw Error("DBD_INVALID", "A build selector is malformed.");
                            var first = DbdBuildVersion.Parse(range[0]); var last = range.Length == 2 ? DbdBuildVersion.Parse(range[1]) : first;
                            if (first.CompareTo(last) > 0) throw Error("DBD_INVALID", "A build selector range is reversed.");
                            builds.Add(new(first, last));
                        }
                    }
                    continue;
                }
                var match = FieldSyntax.Match(body);
                if (!match.Success) throw Error("DBD_UNSUPPORTED", "A field declaration uses unsupported DBD grammar.");
                var name = match.Groups[2].Value;
                if (!columns.TryGetValue(name, out var column) || !seen.Add(name)) throw Error("DBD_INVALID", "A block uses an undefined or repeated field.");
                var annotations = new HashSet<string>(StringComparer.Ordinal);
                if (match.Groups[1].Success)
                    foreach (var annotation in match.Groups[1].Value.Split(','))
                        if (annotation is not ("id" or "noninline" or "relation") || !annotations.Add(annotation))
                            throw Error("DBD_UNSUPPORTED", "A field annotation is unsupported or repeated.");
                int? bits = null; bool? unsigned = null;
                if (match.Groups[4].Success)
                {
                    if (column.Family != "int" || !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is not (8 or 16 or 32 or 64))
                        throw Error("DBD_UNSUPPORTED", "A field width is unsupported for its scalar family.");
                    bits = width; unsigned = match.Groups[3].Value == "u";
                }
                var array = 1;
                if (match.Groups[5].Success && (!int.TryParse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out array) || array < 1))
                    throw Error("DBD_INVALID", "An array length must be a positive bounded integer.");
                if (array > limits.MaxArrayLength) throw Error("DBD_LIMIT", "A field array exceeds its bound.");
                var comment = column.Comment is null ? lineComment : lineComment is null ? column.Comment : column.Comment + "\n" + lineComment;
                if (comment?.Length > limits.MaxCommentCharacters) throw Error("DBD_LIMIT", "A field comment exceeds its bound.");
                fields.Add(new(column, bits, unsigned, array, annotations.Contains("id"), annotations.Contains("noninline"), annotations.Contains("relation"), comment));
                if (fields.Count > limits.MaxFieldsPerBlock) throw Error("DBD_LIMIT", "A definition block exceeds its field bound.");
            }
            if (builds.Count == 0 || fields.Count == 0) throw Error("DBD_INVALID", "Every definition block requires BUILD selectors and fields.");
            blocks.Add(new(layouts.AsReadOnly(), builds.AsReadOnly(), fields.AsReadOnly(), blockComment));
        }
        if (blocks.Count == 0) throw Error("DBD_INVALID", "A definition requires at least one version block.");
        token.ThrowIfCancellationRequested();
        return new(new ReadOnlyCollection<DbdColumn>(columns.Values.ToArray()), blocks.AsReadOnly());
    }
    internal static bool IsLayout(string value) => value.Length == 8 && value.All(char.IsAsciiHexDigit);
    internal static DiscoveryException Error(string code, string message) => new(code, message);
}
