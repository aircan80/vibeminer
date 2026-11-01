namespace VibeMiner.Formats.Generated;

/// <summary>
/// A decoded table to store. <see cref="KeysUsed"/> are the TACT key names (16 upper hex) the decode depended on, known or not: the
/// keys of the keyed DB2 sections, decrypted or not, plus the key names of the BLTE chunks that were holes in the file (the two can
/// differ). Every keyed section's key must be among them, so a decrypted table is filed under its key's fingerprint and a changed
/// key rebuilds it, and <see cref="Db2Completion.ExcludedEncryptedSections"/> must count the hidden (keyed, not decrypted) sections. <see cref="Rows"/> is read once, in order (the reader's order: physical rows, then copies). <see cref="Owner"/> is
/// what the rows read from (an open DB2 file, say): <see cref="IGeneratedTables.StoreAsync"/> disposes it once it is done with the
/// content, however the store ends, also when it never reads the rows. <see cref="HoleKeys"/> name the layer 4 holes the decode
/// met (BLTE chunks it couldn't decrypt) and <see cref="ChunkKeys"/> every encrypted chunk's key, decrypted or not, each among
/// <see cref="KeysUsed"/>. Which chunks are encrypted comes from the build's encoding of the file, not its content key, and a
/// decrypted chunk's bytes depend on its key, so a table with any (<see cref="Encrypted"/>) is filed under the source's
/// <see cref="GeneratedTableSource.EncodingScope"/> (D350).
/// </summary>
public sealed record GeneratedTableContent(Db2TableHeader Header, IReadOnlyList<Db2Section> Sections, Db2Completion Completion,
    IReadOnlyCollection<string> KeysUsed, IAsyncEnumerable<Db2Row> Rows, IAsyncDisposable? Owner = null, IReadOnlyCollection<string>? HoleKeys = null, IReadOnlyCollection<string>? ChunkKeys = null)
{
    public bool Encrypted => HoleKeys is { Count: > 0 } || ChunkKeys is { Count: > 0 };
}
