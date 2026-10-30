using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed partial class GeometrySourceClosure
{
    internal const long SourcePayloadBytes = 32L << 20;
    private static readonly M2Limits Limits = new(MaxSourceBytes: 8 << 20, MaxOutputBytes: 8 << 20,
        MaxVertices: 65536, MaxVertexLookup: 65536, MaxTriangleIndices: 131070, MaxSections: 256,
        MaxMaterials: 256, MaxBatches: 512, MaxTextures: 32, MaxSkinProfiles: 32, MaxChunks: 128,
        MaxNameBytes: 4096, MaxMetadataEntries: 8192);

    internal static async Task<GeometryPreparedSources> PrepareAsync(GeometrySourceServices services, IGeometrySelectionFence selection,
        IGeometryPayloadBudget payload, Func<CreatureGeometryProgress, Task> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(progress);
        token.ThrowIfCancellationRequested();
        // Caller has reserved the one global48MiB producer slice. This is a child of its local
        // ledger;16MiB remains charged by Store for its original C1/C2/opening capture.
        var reservation = payload.Reserve(SourcePayloadBytes); var transferred = false; var cleanupFailed = false;
        var reached = new List<CreatureGeometrySource>(2); var dependency = CreatureGeometryDependency.Model;
        M2AppearanceModelFacts? appearanceFacts = null; M2AppearancePlan? appearancePlan = null; DiscoveryException? appearanceFailure = null;
        GeometryAppearancePreparation? appearance = null;
        try
        {
            var original = selection.Original;
            var context = Context(original);
            await selection.RevalidateAsync(token);
            await progress(new("sourceContext", "Opening exact shared asset sources", "stage"));
            var handle = await services.Sessions.OpenAsync(context.Request, context.ResearchLocale,
                _ => progress(new("sourceContext", "Preparing exact shared asset source metadata", "stage")), token);
            var handleRetirementAttempted = false; Exception? operationFailure = null;
            try
            {
            RequireHandle(handle.SourceContext, context);
            await selection.RevalidateAsync(token);
            var budget = new GeometryWorkBudget(services.Resources);
            var modelResult = await ResolveAsync(handle, original.ModelFileDataId, context, selection, progress, dependency, budget, token);
            var modelAssociationSha = await GeometrySourceCapsuleCodec.SourceAssociationSha256Async(modelResult.Association, budget, token);
            M2Model model; GeometrySourceCapsule modelProof; GeometryCanonicalSources modelCanonical;
            await using (var node = await GeometrySourceVerification.VerifyAsync(services, modelResult, handle.SourceContext, selection, progress, dependency, token))
            {
                modelCanonical = node.Canonical;
                modelProof = new("model", original.ModelFileDataId, modelResult.Expectation!, modelCanonical.Receipt, modelResult.Association);
                reached.Add(Evidence(modelProof, modelAssociationSha));
                await progress(new("modelParsing", "Reading original source model geometry", "stage", Dependency: dependency));
                using (await services.Resources.EnterCpuAsync(null, token)) model = await M2ModelReader.ReadAsync(node.Decoded, Limits, token);
                // Texture facts read the same verified bytes; their failure never fails geometry (#63).
                try { node.Decoded.Position = 0; using (await services.Resources.EnterCpuAsync(null, token)) appearanceFacts = await M2Appearance.ReadModelAsync(node.Decoded, model, token); }
                catch (DiscoveryException error) when (error.Code.StartsWith("MODEL_", StringComparison.Ordinal)) { appearanceFailure = error; }
            }
            if (M2GeometrySkinReader.ProfileRefusal(model) is { } refusal) throw new DiscoveryException("MODEL_UNSUPPORTED", refusal);
            if (model.SkinFileDataIds.Count == 0 || model.SkinFileDataIds[0] == 0) throw new DiscoveryException("MODEL_UNSUPPORTED", "The model has no positive declared main SKIN.");
            var skinId = model.SkinFileDataIds[0]; dependency = CreatureGeometryDependency.Skin;
            await selection.RevalidateAsync(token);
            var skinResult = await ResolveAsync(handle, skinId, context, selection, progress, dependency, budget, token);
            var skinAssociationSha = await GeometrySourceCapsuleCodec.SourceAssociationSha256Async(skinResult.Association, budget, token);
            M2GeometrySkin skin; GeometrySourceCapsule skinProof; GeometryCanonicalSources skinCanonical;
            await using (var node = await GeometrySourceVerification.VerifyAsync(services, skinResult, handle.SourceContext, selection, progress, dependency, token))
            {
                skinCanonical = node.Canonical; skinProof = new("skin", skinId, skinResult.Expectation!, skinCanonical.Receipt, skinResult.Association);
                reached.Add(Evidence(skinProof, skinAssociationSha));
                await progress(new("skinParsing", "Reading all declared main-SKIN source geometry", "stage", Dependency: dependency));
                using (await services.Resources.EnterCpuAsync(null, token)) skin = await M2GeometrySkinReader.ReadAsync(node.Decoded, model, Limits, token);
                if (appearanceFacts is not null)
                    try { node.Decoded.Position = 0; using (await services.Resources.EnterCpuAsync(null, token)) appearancePlan = await M2Appearance.AdmitAsync(node.Decoded, skin, appearanceFacts, token); }
                    catch (DiscoveryException error) when (error.Code.StartsWith("MODEL_", StringComparison.Ordinal)) { appearanceFailure = error; }
            }
            // Both readers were given the same8MiB MaxOutputBytes; the original SKIN
            // core charges model retained output plus skin arrays/reserved resolved indices.
            if (modelCanonical.EncodingLookup != skinCanonical.EncodingLookup || modelCanonical.RootLookup != skinCanonical.RootLookup)
                throw GeometrySourceCapsuleCodec.Invalid();
            int q = 0;
            using (await services.Resources.EnterCpuAsync(null, token))
                foreach (var section in skin.Sections)
                { token.ThrowIfCancellationRequested(); q = checked(q + section.TriangleCount); if (q > 131070) throw GeometrySourceCapsuleCodec.Limit(); }
            await Task.Yield();
            var counts = new GeometryNativeCounts(model.Vertices.Count, skin.VertexLookup.Count, skin.TriangleLookup.Count, q, skin.Sections.Count);
            var pair = new GeometrySourceCapsulePair(GeometrySourceCapsuleCodec.Revision, M2GeometrySkin.InterpretationPolicy,
                AssetResolutionSession.VariantPolicy, AssetResolutionSession.EncodingSelectionPolicy, context, modelProof, skinProof,
                Array.AsReadOnly(new[] { modelCanonical.EncodingLookup, modelCanonical.RootLookup }), 0, skinId, counts);
            var encoding = await GeometrySourceCapsuleCodec.EncodeAsync(pair, budget, token);
            var binding = new GeometrySourceBinding(Source(modelProof.Receipt), Source(skinProof.Receipt), modelProof.FileDataId, skinId, 0);
            string bindingSha;
            using (await services.Resources.EnterCpuAsync(null, token)) bindingSha = ModelGeometryArtifacts.ComputeBindingSha256(binding, token);
            await selection.RevalidateAsync(token);
            if (q != 0) appearance = await AppearanceAsync(services, handle, context, selection, appearancePlan, appearanceFailure, bindingSha, payload, progress, token);
            // Handle retirement happens before any source proof escapes or its budget can retire.
            handleRetirementAttempted = true;
            await RetireHandleAsync(handle, null); token.ThrowIfCancellationRequested();
            await selection.RevalidateAsync(token);
            var closure = new GeometrySourceClosure(encoding, binding, bindingSha);
            transferred = true; return new(closure, skin, Array.AsReadOnly(reached.ToArray()), reservation, appearance);
            }
            catch (Exception error) { operationFailure = error; throw; }
            finally
            {
                if (!handleRetirementAttempted)
                { handleRetirementAttempted = true; await RetireHandleAsync(handle, operationFailure); }
            }
        }
        catch (GeometrySourceCleanupException e)
        { cleanupFailed = true; e.RetainPayload(reservation); throw; }
        catch (DiscoveryException e) when (e.Code.EndsWith("CLEANUP_FAILED", StringComparison.Ordinal) || e.Code is "ASSET_SOURCE_CLEANUP" or "ASSET_CLEANUP_FAILED")
        { cleanupFailed = true; throw; }
        catch (DiscoveryException e) when (!RequestFailure(e.Code))
        { throw Failure(e, dependency, reached); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw Failure(new("CREATURE_GEOMETRY_SOURCE_UNAVAILABLE", "Original geometry source storage is unavailable.", true), dependency, reached); }
        catch (OverflowException) { throw Failure(GeometrySourceCapsuleCodec.Limit(), dependency, reached); }
        finally { if (!transferred) appearance?.Dispose(); if (!transferred && !cleanupFailed) reservation.Dispose(); }
    }
    // A texture integrity refusal is an appearance outcome, not a geometry failure. Request, stale and cleanup failures propagate.
    private static async Task<GeometryAppearancePreparation> AppearanceAsync(GeometrySourceServices services, AssetResolutionSession handle,
        GeometrySourceContextIdentity context, IGeometrySelectionFence selection, M2AppearancePlan? plan, DiscoveryException? failure,
        string bindingSha, IGeometryPayloadBudget payload, Func<CreatureGeometryProgress, Task> progress, CancellationToken token)
    {
        try { return await GeometryAppearanceSources.PrepareAsync(services, handle, context, selection, plan, failure, bindingSha, payload, progress, token); }
        catch (DiscoveryException error) when (!RequestFailure(error.Code) && error.Code is not ("ASSET_SOURCE_CLEANUP" or "ASSET_CLEANUP_FAILED"))
        {
            var limit = error.Code.Contains("LIMIT", StringComparison.Ordinal) || error.Code.Contains("CAPACITY", StringComparison.Ordinal);
            return new(GeometryAppearanceSources.Outcome(limit ? CreatureGeometryAppearanceState.ResourceLimit : CreatureGeometryAppearanceState.Corrupt,
                GeometryAppearanceSources.Display(selection.Original), [], plan is null ? null : GeometryAppearanceSources.Effects(plan.Effects),
                new(error.Code.Length <= 64 ? error.Code : "CREATURE_APPEARANCE_FAILED", "The texture chain could not be admitted.", error.Retryable)), false);
        }
    }
    internal static async Task RetireHandleAsync(IAsyncDisposable handle, Exception? operationFailure)
    {
        try { await handle.DisposeAsync(); }
        catch (Exception cause)
        {
            // A finally must not mask an earlier stream-owner failure. Keep both
            // uncertain owners in the exact exception retained by Store's failed group.
            if (operationFailure is GeometrySourceCleanupException existing)
            { existing.RetainHandle(handle, cause); throw existing; }
            var owner = new GeometrySourceVerification.Owner(null); throw owner.RetainHandle(handle, cause);
        }
    }
    private static async Task<AssetResolutionResult> ResolveAsync(AssetResolutionSession handle, uint id, GeometrySourceContextIdentity context,
        IGeometrySelectionFence selection, Func<CreatureGeometryProgress, Task> progress, CreatureGeometryDependency dependency,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        var stage = dependency == CreatureGeometryDependency.Model ? "modelResolution" : "skinResolution";
        var batch = await handle.ResolveAsync([id], _ => progress(new(stage, "Resolving complete original asset memberships", "stage", Dependency: dependency)), token);
        await selection.RevalidateAsync(token);
        if (batch.Results.Count != 1 || batch.Results[0].FileDataId != id) throw GeometrySourceCapsuleCodec.Invalid();
        var result = batch.Results[0];
        if (result.Status != AssetResolutionStatus.Ready)
        {
            var (state, code) = result.Status switch
            {
                AssetResolutionStatus.MissingMembership => (CreatureGeometryState.MissingMembership, "CREATURE_GEOMETRY_MEMBERSHIP_MISSING"),
                AssetResolutionStatus.AmbiguousVariants => (CreatureGeometryState.AmbiguousVariants, "CREATURE_GEOMETRY_VARIANTS_AMBIGUOUS"),
                _ => (CreatureGeometryState.EncodingUnresolved, "CREATURE_GEOMETRY_ENCODING_UNRESOLVED")
            };
            throw new DiscoveryException(code, state.ToString());
        }
        var association = await GeometrySourceCapsuleCodec.SnapshotAssociationAsync(result.Association, budget, token);
        var expected = result.Expectation;
        if (expected is null) throw GeometrySourceCapsuleCodec.Limit();
        GeometrySourceVerification.RequireSourceSize(expected, dependency);
        if (association.Request != context.Request || association.SourceReference != context.Opening || association.ResearchLocale != context.ResearchLocale ||
            association.Configurations[0].Key != context.BuildConfigurationKey || association.Configurations[0].Sha256 != context.BuildConfigurationSha256 ||
            association.Configurations[1].Key != context.CdnConfigurationKey || association.Configurations[1].Sha256 != context.CdnConfigurationSha256 ||
            association.Memberships.Any(m => m.Entry.FileDataId != id || m.Entry.ContentKey != expected.ContentKey)) throw GeometrySourceCapsuleCodec.Invalid();
        var coreAssociation = selection.Original.Core.Artifact.Binding.Source.Association;
        if (!MetadataReceiptIdentity.Same(coreAssociation.EncodingSource, association.EncodingSource) ||
            !MetadataReceiptIdentity.Same(coreAssociation.RootSource, association.RootSource)) throw GeometrySourceCapsuleCodec.Invalid();
        return result with { Association = association, Expectation = expected with { } };
    }
    private static GeometrySourceContextIdentity Context(GeometryDisplayAuthorization authorization)
    {
        if (authorization is null || authorization.ModelFileDataId == 0) throw GeometrySourceCapsuleCodec.Invalid();
        var core = authorization.Core; var c = core.Snapshot.Context;
        var build = core.Artifact.Binding.Configurations.Single(r => r.Role == "buildConfig"); var cdn = core.Artifact.Binding.Configurations.Single(r => r.Role == "cdnConfig");
        if (core.Opening.Build.Id != c.BuildIdentity || core.Opening.Build.ProductId != c.ProductId || core.Opening.Reference.Region != c.SourceRegion ||
            build.Key != core.Opening.Build.BuildConfig || cdn.Key != core.Opening.Build.CdnConfig || c.ResearchLocale != "enUS") throw GeometrySourceCapsuleCodec.Invalid();
        return new(new(c.ProductId, c.BuildIdentity, c.SourceRegion), c.ResearchLocale, core.Opening.Reference, build.Key, cdn.Key, build.Sha256, cdn.Sha256);
    }
    private static void RequireHandle(EncodedSourceContext source, GeometrySourceContextIdentity c)
    {
        if (source.ProductId != c.Request.ProductId || source.BuildIdentity != c.Request.BuildIdentity || source.SourceRegion != c.Request.SourceRegion ||
            source.SourceReference != c.Opening || source.CdnConfigurationSha256 != c.CdnConfigurationSha256) throw new DiscoveryException("CREATURE_GEOMETRY_STALE", "The original selected source changed during preparation.");
    }
    private static GeometryOriginalSource Source(ContentReceipt receipt) => new(receipt.ContentKey, receipt.EncodingKey, receipt.Sha256,
        long.Parse(receipt.EncodedBytes, CultureInfo.InvariantCulture), long.Parse(receipt.DecodedBytes, CultureInfo.InvariantCulture), receipt.VerifierRevision);
    private static CreatureGeometrySource Evidence(GeometrySourceCapsule source, string associationSha) => new(source.Role == "model" ? CreatureGeometrySourceRole.Model : CreatureGeometrySourceRole.Skin,
        source.FileDataId.ToString(CultureInfo.InvariantCulture), source.Receipt.ContentKey, source.Receipt.EncodingKey, source.Receipt.Sha256,
        source.Expectation.EncodedBytes, source.Expectation.DecodedBytes, source.Receipt.VerifierRevision, associationSha, source.Association.VariantPolicy,
        source.Association.EncodingLookupRevision, source.Association.RootLookupRevision, source.Association.EncodingSelectionPolicy);
    private static bool RequestFailure(string code) => code.Contains("STALE", StringComparison.Ordinal) || code.Contains("SOURCE_CHANGED", StringComparison.Ordinal) ||
        code.Contains("BUSY", StringComparison.Ordinal) || code.EndsWith("CLEANUP_FAILED", StringComparison.Ordinal) || code is "ENGINE_STOPPING" or "INVALID_REQUEST";
    private static bool OwnMessage(string code) => code is "MODEL_UNSUPPORTED" or "MODEL_INVALID" or "MODEL_LIMIT" or "CREATURE_GEOMETRY_RESOURCE_LIMIT";
    internal static GeometrySourceFailureException Failure(DiscoveryException e, CreatureGeometryDependency dependency, IReadOnlyList<CreatureGeometrySource> reached)
    {
        var state = e.Code switch
        {
            "CREATURE_GEOMETRY_MEMBERSHIP_MISSING" => CreatureGeometryState.MissingMembership,
            "CREATURE_GEOMETRY_VARIANTS_AMBIGUOUS" => CreatureGeometryState.AmbiguousVariants,
            "CREATURE_GEOMETRY_ENCODING_UNRESOLVED" => CreatureGeometryState.EncodingUnresolved,
            "BLTE_ENCRYPTED" => CreatureGeometryState.Protected,
            _ when e.Code.Contains("LIMIT", StringComparison.Ordinal) || e.Code.Contains("CAPACITY", StringComparison.Ordinal) => CreatureGeometryState.ResourceLimit,
            _ when e.Code.Contains("UNSUPPORTED", StringComparison.Ordinal) => CreatureGeometryState.Unsupported,
            _ when e.Code.Contains("UNAVAILABLE", StringComparison.Ordinal) || e.Code == "LOCAL_OBJECT_MISSING" => CreatureGeometryState.Unavailable,
            _ => CreatureGeometryState.Corrupt
        };
        // Our own readers and size checks name the feature, malformation or limit in a short fixed message (#75, #98); keep it.
        // A limit leads with what tripped it, such as the file's sizes and the caps, in plain words (#111).
        var own = OwnMessage(e.Code) && e.Message.Length is > 0 and <= 400;
        var message = state == CreatureGeometryState.ResourceLimit
            ? own ? e.Message : "The geometry source is over one of its limits."
            : "The original geometry source could not be admitted (" + state + ")" + (own ? ": " + e.Message : ".");
        // The source-size refusal already says it is a size limit. Codes mix byte and count caps (memberships, vertices,
        // output bytes), so every other limit says only "a limit", which is true of both.
        if (state == CreatureGeometryState.ResourceLimit && !e.Retryable && !message.EndsWith(GeometrySourceVerification.SizeLimit, StringComparison.Ordinal))
            message += " This is a limit, not a corrupt file.";
        return new(state, dependency, new(e.Code, message, e.Retryable), reached);
    }
}
