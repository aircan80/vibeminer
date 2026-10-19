namespace VibeMiner.Application;

public enum ItemEffectsOperation
{ Read, PrepareLinks, ReadPage, ReadDetails, PrepareDetails, ReadNames, PrepareNames }

/// <summary>One operation attempt and its publisher. Dispose only after the actual writer retires.</summary>
public sealed class ItemEffectsOperationLease : IAsyncDisposable
{
    internal ItemEffectsOperationLease(ItemEffectsService owner, ItemEffectsOperation operation)
    { Owner = owner; Operation = operation; }
    internal ItemEffectsService Owner { get; }
    internal ItemEffectsOperation Operation { get; }
    internal CancellationTokenSource Cancellation { get; } = new();
    internal TaskCompletionSource OperationRetired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource PublicationRetired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource? CancellationRetired;
    internal bool Claimed, Disposing, ProducerFinished;
    internal Exception? CleanupFailure;
    internal bool IsLong => Operation is ItemEffectsOperation.PrepareLinks or ItemEffectsOperation.PrepareDetails or ItemEffectsOperation.PrepareNames;
    public ValueTask DisposeAsync() => new(Owner.RetirePublicationAsync(this));
}
