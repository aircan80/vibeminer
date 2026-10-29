using System.Collections.Concurrent;
using VibeMiner.Application;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Which table links an Item to its effects in an exact build (#86, D104). The ItemEffect definition decides: the one
/// Tables chooses for the admitted ItemEffect rows (D101, bundled or fetched), or before that file is admitted the bundled one.
/// A build the definition lists uses the blocks listing it; an unlisted build its layout candidates on the version line, as
/// Tables opens it by layout (D080). When every such block stores ParentItemID as a relation the build links through
/// ItemEffect.ParentItemID; when none does, through ItemXItemEffect, as Retail does. When they disagree (the 1.x line holds
/// Classic layouts with ParentItemID and Classic Beta 1.60's 4CA77678 without), the ItemEffect file's own layout decides;
/// a layout no candidate knows links through ItemEffect, so that ItemEffect layout reads as unsupported rather than
/// ItemXItemEffect as missing. Null means undecided until the ItemEffect file is admitted.</summary>
internal static class ItemEffectsLinkChoice
{
    // Tests only: builds treated as undecided until their ItemEffect file is admitted, so the order "ItemEffect first, then
    // the link its layout chose" is exercised on builds the authored Items fixtures can open. Empty in production.
    internal static readonly ConcurrentDictionary<string, bool> UndecidedForTests = new(StringComparer.Ordinal);

    internal static ItemEffectsLink? For(BundledTableDefinitions provider, string version, string? itemEffectLayout, CancellationToken token) =>
        provider.Definitions.TryGetValue(ItemEffectsLinks.ClassicItemEffect.TableId, out var effect)
            ? For(effect.Document, version, itemEffectLayout, token) : ItemEffectsLinks.Bridge;
    internal static ItemEffectsLink? For(DbdDocument document, string version, string? itemEffectLayout, CancellationToken token)
    {
        if (UndecidedForTests.ContainsKey(version) && itemEffectLayout is null) return null;
        var listed = document.SelectBuild(version, token: token).Blocks;
        if (listed.Count > 0) return Unanimous(listed) ?? ItemEffectsLinks.Bridge;
        var candidates = document.LayoutCandidates(version, token);
        // No block covers the build at all: Retail's bridge, whose admission then says why it can't read.
        if (candidates.Count == 0) return ItemEffectsLinks.Bridge;
        var general = Unanimous(candidates.Select(candidate => candidate.Block).ToArray());
        if (general is not null) return general;
        if (itemEffectLayout is null) return null;
        var matching = candidates.Where(candidate => string.Equals(candidate.LayoutHash, itemEffectLayout, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Block).ToArray();
        return Unanimous(matching) ?? ItemEffectsLinks.Parent;
    }
    private static ItemEffectsLink? Unanimous(IReadOnlyList<DbdBlock> blocks)
    {
        if (blocks.Count == 0) return null;
        var parent = blocks.Count(block => block.Fields.Any(field => field.Column.Name == "ParentItemID" && field.RelationStorage));
        return parent == blocks.Count ? ItemEffectsLinks.Parent : parent == 0 ? ItemEffectsLinks.Bridge : null;
    }

    /// <summary>The reviewed DBD shape of one profile column: ID first, Retail's raw widths and the Classic ParentItemID relation.</summary>
    internal static bool FieldMatches(ItemEffectsTableProfile profile, int index, DbdField field)
    {
        var name = profile.Columns[index];
        if (profile.TableId == ItemEffectsLinks.SpellName.TableId)
            return field.Column.VerifiedName && field.Column.Name == name && field.Column.Family == (index == 1 ? "locstring" : "int") &&
                field.ArrayLength == 1 && field.IsId == (index == 0) && field.NonInline == (index == 0) && !field.RelationStorage &&
                (index == 1 || field.IntegerBits == 32 && field.Unsigned == false);
        var effect = profile.TableId == ItemEffectsLinks.ClassicItemEffect.TableId;
        var relation = effect ? name == "ParentItemID" : index == 2;
        var bits = effect ? index is 1 or 2 ? 8 : index is 3 or 6 or 8 ? 16 : 32 : 32;
        var unsigned = effect && index is 1 or 2 or 6 or 8;
        return field.Column.VerifiedName != (effect && index == 9) && field.Column.Name == name && field.Column.Family == "int" &&
            field.ArrayLength == 1 && field.IsId == (index == 0) && field.NonInline == (index == 0 || relation) && field.RelationStorage == relation &&
            field.IntegerBits == bits && field.Unsigned == unsigned;
    }
}
