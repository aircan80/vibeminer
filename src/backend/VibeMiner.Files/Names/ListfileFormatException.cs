namespace VibeMiner.Files.Names;

/// <summary>A community listfile (D105) the parser refuses. The whole file is refused; the detail names the line.</summary>
public sealed class ListfileFormatException(string detail) : Exception(detail);
