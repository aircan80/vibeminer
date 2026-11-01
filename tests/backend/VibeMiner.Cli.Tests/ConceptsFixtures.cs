using VibeMiner.Concepts.Items;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Cli.Tests;

/// <summary>Item appearances and sourced icons for the CLI's layer 6 tests.</summary>
internal static class ConceptsFixtures
{
    public const uint DefaultIcon = 777, Model = 5550, Texture = 5551;

    private static TableRow Row(uint id) => new(0, new Db2Row(id, false, null, [$"{id}"]));

    /// <summary>
    /// One default ItemModifiedAppearance row (ID 900) -> ItemAppearance 31 (icon <see cref="DefaultIcon"/>, display 41) ->
    /// slot 0 model resource 51 with file <see cref="Model"/> (component race 1), material 61 Unknown; slot 1 empty. The model reads
    /// with one texture <see cref="Texture"/>; a second model file past the cap is NotRead.
    /// </summary>
    public static ItemAppearanceSet Appearances()
    {
        var component = new ItemComponentKey(ItemRefState.Found, 1, 0, 0, 2);
        var model = new ItemResourceRef(ItemResourceKind.Model, 0, 51, ItemResourceState.Found, true, [new(Model, Row(Model), component)], 1, null);
        var material = new ItemResourceRef(ItemResourceKind.Material, 0, 61, ItemResourceState.Unknown, false, [], 0, null, ["ABCDEF0123456789"]);
        var empty = new ItemResourceRef(ItemResourceKind.Model, 1, 0, ItemResourceState.NoResource, true, [], 0, null);
        var emptyMaterial = new ItemResourceRef(ItemResourceKind.Material, 1, 0, ItemResourceState.NoResource, true, [], 0, null);
        var display = new ItemDisplayRef(ItemRefState.Found, 41, Row(41), [new(0, model, material), new(1, empty, emptyMaterial)], null);
        var appearance = new ItemAppearanceRef(ItemRefState.Found, 31, Row(31), DefaultIcon, 41, display, null);
        var models = new ItemModelRefs(ItemModelsState.Capped, [
            new(Model, ItemModelRefState.Read, ModelReadState.Read, [new(0, 1, Texture, ModelReferenceState.Found)], 1),
            new(Model + 10, ItemModelRefState.NotRead)], "ITEM_MODEL_CAP");
        return new(ItemAppearancesState.Complete, [new(Row(900), 0, 0, 31, appearance)], ItemDefaultAppearanceState.Default, 0, 1, null, models);
    }

    /// <summary>The item's own icon, then the default appearance's (FileFailed: not stored).</summary>
    public static IReadOnlyList<ItemIconSourced> Icons(uint itemId, ItemIcon own) =>
    [
        new("Item", "IconFileDataID", itemId, own),
        new("ItemAppearance", "DefaultIconFileDataID", 31, new ItemIcon(ItemIconState.FileFailed, DefaultIcon, BlpImageState.FileFailed, FileState: VibeMiner.Files.FileReadState.NeedsNetwork)),
    ];
}
