namespace VibeMiner.Objects.Testing;

/// <summary>Integration-only failures at real atomic payload/claim publication boundaries.</summary>
public static class LibraryAcquisitionFaults
{
    public static ObjectStoreOptions AttributionFailure(ObjectStoreOptions options, Func<bool> fail) => options with
    { Fault = point => { if (point == StorePoint.LibraryClaimInsideCommit && fail()) throw new IOException("Authored attribution transaction failure."); } };
    public static ObjectStoreOptions PublicationFailure(ObjectStoreOptions options, Func<bool> fail) => options with
    { Fault = point => { if (point == StorePoint.InsideIndexCommit && fail()) throw new IOException("Authored payload publication failure."); } };    public static ObjectStoreOptions AttributionGate(ObjectStoreOptions options, Action entered) => options with
    { Fault = point => { if (point == StorePoint.LibraryClaimInsideCommit) entered(); } };

}
