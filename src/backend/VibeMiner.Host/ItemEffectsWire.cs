using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Host;

internal static class ItemEffectsWire
{
    internal sealed record Input(ItemEffectsOperation Operation, ItemEffectsSelection Selection,
        ItemEffectsPageRequest? Page = null, ItemEffectDetailsRequest? Details = null, ItemEffectNamesRequest? Names = null);
    internal static Input Request(string method, JsonElement value)
    {
        var operation = method switch
        {
            "items.effects.data" => ItemEffectsOperation.Read, "items.effects.prepare" => ItemEffectsOperation.PrepareLinks,
            "items.effects.page" => ItemEffectsOperation.ReadPage, "items.effects.details" => ItemEffectsOperation.ReadDetails,
            "items.effects.details.prepare" => ItemEffectsOperation.PrepareDetails, "items.effects.names" => ItemEffectsOperation.ReadNames,
            "items.effects.names.prepare" => ItemEffectsOperation.PrepareNames, _ => throw Invalid()
        };
        string[] common = ["productId", "buildIdentity", "sourceRegion", "researchLocale", "itemsArtifactId", "itemId"];
        var extra = operation switch
        {
            ItemEffectsOperation.ReadPage => new[] { "bridgeIndexArtifactId", "limit", "after" },
            ItemEffectsOperation.ReadDetails or ItemEffectsOperation.PrepareDetails => ["bridgeIndexArtifactId", "edges"],
            ItemEffectsOperation.ReadNames or ItemEffectsOperation.PrepareNames => ["bridgeIndexArtifactId", "itemEffectArtifactId", "edges", "policyRevision"],
            _ => []
        };
        var allowed = new HashSet<string>(common.Concat(extra), StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var field in value.EnumerateObject()) if (!allowed.Contains(field.Name) || !seen.Add(field.Name)) throw Invalid();
        var selection = new ItemEffectsSelection(new(Text(value, "productId"), Text(value, "buildIdentity"), Text(value, "sourceRegion"), Text(value, "researchLocale")),
            Text(value, "itemsArtifactId"), Text(value, "itemId"));
        ItemEffectsValidation.Validate(selection);
        if (operation == ItemEffectsOperation.ReadPage)
        {
            var limit = 16;
            if (value.TryGetProperty("limit", out var count) && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out limit))) throw Invalid();
            var request = new ItemEffectsPageRequest(selection, Text(value, "bridgeIndexArtifactId"), limit, Optional(value, "after"));
            ItemEffectsValidation.Validate(request); return new(operation, selection, Page: request);
        }
        if (operation is ItemEffectsOperation.ReadDetails or ItemEffectsOperation.PrepareDetails)
        {
            var request = new ItemEffectDetailsRequest(selection, Text(value, "bridgeIndexArtifactId"), Edges(value,16));
            ItemEffectsValidation.Validate(request); return new(operation, selection, Details: request);
        }
        if (operation is ItemEffectsOperation.ReadNames or ItemEffectsOperation.PrepareNames)
        {
            var request = new ItemEffectNamesRequest(selection, Text(value, "bridgeIndexArtifactId"), Text(value, "itemEffectArtifactId"), Edges(value,8),
                Optional(value, "policyRevision") ?? ItemEffectsValidation.NamePolicy);
            ItemEffectsValidation.Validate(request); return new(operation, selection, Names: request);
        }
        return new(operation, selection);
    }
    private static IReadOnlyList<ItemEffectsEdgeKey> Edges(JsonElement value,int maximum)
    {
        if(!value.TryGetProperty("edges",out var entries)||entries.ValueKind!=JsonValueKind.Array||entries.GetArrayLength() is <1||entries.GetArrayLength()>maximum)throw Invalid();
        var result=new ItemEffectsEdgeKey[entries.GetArrayLength()];var i=0;
        foreach(var entry in entries.EnumerateArray())
        {
            if(entry.ValueKind!=JsonValueKind.Object)throw Invalid();var count=0;
            foreach(var property in entry.EnumerateObject())if(property.Name!="indexOrdinal"||++count>1)throw Invalid();
            result[i++]=new(Text(entry,"indexOrdinal"));
        }
        return Array.AsReadOnly(result);
    }
    private static string Text(JsonElement value,string key)=>value.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?field.GetString()!:throw Invalid();
    private static string? Optional(JsonElement value,string key)=>value.TryGetProperty(key,out var field)?field.ValueKind==JsonValueKind.String?field.GetString():throw Invalid():null;
    private static DiscoveryException Invalid()=>new("INVALID_REQUEST","Choose one exact selected Item and bounded effects request.");
    private static string Name<T>(T value)where T:struct,Enum=>Enum.IsDefined(value)?JsonNamingPolicy.CamelCase.ConvertName(value.ToString()):
        throw new DiscoveryException("ITEM_EFFECTS_RESULT_INVALID","The effects result uses an undefined state.");
    internal static object Readiness(ItemEffectsReadiness value)=>new
    {value.Selection,value.Admitted,state=Name(value.State),value.Snapshot,value.ObservedAssociations,source=value.Source is null?null:Source(value.Source),value.Problem,value.Link};
    private static object Source(ItemEffectsSource value)=>new{role=Name(value.Role),state=Name(value.State),value.Snapshot,value.HistoricalArtifactId,value.Problem};
    internal static object Page(ItemEffectsPage value)=>new{value.Selection,value.BridgeIndexArtifactId,value.OrderPolicy,value.BridgeSource,value.ObservedAssociations,value.Edges,value.NextCursor};
    internal static object Details(ItemEffectDetailsResult value)=>new
    {
        value.Selection,value.BridgeIndexArtifactId,value.PolicyRevision,source=Source(value.Source),
        outcomes=value.Outcomes.Select(outcome=>new
        {
            outcome.Key,outcome.ItemEffectId,state=Name(outcome.State),outcome.ObservedCandidates,outcome.Target,
            fields=outcome.Fields?.Select(field=>new{field.Name,field.ColumnIndex,field.Value,evidence=Name(field.Evidence),field.Interpretation,field.CandidateReference}),
            absence=outcome.Absence is {} absence?Name(absence):null,outcome.Problem
        })
    };
    internal static object Names(ItemEffectNamesResult value)=>new
    {
        value.Selection,value.BridgeIndexArtifactId,value.ItemEffectArtifactId,value.PolicyRevision,sources=value.Sources.Select(Source),
        outcomes=value.Outcomes.Select(outcome=>new
        {
            outcome.Key,effect=new{state=Name(outcome.Effect.State),outcome.Effect.ItemEffectId,outcome.Effect.ObservedCandidates,outcome.Effect.Target,outcome.Effect.SpellId,
                absence=outcome.Effect.Absence is {} effectAbsence?Name(effectAbsence):null,outcome.Effect.Problem},
            state=Name(outcome.State),outcome.ObservedCandidates,outcome.Target,outcome.Text,outcome.Issue,outcome.TextUnits,
            absence=outcome.Absence is {} absence?Name(absence):null,outcome.Problem
        })
    };
}
