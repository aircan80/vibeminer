using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal interface IGeometrySelectionFence
{
    GeometryDisplayAuthorization Original { get; }
    Task RevalidateAsync(CancellationToken token);
}
internal sealed record GeometrySourceServices(DiscoveryCatalog Catalog, VerifiedObjectStore Objects,
    AssetResolutionSessions Sessions, ISharedAcquisition Shared, AcquisitionResources Resources);
internal sealed record GeometrySourceContextIdentity(WorkspaceRequest Request, string ResearchLocale,
    OpeningSourceReference Opening, string BuildConfigurationKey, string CdnConfigurationKey,
    string BuildConfigurationSha256, string CdnConfigurationSha256);
internal sealed record GeometrySourceCapsule(string Role, uint FileDataId, MetadataExpectation Expectation,
    ContentReceipt Receipt, AssetSourceAssociation Association);
internal sealed record GeometryNativeCounts(int Vertices, int VertexLookup, int GlobalTriangleIndices,
    int SectionTriangleIndices, int Sections);
internal sealed record GeometrySourceCapsulePair(string Revision, string InterpretationPolicy,
    string VariantPolicy, string EncodingSelectionPolicy, GeometrySourceContextIdentity SourceContext,
    GeometrySourceCapsule Model, GeometrySourceCapsule Skin, IReadOnlyList<ResolverArtifact> Resolvers,
    int MainSkinOrdinal, uint MainSkinFileDataId, GeometryNativeCounts Counts);
internal sealed record GeometryCapsuleEncoding(GeometrySourceCapsulePair Capsule,
    ReadOnlyMemory<byte> CanonicalUtf8, string AuthoritySha256);

// Fresh construction is private to the actual two-node source pipeline in this partial type.
// The historical capsule codec deliberately cannot manufacture this authority capability.
internal sealed partial class GeometrySourceClosure
{
    private GeometrySourceClosure(GeometryCapsuleEncoding encoding, GeometrySourceBinding binding, string bindingSha256)
    { Capsule = encoding.Capsule; CanonicalUtf8 = encoding.CanonicalUtf8; AuthoritySha256 = encoding.AuthoritySha256; DataBinding = binding; DataBindingSha256 = bindingSha256; }
    internal GeometrySourceCapsulePair Capsule { get; }
    internal ReadOnlyMemory<byte> CanonicalUtf8 { get; }
    internal string AuthoritySha256 { get; }
    internal GeometrySourceBinding DataBinding { get; }
    internal string DataBindingSha256 { get; }
}

internal sealed class GeometryPreparedSources(GeometrySourceClosure closure, M2GeometrySkin skin, IReadOnlyList<CreatureGeometrySource> sources, IDisposable payload) : IAsyncDisposable
{
    private M2GeometrySkin? ownedSkin = skin;
    private IDisposable? reservation = payload;
    internal GeometrySourceClosure Closure { get; } = closure;
    internal IReadOnlyList<CreatureGeometrySource> Sources { get; } = sources;
    internal M2GeometrySkin Skin => ownedSkin ?? throw new ObjectDisposedException(nameof(GeometryPreparedSources));
    internal GeometrySourceBinding DataBinding => Closure.DataBinding;
    internal string DataBindingSha256 => Closure.DataBindingSha256;
    public ValueTask DisposeAsync()
    { ownedSkin = null; Interlocked.Exchange(ref reservation, null)?.Dispose(); return ValueTask.CompletedTask; }
}


internal sealed class GeometrySourceFailureException(CreatureGeometryState state, CreatureGeometryDependency dependency,
    DiscoveryException problem, IReadOnlyList<CreatureGeometrySource> sources) : Exception(problem.Message, problem)
{
    internal CreatureGeometryState State { get; } = state;
    internal CreatureGeometryDependency Dependency { get; } = dependency;
    internal DiscoveryException Problem { get; } = problem;
    internal IReadOnlyList<CreatureGeometrySource> Sources { get; } = Snapshot(sources);
    private static IReadOnlyList<CreatureGeometrySource> Snapshot(IReadOnlyList<CreatureGeometrySource> values)
    {
        var count = values.Count;
        if (count < 0) throw GeometrySourceCapsuleCodec.Invalid();
        if (count > 2) throw GeometrySourceCapsuleCodec.Limit();
        var copy = new CreatureGeometrySource[count];
        try { for (var i = 0; i < count; i++) copy[i] = values[i] ?? throw GeometrySourceCapsuleCodec.Invalid(); }
        catch (ArgumentOutOfRangeException) { throw GeometrySourceCapsuleCodec.Invalid(); }
        catch (IndexOutOfRangeException) { throw GeometrySourceCapsuleCodec.Invalid(); }
        return Array.AsReadOnly(copy);
    }
}
