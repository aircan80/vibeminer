namespace VibeMiner.Formats.Generated;

/// <summary>
/// A decoded table to store. <see cref="KeysUsed"/> are the TACT key names (16 upper hex) the decode depended on, known or not: the
/// keys of the keyed DB2 sections plus the key names of the BLTE chunks that were holes in the file (the two can differ). Every
/// encrypted section's key must be among them, and <see cref="Db2Completion.ExcludedEncryptedSections"/> must count the encrypted
/// sections. <see cref="Rows"/> is read once, in order (the reader's order: physical rows, then copies). <see cref="Owner"/> is
/// what the rows read from (an open DB2 file, say): <see cref="IGeneratedTables.StoreAsync"/> disposes it once it is done with the
/// content, however the store ends, also when it never reads the rows.
/// </summary>
public sealed record GeneratedTableContent(Db2TableHeader Header, IReadOnlyList<Db2Section> Sections, Db2Completion Completion,
    IReadOnlyCollection<string> KeysUsed, IAsyncEnumerable<Db2Row> Rows, IAsyncDisposable? Owner = null);
