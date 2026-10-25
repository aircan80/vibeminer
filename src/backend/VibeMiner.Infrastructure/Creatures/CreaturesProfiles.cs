using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class CreaturesStore
{
    // D101: the Creature table is read with the definition Tables chooses, under the same Settings switch. Its fields are
    // still picked by name and checked for width, so any definition that describes them the same way qualifies.
    private async Task<CreaturesBinding> BindingAsync(CreaturesContext context, CapturedOpeningContext opening,
        StoredTableAdmission admission, IReadOnlyList<ConfigurationReceipt> configurations, CancellationToken token)
    {
        var definition = await definitions.CurrentForFeatureAsync(admission, CreaturesPolicy.Table, opening.Build.Version, token) ?? throw CreaturesPolicy.Stale();
        var (_, bundled) = await TableDefinitionSources.FeatureTableAsync(CreaturesPolicy.Table, token);
        var source = TableDataStore.Snapshot(Table(context), admission, definition, opening.Build.Version, bundled);
        var fields = CreaturesPolicy.SelectFields(definition.Document, opening.Build.Version, source.LayoutHash, source.Columns);
        var binding = new CreaturesBinding(context, opening.Build.Version, opening.Reference, configurations, source, admission.Artifact, fields);
        await ValidateProfileAsync(binding, token); return binding;
    }
    private async Task ValidateProfileAsync(CreaturesBinding binding, CancellationToken token)
    {
        CreaturesPolicy.ValidateBinding(binding);
        var (_, bundled) = await TableDefinitionSources.FeatureTableAsync(CreaturesPolicy.Table, token);
        var definition = await definitions.ForFeatureRowsAsync(CreaturesPolicy.Table, binding.BuildVersion, binding.Rows, token);
        if (binding.Source.DefinitionSha256 != definition.Sha256 || binding.Source.ProviderRevision != definition.Revision ||
            binding.Source.DecoderRevision != TableDataStore.AdmissionRevision(CreaturesPolicy.Table, definition, bundled, binding.BuildVersion,
                binding.Rows.Header.LayoutHash, binding.Source.Source.Projection is not null, token) ||
            binding.Rows.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, binding.BuildVersion, token)) throw CreaturesPolicy.Stale();
        var current = CreaturesPolicy.SelectFields(definition.Document, binding.BuildVersion, binding.Source.LayoutHash, binding.Source.Columns);
        if (!current.SequenceEqual(binding.Fields)) throw CreaturesPolicy.Unsupported();
    }
}
