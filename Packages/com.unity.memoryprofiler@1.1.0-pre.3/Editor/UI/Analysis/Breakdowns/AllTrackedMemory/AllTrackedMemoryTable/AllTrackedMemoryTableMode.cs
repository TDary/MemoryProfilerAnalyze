namespace Unity.MemoryProfiler.Editor.UI
{
    enum AllTrackedMemoryTableMode
    {
        OnExportAllOfMemory = -3,
        OnExportAllUnityObject = -2,
        OnlyExport = -1,
        OnlyCommitted = 0,
        OnlyResident,
        CommittedAndResident,
    }
}
