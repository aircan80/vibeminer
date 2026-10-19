using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed record GeometryArtifactDescriptor(GeometrySourceBinding Binding, CreatureGeometryManifest Manifest);
internal sealed record GeometryCanonicalSources(ContentReceipt Receipt, ResolverArtifact EncodingLookup, ResolverArtifact RootLookup);
internal enum GeometryFinalAction { Publish, ValidateNoDeclaredTriangles }

// Only a bounded catalog lookup can obtain this catalog's private ownership seal. Parsing a
// historical capsule gives no fresh source capability and cannot manufacture an owned locator.
internal sealed class GeometryLocator
{
    private readonly object owner;
    private GeometryLocator(object owner, string key, GeometryArtifactDescriptor artifact, GeometryCapsuleEncoding source,
        string originalDescriptor, string originalCapsule, IReadOnlyList<CreatureGeometrySource> sources)
    { this.owner = owner; Key = key; Descriptor = artifact; Source = source; OriginalDescriptor = originalDescriptor; OriginalCapsule = originalCapsule; Sources = sources; }
    internal string Key { get; }
    internal GeometryArtifactDescriptor Descriptor { get; }
    internal GeometrySourceCapsulePair Capsule => Source.Capsule;
    internal string AuthoritySha256 => Source.AuthoritySha256;
    internal ReadOnlyMemory<byte> CanonicalUtf8 => Source.CanonicalUtf8;
    internal IReadOnlyList<CreatureGeometrySource> Sources { get; }
    internal GeometryCapsuleEncoding Source { get; }
    internal string OriginalDescriptor { get; }
    internal string OriginalCapsule { get; }
    internal bool OwnedBy(object seal) => ReferenceEquals(owner, seal);
    internal static GeometryLocator FromCatalog(object seal, string key, GeometryArtifactDescriptor artifact,
        GeometryCapsuleEncoding source, string descriptor, string capsule, IReadOnlyList<CreatureGeometrySource> sources) => new(seal, key, artifact, source, descriptor, capsule, sources);
}
