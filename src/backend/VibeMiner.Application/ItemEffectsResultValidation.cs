using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

public static partial class ItemEffectsValidation
{
    private const string Provider = "b9a63096cdd99b26967f17f9515377ae62cadfd7";
    private static readonly string[] EffectFields = ["ID", "LegacySlotIndex", "TriggerType", "Charges", "CoolDownMSec", "CategoryCoolDownMSec", "SpellCategoryID", "SpellID", "ChrSpecializationID", "PlayerConditionID"];

    public static void Validate(ItemEffectsReadiness value, ItemEffectsSelection selection)
    {
        Require(value is not null && value.Selection == selection && Enum.IsDefined(value.State));
        if (value.Source is not null) Source(value.Source, ItemEffectsSourceRole.Bridge, selection.Context);
        if (value.State == ItemEffectsReadinessState.MissingItem)
        { Require(!value.Admitted && value.Snapshot is null && value.Source is null && value.ObservedAssociations is null && value.Problem is null); return; }
        Require(value.Admitted);
        if (value.State == ItemEffectsReadinessState.ItemKeyOutOfRange)
        { Require(U(selection.ItemId) > int.MaxValue && value.Snapshot is null && value.Source is null && value.ObservedAssociations is null && value.Problem is null); return; }
        Require(U(selection.ItemId) <= int.MaxValue);
        if (value.State == ItemEffectsReadinessState.Ready)
        {
            Require(value.Snapshot is not null && value.Source?.State == ItemEffectsSourceState.Ready && value.Problem is null);
            Index(value.Snapshot, selection.Context);
            Require(value.Source.Snapshot == value.Snapshot.BridgeSource && Count(value.ObservedAssociations, U(value.Snapshot.EmittedRows)));
        }
        else
        {
            Require(value.Snapshot is null && value.ObservedAssociations is null);
            if (value.State == ItemEffectsReadinessState.NotPrepared)
                Require(value.Problem is null && (value.Source is null || value.Source.State is ItemEffectsSourceState.NotChecked or ItemEffectsSourceState.NotPrepared or ItemEffectsSourceState.Ready));
            else
            {
                var state = SourceState(value.State.ToString());
                Require(value.Source?.State == state && value.Problem == value.Source.Problem);
                Problem(value.Problem, state.ToString());
            }
        }
    }
    public static void Validate(ItemEffectsPage value, ItemEffectsPageRequest request)
    {
        Require(Unsigned(request.Selection.ItemId, int.MaxValue));
        Require(value is not null && value.Selection == request.Selection && value.BridgeIndexArtifactId == request.BridgeIndexArtifactId && value.OrderPolicy == OrderPolicy);
        Snapshot(value.BridgeSource, ItemEffectsSourceRole.Bridge, request.Selection.Context);
        var total = U(value.BridgeSource.RowCount);
        Require(total <= 65536 && Count(value.ObservedAssociations, total));
        var edges = Entries(value.Edges, request.Limit);
        Require((edges.Length == 0) == (value.ObservedAssociations == "0") && (ulong)edges.Length <= U(value.ObservedAssociations));
        ulong? previous = null;
        foreach (var edge in edges)
        {
            Require(edge is not null && edge.Key is not null && Count(edge.Key.IndexOrdinal, 65535));
            var ordinal = U(edge.Key.IndexOrdinal);
            Require(ordinal < total && (previous is null || ordinal == previous + 1)); previous = ordinal;
            Reference(edge.Bridge, value.BridgeSource, null);
            Require(Count(edge.Bridge.SourceOrdinal, total == 0 ? 0 : total - 1));
            Numeric(edge.ParentItemId, BridgeTable, 2); Numeric(edge.ItemEffectId, BridgeTable, 1);
            Require(edge.ParentItemId.Value == request.Selection.ItemId);
        }
        if (request.After is not null)
        {
            Require(ReadCursor(request.After, out var requestedBinding, out var after) && requestedBinding == CursorBinding(request));
            Require(edges.Length > 0 && U(edges[0].Key.IndexOrdinal) == after);
        }
        else Require((value.NextCursor is not null) == (U(value.ObservedAssociations) > (ulong)edges.Length));
        if (value.NextCursor is not null)
        {
            Require(edges.Length != 0);
            Require(ReadCursor(value.NextCursor, out var binding, out var next) && binding == CursorBinding(request));
            Require(next == previous + 1 && next < total);
        }
    }
    public static void Validate(ItemEffectDetailsResult value, ItemEffectDetailsRequest request)
    {
        Require(Unsigned(request.Selection.ItemId, int.MaxValue));
        Require(value is not null && value.Selection == request.Selection && value.BridgeIndexArtifactId == request.BridgeIndexArtifactId && value.PolicyRevision == InputPolicy);
        Source(value.Source, ItemEffectsSourceRole.Effect, request.Selection.Context);
        var outcomes = Entries(value.Outcomes, 16); Require(outcomes.Length == request.Edges.Count);
        var eligible = false;
        for (var i = 0; i < outcomes.Length; i++)
        {
            var outcome = outcomes[i];
            Require(outcome is not null && outcome.Key == request.Edges[i]);
            Effect(outcome.State, outcome.ItemEffectId, outcome.ObservedCandidates, outcome.Target, outcome.Absence, outcome.Problem, value.Source);
            eligible |= outcome.State != ItemEffectState.EffectKeyOutOfRange;
            if (outcome.State == ItemEffectState.ObservedReady) Fields(outcome.Fields, value.Source.Snapshot!);
            else Require(outcome.Fields is null);
        }
        if (!eligible) Require(value.Source.State == ItemEffectsSourceState.NotChecked);
    }
    public static void Validate(ItemEffectNamesResult value, ItemEffectNamesRequest request)
    {
        Require(Unsigned(request.Selection.ItemId, int.MaxValue));
        Require(value is not null && value.Selection == request.Selection && value.BridgeIndexArtifactId == request.BridgeIndexArtifactId &&
            value.ItemEffectArtifactId == request.ItemEffectArtifactId && value.PolicyRevision == NamePolicy);
        var sources = Entries(value.Sources, 2); Require(sources.Length == 2);
        Source(sources[0], ItemEffectsSourceRole.Effect, request.Selection.Context); Source(sources[1], ItemEffectsSourceRole.Name, request.Selection.Context);
        if (sources[0].State == ItemEffectsSourceState.Ready) Require(sources[0].Snapshot!.ArtifactId == request.ItemEffectArtifactId);
        var outcomes = Entries(value.Outcomes, 8); Require(outcomes.Length == request.Edges.Count);
        var effectEligible = false; var nameEligible = false;
        for (var i = 0; i < outcomes.Length; i++)
        {
            var outcome = outcomes[i]; Require(outcome is not null && outcome.Key == request.Edges[i] && outcome.Effect is not null && Enum.IsDefined(outcome.State));
            var effect = outcome.Effect;
            Effect(effect.State, effect.ItemEffectId, effect.ObservedCandidates, effect.Target, effect.Absence, effect.Problem, sources[0]);
            effectEligible |= effect.State != ItemEffectState.EffectKeyOutOfRange;
            if (effect.State != ItemEffectState.ObservedReady)
            { Require(effect.SpellId is null && outcome.State == ItemEffectNameState.EffectNotReady); NoNameEvidence(outcome); continue; }
            Numeric(effect.SpellId, EffectTable, 7);
            if (effect.SpellId.Value[0] == '-')
            { Require(outcome.State == ItemEffectNameState.SpellKeyOutOfRange); NoNameEvidence(outcome); continue; }
            nameEligible = true;
            Name(outcome, sources[1], effect.SpellId.Value);
        }
        if (!effectEligible) Require(sources[0].State == ItemEffectsSourceState.NotChecked);
        // A final Effect fence can invalidate facts after Name was already consulted.
        // Preserve that independently fenced source, while the rules above clear its dependent evidence.
        if (!nameEligible && sources[0].State is not (ItemEffectsSourceState.Stale or ItemEffectsSourceState.Corrupt or ItemEffectsSourceState.ResourceLimit or ItemEffectsSourceState.Unavailable))
            Require(sources[1].State == ItemEffectsSourceState.NotChecked);
    }
    private static void Effect(ItemEffectState state, string id, string? candidates, ItemEffectsSourceReference? target,
        ItemEffectsAbsence? absence, ItemEffectsProblem? problem, ItemEffectsSource source)
    {
        Require(Enum.IsDefined(state) && Signed(id));
        if (state == ItemEffectState.EffectKeyOutOfRange)
        { Require(id[0] == '-' && candidates is null && target is null && absence is null && problem is null); return; }
        Require(id[0] != '-');
        if (state is ItemEffectState.ObservedReady or ItemEffectState.Ambiguous or ItemEffectState.NotFoundInAdmittedScope)
        {
            Require(source.State == ItemEffectsSourceState.Ready && Count(candidates, U(source.Snapshot!.RowCount)) && problem is null);
            if (state == ItemEffectState.ObservedReady)
            { Require(candidates == "1" && absence is null); Reference(target, source.Snapshot, id); Require(target.SourceOrdinal is null); }
            else
            {
                Require(target is null);
                if (state == ItemEffectState.Ambiguous) Require(U(candidates!) > 1 && absence is null);
                else Require(candidates == "0" && absence == Absence(source.Snapshot));
            }
        }
        else
        {
            Require(candidates is null && target is null && absence is null);
            if (state == ItemEffectState.NotPrepared) Require(source.State == ItemEffectsSourceState.NotPrepared && problem is null);
            else if (state == ItemEffectState.FieldUnavailable) { Require(source.State == ItemEffectsSourceState.Ready); Problem(problem, "FieldUnavailable"); }
            else { Require(source.State == SourceState(state.ToString()) && problem == source.Problem); Problem(problem, state.ToString()); }
        }
    }
    private static void Name(ItemEffectNameOutcome value, ItemEffectsSource source, string spell)
    {
        if (value.State is ItemEffectNameState.Ready or ItemEffectNameState.EmptyText or ItemEffectNameState.InvalidText or ItemEffectNameState.TextTooLarge)
        {
            Require(source.State == ItemEffectsSourceState.Ready && value.ObservedCandidates == "1" && value.Absence is null && value.Problem is null);
            Reference(value.Target, source.Snapshot!, spell); Require(value.Target.SourceOrdinal is null);
            switch (value.State)
            {
                case ItemEffectNameState.Ready:
                    Require(value.Text is { Length: >= 1 and <= 4096 } && WellFormed(value.Text) && value.Issue is null && value.TextUnits is null); break;
                case ItemEffectNameState.EmptyText: Require(value.Text == "" && value.Issue is null && value.TextUnits is null); break;
                case ItemEffectNameState.InvalidText:
                    Require(U(source.Snapshot!.TextIssueCount) > 0 && value.Text is null && value.TextUnits is null && value.Issue is not null && value.Issue.Code == "DB2_TEXT_ENCODING_INVALID");
                    Reference(value.Issue.Source, source.Snapshot!, spell, 1);
                    Require(value.Issue.Source with { ColumnIndex = null } == value.Target); break;
                case ItemEffectNameState.TextTooLarge:
                    Require(value.Text is null && value.Issue is null && Count(value.TextUnits, 65536) && U(value.TextUnits!) > 4096); break;
            }
        }
        else if (value.State is ItemEffectNameState.Ambiguous or ItemEffectNameState.NotFoundInAdmittedScope)
        {
            Require(source.State == ItemEffectsSourceState.Ready && Count(value.ObservedCandidates, U(source.Snapshot!.RowCount)) && value.Target is null &&
                value.Text is null && value.Issue is null && value.TextUnits is null && value.Problem is null);
            if (value.State == ItemEffectNameState.Ambiguous) Require(U(value.ObservedCandidates!) > 1 && value.Absence is null);
            else Require(value.ObservedCandidates == "0" && value.Absence == Absence(source.Snapshot));
        }
        else
        {
            Require(value.State is not (ItemEffectNameState.EffectNotReady or ItemEffectNameState.SpellKeyOutOfRange));
            Require(value.ObservedCandidates is null && value.Target is null && value.Text is null && value.Issue is null && value.TextUnits is null && value.Absence is null);
            if (value.State == ItemEffectNameState.NotPrepared) Require(source.State == ItemEffectsSourceState.NotPrepared && value.Problem is null);
            else { Require(source.State == SourceState(value.State.ToString()) && value.Problem == source.Problem); Problem(value.Problem, value.State.ToString()); }
        }
    }
    private static void NoNameEvidence(ItemEffectNameOutcome value) => Require(value.ObservedCandidates is null && value.Target is null && value.Text is null &&
        value.Issue is null && value.TextUnits is null && value.Absence is null && value.Problem is null);

    private static void Source(ItemEffectsSource value, ItemEffectsSourceRole role, ItemsContext context)
    {
        Require(value is not null && value.Role == role && Enum.IsDefined(value.State));
        if (value.State == ItemEffectsSourceState.Ready)
        { Require(value.Snapshot is not null && value.HistoricalArtifactId is null && value.Problem is null); Snapshot(value.Snapshot, role, context); }
        else
        {
            Require(value.Snapshot is null);
            if (value.State == ItemEffectsSourceState.NotPrepared) Require((value.HistoricalArtifactId is null || Hash(value.HistoricalArtifactId)) && value.Problem is null);
            else if (value.State == ItemEffectsSourceState.NotChecked) Require(value.HistoricalArtifactId is null && value.Problem is null);
            else { Require(value.HistoricalArtifactId is null); Problem(value.Problem, value.State.ToString()); }
        }
    }
    private static void Snapshot(TableDataSnapshot value, ItemEffectsSourceRole role, ItemsContext context)
    {
        var (table, fdid, layout, definition, count) = role switch
        {
            ItemEffectsSourceRole.Bridge => (BridgeTable, "3177687", "96F083AD", "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb", 3),
            ItemEffectsSourceRole.Effect => (EffectTable, "969941", "4CA77678", "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c", 10),
            _ => (NameTable, "1990283", "782EE721", "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667", 2)
        };
        Require(value is not null && value.ProductId == context.ProductId && value.BuildIdentity == context.BuildIdentity && value.SourceRegion == context.SourceRegion &&
            value.ResearchLocale == context.ResearchLocale && value.TableId == table && value.LayoutHash == layout && value.DefinitionSha256 == definition && value.ProviderRevision == Provider &&
            Hash(value.ArtifactId) && Time(value.PreparedAt) && Count(value.RowCount, 1_000_000) && Count(value.TextIssueCount, 10_000_000));
        Require(value.Source is not null && value.Source.Location is not null && Hash(value.Source.Sha256) && Hex(value.Source.ContentKey, 32) && Hex(value.Source.EncodingKey, 32) &&
            Unsigned(value.Source.EncodedBytes, long.MaxValue) && Unsigned(value.Source.DecodedBytes, long.MaxValue) && Time(value.Source.RetrievedAt) && Time(value.Source.VerifiedAt) &&
            BoundedText(value.Source.SourceUrl, 4096) && BoundedText(value.Source.VerifierRevision, 128));
        var decoder = "wdc5-values-v7+table-admission" + (value.Source.Projection is null ? "" : "-projection") + (role == ItemEffectsSourceRole.Effect ? "-v4" : "-v3");
        Require(value.DecoderRevision == decoder);
        Require(value.Association is not null && value.Association.FileDataId == fdid && value.Association.ResearchLocale == "enUS" &&
            value.Association.EncodingSource is not null && value.Association.RootSource is not null);
        _ = Entries(value.Association.Memberships, 256);
        var columns = Entries(value.Columns, count); Require(columns.Length == count);
        for (var i = 0; i < count; i++)
        {
            var column = columns[i];
            var name = role switch { ItemEffectsSourceRole.Bridge => i switch { 0 => "ID", 1 => "ItemEffectID", _ => "ItemID" }, ItemEffectsSourceRole.Effect => EffectFields[i], _ => i == 0 ? "ID" : "Name_lang" };
            Require(column is not null && column.Name == name && column.IsId == (i == 0) && column.Family == (role == ItemEffectsSourceRole.Name && i == 1 ? "string" : "integer"));
            if (role == ItemEffectsSourceRole.Effect && i is 3 or 6 or 8) { Raw(column.Interpretation, i); Require(column.Reference is null); }
            else Require(column.Interpretation is null);
            var reference = role switch
            {
                ItemEffectsSourceRole.Bridge => i switch { 1 => new TableReference("ItemEffect", "ID"), 2 => new("Item", "ID"), _ => null },
                ItemEffectsSourceRole.Effect => i switch { 7 => new TableReference("Spell", "ID"), 9 => new("PlayerCondition", "ID"), _ => null }, _ => null
            };
            Require(column.Reference == reference);
        }
        var coverage = value.Coverage; Require(coverage is not null && coverage.Scope is "complete" or "unencryptedSections");
        var coverageCounts = new[] { coverage.DeclaredRecords, coverage.PhysicalRows, coverage.CopyRows, coverage.ExcludedKeyedSections, coverage.ExcludedDeclaredRecords,
            coverage.DeclaredCopyRecords, coverage.ExcludedPhysicalRecords, coverage.ExcludedCopyRecords };
        Require(coverageCounts.All(c => Count(c, 2_000_000)));
        Require(U(coverage.PhysicalRows) + U(coverage.CopyRows) == U(value.RowCount) && U(coverage.PhysicalRows) + U(coverage.ExcludedPhysicalRecords) == U(coverage.DeclaredRecords) &&
            U(coverage.CopyRows) + U(coverage.ExcludedCopyRecords) == U(coverage.DeclaredCopyRecords) && U(coverage.ExcludedDeclaredRecords) == U(coverage.ExcludedPhysicalRecords) + U(coverage.ExcludedCopyRecords));
        if (coverage.Scope == "complete") Require(coverage.ExcludedKeyedSections == "0" && coverage.ExcludedDeclaredRecords == "0");
        Require(U(value.TextIssueCount) <= U(value.RowCount) * (ulong)count);
    }
    private static void Index(ItemEffectsIndexSnapshot value, ItemsContext context)
    {
        Require(value is not null && value.Context == context && Hash(value.ArtifactId) && Time(value.PreparedAt) && value.Revision == IndexRevision && value.OrderPolicy == OrderPolicy);
        Snapshot(value.BridgeSource, ItemEffectsSourceRole.Bridge, context);
        Require(Count(value.EmittedRows, 65536) && value.EmittedRows == value.BridgeSource.RowCount && value.Fields is not null &&
            value.Fields.RowId == new ItemEffectsFieldReference(BridgeTable, 0) && value.Fields.ItemEffectId == new ItemEffectsFieldReference(BridgeTable, 1) && value.Fields.ItemId == new ItemEffectsFieldReference(BridgeTable, 2));
    }
    private static void Fields(IReadOnlyList<ItemEffectFieldEvidence>? values, TableDataSnapshot source)
    {
        var fields = Entries(values, 9); Require(fields.Length == 9);
        for (var j = 0; j < fields.Length; j++)
        {
            var i = j + 1; var field = fields[j];
            Require(field is not null && field.Name == EffectFields[i] && field.ColumnIndex == i && Enum.IsDefined(field.Evidence));
            if (i is 3 or 6 or 8)
            {
                Require(field.Evidence == ItemEffectEvidenceKind.RawPhysicalInteger && Unsigned(field.Value, uint.MaxValue)); Raw(field.Interpretation, i);
                Require(field.Interpretation == source.Columns[i].Interpretation && field.CandidateReference == (i switch { 6 => new TableReference("SpellCategory", "ID"), 8 => new("ChrSpecialization", "ID"), _ => null }));
            }
            else if (i == 9) Require(field.Evidence == ItemEffectEvidenceKind.UnverifiedCandidate && Signed(field.Value) && field.Interpretation is null && field.CandidateReference == new TableReference("PlayerCondition", "ID"));
            else Require(field.Evidence == ItemEffectEvidenceKind.StrictNumeric && (i is 1 or 2 ? Unsigned(field.Value, 255) : Signed(field.Value)) && field.Interpretation is null && field.CandidateReference is null);
        }
    }
    private static void Raw(TableColumnInterpretation? value, int index) => Require(value is not null && value.Policy == "raw-physical-integer-v1" && value.PhysicalBits == 32 && !value.PhysicalSigned &&
        value.CandidateBits == 16 && value.CandidateUnsigned == (index != 3) && BoundedText(value.Warning, 512));
    private static void Numeric([NotNull] ItemEffectsSourceValue? value, string table, int column) => Require(value is not null && value.TableId == table && value.ColumnIndex == column && Signed(value.Value));
    private static void Reference([NotNull] ItemEffectsSourceReference? value, TableDataSnapshot source, string? row, int? column = null) =>
        Require(value is not null && value.TableId == source.TableId && value.ArtifactId == source.ArtifactId && Unsigned(value.RowId, uint.MaxValue) && (row is null || value.RowId == row) &&
            value.IsCopy == (value.CopySourceId is not null) && (value.CopySourceId is null || Unsigned(value.CopySourceId, uint.MaxValue)) && value.ColumnIndex == column);
    private static void Problem(ItemEffectsProblem? value, string state)
    {
        var (code, message) = state switch
        {
            "Unavailable" => ("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable."),
            "Protected" => ("ITEM_EFFECTS_SOURCE_PROTECTED", "The selected source is protected."),
            "Unsupported" => ("ITEM_EFFECTS_SOURCE_UNSUPPORTED", "The selected source profile is unsupported."),
            "Corrupt" => ("ITEM_EFFECTS_SOURCE_CORRUPT", "The selected source failed integrity validation."),
            "ResourceLimit" => ("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded."),
            "Stale" => ("ITEM_EFFECTS_TARGET_STALE", "The selected target source changed."),
            "NotInBuild" => ("ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", "This build does not include the selected source."),
            "FieldUnavailable" => ("ITEM_EFFECTS_FIELD_UNAVAILABLE", "A required effect input is unavailable."),
            _ => throw BadResult()
        };
        Require(value is not null && value.Code == code && value.Message == message && value.Retryable == (state is "Unavailable" or "Stale"));
    }
    private static ItemEffectsSourceState SourceState(string state) => Enum.TryParse<ItemEffectsSourceState>(state, out var result) && Enum.IsDefined(result) ? result : throw BadResult();
    private static ItemEffectsAbsence Absence(TableDataSnapshot source) => source.Coverage.Scope == "complete" ? ItemEffectsAbsence.AbsentFromCompleteSource : ItemEffectsAbsence.UnknownOutsideReadableScope;
    private static T[] Entries<T>(IReadOnlyList<T>? values, int maximum)
    {
        try
        {
            Require(values is not null); var count = values.Count; Require(count >= 0 && count <= maximum);
            var result = new T[count]; for (var i = 0; i < count; i++) { result[i] = values[i]; Require(result[i] is not null); } return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw BadResult(); }
    }
    private static bool Count(string? value, ulong maximum) => Unsigned(value, maximum);
    private static ulong U(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static bool Time(string? value) => value is { Length: >= 20 and <= 40 } && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Offset == TimeSpan.Zero;
    private static bool WellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw BadResult(); }
    private static DiscoveryException BadResult() => new("ITEM_EFFECTS_RESULT_INVALID", "The effects result did not satisfy its bounded source and state contract.");
}
