using System.Text;

namespace VibeMiner.Builds;

public sealed partial class BuildRegistry
{
    private static BuildRegistryException Invalid(string message) => new(BuildRegistryCodes.Validation, message);
    private static BuildRegistryException Unreadable(string message, Exception? e = null) => new(BuildRegistryCodes.Unreadable, message + " Preserve the registry for repair.", e);
    private static void Text(string? value, int bytes, string name, bool empty = false)
    {
        if (value is null || (!empty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > bytes)
            throw Invalid($"Invalid or oversized {name}.");
    }
    private static void Id(string? id)
    {
        if (id is not { Length: 64 } || !id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) throw Invalid("A build id is 64 lowercase hexadecimal digits.");
    }
    internal static BuildIdentity Identity(BuildIdentity identity)
    {
        if (identity is null || identity.Fields is null) throw Invalid("A canonical identity is required.");
        Id(identity.Id); Text(identity.Canonical, BuildRegistryLimits.MaxIdentityBytes, "canonical identity");
        Text(identity.ProductId, 64, "product");
        if (identity.Fields.Count > BuildRegistryLimits.MaxFields) throw Invalid("Too many identity fields.");
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in identity.Fields)
        {
            Text(pair.Key, BuildRegistryLimits.MaxFieldNameBytes, "field name");
            Text(pair.Value, BuildRegistryLimits.MaxFieldValueBytes, "field value", empty: true);
            copy.Add(pair.Key, pair.Value);
        }
        BuildIdentity computed;
        try { computed = BuildIdentity.FromFields(identity.ProductId, copy); }
        catch (BuildIdentityException e) { throw Invalid(e.Message); }
        Text(computed.Canonical, BuildRegistryLimits.MaxIdentityBytes, "canonical identity");
        if (computed.Id != identity.Id || computed.Canonical != identity.Canonical || computed.Fields.Count != copy.Count ||
            computed.Fields.Any(p => !copy.TryGetValue(p.Key, out var value) || value != p.Value)) throw Invalid("Identity Id, canonical string and fields disagree.");
        return computed;
    }
    private static void Selection(BuildIdentity identity, BuildRef selection, string locale)
    {
        if (selection is null || selection.Product != identity.ProductId || selection.BuildConfig != identity.Fields["BuildConfig"])
            throw Invalid("The selected product and BuildConfig must match the canonical identity.");
        Text(locale, BuildRegistryLimits.MaxLocaleBytes, "locale");
        try { _ = BuildLocales.Bit(locale); } catch (ArgumentException e) { throw Invalid(e.Message); }
    }
    private static void Diagnostic(BuildRegistryDiagnostic? diagnostic)
    {
        if (diagnostic is null) return;
        Text(diagnostic.Code, BuildRegistryLimits.MaxDiagnosticCodeBytes, "diagnostic code");
        Text(diagnostic.Message, BuildRegistryLimits.MaxDiagnosticMessageBytes, "diagnostic message", empty: true);
    }
    private static Stored Validate(Stored stored)
    {
        if (stored is null || stored.Entry is null) throw Invalid("Missing registry entry.");
        var e = stored.Entry; var identity = Identity(e.Identity);
        Selection(identity, e.ObservedBuild, e.ObservedLocale); Diagnostic(e.Diagnostic);
        if (!Enum.IsDefined(e.State) || e.Generation < 0 || e.FirstObservedAt > e.LastObservedAt) throw Invalid("Invalid registry state or observation times.");
        if (e.State == BuildLoadState.Discovered)
        {
            if (e.Generation != 0 || e.AttemptBuild is not null || e.AttemptLocale is not null || e.AttemptStartedAt is not null || e.FinishedAt is not null || stored.Nonce is not null || e.Diagnostic is not null)
                throw Invalid("A discovered build cannot have an attempt.");
        }
        else
        {
            if (e.Generation <= 0 || e.AttemptBuild is null || e.AttemptLocale is null || e.AttemptStartedAt is null || !Hex32(stored.Nonce)) throw Invalid("An attempt snapshot is incomplete.");
            Selection(identity, e.AttemptBuild, e.AttemptLocale);
            if (e.State == BuildLoadState.Loading ? e.FinishedAt is not null || e.Diagnostic is not null : e.FinishedAt is null)
                throw Invalid("The attempt terminal outcome is inconsistent.");
        }
        return stored with { Entry = e with { Identity = identity } };
    }
    private static bool Hex32(string? value) => value is { Length: 32 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
