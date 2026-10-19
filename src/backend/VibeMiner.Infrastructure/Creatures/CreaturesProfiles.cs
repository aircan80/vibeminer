using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class CreaturesStore
{
    private static async Task<CreaturesBinding> BindingAsync(CreaturesContext context, CapturedOpeningContext opening,
        StoredTableAdmission admission, IReadOnlyList<ConfigurationReceipt> configurations, CancellationToken token)
    {
        var r = admission.Artifact; var c = r.Completion;
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        if (!provider.Definitions.TryGetValue(CreaturesPolicy.Table, out var definition)) throw CreaturesPolicy.Unsupported();
        var source = new TableDataSnapshot(context.ProductId, context.BuildIdentity, context.SourceRegion, CreaturesPolicy.Table, context.ResearchLocale,
            r.ContentKey, admission.PreparedAt, r.Header.LayoutHash.ToUpperInvariant(), r.Binding.DefinitionSha256, r.Binding.ProviderRevision, r.Binding.DecoderRevision,
            admission.Source, admission.Association, r.Header.Columns.Select(v => TableDataStore.ProjectColumn(v, definition.Document)).ToArray(),
            new(c.Scope, CreaturesPolicy.N(c.DeclaredRecords), CreaturesPolicy.N(c.PhysicalRows), CreaturesPolicy.N(c.CopyRows), CreaturesPolicy.N(c.ExcludedEncryptedSections),
                CreaturesPolicy.N(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords), CreaturesPolicy.N(r.Header.DeclaredCopyRecords), CreaturesPolicy.N(c.ExcludedPhysicalRecords), CreaturesPolicy.N(c.ExcludedCopyRecords)),
            CreaturesPolicy.N(c.PhysicalRows + c.CopyRows), CreaturesPolicy.N(c.TextIssueCount));
        var fields = CreaturesPolicy.SelectFields(definition.Document, opening.Build.Version, source.LayoutHash, source.Columns);
        var binding = new CreaturesBinding(context, opening.Build.Version, opening.Reference, configurations, source, r, fields);
        await ValidateProfileAsync(binding, token); return binding;
    }
    private static async Task ValidateProfileAsync(CreaturesBinding binding, CancellationToken token)
    {
        CreaturesPolicy.ValidateBinding(binding); var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        if (!provider.Definitions.TryGetValue(CreaturesPolicy.Table, out var definition) || binding.Source.DefinitionSha256 != definition.Sha256 ||
            binding.Source.ProviderRevision != BundledTableDefinitions.Revision || binding.Source.DecoderRevision !=
                (binding.Source.Source.Projection is null ? TableDataStore.DecoderRevision : TableDataStore.ProjectionDecoderRevision) ||
            binding.Rows.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, binding.BuildVersion, token)) throw CreaturesPolicy.Stale();
        var current = CreaturesPolicy.SelectFields(definition.Document, binding.BuildVersion, binding.Source.LayoutHash, binding.Source.Columns);
        if (!current.SequenceEqual(binding.Fields)) throw CreaturesPolicy.Unsupported();
    }
}
