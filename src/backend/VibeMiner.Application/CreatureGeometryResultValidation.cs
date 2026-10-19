using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

public static partial class CreatureGeometryValidation
{
    public static CreatureGeometryResult Snapshot(CreatureGeometryResult value, CreatureDisplayMetadataRequest selection,
        string? expectedGeometryArtifactId = null)
    {
        Require(value is not null && value.Context == selection.Context && value.ArtifactId == selection.ArtifactId &&
            value.CreatureId == selection.CreatureId && value.Slot == selection.Slot && value.Policy == Policy && Enum.IsDefined(value.State));
        Require(value.Opening is not null && value.Opening.Region == selection.Context.SourceRegion &&
            Unsigned(value.Opening.Generation, long.MaxValue) && Hash(value.Opening.VersionsSha256) && Hash(value.Opening.CdnsSha256) &&
            value.Opening.VersionsRowOrdinal >= 0 && value.Opening.CdnsRowOrdinal >= 0);
        var metadata = Metadata(value.Metadata, selection); var sources = Entries(value.Sources, 2);
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i]; Require(source is not null && source.Role == (i == 0 ? CreatureGeometrySourceRole.Model : CreatureGeometrySourceRole.Skin));
            Require(Unsigned(source.FileDataId) && U(source.FileDataId) > 0 && Hash(source.ContentKey, 32) && Hash(source.EncodingKey, 32) && Hash(source.EncodedSha256) &&
                source.EncodedBytes is >= 1 and <= 4194304 && source.DecodedBytes is >= 1 and <= 8388608 && source.FullVerifierRevision == "content-blte-v1" &&
                Hash(source.AssociationSha256) && source.VariantPolicy == "all-root-variants-consistent-asset-v1" &&
                source.EncodingLookupRevision == "encoding-native-file-v1" && source.RootLookupRevision == "root-native-file-v1" &&
                source.EncodingSelectionPolicy == "lexicographic-complete-encoding-v1");
            if (i == 0) Require(source.FileDataId == metadata.ModelFileDataId);
            sources[i] = source with { };
        }
        if (value.Problem is { } problem)
            Require(Text(problem.Code, 64) && problem.Code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') && Text(problem.Message, 512) &&
                (problem.Dependency is null || Enum.IsDefined(problem.Dependency.Value)) && (problem.Target is null ||
                    problem.Dependency == CreatureGeometryDependency.Metadata && Enum.IsDefined(problem.Target.Value)));
        if (metadata.State is CreatureDisplayMetadataState.Protected or CreatureDisplayMetadataState.Unsupported or
            CreatureDisplayMetadataState.Unavailable or CreatureDisplayMetadataState.Corrupt or CreatureDisplayMetadataState.ResourceLimit)
            Require(value.Problem is { Dependency: CreatureGeometryDependency.Metadata } && value.Problem.Target ==
                (metadata.ModelId is null ? CreatureDisplayTargetRole.Display : CreatureDisplayTargetRole.Model));
        CreatureGeometryManifest? manifest = null;
        if (value.State == CreatureGeometryState.Ready)
        {
            Require(value.Manifest is not null && value.Problem is null && sources.Length == 2 && metadata.State == CreatureDisplayMetadataState.MetadataResolved);
            manifest = Snapshot(value.Manifest);
            Require(manifest.ModelFileDataId.ToString(CultureInfo.InvariantCulture) == sources[0].FileDataId &&
                manifest.SkinFileDataId.ToString(CultureInfo.InvariantCulture) == sources[1].FileDataId);
            if (expectedGeometryArtifactId is not null && manifest.ArtifactId != expectedGeometryArtifactId)
                throw new DiscoveryException("CREATURE_GEOMETRY_STALE", "The exact geometry artifact changed. Retry the selected slot.", true);
        }
        else
        {
            Require(value.Manifest is null);
            if (value.State == CreatureGeometryState.NotPrepared) Require(value.Problem is null && sources.Length == 0);
            else if (value.State == CreatureGeometryState.MetadataNotResolved)
                Require(metadata.State != CreatureDisplayMetadataState.MetadataResolved && sources.Length == 0 && (value.Problem is null || value.Problem.Dependency == CreatureGeometryDependency.Metadata));
            else if (value.State == CreatureGeometryState.NoDeclaredTriangles)
                Require(value.Problem is null && sources.Length == 2 && metadata.State == CreatureDisplayMetadataState.MetadataResolved);
            else Require(value.Problem is not null && metadata.State == CreatureDisplayMetadataState.MetadataResolved);
        }
        var owned = value with
        {
            Context = value.Context with { },
            Opening = value.Opening with { },
            Metadata = metadata,
            Sources = Array.AsReadOnly(sources),
            Manifest = manifest,
            Problem = value.Problem is null ? null : value.Problem with { }
        };
        Bound(owned); return owned;
    }

    private static CreatureGeometryMetadata Metadata(CreatureGeometryMetadata value, CreatureDisplayMetadataRequest selection)
    {
        Require(value is not null && Enum.IsDefined(value.State) && value.Appearance == CreatureDisplayAppearance.NotEvaluated &&
            value.CreatureSource is not null && value.CreatureSource.TableId == "c9d6b6b3" && Hash(value.CreatureSource.ArtifactId) &&
            value.CreatureSource.RowId == selection.CreatureId && value.CreatureSource.ColumnIndex is null);
        Coverage(value.CreatureCoverage); var targets = Entries(value.Targets, 2);
        var selected = value.Selected;
        if (selected is not null)
        {
            Require(selected.Slot == selection.Slot && selected.DisplayId is { Length: >= 1 and <= 11 } &&
                selected.Probability is { Length: >= 1 and <= 32 } && int.TryParse(selected.DisplayId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var display) &&
                display.ToString(CultureInfo.InvariantCulture) == selected.DisplayId && float.TryParse(selected.Probability, NumberStyles.Float, CultureInfo.InvariantCulture, out var probability) &&
                float.IsFinite(probability) && probability.ToString("R", CultureInfo.InvariantCulture) == selected.Probability);
            var id = int.Parse(selected.DisplayId, CultureInfo.InvariantCulture);
            Require(selected.State == (id > 0 ? CreatureDisplayState.Declared : id == 0 ? CreatureDisplayState.NoDeclaredDisplay : CreatureDisplayState.InvalidDisplayReference));
            Require(selected.DisplayField is { TableId: "c9d6b6b3", ColumnIndex: >= 0 and < 512 } &&
                selected.ProbabilityField is { TableId: "c9d6b6b3", ColumnIndex: >= 0 and < 512 } && selected.DisplayField != selected.ProbabilityField);
            selected = selected with { DisplayField = selected.DisplayField with { }, ProbabilityField = selected.ProbabilityField with { } };
        }
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i]; var table = i == 0 ? "bfdaf9f1" : "7f695fa5";
            Require(target is not null && target.Role == (i == 0 ? CreatureDisplayTargetRole.Display : CreatureDisplayTargetRole.Model) &&
                target.TableId == table && Hash(target.ArtifactId) && (target.MatchCount is null || Unsigned(target.MatchCount)));
            Coverage(target.Coverage);
            Require(target.MatchCount is null || U(target.MatchCount) <= Emitted(target.Coverage));
            if (target.Row is { } row)
            {
                Require(target.MatchCount == "1" && row.Source is not null && row.EdgeValue is not null && row.Source.TableId == table &&
                    row.Source.ArtifactId == target.ArtifactId && Unsigned(row.Source.RowId) && row.Source.ColumnIndex is >= 0 and < 512 &&
                    row.EdgeValue.TableId == table && row.EdgeValue.ColumnIndex == row.Source.ColumnIndex &&
                    Unsigned(row.EdgeValue.Value, i == 0 ? ushort.MaxValue : uint.MaxValue) && row.IsCopy == (row.CopySourceId is not null) &&
                    (row.CopySourceId is null || Unsigned(row.CopySourceId) && row.CopySourceId != row.Source.RowId) &&
                    U(row.IsCopy ? target.Coverage.CopyRows : target.Coverage.PhysicalRows) > 0);
                Require(row.Source.RowId == (i == 0 ? selected?.DisplayId : value.ModelId));
            }
            else Require(target.MatchCount != "1");
            targets[i] = target with
            {
                Coverage = target.Coverage with { },
                Row = target.Row is null ? null : target.Row with
                { Source = target.Row.Source with { }, EdgeValue = target.Row.EdgeValue with { } }
            };
        }
        Require(value.ModelId is null || Unsigned(value.ModelId, ushort.MaxValue) && U(value.ModelId) > 0);
        Require(value.ModelFileDataId is null || Unsigned(value.ModelFileDataId) && U(value.ModelFileDataId) > 0);
        bool Unique(int i) => targets.Length > i && targets[i].MatchCount == "1" && targets[i].Row is not null;
        bool Edge(int i, string id) => Unique(i) && targets[i].Row!.EdgeValue.Value == id;
        if (selected is not null) Require(Emitted(value.CreatureCoverage) > 0);
        if (targets.Length > 0) Require(selected?.State == CreatureDisplayState.Declared);
        if (targets.Length > 1 || value.ModelId is not null)
            Require(value.ModelId is not null && Edge(0, value.ModelId));
        if (value.ModelFileDataId is not null)
            Require(value.State == CreatureDisplayMetadataState.MetadataResolved && Edge(1, value.ModelFileDataId));
        var missingCreature = value.State == CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope;
        Require(missingCreature ? selected is null : selected is not null);
        var missingDisplay = value.State == CreatureDisplayMetadataState.DisplayMissingInAdmittedScope;
        var missingModel = value.State == CreatureDisplayMetadataState.ModelMissingInAdmittedScope;
        if (missingCreature || missingDisplay || missingModel)
        {
            var coverage = missingCreature ? value.CreatureCoverage : targets.Length > (missingDisplay ? 0 : 1)
                ? targets[missingDisplay ? 0 : 1].Coverage : null;
            Require(coverage is not null && value.Absence == (coverage.Scope == "complete"
                ? CreatureDisplayAbsence.AbsentFromCompleteSource : CreatureDisplayAbsence.UnknownOutsideReadableScope));
        }
        else Require(value.Absence is null);
        Require(value.State == CreatureDisplayMetadataState.NotPrepared ? value.RequiredTableId is "bfdaf9f1" or "7f695fa5" : value.RequiredTableId is null);
        switch (value.State)
        {
            case CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope:
                Require(targets.Length == 0 && value.ModelId is null); break;
            case CreatureDisplayMetadataState.NoDeclaredDisplay:
            case CreatureDisplayMetadataState.InvalidDisplayReference:
                Require(targets.Length == 0 && value.ModelId is null && selected!.State ==
                    (value.State == CreatureDisplayMetadataState.NoDeclaredDisplay ? CreatureDisplayState.NoDeclaredDisplay : CreatureDisplayState.InvalidDisplayReference)); break;
            case CreatureDisplayMetadataState.NotPrepared:
                Require(selected!.State == CreatureDisplayState.Declared && (value.RequiredTableId == "bfdaf9f1"
                    ? targets.Length == 0 && value.ModelId is null : targets.Length == 1 && value.ModelId is not null)); break;
            case CreatureDisplayMetadataState.DisplayMissingInAdmittedScope:
            case CreatureDisplayMetadataState.DisplayAmbiguous:
                Require(targets.Length == 1 && value.ModelId is null && targets[0].MatchCount is not null &&
                    (missingDisplay ? targets[0].MatchCount == "0" : U(targets[0].MatchCount!) > 1)); break;
            case CreatureDisplayMetadataState.NoDeclaredModel:
                Require(targets.Length == 1 && value.ModelId is null && Edge(0, "0")); break;
            case CreatureDisplayMetadataState.ModelMissingInAdmittedScope:
            case CreatureDisplayMetadataState.ModelAmbiguous:
                Require(targets.Length == 2 && value.ModelId is not null && targets[1].MatchCount is not null &&
                    (missingModel ? targets[1].MatchCount == "0" : U(targets[1].MatchCount!) > 1)); break;
            case CreatureDisplayMetadataState.NoDeclaredModelFile:
                Require(targets.Length == 2 && value.ModelId is not null && Edge(1, "0")); break;
            case CreatureDisplayMetadataState.MetadataResolved:
                Require(targets.Length == 2 && value.ModelId is not null && value.ModelFileDataId is not null); break;
            default:
                // Failed probes can stop before delivery or carry one failed, non-queryable target.
                Require(selected!.State == CreatureDisplayState.Declared &&
                    (targets.Length == 0 ? value.ModelId is null : targets.Length == 1 && value.ModelId is not null || targets[^1].MatchCount is null && targets[^1].Row is null) &&
                    (targets.Length < 2 || value.ModelId is not null)); break;
        }
        return value with
        {
            CreatureSource = value.CreatureSource with { },
            CreatureCoverage = value.CreatureCoverage with { },
            Selected = selected,
            Targets = Array.AsReadOnly(targets)
        };
    }
    private static ulong Emitted(TableDataCoverage value) => U(value.PhysicalRows) + U(value.CopyRows);
    private static void Coverage(TableDataCoverage value)
    {
        Require(value is not null && value.Scope is "complete" or "unencryptedSections");
        foreach (var count in new[] { value.DeclaredRecords, value.PhysicalRows, value.CopyRows,
            value.ExcludedDeclaredRecords, value.DeclaredCopyRecords, value.ExcludedPhysicalRecords, value.ExcludedCopyRecords }) Require(Unsigned(count, 1_000_000));
        Require(Unsigned(value.ExcludedKeyedSections, 128));
        var excludedSections = U(value.ExcludedKeyedSections); var excludedCopies = U(value.ExcludedCopyRecords);
        Require(U(value.PhysicalRows) + U(value.ExcludedPhysicalRecords) == U(value.DeclaredRecords) &&
            U(value.CopyRows) + excludedCopies == U(value.DeclaredCopyRecords) &&
            U(value.ExcludedDeclaredRecords) == U(value.ExcludedPhysicalRecords) + excludedCopies &&
            U(value.DeclaredRecords) + U(value.DeclaredCopyRecords) <= 1_000_000 &&
            (U(value.ExcludedPhysicalRecords) == 0 || excludedSections > 0) &&
            (value.Scope == "complete" ? excludedSections == 0 && excludedCopies == 0 : excludedSections > 0 || excludedCopies > 0));
    }
}
