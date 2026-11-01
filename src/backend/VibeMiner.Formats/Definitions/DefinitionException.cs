namespace VibeMiner.Formats.Definitions;

/// <summary>Which table a definition failure is about and how the WoWDBDefs switch read ("on" or "off"), so views decide
/// from fields, never from the words.</summary>
public sealed record DefinitionProblem(string TableId, string DefinitionFetch);

/// <summary>A table definition that can't be read, found or fetched; <see cref="Code"/> says why.</summary>
public sealed class DefinitionException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    /// <summary>Set when nothing can read a table's build or layout (<see cref="TableDefinitions"/>).</summary>
    public DefinitionProblem? Definition { get; init; }
}
