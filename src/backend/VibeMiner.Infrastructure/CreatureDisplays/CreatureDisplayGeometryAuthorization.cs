using System.Globalization;
using VibeMiner.Domain;
using VibeMiner.Application;

namespace VibeMiner.Infrastructure;

internal sealed record GeometryDisplayAuthorization(CreaturesDisplayAuthorization Core, CreatureDisplaySlot Selected,
    CreatureDisplayProbe Display, CreatureDisplayProbe Model, uint ModelFileDataId);
internal sealed record GeometryDisplayAuthorizationResult(CreaturesDisplayAuthorization Core, CreatureDisplaySlot? Selected,
    IReadOnlyList<CreatureDisplayProbe> Delivered, CreatureGeometryMetadata Metadata, GeometryDisplayAuthorization? Authorization,
    CreatureDisplayMetadataProblem? Problem = null);

internal sealed partial class CreatureDisplayCoordinator
{
    // Same immediate reads2/shutdown/notification lifetime as Read; no public Read recursion or source wait.
    internal Task<GeometryDisplayAuthorizationResult> AuthorizeGeometryAsync(CreatureDisplayMetadataRequest original, CancellationToken token) =>
        ExecuteLocalAsync(original, token, static (consumer, result) => GeometryAuthorization(consumer, result));

    private static GeometryDisplayAuthorizationResult GeometryAuthorization(Consumer consumer, CreatureDisplayMetadataResult result)
    {
        var core = consumer.Core ?? throw CreatureDisplayPolicy.Invalid();
        var selected = consumer.Selected;
        if (result.Selected != selected || result.CreatureSource.TableId != CreaturesPolicy.Table ||
            result.CreatureSource.ArtifactId != core.Snapshot.CreatureSource.ArtifactId || result.CreatureSource.RowId != consumer.Request.CreatureId ||
            result.Appearance != CreatureDisplayAppearance.NotEvaluated || result.Sources.Count != consumer.Delivered.Count || consumer.Delivered.Count > 2)
            throw CreatureDisplayPolicy.Invalid();
        var delivered = consumer.Delivered.ToArray();
        for (var i = 0; i < delivered.Length; i++)
        {
            var role = i == 0 ? CreatureDisplayTargetRole.Display : CreatureDisplayTargetRole.Model;
            var id = i == 0 ? selected?.DisplayId : delivered[0].Edge;
            ValidateGeometryProbe(core, delivered[i], role, id ?? throw CreatureDisplayPolicy.Invalid());
            if (result.Sources[i] != delivered[i].Target) throw CreatureDisplayPolicy.Invalid();
        }
        var targets = Array.AsReadOnly(delivered.Select(p => new CreatureGeometryTarget(p.Target.Role,
            p.Target.Snapshot.TableId, p.Target.Snapshot.ArtifactId, p.Target.Snapshot.Coverage, p.Target.MatchCount, p.Target.Row)).ToArray());
        var metadata = new CreatureGeometryMetadata(result.State, result.CreatureSource, result.CreatureCoverage, selected,
            targets, result.Absence, result.ModelId, result.ModelFileDataId, result.RequiredTableId, result.Appearance);
        GeometryDisplayAuthorization? authorization = null;
        if (result.State == CreatureDisplayMetadataState.MetadataResolved)
        {
            if (selected is null || selected.State != CreatureDisplayState.Declared || delivered.Length != 2 || result.Problem is not null ||
                delivered.Any(p => p.Failure is not null || p.Problem is not null || p.Target.MatchCount != "1" || p.Target.Row is null) ||
                !Unsigned(selected.DisplayId, int.MaxValue, out var displayId) || displayId == 0 ||
                !Unsigned(delivered[0].Edge, ushort.MaxValue, out var modelId) || modelId == 0 || result.ModelId != delivered[0].Edge ||
                !Unsigned(delivered[1].Edge, uint.MaxValue, out var fileId) || fileId == 0 || result.ModelFileDataId != delivered[1].Edge)
                throw CreatureDisplayPolicy.Invalid();
            authorization = new(core, selected, delivered[0], delivered[1], fileId);
        }
        return new(core, selected, Array.AsReadOnly(delivered), metadata, authorization, result.Problem);
    }
    private static void ValidateGeometryProbe(CreaturesDisplayAuthorization core, CreatureDisplayProbe probe,
        CreatureDisplayTargetRole role, string id)
    {
        if (probe is null || probe.Target is null || probe.Binding is null || probe.Binding.Admission is null ||
            probe.Binding.Admission.Artifact is null || probe.Binding.Admission.Artifact.Binding is null || probe.Binding.Admission.Artifact.Completion is null) throw CreatureDisplayPolicy.Invalid();
        var source = probe.Target.Snapshot; var admission = probe.Binding.Admission; var rows = admission.Artifact;
        var table = CreatureDisplayPolicy.Table(role); var context = core.Snapshot.Context;
        if (probe.Target.Role != role || probe.Binding.TableId != table || source is null || source.TableId != table ||
            source.ProductId != context.ProductId || source.BuildIdentity != context.BuildIdentity || source.SourceRegion != context.SourceRegion || source.ResearchLocale != context.ResearchLocale ||
            source.ArtifactId != rows.ContentKey || source.PreparedAt != admission.PreparedAt || source.Columns is null || rows.Header is null || rows.Header.Columns is null ||
            source.Columns.Count != rows.Header.Columns.Count || source.Columns.Count is < 1 or > 512 || source.Coverage is null ||
            source.LayoutHash != rows.Header.LayoutHash.ToUpperInvariant() || !string.Equals(rows.Header.TableHash, table, StringComparison.OrdinalIgnoreCase) ||
            source.DefinitionSha256 != rows.Binding.DefinitionSha256 || source.ProviderRevision != rows.Binding.ProviderRevision || source.DecoderRevision != rows.Binding.DecoderRevision ||
            source.RowCount != CreatureDisplayPolicy.N(checked(rows.Completion.PhysicalRows + rows.Completion.CopyRows)) ||
            source.TextIssueCount != CreatureDisplayPolicy.N(rows.Completion.TextIssueCount) || source.Coverage != GeometryCoverage(rows))
            throw CreatureDisplayPolicy.Invalid();
        if (probe.Failure is not null)
        {
            if (probe.Target.MatchCount is not null || probe.Target.Row is not null || probe.Edge is not null || probe.Problem is null || probe.Problem.Target != role)
                throw CreatureDisplayPolicy.Invalid();
            return;
        }
        if (probe.Problem is not null || !Unsigned(probe.Target.MatchCount, TableRowArtifacts.MaxRows, out var count) ||
            count > rows.Completion.PhysicalRows + rows.Completion.CopyRows) throw CreatureDisplayPolicy.Invalid();
        if (count != 1)
        { if (probe.Target.Row is not null || probe.Edge is not null) throw CreatureDisplayPolicy.Invalid(); return; }
        var row = probe.Target.Row; var column = row?.Source?.ColumnIndex;
        if (row is null || row.Source is null || row.EdgeValue is null || row.Source.TableId != table || row.Source.ArtifactId != source.ArtifactId || row.Source.RowId != id ||
            !Unsigned(id, uint.MaxValue, out _) || column is null || column < 0 || column >= source.Columns.Count ||
            row.EdgeValue.TableId != table || row.EdgeValue.ColumnIndex != column || row.EdgeValue.Value != probe.Edge ||
            source.Columns[column.Value] is not { Family: "integer", IsId: false, Interpretation: null } field || field.Name != CreatureDisplayPolicy.Field(role) ||
            rows.Header.Columns[column.Value] is not { Kind: "integer", IsId: false, Interpretation: null } raw || raw.Name != field.Name ||
            row.IsCopy != (row.CopySourceId is not null) || row.CopySourceId is { } copy && !Unsigned(copy, uint.MaxValue, out _) ||
            !Unsigned(probe.Edge, role == CreatureDisplayTargetRole.Display ? ushort.MaxValue : uint.MaxValue, out _)) throw CreatureDisplayPolicy.Invalid();
    }
    private static TableDataCoverage GeometryCoverage(TableRowsArtifact rows)
    {
        var c = rows.Completion;
        return new(c.Scope, CreatureDisplayPolicy.N(c.DeclaredRecords), CreatureDisplayPolicy.N(c.PhysicalRows), CreatureDisplayPolicy.N(c.CopyRows),
            CreatureDisplayPolicy.N(c.ExcludedEncryptedSections), CreatureDisplayPolicy.N(checked(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords)),
            CreatureDisplayPolicy.N(rows.Header.DeclaredCopyRecords), CreatureDisplayPolicy.N(c.ExcludedPhysicalRecords), CreatureDisplayPolicy.N(c.ExcludedCopyRecords));
    }
    private static bool Unsigned(string? text, uint maximum, out uint value) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum &&
        value.ToString(CultureInfo.InvariantCulture) == text;
}
