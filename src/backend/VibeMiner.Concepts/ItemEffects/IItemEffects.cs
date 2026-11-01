using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.ItemEffects;

/// <summary>
/// Layer 6: an item's effects per build and locale (D458). An item links to ItemEffect rows through the ItemXItemEffect bridge
/// or through ItemEffect.ParentItemID (D104, D459), and each effect names its spell through SpellName. Implementations:
/// <see cref="ItemEffectsService"/> over layer 5, and the in-memory fake in <c>VibeMiner.Concepts.Testing</c>; one contract
/// suite runs against both. Table, column and row problems never throw: they come back as named states. Calls throw only for
/// bad arguments (<see cref="ArgumentException"/>), cancellation, a disposed handle (<see cref="ObjectDisposedException"/>) and
/// what layer 5 throws (<see cref="TablesBusyException"/>).
/// </summary>
public interface IItemEffects
{
    /// <summary>Opens the build's tables for one locale (a <see cref="FileLocales"/> name). Opening reads no table.</summary>
    Task<ItemEffectsOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="IItemEffects.ForAsync"/>. <see cref="Effects"/> is set only when <see cref="State"/> is Open; a failure keeps layer 4's state, code and time.</summary>
public sealed record ItemEffectsOpenResult(BuildRef Build, string Locale, FilesOpenState State, IBuildItemEffects? Effects, string? Code,
    string? Message, DateTimeOffset? ObservedAt);

/// <summary>One build and locale's item effects. Calls may run concurrently. Dispose the handle when done: every later call throws <see cref="ObjectDisposedException"/>.</summary>
public interface IBuildItemEffects : IAsyncDisposable
{
    BuildRef Build { get; }
    string Locale { get; }

    /// <summary>One page of the item's effects, in the link table's row order, each with its ItemEffect inputs and its spell name. See <see cref="ItemEffectsPage"/>.</summary>
    Task<ItemEffectsPage> GetAsync(uint itemId, ItemEffectsQuery query, TablePriority priority, CancellationToken cancellationToken);
}

/// <summary>
/// A page request. <see cref="Limit"/> is 1 to <see cref="ItemEffectQueries.MaxLimit"/> (16). <see cref="After"/> is the
/// <see cref="ItemEffectsPage.Next"/> of the previous page of the same item and limit; see <see cref="ItemEffectQueries.Validate"/>.
/// </summary>
public sealed record ItemEffectsQuery(int Limit = ItemEffectQueries.MaxLimit, string? After = null);

/// <summary>How the build links an item to its effects (D104, D459).</summary>
public enum ItemEffectsLink
{
    /// <summary>ItemXItemEffect rows with ItemID equal to the item; ItemEffectID names the effect. Retail.</summary>
    ItemXItemEffect,
    /// <summary>ItemEffect rows with ParentItemID equal to the item; the row is its own effect. Classic.</summary>
    ParentItemId,
}

public enum ItemEffectsPageState
{
    /// <summary>The page's entries. <see cref="ItemEffectsPage.Next"/> continues, null after the item's last link.</summary>
    Ok,
    /// <summary>
    /// RELATION_INDEX_LIMIT: indexing the link table's item column would go past the handle's relation index budget (D471). No
    /// entries, and never "no effects"; kept for the handle's life.
    /// </summary>
    IndexLimit,
    /// <summary>The Item table isn't readable (not Ready or Partial); <see cref="ItemEffectsPage.Item"/> says why.</summary>
    ItemNotReadable,
    /// <summary>No Item row has the ID, and Item is complete (Ready).</summary>
    MissingItem,
    /// <summary>The ID is outside the readable sections of a Partial Item table. Never proof of absence.</summary>
    ItemUnknown,
    /// <summary>The ID is in a keyed section of Item whose key isn't known; <see cref="ItemEffectsPage.ProtectedKey"/> names it.</summary>
    ItemProtected,
    /// <summary>The item exists but its ID is above int32: the link tables store the item as a signed 32-bit key, so no row can name it.</summary>
    ItemKeyOutOfRange,
    /// <summary>The link source isn't Ready; <see cref="ItemEffectsPage.Bridge"/> says why. The effect and name sources are NotChecked.</summary>
    BridgeNotReady,
    /// <summary>The link table's identity, or the build's link, changed since the cursor was made; the page is never re-based.</summary>
    Stale,
    /// <summary>INVALID_CURSOR: the cursor belongs to another item, limit, build or locale, or points past the table.</summary>
    InvalidCursor,
}

/// <summary>What a request reads from a table: the link rows, the ItemEffect inputs, or the SpellName names.</summary>
public enum ItemEffectsRole { Bridge, Effect, Name }

/// <summary>
/// A role's source state (D460). NotChecked means the request never needed the table (no eligible key upstream, or the step
/// before it failed). The rest come from the table's layer 5 state, which <see cref="ItemEffectsSource.Table"/> keeps exactly,
/// or from the reviewed profile and the bounds.
/// </summary>
public enum ItemEffectsSourceState
{
    NotChecked,
    /// <summary>Ready or Partial in layer 5, and the reviewed profile matches. A Partial table's misses are Unknown, never absent.</summary>
    Ready,
    /// <summary>ITEM_EFFECTS_SOURCE_NOT_IN_BUILD (D102): the build doesn't ship the table (NotInBuild). Not retryable.</summary>
    NotInBuild,
    /// <summary>ITEM_EFFECTS_SOURCE_NOT_IN_LOCALE: the build ships the table in other locales, not this one (NotInLocale). Not retryable.</summary>
    NotInLocale,
    /// <summary>ITEM_EFFECTS_SOURCE_UNAVAILABLE, retryable: bytes not local (NeedsNetwork, Missing, Unavailable, NotInEncoding), or a failed WoWDBDefs fetch (D118).</summary>
    Unavailable,
    /// <summary>ITEM_EFFECTS_SOURCE_PROTECTED: every section keyed with an unknown key (Encrypted), or WrongKey.</summary>
    Protected,
    /// <summary>ITEM_EFFECTS_SOURCE_UNSUPPORTED: no usable definition (D118), a layer 5 Unsupported, or a profile that isn't the reviewed one.</summary>
    Unsupported,
    /// <summary>ITEM_EFFECTS_SOURCE_CORRUPT: layer 5 Corrupt, Rejected or StoreFailed, or a row that doesn't line up with its profile.</summary>
    Corrupt,
    /// <summary>ITEM_EFFECTS_LIMIT: layer 5 TooLarge.</summary>
    ResourceLimit,
}

/// <summary>
/// A fixed problem per source state (the old codes and messages). <see cref="Definition"/> is set only on an Unavailable or
/// Unsupported source whose table no definition reads (D118): layer 5's <see cref="DefinitionState"/> for it; FetchingOff means
/// the Settings switch is off. Layer 5's own code stays in the table entry's reason.
/// </summary>
public sealed record ItemEffectsProblem(string Code, string Message, bool Retryable, DefinitionState? Definition = null);

/// <summary>One role's source. <see cref="Table"/> is layer 5's checked entry (null for NotChecked); <see cref="Problem"/> is set for every state but NotChecked and Ready.</summary>
public sealed record ItemEffectsSource(ItemEffectsRole Role, ItemEffectsSourceState State, TableEntry? Table = null, ItemEffectsProblem? Problem = null);

/// <summary>Why a key matched no row: the table is complete (Ready), or the row may be outside a Partial table's readable sections.</summary>
public enum ItemEffectsAbsence { AbsentFromCompleteSource, UnknownOutsideReadableScope }

public enum ItemEffectState
{
    /// <summary>One ItemEffect row has the ID and its nine inputs pass their width rules; <see cref="ItemEffect.Fields"/> are set.</summary>
    ObservedReady,
    /// <summary>The link's ItemEffectID is negative: no ItemEffect row can have it, so none is looked up.</summary>
    EffectKeyOutOfRange,
    /// <summary>ITEM_EFFECTS_FIELD_UNAVAILABLE: the row was found but an input fails its width or sign rule, or has a text issue.</summary>
    FieldUnavailable,
    /// <summary>No row has the ID; <see cref="ItemEffect.Absence"/> says whether that is proven.</summary>
    NotFoundInAdmittedScope,
    /// <summary>The ID is in a keyed section of ItemEffect whose key isn't known; <see cref="ItemEffect.ProtectedKey"/> names it.</summary>
    Protected,
    /// <summary>The effect source isn't Ready; <see cref="ItemEffectsPage.Effect"/> says why.</summary>
    SourceNotReady,
}

/// <summary>How an input was read (D108): a number at its declared width, the 32-bit physical value of a column that didn't narrow, or a name the definition marks unverified.</summary>
public enum ItemEffectEvidence { StrictNumeric, RawPhysicalInteger, UnverifiedCandidate }

/// <summary>
/// One ItemEffect input (columns 1 to 9). <see cref="Interpretation"/> is the header column's raw evidence, set only for
/// RawPhysicalInteger. <see cref="CandidateReference"/> is a hint never looked up: SpellCategory (6), ChrSpecialization (8), PlayerCondition (9).
/// </summary>
public sealed record ItemEffectField(string Name, int ColumnIndex, string Value, ItemEffectEvidence Evidence, Db2ValueInterpretation? Interpretation,
    ForeignKey? CandidateReference);

/// <summary>
/// The effect a link names. <see cref="Row"/> is set when the row was found (ObservedReady, FieldUnavailable).
/// <see cref="SpellId"/> is column 7 when it is a canonical int32 with no text issue, whatever the other inputs say: the name
/// lookup needs only it.
/// </summary>
public sealed record ItemEffect(ItemEffectState State, TableRow? Row = null, IReadOnlyList<ItemEffectField>? Fields = null, string? SpellId = null,
    ItemEffectsAbsence? Absence = null, string? ProtectedKey = null);

public enum ItemEffectNameState
{
    /// <summary>The effect has no SpellID to look up (not found, out of range, source not ready, or column 7 unusable).</summary>
    EffectNotReady,
    /// <summary>The SpellID is negative: no SpellName row can have it, so none is looked up.</summary>
    SpellKeyOutOfRange,
    NotFoundInAdmittedScope,
    /// <summary>The SpellID is in a keyed section of SpellName whose key isn't known; <see cref="ItemEffectName.ProtectedKey"/> names it.</summary>
    Protected,
    /// <summary>The name source isn't Ready; <see cref="ItemEffectsPage.Name"/> says why.</summary>
    SourceNotReady,
    /// <summary>Name_lang has a DB2_TEXT_ENCODING_INVALID issue; <see cref="ItemEffectName.IssueCode"/> keeps it and there is no text.</summary>
    InvalidText,
    /// <summary>Name_lang is longer than <see cref="ItemEffectQueries.MaxNameUnits"/> UTF-16 units; <see cref="ItemEffectName.TextUnits"/> says how long, and there is no text.</summary>
    TextTooLarge,
    /// <summary>Name_lang has length 0.</summary>
    EmptyText,
    /// <summary>Name_lang as stored, whitespace included.</summary>
    Ready,
}

public sealed record ItemEffectName(ItemEffectNameState State, TableRow? Row = null, string? Text = null, int? TextUnits = null, string? IssueCode = null,
    ItemEffectsAbsence? Absence = null, string? ProtectedKey = null);

/// <summary>
/// One link: the link table row (ItemXItemEffect, or the ItemEffect row itself for ParentItemID), the item key and effect key it
/// holds as stored, and what the effect and name lookups found. Zero is an ordinary key everywhere.
/// </summary>
public sealed record ItemEffectEntry(TableRow Link, string ParentItemId, string ItemEffectId, ItemEffect Effect, ItemEffectName Name);

/// <summary>
/// A page's outcome. Only Ok carries entries. <see cref="Link"/> is the build's link, set once the item was found in
/// range. <see cref="Matches"/> is how many link rows the item has in the index (all of them when the link table is Ready). The three sources always come back; a role the request didn't reach
/// is NotChecked.
/// </summary>
public sealed record ItemEffectsPage(ItemEffectsPageState State, uint ItemId, ItemEffectsLink? Link, IReadOnlyList<ItemEffectEntry> Entries, string? Next,
    int Matches, TableEntry? Item, ItemEffectsSource Bridge, ItemEffectsSource Effect, ItemEffectsSource Name, string? ProtectedKey = null,
    string? Code = null, string? Message = null);
