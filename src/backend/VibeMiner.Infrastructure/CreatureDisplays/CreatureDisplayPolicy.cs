using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal static class CreatureDisplayPolicy
{
    internal const string DisplayTable = "bfdaf9f1", ModelTable = "7f695fa5";
    internal static string Table(CreatureDisplayTargetRole role) => role == CreatureDisplayTargetRole.Display ? DisplayTable : ModelTable;
    internal static string Definition(CreatureDisplayTargetRole role) => role == CreatureDisplayTargetRole.Display
        ? "64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425"
        : "ace9dfbb1acdec0eae7899273a4e983d851d2de8d6471fd8eb95ac8e70584e2a";
    // Retail layouts, then the Classic layouts (F75F884C/3FFEE408) that 1.15, 2.5 and 5.5 builds share.
    internal static IReadOnlyList<string> Layouts(CreatureDisplayTargetRole role) => role == CreatureDisplayTargetRole.Display ? ["7275F5F6", "F75F884C"] : ["0F5449F8", "3FFEE408"];
    internal static string Layout(CreatureDisplayTargetRole role) => Layouts(role)[0];
    internal static bool IsLayout(CreatureDisplayTargetRole role, string? layout) => layout is not null && Layouts(role).Contains(layout.ToUpperInvariant());
    internal const string ExtendedField = "ExtendedDisplayInfoID", VariationField = "TextureVariationFileDataID";
    /// <summary>Texture fields of an already-read CreatureDisplayInfo row. Null when a field is absent, not a plain
    /// integer column, has a text issue or is not a canonical signed32 value; nothing is guessed or defaulted.</summary>
    internal static CreatureDisplayTextureEvidence? TextureEvidence(Db2Row row, Wdc5TableHeader header)
    {
        int Column(string name)
        {
            var index = -1;
            for (var i = 0; i < header.Columns.Count; i++)
                if (header.Columns[i].Name == name) { if (index != -1) return -1; index = i; }
            return index >= 0 && header.Columns[index] is { Kind: "integer", IsId: false, Interpretation: null } && index < row.Values.Count &&
                row.TextIssues?.Any(issue => issue.ColumnIndex == index) != true ? index : -1;
        }
        string? Value(int column) => column >= 0 && int.TryParse(row.Values[column], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) &&
            N(value) == row.Values[column] ? row.Values[column] : null;
        var extended = Column(ExtendedField); var variations = Enumerable.Range(0, 4).Select(i => Column(VariationField + "[" + i + "]")).ToArray();
        var extendedValue = Value(extended); var values = variations.Select(Value).ToArray();
        if (extendedValue is null || values.Any(v => v is null)) return null;
        return new(extended, extendedValue, Array.AsReadOnly(variations), Array.AsReadOnly(values.Select(v => v!).ToArray()));
    }
    internal static string File(CreatureDisplayTargetRole role) => role == CreatureDisplayTargetRole.Display ? "1108759" : "1365368";
    internal static string Field(CreatureDisplayTargetRole role) => role == CreatureDisplayTargetRole.Display ? "ModelID" : "FileDataID";
    internal static CreatureDisplayAbsence Absence(TableDataCoverage coverage) => coverage.Scope switch
    {
        "complete" => CreatureDisplayAbsence.AbsentFromCompleteSource,
        "unencryptedSections" => CreatureDisplayAbsence.UnknownOutsideReadableScope,
        _ => throw Invalid()
    };
    internal static CreatureDisplaySlot? Select(CreaturesDisplayAuthorization core, CreatureDisplayMetadataRequest request)
    {
        if (core.Snapshot.Context != request.Context || core.Snapshot.ArtifactId != request.ArtifactId ||
            core.Snapshot.CreatureSource.TableId != CreaturesPolicy.Table) throw CreaturesPolicy.Stale();
        if (core.Summary is null) return null;
        var fields = core.Snapshot.Fields; var slots = core.Summary.DisplaySlots;
        if (core.Summary.Id != request.CreatureId || slots.Count != 4 || fields.DisplayIds.Count != 4 || fields.Probabilities.Count != 4 ||
            fields.DisplayIds.Concat(fields.Probabilities).Any(f => f.TableId != CreaturesPolicy.Table || f.ColumnIndex < 0 || f.ColumnIndex >= core.Snapshot.CreatureSource.Columns.Count) ||
            fields.DisplayIds.Concat(fields.Probabilities).Select(f => f.ColumnIndex).Distinct().Count() != 8) throw Invalid();
        for (var i = 0; i < 4; i++)
        {
            var slot = slots[i];
            if (slot.Slot != i || slot.DisplayField != fields.DisplayIds[i] || slot.ProbabilityField != fields.Probabilities[i] ||
                !int.TryParse(slot.DisplayId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var display) || N(display) != slot.DisplayId ||
                !float.TryParse(slot.Probability, NumberStyles.Float, CultureInfo.InvariantCulture, out var probability) || !float.IsFinite(probability) || probability.ToString("R", CultureInfo.InvariantCulture) != slot.Probability ||
                slot.State != (display > 0 ? CreatureDisplayState.Declared : display == 0 ? CreatureDisplayState.NoDeclaredDisplay : CreatureDisplayState.InvalidDisplayReference)) throw Invalid();
        }
        return slots[request.Slot] with { };
    }
    internal static void SameCore(CreaturesDisplayAuthorization original, CreaturesDisplayAuthorization current, int slot)
    {
        if (original.Opening.Reference != current.Opening.Reference || original.Artifact.ContentKey != current.Artifact.ContentKey ||
            original.Snapshot.ArtifactId != current.Snapshot.ArtifactId || original.Snapshot.CreatureSource.ArtifactId != current.Snapshot.CreatureSource.ArtifactId ||
            original.Summary is null != (current.Summary is null) || original.Summary is not null &&
            (original.Summary.Id != current.Summary!.Id || original.Summary.DisplaySlots[slot] != current.Summary.DisplaySlots[slot])) throw CreaturesPolicy.Stale();
    }
    internal static int SelectField(DbdDocument definition, string version, TableRowsArtifact rows, CreatureDisplayTargetRole role)
    {
        if (!string.Equals(rows.Header.TableHash, Table(role), StringComparison.OrdinalIgnoreCase) || !IsLayout(role, rows.Header.LayoutHash)) throw Unsupported();
        var selected = definition.SelectFile(version, rows.Header.LayoutHash);
        if (selected.Blocks.Count != 1) throw Unsupported();
        var offset = 0; var result = -1;
        foreach (var field in selected.Blocks[0].Fields)
        {
            if (field.Column.Name == Field(role))
            {
                if (result != -1 || !field.Column.VerifiedName || field.Column.Family != "int" || field.ArrayLength != 1 ||
                    field.IntegerBits != (role == CreatureDisplayTargetRole.Display ? 16 : 32) || field.Unsigned != true ||
                    field.IsId || field.NonInline || field.RelationStorage || offset >= rows.Header.Columns.Count ||
                    rows.Header.Columns[offset] is not { Kind: "integer", Interpretation: null } column || column.Name != Field(role) || column.IsId) throw Unsupported();
                result = offset;
            }
            offset = checked(offset + field.ArrayLength);
        }
        if (result < 0 || offset != rows.Header.Columns.Count) throw Unsupported();
        return result;
    }
    internal static string Edge(Db2Row row, int column, CreatureDisplayTargetRole role, string id)
    {
        if (N(row.Id) != id || row.IsCopy != row.CopySourceId.HasValue || column < 0 || column >= row.Values.Count ||
            row.TextIssues?.Any(i => i.ColumnIndex == column) == true ||
            !uint.TryParse(row.Values[column], NumberStyles.None, CultureInfo.InvariantCulture, out var edge) || N(edge) != row.Values[column] ||
            role == CreatureDisplayTargetRole.Display && edge > ushort.MaxValue) throw Invalid();
        return row.Values[column];
    }
    internal static CreatureDisplayMetadataState FailureState(Exception error)
    {
        var code = (error as DiscoveryException)?.Code ?? "";
        if (code.Contains("LIMIT", StringComparison.Ordinal) || code.Contains("BUDGET", StringComparison.Ordinal)) return CreatureDisplayMetadataState.ResourceLimit;
        if (code is "BLTE_ENCRYPTED" or "BLTE_KEY_UNAVAILABLE" or "BLTE_RANGE_UNAVAILABLE" || code.Contains("ENCRYPT", StringComparison.Ordinal) || code.Contains("PROTECTED", StringComparison.Ordinal)) return CreatureDisplayMetadataState.Protected;
        if (code.Contains("UNSUPPORTED", StringComparison.Ordinal) || code is "DB2_SCHEMA_MISSING" or "DB2_SCHEMA_AMBIGUOUS" or "SCHEMA_BUILD_NOT_LISTED" or "DEFINITION_NOT_BUNDLED") return CreatureDisplayMetadataState.Unsupported;
        if (error is IOException or UnauthorizedAccessException || code.Contains("UNAVAILABLE", StringComparison.Ordinal) || code.Contains("MISSING", StringComparison.Ordinal) ||
            code.Contains("HTTP", StringComparison.Ordinal) || code.Contains("BUSY", StringComparison.Ordinal) || code.Contains("NETWORK", StringComparison.Ordinal)) return CreatureDisplayMetadataState.Unavailable;
        return CreatureDisplayMetadataState.Corrupt;
    }
    internal static CreatureDisplayMetadataProblem Problem(Exception error, CreatureDisplayTargetRole role)
    {
        var code = error is DiscoveryException discovery && discovery.Code.Length is >= 1 and <= 128 && !discovery.Code.Any(char.IsControl)
            ? discovery.Code : "CREATURE_DISPLAY_UNAVAILABLE";
        // Never project transport URLs, exception text or original game bytes into an optional failure.
        return new(code, "The optional display metadata could not be resolved from its exact source.", (error as DiscoveryException)?.Retryable == true || error is IOException, role);
    }
    internal static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static DiscoveryException Invalid() => new("CREATURE_DISPLAY_INTEGRITY", "The selected Creature slot or exact display/model source evidence is inconsistent.");
    internal static DiscoveryException Unsupported() => new("CREATURE_DISPLAY_SCHEMA_UNSUPPORTED", "This exact target layout does not qualify the required unsigned scalar metadata edge.");
    internal static DiscoveryException Stale() => new("CREATURE_DISPLAY_STALE", "A delivered display/model source changed. Retry the selected Creature slot.", true);
    internal static DiscoveryException Busy() => new("CREATURES_BUSY", "The two target source readers are opening, borrowed or retiring for another exact source context.", true);
}
