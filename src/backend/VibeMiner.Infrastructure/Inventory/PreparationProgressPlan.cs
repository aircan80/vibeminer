using VibeMiner.Application;

namespace VibeMiner.Infrastructure;

internal enum PreparationStage
{
    Configurations, Cleanup, EncodingAcquisition, EncodingContentIndex, EncodingObjectIndex,
    RootAcquisition, RootIndex, InstallAcquisition, InstallIndex,
    DownloadAcquisition, DownloadIndex, DownloadResolution, DownloadSave,
    SizeAcquisition, SizeIndex, SizeResolution, SizeSave,
    RootResolution, InstallResolution, CatalogDigest, SharedAssessment, Seal,
    CachedIndex, EncodingRevalidation, RootRevalidation, InstallRevalidation,
    DownloadRevalidation, SizeRevalidation
}

/// <summary>Actual phase order, independent of stage text and per-phase counters.</summary>
internal sealed class PreparationProgressPlan(Func<WorkspacePreparationProgress, Task> progress, CancellationToken token)
{
    private PreparationStage[]? stages;
    private PreparationStage current = PreparationStage.Configurations;
    private int index = 1;
    internal PreparationStep CurrentStep => new(index, stages?.Length, Description(current));
    internal IReadOnlyList<PreparationStage> Stages => Array.AsReadOnly(stages ?? [PreparationStage.Configurations]);

    internal void ConfigureFresh(bool download, bool size)
    {
        var result = new List<PreparationStage>
        {
            PreparationStage.Configurations, PreparationStage.Cleanup, PreparationStage.EncodingAcquisition,
            PreparationStage.EncodingContentIndex, PreparationStage.EncodingObjectIndex,
            PreparationStage.RootAcquisition, PreparationStage.RootIndex,
            PreparationStage.InstallAcquisition, PreparationStage.InstallIndex
        };
        if (download) result.AddRange([PreparationStage.DownloadAcquisition, PreparationStage.DownloadIndex,
            PreparationStage.DownloadResolution, PreparationStage.DownloadSave]);
        if (size) result.AddRange([PreparationStage.SizeAcquisition, PreparationStage.SizeIndex,
            PreparationStage.SizeResolution, PreparationStage.SizeSave]);
        result.AddRange([PreparationStage.RootResolution, PreparationStage.InstallResolution,
            PreparationStage.CatalogDigest, PreparationStage.SharedAssessment, PreparationStage.Seal]);
        Configure(result.ToArray());
    }

    internal void ConfigureCached(IEnumerable<string> roles)
    {
        var supplied = roles.ToArray();
        string[] supported = ["encoding", "root", "install", "download", "size"];
        if (supplied.Distinct(StringComparer.Ordinal).Count() != supplied.Length ||
            supplied.Any(role => !supported.Contains(role, StringComparer.Ordinal)) ||
            new[] { "encoding", "root", "install" }.Any(role => !supplied.Contains(role, StringComparer.Ordinal)))
            throw new ArgumentException("A cached plan requires unique supported metadata roles including encoding, root and install.", nameof(roles));
        var result = new List<PreparationStage> { PreparationStage.Configurations, PreparationStage.CachedIndex };
        result.AddRange(supported.Where(role => supplied.Contains(role, StringComparer.Ordinal)).Select(Revalidation));
        result.AddRange([PreparationStage.SharedAssessment, PreparationStage.Seal]);
        Configure(result.ToArray());
    }

    private void Configure(PreparationStage[] value)
    {
        if (stages is not null) throw new InvalidOperationException("The actual preparation plan is already known.");
        if (value.Length == 0 || value[0] != PreparationStage.Configurations || value.Distinct().Count() != value.Length)
            throw new ArgumentException("Preparation stages must be unique and begin with configurations.", nameof(value));
        foreach (var stage in value)
            if (Description(stage).Length is < 1 or > 400)
                throw new ArgumentException("Preparation explanations must contain 1 to 400 characters.", nameof(value));
        stages = value;
    }

    internal void Select(PreparationStage stage)
    {
        if (stages is null)
        {
            if (stage != PreparationStage.Configurations)
                throw new InvalidOperationException("Configurations must establish the actual plan first.");
            return;
        }
        var next = Array.IndexOf(stages, stage);
        if (next < 0 || next + 1 < index)
            throw new InvalidOperationException("A preparation phase is absent or would move backwards.");
        current = stage;
        index = next + 1;
    }

    internal Task BeginAsync(PreparationStage stage, string label)
    {
        Select(stage);
        return ReportAsync(label);
    }

    internal async Task ReportAsync(string stage)
    {
        token.ThrowIfCancellationRequested();
        var step = CurrentStep;
        if (step.Index < 1 || step.Total is { } total && step.Index > total || step.Description.Length is < 1 or > 400)
            throw new InvalidOperationException("Preparation progress is outside its actual plan.");
        await progress(new(stage, step));
        token.ThrowIfCancellationRequested();
    }

    internal static PreparationStage Revalidation(string role) => role switch
    {
        "encoding" => PreparationStage.EncodingRevalidation, "root" => PreparationStage.RootRevalidation,
        "install" => PreparationStage.InstallRevalidation, "download" => PreparationStage.DownloadRevalidation,
        "size" => PreparationStage.SizeRevalidation, _ => throw new ArgumentException("Unsupported metadata role.", nameof(role))
    };
    internal static PreparationStage Acquisition(string role) => role switch
    {
        "encoding" => PreparationStage.EncodingAcquisition, "root" => PreparationStage.RootAcquisition,
        "install" => PreparationStage.InstallAcquisition, "download" => PreparationStage.DownloadAcquisition,
        "size" => PreparationStage.SizeAcquisition, _ => throw new ArgumentException("Unsupported metadata role.", nameof(role))
    };
    internal static PreparationStage Index(string role) => role switch
    {
        "encoding" => PreparationStage.EncodingContentIndex, "root" => PreparationStage.RootIndex,
        "install" => PreparationStage.InstallIndex, "download" => PreparationStage.DownloadIndex,
        "size" => PreparationStage.SizeIndex, _ => throw new ArgumentException("Unsupported metadata role.", nameof(role))
    };
    private static string Description(PreparationStage stage) => stage switch
    {
        PreparationStage.Configurations => "Verify the build and CDN configurations, then check saved preparation work for this exact build.",
        PreparationStage.Cleanup => "Prepare catalog storage and clear unfinished work that cannot be resumed; verified downloads and completed builds are preserved.",
        PreparationStage.EncodingAcquisition => "Download or reuse the encoding metadata and verify its complete encoded and decoded content.",
        PreparationStage.EncodingContentIndex => "Build or restore the content index that connects published files to candidate encodings.",
        PreparationStage.EncodingObjectIndex => "Build or restore the index of encoded object identities and sizes for exact download accounting.",
        PreparationStage.RootAcquisition => "Download or reuse the build's ROOT metadata and verify its complete content.",
        PreparationStage.RootIndex => "Build or restore the ROOT index, preserving published FileDataIDs, content keys and locale variants.",
        PreparationStage.InstallAcquisition => "Download or reuse installation metadata containing published names and file requirements.",
        PreparationStage.InstallIndex => "Build or restore the installation index of names, content identities and sizes without downloading those files.",
        PreparationStage.DownloadAcquisition => "Download or reuse the published download manifest and verify its complete content.",
        PreparationStage.DownloadIndex => "Build or restore the download manifest index of exact encoded objects, sizes and original memberships.",
        PreparationStage.DownloadResolution => "Match published download objects against known encodings and check their declared sizes.",
        PreparationStage.DownloadSave => "Save the verified download mappings into the hidden catalog generation.",
        PreparationStage.SizeAcquisition => "Download or reuse the published size manifest and verify its complete content.",
        PreparationStage.SizeIndex => "Build or restore the size manifest index of encoded references, sizes and original memberships.",
        PreparationStage.SizeResolution => "Resolve size-manifest references to unique full encoded identities without guessing ambiguous prefixes.",
        PreparationStage.SizeSave => "Save the verified size mappings into the hidden catalog generation.",
        PreparationStage.RootResolution => "Resolve ROOT content to encoded objects and sizes, checking verified reusable candidates before selection.",
        PreparationStage.InstallResolution => "Resolve installation requirements to encoded objects and check their decoded sizes.",
        PreparationStage.CatalogDigest => "Verify a digest of the complete catalog before it can become queryable ready data.",
        PreparationStage.SharedAssessment => "Verify locally retained shared objects and calculate the encoded bytes still required by this build.",
        PreparationStage.Seal => "Publish the complete file catalog and measured download totals, then open the build overview.",
        PreparationStage.CachedIndex => "Recheck the saved catalog digest before trusting its file list and download accounting.",
        PreparationStage.EncodingRevalidation => "Verify the retained encoding metadata against its original source identities without downloading it again.",
        PreparationStage.RootRevalidation => "Verify the retained ROOT metadata against its original source identities without downloading it again.",
        PreparationStage.InstallRevalidation => "Verify the retained installation metadata against its original source identities without downloading it again.",
        PreparationStage.DownloadRevalidation => "Verify the retained download manifest against its original source identities without downloading it again.",
        PreparationStage.SizeRevalidation => "Verify the retained size manifest against its original source identities without downloading it again.",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}
