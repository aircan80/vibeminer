using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

// Created only after the catalog's combined raw-authority preflight, before any
// feature graph or generic eager source read. Receipts retain original authority.
internal sealed record ItemEffectsItemsAuthority(StoredItemsAdmission Items,
    StoredTableAdmission Item, StoredTableAdmission Sparse,
    IReadOnlyList<ConfigurationReceipt> Configurations);

// Compact owned evidence, never an Items session/read lease or a timeless capability.
// Every delivery must still pass the separately owned coherent catalog fence.
internal sealed record ItemEffectsItemsProof(CapturedOpeningContext Opening,
    ItemsProjectionArtifact Artifact, ItemsSnapshot Snapshot, AuthorizedItemSummary Item,
    IReadOnlyList<ConfigurationReceipt> Configurations);
