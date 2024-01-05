using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Scipts.Containers;
using Scipts.Diagnostics;
using Scipts.FileData;
using Scipts.Tools;
using Scipts.Ultilties.Unsafe;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using VirtualMachineInformation = Scipts.FileData.VirtualMachineInformation;

namespace Scipts.AnalyzeDatas
{
    internal static class TypeTools
    {
        public enum FieldFindOptions
        {
            OnlyInstance,
            OnlyStatic
        }

        static void RecurseCrawlFields(ref List<int> fieldsBuffer, int typeIndex, MemorySnapshot.TypeDescriptionEntriesCache typeDescriptions, MemorySnapshot.FieldDescriptionEntriesCache fieldDescriptions, FieldFindOptions fieldFindOptions, bool crawlBase)
        {
            bool isValueType = typeDescriptions.HasFlag(typeIndex, TypeFlags.kValueType);
            if (crawlBase)
            {
                int baseTypeIndex = typeDescriptions.BaseOrElementTypeIndex[typeIndex];
                if (crawlBase && baseTypeIndex != -1 && !isValueType)
                {
                    RecurseCrawlFields(ref fieldsBuffer, baseTypeIndex, typeDescriptions, fieldDescriptions, fieldFindOptions, true);
                }
            }


            var fieldIndices = typeDescriptions.FieldIndices[typeIndex];
            for (int i = 0; i < fieldIndices.Count; ++i)
            {
                var iField = fieldIndices[i];

                if (!FieldMatchesOptions(iField, fieldDescriptions, fieldFindOptions))
                    continue;

                if (fieldDescriptions.TypeIndex[iField] == typeIndex && isValueType)
                {
                    // this happens in primitive types like System.Single, which is a weird type that has a field of its own type.
                    continue;
                }

                if (fieldDescriptions.Offset[iField] == -1) //TODO: verify this assumption
                {
                    // this is how we encode TLS fields. We don't support TLS fields yet.
                    continue;
                }

                fieldsBuffer.Add(iField);
            }
        }

        public static void AllFieldArrayIndexOf(ref List<int> fieldsBuffer, int ITypeArrayIndex, MemorySnapshot.TypeDescriptionEntriesCache typeDescriptions, MemorySnapshot.FieldDescriptionEntriesCache fieldDescriptions, FieldFindOptions findOptions, bool includeBase)
        {
            //make sure we clear before we start crawling
            fieldsBuffer.Clear();
            RecurseCrawlFields(ref fieldsBuffer, ITypeArrayIndex, typeDescriptions, fieldDescriptions, findOptions, includeBase);
        }

        static bool FieldMatchesOptions(int fieldIndex, MemorySnapshot.FieldDescriptionEntriesCache fieldDescriptions, FieldFindOptions options)
        {
            if (options == FieldFindOptions.OnlyStatic)
            {
                return fieldDescriptions.IsStatic[fieldIndex] == 1;
            }
            if (options == FieldFindOptions.OnlyInstance)
            {
                return fieldDescriptions.IsStatic[fieldIndex] == 0;
            }
            return false;
        }
    }

    internal class MemorySnapshot
    {
        IFileReader m_Reader;
        FormatVersion m_SnapshotVersion;
        public DateTime TimeStamp { get; private set; }
        public Tools.VirtualMachineInformation VirtualMachineInformation { get; private set; }
        public MetaData MetaData { get; private set; }
        public NativeAllocationSiteEntriesCache NativeAllocationSites;
        public FieldDescriptionEntriesCache FieldDescriptions;
        public TypeDescriptionEntriesCache TypeDescriptions;
        public NativeTypeEntriesCache NativeTypes;
        public NativeRootReferenceEntriesCache NativeRootReferences;
        public NativeObjectEntriesCache NativeObjects;
        public NativeMemoryRegionEntriesCache NativeMemoryRegions;
        public NativeMemoryLabelEntriesCache NativeMemoryLabels;
        public NativeCallstackSymbolEntriesCache NativeCallstackSymbols;
        public NativeAllocationEntriesCache NativeAllocations;
        public ManagedMemorySectionEntriesCache ManagedStacks;
        public ManagedMemorySectionEntriesCache ManagedHeapSections;
        // public GCHandleEntriesCache GcHandles;
        // public ConnectionEntriesCache Connections;
        
        public enum MemorySectionType : byte
        {
            GarbageCollector,
            VirtualMachine
        }
        
        public bool HasMemoryLabelSizesAndGCHeapTypes
        {
            get { return m_SnapshotVersion >= FormatVersion.MemLabelSizeAndHeapIdVersion; }
        }
        
        // Eventual TODO: Add on demand load of sections, and unused chunks unload
        public class ManagedMemorySectionEntriesCache : IDisposable
        {
            static readonly ProfilerMarker k_CacheFind = new ProfilerMarker("ManagedMemorySectionEntriesCache.Find");
            public long Count;
            public DynamicArray<ulong> StartAddress = default;
            public DynamicArray<ulong> SectionSize = default;
            public DynamicArray<MemorySectionType> SectionType = default;
            public string[] SectionName = default;
            public NestedDynamicArray<byte> Bytes => m_BytesReadOp?.CompleteReadAndGetNestedResults() ?? default;
            NestedDynamicSizedArrayReadOperation<byte> m_BytesReadOp;
            ulong m_MinAddress;
            ulong m_MaxAddress;
            const ulong k_ReferenceBit = 1UL << 63;

            static readonly string k_VMSection = UnityEditor.L10n.Tr("Virtual Machine Memory Section");
            static readonly string k_GCSection = UnityEditor.L10n.Tr("Managed Heap Section");
            static readonly string k_ActiveGCSection = UnityEditor.L10n.Tr("Active Managed Heap Section");
            static readonly string k_StackSection = UnityEditor.L10n.Tr("Managed Stack Section");
            static readonly string k_ManagedMemorySection = UnityEditor.L10n.Tr("Managed Memory Section (unclear if Heap or Virtual Machine memory, please update Unity)");

            public readonly ulong VirtualMachineMemoryReserved = 0;
            // if the snapshot format is missing the VM section bit, this number will include VM memory
            public readonly ulong ManagedHeapMemoryReserved = 0;
            public readonly ulong TotalActiveManagedHeapSectionReserved = 0;
            public readonly ulong StackMemoryReserved = 0;

            public readonly long FirstAssumedActiveHeapSectionIndex = 0;
            public readonly long LastAssumedActiveHeapSectionIndex = 0;

            public ManagedMemorySectionEntriesCache(ref IFileReader reader, bool HasGCHeapTypes, bool readStackMemory)
            {
                Count = reader.GetEntryCount(readStackMemory ? EntryType.ManagedStacks_StartAddress : EntryType.ManagedHeapSections_StartAddress);
                m_MinAddress = m_MaxAddress = 0;

                if (Count == 0)
                    return;

                SectionType = new DynamicArray<MemorySectionType>(Count, Allocator.Persistent, true);
                SectionName = new string[Count];
                StartAddress = reader.Read(readStackMemory ? EntryType.ManagedStacks_StartAddress : EntryType.ManagedHeapSections_StartAddress, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();

                //long heapSectionIndex = 0;
                //long vmSectionIndex = 0;
                if (HasGCHeapTypes)
                {
                    for (long i = 0; i < StartAddress.Count; ++i)
                    {
                        var encoded = StartAddress[i];
                        StartAddress[i] = encoded & ~k_ReferenceBit; //unmask addr
                        var isVMSection = (encoded & k_ReferenceBit) == k_ReferenceBit;
                        SectionType[i] = isVMSection ? MemorySectionType.VirtualMachine : MemorySectionType.GarbageCollector; //get heaptype
                        // numbering the sections could be confusing as people might expect the numbers to stay comparable over time,
                        // but if one section is unloaded or merged/split in a following snapshot, people might confuse them as the same one
                        // also, grouping the columns by name doesn't work nicely then either so, only number them for debugging purposes
                        // bonus: waaaay less string memory usage and no GC.Allocs for these!
                        if (isVMSection)
                            SectionName[i] = k_VMSection;//"Managed Virtual Machine Memory Section " + vmSectionIndex++;
                        else
                            SectionName[i] = k_GCSection;//"Managed Heap Section " + heapSectionIndex++;
                    }
                }
                else
                {
                    for (long i = 0; i < StartAddress.Count; ++i)
                    {
                        SectionName[i] = k_ManagedMemorySection;
                    }
                }
                if (readStackMemory)
                {
                    for (long i = 0; i < Count; ++i)
                    {
                        SectionName[i] = k_StackSection;//"Managed Stack Section " + i;
                    }
                }

                var entryType = readStackMemory ? EntryType.ManagedStacks_Bytes : EntryType.ManagedHeapSections_Bytes;

                m_BytesReadOp = reader.AsyncReadDynamicSizedArray<byte>(entryType, 0, Count, Allocator.Persistent);

                SectionSize = new DynamicArray<ulong>(Count, Allocator.Persistent);
                // For Sorting we don't need the Async reading of the Managed Stack / Heap bytes to be loaded yet
                SortSectionEntries(ref StartAddress, ref SectionSize, ref SectionType, ref SectionName, ref m_BytesReadOp, readStackMemory);
                m_MinAddress = StartAddress[0];
                m_MaxAddress = StartAddress[Count - 1] + (ulong)m_BytesReadOp.UnsafeAccessToNestedDynamicSizedArray.Count(Count - 1);

                var foundLastAssumedActiveHeap = false;
                var foundFirstAssumedActiveHeap = false;

                for (long i = Count - 1; i >= 0; i--)
                {
                    if (readStackMemory)
                        StackMemoryReserved += SectionSize[i];
                    else
                    {
                        if (SectionType[i] == MemorySectionType.GarbageCollector)
                        {
                            ManagedHeapMemoryReserved += SectionSize[i];
                            if (!foundLastAssumedActiveHeap)
                            {
                                FirstAssumedActiveHeapSectionIndex = i;
                                LastAssumedActiveHeapSectionIndex = i;
                                foundLastAssumedActiveHeap = true;
                            }
                            else if (!foundFirstAssumedActiveHeap && StartAddress[i] + SectionSize[i] + VMTools.X64ArchPtrSize > StartAddress[FirstAssumedActiveHeapSectionIndex])
                            {
                                FirstAssumedActiveHeapSectionIndex = i;
                            }
                            else
                                foundFirstAssumedActiveHeap = true;
                        }
                        else
                            VirtualMachineMemoryReserved += SectionSize[i];
                    }
                }
                if (foundFirstAssumedActiveHeap && foundLastAssumedActiveHeap)
                {
                    for (long i = FirstAssumedActiveHeapSectionIndex; i <= LastAssumedActiveHeapSectionIndex; i++)
                    {
                        SectionName[i] = k_ActiveGCSection;
                    }
                }
                TotalActiveManagedHeapSectionReserved = StartAddress[LastAssumedActiveHeapSectionIndex] + SectionSize[LastAssumedActiveHeapSectionIndex] - StartAddress[FirstAssumedActiveHeapSectionIndex];
            }

            // public BytesAndOffset Find(ulong address, VirtualMachineInformation virtualMachineInformation)
            // {
            //     using (k_CacheFind.Auto())
            //     {
            //         var bytesAndOffset = new BytesAndOffset();
            //
            //         if (address != 0 && address >= m_MinAddress && address < m_MaxAddress)
            //         {
            //             long idx = DynamicArrayAlgorithms.BinarySearch(StartAddress, address);
            //             if (idx < 0)
            //             {
            //                 // -1 means the address is smaller than the first StartAddress, early out with an invalid bytesAndOffset
            //                 if (idx == -1)
            //                     return bytesAndOffset;
            //                 // otherwise, a negative Index just means there was no direct hit and ~idx - 1 will give us the index to the next smaller StartAddress
            //                 idx = ~idx - 1;
            //             }
            //
            //             if (address >= StartAddress[idx] && address < (StartAddress[idx] + (ulong)Bytes.Count(idx)))
            //             {
            //                 bytesAndOffset = new BytesAndOffset(Bytes[idx], virtualMachineInformation.PointerSize, address - StartAddress[idx]);
            //             }
            //         }
            //
            //         return bytesAndOffset;
            //     }
            // }

            readonly struct SortIndexHelper : IComparable<SortIndexHelper>
            {
                public readonly long Index;
                public readonly ulong StartAddress;

                public SortIndexHelper(ref long index, ref ulong startAddress)
                {
                    Index = index;
                    StartAddress = startAddress;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public int CompareTo(SortIndexHelper other) => StartAddress.CompareTo(other.StartAddress);
            }

            static void SortSectionEntries(ref DynamicArray<ulong> startAddresses, ref DynamicArray<ulong> sizes, ref DynamicArray<MemorySectionType> associatedSectionType, ref string[] associatedSectionNames,
                ref NestedDynamicSizedArrayReadOperation<byte> associatedByteArrayReadOp, bool isStackMemory)
            {
                using var sortMapping = new DynamicArray<SortIndexHelper>(startAddresses.Count, Allocator.Temp);

                for (long i = 0; i < sortMapping.Count; ++i)
                {
                    sortMapping[i] = new SortIndexHelper(ref i, ref startAddresses[i]);
                }

                var startAddr = startAddresses;
                //DynamicArrayAlgorithms.IntrospectiveSort(sortMapping, 0, startAddresses.Count);
                using var newSortedAddresses = new DynamicArray<ulong>(startAddresses.Count, Allocator.Temp);
                unsafe
                {
                    var newSortedSectionTypes = isStackMemory ? null : new MemorySectionType[startAddresses.Count];
                    var newSortedSectionNames = new string[startAddresses.Count];

                    for (long i = 0; i < startAddresses.Count; ++i)
                    {
                        long idx = sortMapping[i].Index;
                        newSortedAddresses[i] = startAddresses[idx];
                        newSortedSectionNames[i] = associatedSectionNames[idx];

                        if (!isStackMemory)
                            newSortedSectionTypes[i] = associatedSectionType[idx];
                    }

                    using (var sortedIndice = new DynamicArray<long>(startAddresses.Count, Allocator.Temp))
                    {
                        UnsafeUtility.MemCpyStride(sortedIndice.GetUnsafePtr(), sizeof(long), sortMapping.GetUnsafePtr(), sizeof(SortIndexHelper), sizeof(SortIndexHelper), (int)startAddresses.Count);
                        associatedByteArrayReadOp.UnsafeAccessToNestedDynamicSizedArray.Sort(sortedIndice);
                    }

                    UnsafeUtility.MemCpy(startAddresses.GetUnsafePtr(), newSortedAddresses.GetUnsafePtr(), sizeof(ulong) * startAddresses.Count);
                    for (long i = 0; i < startAddresses.Count; ++i)
                    {
                        sizes[i] = (ulong)associatedByteArrayReadOp.UnsafeAccessToNestedDynamicSizedArray.Count(i);
                        if (!isStackMemory)
                            associatedSectionType[i] = newSortedSectionTypes[i];
                    }
                    associatedSectionNames = newSortedSectionNames;

                }
                sortMapping.Dispose();
            }

            public void Dispose()
            {
                Count = 0;
                m_MinAddress = m_MaxAddress = 0;
                StartAddress.Dispose();
                SectionType.Dispose();
                SectionSize.Dispose();
                SectionName = null;
                if (m_BytesReadOp != null)
                {
                    Bytes.Dispose();
                    m_BytesReadOp.Dispose();
                    m_BytesReadOp = null;
                }
            }
        }

        public class NativeAllocationEntriesCache : IDisposable
        {
            public long Count;
            public DynamicArray<int> MemoryRegionIndex = default;
            public DynamicArray<long> RootReferenceId = default;
            public DynamicArray<ulong> Address = default;
            public DynamicArray<ulong> Size = default;
            public DynamicArray<int> OverheadSize = default;
            public DynamicArray<int> PaddingSize = default;
            public DynamicArray<long> AllocationSiteId = default;

            public NativeAllocationEntriesCache(ref IFileReader reader, bool allocationSites /*do not read allocation sites if they aren't present*/)
            {
                Count = reader.GetEntryCount(EntryType.NativeAllocations_Address);

                if (Count == 0)
                    return;

                MemoryRegionIndex = reader.Read(EntryType.NativeAllocations_MemoryRegionIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                RootReferenceId = reader.Read(EntryType.NativeAllocations_RootReferenceId, 0, Count, Allocator.Persistent).Result.Reinterpret<long>();
                Address = reader.Read(EntryType.NativeAllocations_Address, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
                Size = reader.Read(EntryType.NativeAllocations_Size, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
                OverheadSize = reader.Read(EntryType.NativeAllocations_OverheadSize, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                PaddingSize = reader.Read(EntryType.NativeAllocations_PaddingSize, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();

                if (allocationSites)
                    AllocationSiteId = reader.Read(EntryType.NativeAllocations_AllocationSiteId, 0, Count, Allocator.Persistent).Result.Reinterpret<long>();
            }

            public void Dispose()
            {
                Count = 0;
                MemoryRegionIndex.Dispose();
                RootReferenceId.Dispose();
                Address.Dispose();
                Size.Dispose();
                OverheadSize.Dispose();
                PaddingSize.Dispose();
                AllocationSiteId.Dispose();
            }
        }
        public class NativeMemoryLabelEntriesCache : IDisposable
        {
            public long Count;
            public string[] MemoryLabelName;
            public DynamicArray<ulong> MemoryLabelSizes = default;

            public NativeMemoryLabelEntriesCache(ref IFileReader reader, bool hasLabelSizes)
            {
                Count = reader.GetEntryCount(EntryType.NativeMemoryLabels_Name);
                MemoryLabelName = new string[Count];

                if (Count == 0)
                    return;

                if (hasLabelSizes)
                    MemoryLabelSizes = reader.Read(EntryType.NativeMemoryLabels_Size, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();

                using (DynamicArray<byte> tmp = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpSize = reader.GetSizeForEntryRange(EntryType.NativeMemoryLabels_Name, 0, Count);
                    tmp.Resize(tmpSize, false);
                    reader.Read(EntryType.NativeMemoryLabels_Name, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref MemoryLabelName);
                }
            }

            public void Dispose()
            {
                Count = 0;
                MemoryLabelSizes.Dispose();
                MemoryLabelName = null;
            }

            public ulong GetLabelSize(string label)
            {
                if (Count <= 0)
                    return 0;

                var labelIndex = Array.IndexOf(MemoryLabelName, label);
                if (labelIndex == -1)
                    return 0;

                return MemoryLabelSizes[labelIndex];
            }
        }
        public class NativeMemoryRegionEntriesCache : IDisposable
        {
            public long Count;
            public string[] MemoryRegionName;
            public DynamicArray<int> ParentIndex = default;
            public DynamicArray<ulong> AddressBase = default;
            public DynamicArray<ulong> AddressSize = default;
            public DynamicArray<int> FirstAllocationIndex = default;
            public DynamicArray<int> NumAllocations = default;
            public readonly bool UsesDynamicHeapAllocator = false;
            public readonly bool UsesSystemAllocator;
            public readonly long SwitchGPUAllocatorIndex = -1;

            const string k_DynamicHeapAllocatorName = "ALLOC_DEFAULT_MAIN";
            const string k_SwitchGPUAllocatorName = "ALLOC_GPU";

            public NativeMemoryRegionEntriesCache(ref IFileReader reader)
            {
                Count = reader.GetEntryCount(EntryType.NativeMemoryRegions_AddressBase);
                MemoryRegionName = new string[Count];

                if (Count == 0)
                    return;

                ParentIndex = reader.Read(EntryType.NativeMemoryRegions_ParentIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                AddressBase = reader.Read(EntryType.NativeMemoryRegions_AddressBase, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
                AddressSize = reader.Read(EntryType.NativeMemoryRegions_AddressSize, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
                FirstAllocationIndex = reader.Read(EntryType.NativeMemoryRegions_FirstAllocationIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                NumAllocations = reader.Read(EntryType.NativeMemoryRegions_NumAllocations, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();

                using (DynamicArray<byte> tmp = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpSize = reader.GetSizeForEntryRange(EntryType.NativeMemoryRegions_Name, 0, Count);
                    tmp.Resize(tmpSize, false);
                    reader.Read(EntryType.NativeMemoryRegions_Name, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref MemoryRegionName);
                }

                for (long i = 0; i < Count; i++)
                {
                    if (!UsesDynamicHeapAllocator && AddressSize[i] > 0 && MemoryRegionName[i].StartsWith(k_DynamicHeapAllocatorName))
                    {
                        UsesDynamicHeapAllocator = true;
                    }

                    if (SwitchGPUAllocatorIndex == -1 && MemoryRegionName[i].Equals(k_SwitchGPUAllocatorName))
                    {
                        SwitchGPUAllocatorIndex = i;
                    }

                    // Nothing left to check if we've found an instance of both
                    if (UsesDynamicHeapAllocator && SwitchGPUAllocatorIndex != -1)
                        break;
                }
                if (Count > 0)
                    UsesSystemAllocator = !UsesDynamicHeapAllocator;
            }

            public void Dispose()
            {
                Count = 0;
                MemoryRegionName = null;
                ParentIndex.Dispose();
                AddressBase.Dispose();
                AddressSize.Dispose();
                FirstAllocationIndex.Dispose();
                NumAllocations.Dispose();
            }
        }
        public class NativeAllocationSiteEntriesCache : IDisposable
        {
            public long Count;
            public DynamicArray<long> id = default;
            public DynamicArray<int> memoryLabelIndex = default;

            public NestedDynamicArray<ulong> callstackSymbols => m_callstackSymbolsReadOp?.CompleteReadAndGetNestedResults() ?? default;
            NestedDynamicSizedArrayReadOperation<ulong> m_callstackSymbolsReadOp;

            unsafe public NativeAllocationSiteEntriesCache(ref IFileReader reader)
            {
                Count = reader.GetEntryCount(EntryType.NativeAllocationSites_Id);

                if (Count == 0)
                    return;

                id = reader.Read(EntryType.NativeAllocationSites_Id, 0, Count, Allocator.Persistent).Result.Reinterpret<long>();
                memoryLabelIndex = reader.Read(EntryType.NativeAllocationSites_MemoryLabelIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();

                m_callstackSymbolsReadOp = reader.AsyncReadDynamicSizedArray<ulong>(EntryType.NativeAllocationSites_CallstackSymbols, 0, Count, Allocator.Persistent);
            }

            public string GetReadableCallstackForId(NativeCallstackSymbolEntriesCache symbols, long id)
            {
                long entryIdx = -1;
                for (long i = 0; i < this.id.Count; ++i)
                {
                    if (this.id[i] == id)
                    {
                        entryIdx = i;
                        break;
                    }
                }

                return entryIdx < 0 ? string.Empty : GetReadableCallstack(symbols, entryIdx);
            }

            public string GetReadableCallstack(NativeCallstackSymbolEntriesCache symbols, long idx)
            {
                string readableStackTrace = "";

                var callstackSymbols = this.callstackSymbols[idx];

                for (int i = 0; i < callstackSymbols.Count; ++i)
                {
                    long symbolIdx = -1;
                    ulong targetSymbol = callstackSymbols[i];
                    for (int j = 0; j < symbols.Symbol.Count; ++i)
                    {
                        if (symbols.Symbol[j] == targetSymbol)
                        {
                            symbolIdx = i;
                            break;
                        }
                    }

                    if (symbolIdx < 0)
                        readableStackTrace += "<unknown>\n";
                    else
                        readableStackTrace += symbols.ReadableStackTrace[symbolIdx];
                }

                return readableStackTrace;
            }

            public void Dispose()
            {
                id.Dispose();
                memoryLabelIndex.Dispose();
                if (m_callstackSymbolsReadOp != null)
                {
                    callstackSymbols.Dispose();
                    m_callstackSymbolsReadOp.Dispose();
                    m_callstackSymbolsReadOp = null;
                }
                Count = 0;
            }
        }
        public class FieldDescriptionEntriesCache : IDisposable
        {
            public long Count;
            public string[] FieldDescriptionName;
            public DynamicArray<int> Offset = default;
            public DynamicArray<int> TypeIndex = default;
            public DynamicArray<byte> IsStatic = default;

            unsafe public FieldDescriptionEntriesCache(ref IFileReader reader)
            {
                Count = reader.GetEntryCount(EntryType.FieldDescriptions_Name);
                FieldDescriptionName = new string[Count];

                if (Count == 0)
                    return;

                Offset = new DynamicArray<int>(Count, Allocator.Persistent);
                TypeIndex = new DynamicArray<int>(Count, Allocator.Persistent);
                IsStatic = new DynamicArray<byte>(Count, Allocator.Persistent);

                Offset = reader.Read(EntryType.FieldDescriptions_Offset, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                TypeIndex = reader.Read(EntryType.FieldDescriptions_TypeIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                IsStatic = reader.Read(EntryType.FieldDescriptions_IsStatic, 0, Count, Allocator.Persistent).Result.Reinterpret<byte>();

                using (var tmp = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpBufferSize = reader.GetSizeForEntryRange(EntryType.FieldDescriptions_Name, 0, Count);
                    tmp.Resize(tmpBufferSize, false);
                    reader.Read(EntryType.FieldDescriptions_Name, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref FieldDescriptionName);
                }
            }

            public void Dispose()
            {
                Count = 0;
                Offset.Dispose();
                TypeIndex.Dispose();
                IsStatic.Dispose();
                FieldDescriptionName = null;
            }
        }
        public class TypeDescriptionEntriesCache : IDisposable
        {
            public const int ITypeInvalid = -1;
            const int k_DefaultFieldProcessingBufferSize = 64;
            public const string UnityObjectTypeName = "UnityEngine.Object";
            public const string UnityNativeObjectPointerFieldName = "m_CachedPtr";
            public int IFieldUnityObjectMCachedPtr { get; private set; }
            public int IFieldUnityObjectMCachedPtrOffset { get; private set; } = -1;

            const string k_UnityMonoBehaviourTypeName = "UnityEngine.MonoBehaviour";
            const string k_UnityScriptableObjectTypeName = "UnityEngine.ScriptableObject";
            const string k_UnityComponentObjectTypeName = "UnityEngine.Component";

            const string k_SystemObjectTypeName = "System.Object";
            const string k_SystemValueTypeName = "System.ValueType";
            const string k_SystemEnumTypeName = "System.Enum";

            const string k_SystemInt16Name = "System.Int16";
            const string k_SystemInt32Name = "System.Int32";
            const string k_SystemInt64Name = "System.Int64";

            const string k_SystemUInt16Name = "System.UInt16";
            const string k_SystemUInt32Name = "System.UInt32";

            const string k_SystemUInt64Name = "System.UInt64";
            const string k_SystemBoolName = "System.Boolean";
            const string k_SystemCharTypeName = "System.Char";
            const string k_SystemCharArrayTypeName = "System.Char[]";
            const string k_SystemDoubleName = "System.Double";
            const string k_SystemSingleName = "System.Single";
            const string k_SystemStringName = "System.String";
            const string k_SystemIntPtrName = "System.IntPtr";
            const string k_SystemByteName = "System.Byte";

            public int Count;
            public DynamicArray<TypeFlags> Flags = default;
            public DynamicArray<int> BaseOrElementTypeIndex = default;
            public DynamicArray<int> Size = default;
            public DynamicArray<ulong> TypeInfoAddress = default;
            //public DynamicArray<int> TypeIndex = default;

            public string[] TypeDescriptionName;
            public string[] Assembly;

            public NestedDynamicArray<int> FieldIndices => m_FieldIndicesReadOp?.CompleteReadAndGetNestedResults() ?? default;
            NestedDynamicSizedArrayReadOperation<int> m_FieldIndicesReadOp;
            public NestedDynamicArray<byte> StaticFieldBytes => m_StaticFieldBytesReadOp?.CompleteReadAndGetNestedResults() ?? default;
            NestedDynamicSizedArrayReadOperation<byte> m_StaticFieldBytesReadOp;

            //secondary data, handled inside InitSecondaryItems
            public int[][] FieldIndicesInstance;//includes all bases' instance fields
            public int[][] fieldIndicesStatic;  //includes all bases' static fields
            public int[][] fieldIndicesOwnedStatic;  //includes only type's static fields

            public int ITypeValueType { get; private set; }
            public int ITypeUnityObject { get; private set; }
            public int ITypeObject { get; private set; }
            public int ITypeEnum { get; private set; }
            public int ITypeInt16 { get; private set; }
            public int ITypeInt32 { get; private set; }
            public int ITypeInt64 { get; private set; }
            public int ITypeUInt16 { get; private set; }
            public int ITypeUInt32 { get; private set; }
            public int ITypeUInt64 { get; private set; }
            public int ITypeBool { get; private set; }
            public int ITypeChar { get; private set; }
            public int ITypeCharArray { get; private set; }
            public int ITypeDouble { get; private set; }
            public int ITypeSingle { get; private set; }
            public int ITypeString { get; private set; }
            public int ITypeIntPtr { get; private set; }
            public int ITypeByte { get; private set; }

            public int ITypeUnityMonoBehaviour { get; private set; }
            public int ITypeUnityScriptableObject { get; private set; }
            public int ITypeUnityComponent { get; private set; }
            public Dictionary<ulong, int> TypeInfoToArrayIndex { get; private set; }
            // only fully initialized after the Managed Crawler is done stitching up Objects. Might be better to be moved over to ManagedData
            public Dictionary<int, int> UnityObjectTypeIndexToNativeTypeIndex { get; private set; }
            public HashSet<int> PureCSharpTypeIndices { get; private set; }

            public TypeDescriptionEntriesCache(ref IFileReader reader, FieldDescriptionEntriesCache fieldDescriptions)
            {
                Count = (int)reader.GetEntryCount(EntryType.TypeDescriptions_TypeIndex);

                TypeDescriptionName = new string[Count];
                Assembly = new string[Count];

                if (Count == 0)
                    return;

                Flags = reader.Read(EntryType.TypeDescriptions_Flags, 0, Count, Allocator.Persistent).Result.Reinterpret<TypeFlags>();
                BaseOrElementTypeIndex = reader.Read(EntryType.TypeDescriptions_BaseOrElementTypeIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                Size = reader.Read(EntryType.TypeDescriptions_Size, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                TypeInfoAddress = reader.Read(EntryType.TypeDescriptions_TypeInfoAddress, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
#if DEBUG_VALIDATION
                if(reader.FormatVersion == FormatVersion.SnapshotMinSupportedFormatVersion)
                {
                    // Nb! This code is left here for posterity in case anyone wonders what EntryType.TypeDescriptions_TypeIndex is, and if it is needed. No it is not.

                    // After thorough archeological digging, there seems to be no evidence that this array was ever needed
                    // At the latest after FormatVersion.StreamingManagedMemoryCaptureFormatVersion (9) it is definitely not needed
                    // as the indices reported in this map exactly to the indices in the array

                    var TypeIndex = reader.Read(EntryType.TypeDescriptions_TypeIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                    for (int i = 0; i < TypeIndex.Count; i++)
                    {
                        if(i != TypeIndex[i])
                        {
                            Debug.LogError("Attempted to load a broken Snapshot file from an ancient Unity version!");
                            break;
                        }
                    }
                }
#endif

                using (DynamicArray<byte> tmp = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpSize = reader.GetSizeForEntryRange(EntryType.TypeDescriptions_Name, 0, Count);
                    tmp.Resize(tmpSize, false);
                    reader.Read(EntryType.TypeDescriptions_Name, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref TypeDescriptionName);

                    tmpSize = reader.GetSizeForEntryRange(EntryType.TypeDescriptions_Assembly, 0, Count);
                    tmp.Resize(tmpSize, false);
                    reader.Read(EntryType.TypeDescriptions_Assembly, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref Assembly);
                }

                m_FieldIndicesReadOp = reader.AsyncReadDynamicSizedArray<int>(EntryType.TypeDescriptions_FieldIndices, 0, Count, Allocator.Persistent);

                m_StaticFieldBytesReadOp = reader.AsyncReadDynamicSizedArray<byte>(EntryType.TypeDescriptions_StaticFieldBytes, 0, Count, Allocator.Persistent);

                //change to consume field descriptions instead
                InitSecondaryItems(this, fieldDescriptions);
            }

            // Check all bases' fields
            public bool HasAnyField(int iType)
            {
                return FieldIndicesInstance[iType].Length > 0 || fieldIndicesStatic[iType].Length > 0;
            }

            // Check all bases' fields
            public bool HasAnyStaticField(int iType)
            {
                return fieldIndicesStatic[iType].Length > 0;
            }

            // Check only the type's fields
            public bool HasStaticField(long iType)
            {
                return fieldIndicesOwnedStatic[iType].Length > 0;
            }

            /// <summary>
            /// Note: A Type may <see cref="HasStaticField"/> but no data for them, presumably because they haven't been initialized yet.
            /// </summary>
            /// <param name="iType"></param>
            /// <returns></returns>
            public bool HasStaticFieldData(long iType)
            {
                return StaticFieldBytes[iType].Count > 0;
            }

            public bool HasFlag(int arrayIndex, TypeFlags flag)
            {
                return (Flags[arrayIndex] & flag) == flag;
            }

            public int GetRank(int arrayIndex)
            {
                int r = (int)(Flags[arrayIndex] & TypeFlags.kArrayRankMask) >> 16;
                Checks.IsTrue(r >= 0);
                return r;
            }

            public int TypeInfo2ArrayIndex(UInt64 aTypeInfoAddress)
            {
                int i;

                if (!TypeInfoToArrayIndex.TryGetValue(aTypeInfoAddress, out i))
                {
                    return -1;
                }
                return i;
            }

            static readonly ProfilerMarker k_TypeFieldArraysBuild = new ProfilerMarker("MemoryProfiler.TypeFields.TypeFieldArrayBuilding");
            void InitSecondaryItems(TypeDescriptionEntriesCache typeDescriptionEntries, FieldDescriptionEntriesCache fieldDescriptions)
            {
                TypeInfoToArrayIndex = Enumerable.Range(0, (int)TypeInfoAddress.Count).ToDictionary(x => TypeInfoAddress[x], x => x);
                UnityObjectTypeIndexToNativeTypeIndex = new Dictionary<int, int>();
                PureCSharpTypeIndices = new HashSet<int>();


                ITypeUnityObject = Array.FindIndex(TypeDescriptionName, x => x == UnityObjectTypeName);
#if DEBUG_VALIDATION //This shouldn't really happen
                if (ITypeUnityObject < 0)
                {
                    throw new Exception("Unable to find UnityEngine.Object");
                }
#endif

                using (k_TypeFieldArraysBuild.Auto())
                {
                    FieldIndicesInstance = new int[Count][];
                    fieldIndicesStatic = new int[Count][];
                    fieldIndicesOwnedStatic = new int[Count][];
                    List<int> fieldProcessingBuffer = new List<int>(k_DefaultFieldProcessingBufferSize);

                    for (int i = 0; i < Count; ++i)
                    {
                        TypeTools.AllFieldArrayIndexOf(ref fieldProcessingBuffer, i, typeDescriptionEntries, fieldDescriptions, TypeTools.FieldFindOptions.OnlyInstance, true);
                        FieldIndicesInstance[i] = fieldProcessingBuffer.ToArray();

                        TypeTools.AllFieldArrayIndexOf(ref fieldProcessingBuffer, i, typeDescriptionEntries, fieldDescriptions, TypeTools.FieldFindOptions.OnlyStatic, true);
                        fieldIndicesStatic[i] = fieldProcessingBuffer.ToArray();

                        TypeTools.AllFieldArrayIndexOf(ref fieldProcessingBuffer, i, typeDescriptionEntries, fieldDescriptions, TypeTools.FieldFindOptions.OnlyStatic, false);
                        fieldIndicesOwnedStatic[i] = fieldProcessingBuffer.ToArray();

                        var typeIndex = i;
                        if (DerivesFromUnityObject(typeIndex))
                            UnityObjectTypeIndexToNativeTypeIndex.Add(typeIndex, -1);
                        else
                            PureCSharpTypeIndices.Add(typeIndex);
                    }
                }
                var fieldIndices = typeDescriptionEntries.FieldIndices[ITypeUnityObject];
                long fieldIndicesIndex = -1;
                for (long i = 0; i < fieldIndices.Count; i++)
                {
                    if (fieldDescriptions.FieldDescriptionName[fieldIndices[i]] == UnityNativeObjectPointerFieldName)
                    {
                        fieldIndicesIndex = i;
                        break;
                    }
                }

                IFieldUnityObjectMCachedPtr = fieldIndicesIndex >= 0 ? typeDescriptionEntries.FieldIndices[ITypeUnityObject][fieldIndicesIndex] : -1;

                IFieldUnityObjectMCachedPtrOffset = -1;

                if (IFieldUnityObjectMCachedPtr >= 0)
                {
                    IFieldUnityObjectMCachedPtrOffset = fieldDescriptions.Offset[IFieldUnityObjectMCachedPtr];
                }

#if DEBUG_VALIDATION
                if (IFieldUnityObjectMCachedPtrOffset < 0)
                {
                    Debug.LogWarning("Could not find unity object instance id field or m_CachedPtr");
                    return;
                }
#endif
                ITypeValueType = Array.FindIndex(TypeDescriptionName, x => x == k_SystemValueTypeName);
                ITypeObject = Array.FindIndex(TypeDescriptionName, x => x == k_SystemObjectTypeName);
                ITypeEnum = Array.FindIndex(TypeDescriptionName, x => x == k_SystemEnumTypeName);
                ITypeChar = Array.FindIndex(TypeDescriptionName, x => x == k_SystemCharTypeName);
                ITypeCharArray = Array.FindIndex(TypeDescriptionName, x => x == k_SystemCharArrayTypeName);
                ITypeInt16 = Array.FindIndex(TypeDescriptionName, x => x == k_SystemInt16Name);
                ITypeInt32 = Array.FindIndex(TypeDescriptionName, x => x == k_SystemInt32Name);
                ITypeInt64 = Array.FindIndex(TypeDescriptionName, x => x == k_SystemInt64Name);
                ITypeIntPtr = Array.FindIndex(TypeDescriptionName, x => x == k_SystemIntPtrName);
                ITypeString = Array.FindIndex(TypeDescriptionName, x => x == k_SystemStringName);
                ITypeBool = Array.FindIndex(TypeDescriptionName, x => x == k_SystemBoolName);
                ITypeSingle = Array.FindIndex(TypeDescriptionName, x => x == k_SystemSingleName);
                ITypeByte = Array.FindIndex(TypeDescriptionName, x => x == k_SystemByteName);
                ITypeDouble = Array.FindIndex(TypeDescriptionName, x => x == k_SystemDoubleName);
                ITypeUInt16 = Array.FindIndex(TypeDescriptionName, x => x == k_SystemUInt16Name);
                ITypeUInt32 = Array.FindIndex(TypeDescriptionName, x => x == k_SystemUInt32Name);
                ITypeUInt64 = Array.FindIndex(TypeDescriptionName, x => x == k_SystemUInt64Name);

                ITypeUnityMonoBehaviour = Array.FindIndex(TypeDescriptionName, x => x == k_UnityMonoBehaviourTypeName);
                ITypeUnityScriptableObject = Array.FindIndex(TypeDescriptionName, x => x == k_UnityScriptableObjectTypeName);
                ITypeUnityComponent = Array.FindIndex(TypeDescriptionName, x => x == k_UnityComponentObjectTypeName);
            }

            public bool DerivesFromUnityObject(int iTypeDescription)
            {
                while (iTypeDescription != ITypeUnityObject && iTypeDescription >= 0)
                {
                    if (HasFlag(iTypeDescription, TypeFlags.kArray))
                        return false;
                    iTypeDescription = BaseOrElementTypeIndex[iTypeDescription];
                }
                return iTypeDescription == ITypeUnityObject;
            }

            public bool DerivesFrom(int iTypeDescription, int potentialBase, bool excludeArrayElementBaseTypes)
            {
                while (iTypeDescription != potentialBase && iTypeDescription >= 0)
                {
                    if (excludeArrayElementBaseTypes && HasFlag(iTypeDescription, TypeFlags.kArray))
                        return false;
                    iTypeDescription = BaseOrElementTypeIndex[iTypeDescription];
                }

                return iTypeDescription == potentialBase;
            }

            public void Dispose()
            {
                Count = 0;
                Flags.Dispose();
                BaseOrElementTypeIndex.Dispose();
                Size.Dispose();
                TypeInfoAddress.Dispose();
                TypeDescriptionName = null;
                Assembly = null;
                if (m_FieldIndicesReadOp != null)
                {
                    FieldIndices.Dispose();
                    m_FieldIndicesReadOp.Dispose();
                    m_FieldIndicesReadOp = null;
                }
                if (m_StaticFieldBytesReadOp != null)
                {
                    StaticFieldBytes.Dispose();
                    m_StaticFieldBytesReadOp.Dispose();
                    m_StaticFieldBytesReadOp = null;
                }

                FieldIndicesInstance = null;
                fieldIndicesStatic = null;
                fieldIndicesOwnedStatic = null;
                ITypeValueType = ITypeInvalid;
                ITypeObject = ITypeInvalid;
                ITypeEnum = ITypeInvalid;
                TypeInfoToArrayIndex = null;
                UnityObjectTypeIndexToNativeTypeIndex = null;
                PureCSharpTypeIndices = null;
            }
        }
        public unsafe class NativeTypeEntriesCache : IDisposable
        {
            public long Count;
            public string[] TypeName;
            public DynamicArray<int> NativeBaseTypeArrayIndex = default;
            const string k_Transform = "Transform";
            public int TransformIdx { get; private set; } = -1;

            const string k_GameObject = "GameObject";
            public int GameObjectIdx { get; private set; } = -1;

            const string k_MonoBehaviour = "MonoBehaviour";
            public int MonoBehaviourIdx { get; private set; } = -1;

            const string k_Component = "Component";
            public int ComponentIdx { get; private set; } = -1;

            const string k_ScriptableObject = "ScriptableObject";
            const int k_ScriptableObjectDefaultTypeArrayIndexOffsetFromEnd = 2;
            public int ScriptableObjectIdx { get; private set; } = -1;

            const string k_EditorScriptableObject = "EditorScriptableObject";
            public int EditorScriptableObjectIdx { get; private set; } = -1;
            const int k_EditorScriptableObjectDefaultTypeArrayIndexOffsetFromEnd = 1;

            public NativeTypeEntriesCache(ref IFileReader reader)
            {
                Count = reader.GetEntryCount(EntryType.NativeTypes_Name);
                TypeName = new string[Count];

                if (Count == 0)
                    return;

                NativeBaseTypeArrayIndex = reader.Read(EntryType.NativeTypes_NativeBaseTypeArrayIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                using (DynamicArray<byte> tmp = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpSize = reader.GetSizeForEntryRange(EntryType.NativeTypes_Name, 0, Count);
                    tmp.Resize(tmpSize, false);
                    reader.Read(EntryType.NativeTypes_Name, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref TypeName);
                }

                TransformIdx = Array.FindIndex(TypeName, x => x == k_Transform);
                GameObjectIdx = Array.FindIndex(TypeName, x => x == k_GameObject);
                MonoBehaviourIdx = Array.FindIndex(TypeName, x => x == k_MonoBehaviour);
                ComponentIdx = Array.FindIndex(TypeName, x => x == k_Component);

                // for the fakable types ScriptableObject and EditorScriptable Objects, with the current backend, Array.FindIndex is always going to hit the worst case
                // in the current format, these types are always added last. Assume that for speed, keep Array.FindIndex as fallback in case the format changes
                ScriptableObjectIdx = FindTypeWithHint(k_ScriptableObject, Count - k_ScriptableObjectDefaultTypeArrayIndexOffsetFromEnd);
                EditorScriptableObjectIdx = FindTypeWithHint(k_EditorScriptableObject, Count - k_EditorScriptableObjectDefaultTypeArrayIndexOffsetFromEnd);
            }

            int FindTypeWithHint(string typeName, long hintAtLikelyIndex)
            {
                if (TypeName[hintAtLikelyIndex] == typeName)
                    return (int)hintAtLikelyIndex;
                else
                    return Array.FindIndex(TypeName, x => x == typeName);
            }

            public bool DerivesFrom(int typeIndexToCheck, int baseTypeToCheckAgainst)
            {
                while (typeIndexToCheck != baseTypeToCheckAgainst && NativeBaseTypeArrayIndex[typeIndexToCheck] >= 0)
                {
                    typeIndexToCheck = NativeBaseTypeArrayIndex[typeIndexToCheck];
                }
                return typeIndexToCheck == baseTypeToCheckAgainst;
            }

            public void Dispose()
            {
                Count = 0;
                NativeBaseTypeArrayIndex.Dispose();
                TypeName = null;
            }
        }
        public class NativeRootReferenceEntriesCache : IDisposable
        {
            public long Count;
            public DynamicArray<long> Id = default;
            public DynamicArray<ulong> AccumulatedSize = default;
            public string[] AreaName;
            public string[] ObjectName;
            public Dictionary<long, long> IdToIndex;
            public readonly ulong ExecutableAndDllsReportedValue;
            public const string ExecutableAndDllsRootReferenceName = "ExecutableAndDlls";
            readonly long k_ExecutableAndDllsRootReferenceIndex = -1;

            public NativeRootReferenceEntriesCache(ref IFileReader reader)
            {
                Count = reader.GetEntryCount(EntryType.NativeRootReferences_Id);

                AreaName = new string[Count];
                ObjectName = new string[Count];

                IdToIndex = new Dictionary<long, long>((int)Count);

                if (Count == 0)
                    return;

                Id = reader.Read(EntryType.NativeRootReferences_Id, 0, Count, Allocator.Persistent).Result.Reinterpret<long>();
                AccumulatedSize = reader.Read(EntryType.NativeRootReferences_AccumulatedSize, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();

                using (var tmpBuffer = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpSize = reader.GetSizeForEntryRange(EntryType.NativeRootReferences_AreaName, 0, Count);
                    tmpBuffer.Resize(tmpSize, false);
                    reader.Read(EntryType.NativeRootReferences_AreaName, tmpBuffer, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmpBuffer, ref AreaName);

                    tmpSize = reader.GetSizeForEntryRange(EntryType.NativeRootReferences_ObjectName, 0, Count);
                    tmpBuffer.Resize(tmpSize, false);
                    reader.Read(EntryType.NativeRootReferences_ObjectName, tmpBuffer, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmpBuffer, ref ObjectName);
                }
                for (long i = 0; i < Count; i++)
                {
                    if (k_ExecutableAndDllsRootReferenceIndex == -1 && ObjectName[i] == ExecutableAndDllsRootReferenceName)
                    {
                        k_ExecutableAndDllsRootReferenceIndex = i;
                        ExecutableAndDllsReportedValue = AccumulatedSize[i];
                    }
                    IdToIndex.Add(Id[i], i);
                }
            }

            public void Dispose()
            {
                Id.Dispose();
                AccumulatedSize.Dispose();
                Count = 0;
                AreaName = null;
                ObjectName = null;
                IdToIndex = null;
            }
        }
        public class NativeObjectEntriesCache : IDisposable
        {
            public const int InstanceIDNone = 0;

            public long Count;
            public string[] ObjectName;
            public DynamicArray<int> InstanceId = default;
            public DynamicArray<ulong> Size = default;
            public DynamicArray<int> NativeTypeArrayIndex = default;
            public DynamicArray<HideFlags> HideFlags = default;
            public DynamicArray<ObjectFlags> Flags = default;
            public DynamicArray<ulong> NativeObjectAddress = default;
            public DynamicArray<long> RootReferenceId = default;
            public DynamicArray<int> ManagedObjectIndex = default;

            //secondary data
            public DynamicArray<int> RefCount = default;
            public Dictionary<ulong, int> NativeObjectAddressToInstanceId { private set; get; }
            public Dictionary<long, int> RootReferenceIdToIndex { private set; get; }
            public SortedDictionary<int, int> InstanceId2Index;

            public readonly ulong TotalSizes = 0ul;
            DynamicArray<int> MetaDataBufferIndicies = default;
            NestedDynamicArray<byte> MetaDataBuffers => m_MetaDataBuffersReadOp?.CompleteReadAndGetNestedResults() ?? default;
            NestedDynamicSizedArrayReadOperation<byte> m_MetaDataBuffersReadOp;

            unsafe public NativeObjectEntriesCache(ref IFileReader reader)
            {
                Count = reader.GetEntryCount(EntryType.NativeObjects_InstanceId);
                NativeObjectAddressToInstanceId = new Dictionary<ulong, int>((int)Count);
                RootReferenceIdToIndex = new Dictionary<long, int>((int)Count);
                InstanceId2Index = new SortedDictionary<int, int>();
                ObjectName = new string[Count];

                if (Count == 0)
                    return;

                InstanceId = reader.Read(EntryType.NativeObjects_InstanceId, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                Size = reader.Read(EntryType.NativeObjects_Size, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
                NativeTypeArrayIndex = reader.Read(EntryType.NativeObjects_NativeTypeArrayIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                HideFlags = reader.Read(EntryType.NativeObjects_HideFlags, 0, Count, Allocator.Persistent).Result.Reinterpret<HideFlags>();
                Flags = reader.Read(EntryType.NativeObjects_Flags, 0, Count, Allocator.Persistent).Result.Reinterpret<ObjectFlags>();
                NativeObjectAddress = reader.Read(EntryType.NativeObjects_NativeObjectAddress, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
                RootReferenceId = reader.Read(EntryType.NativeObjects_RootReferenceId, 0, Count, Allocator.Persistent).Result.Reinterpret<long>();
                ManagedObjectIndex = reader.Read(EntryType.NativeObjects_GCHandleIndex, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                RefCount = new DynamicArray<int>(Count, Allocator.Persistent, true);

                using (var tmp = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpSize = reader.GetSizeForEntryRange(EntryType.NativeObjects_Name, 0, Count);
                    tmp.Resize(tmpSize, false);
                    reader.Read(EntryType.NativeObjects_Name, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref ObjectName);
                }

                for (long i = 0; i < NativeObjectAddress.Count; ++i)
                {
                    var id = InstanceId[i];
                    NativeObjectAddressToInstanceId.Add(NativeObjectAddress[i], id);
                    RootReferenceIdToIndex.Add(RootReferenceId[i], (int)i);
                    InstanceId2Index[id] = (int)i;
                    TotalSizes += Size[i];
                }

                //fallback for the legacy snapshot formats
                //create the managedObjectIndex array and make it -1 on each entry so they can be overridden during crawling
                //TODO: remove this when the new crawler lands :-/
                if (reader.FormatVersion < FormatVersion.NativeConnectionsAsInstanceIdsVersion)
                {
                    ManagedObjectIndex.Dispose();
                    ManagedObjectIndex = new DynamicArray<int>(Count, Allocator.Persistent);
                    for (int i = 0; i < Count; ++i)
                        ManagedObjectIndex[i] = -1;
                }

                // handle formats tht have the new metadata added for native objects
                if (reader.FormatVersion >= FormatVersion.NativeObjectMetaDataVersion)
                {
                    //get the array that tells us how to index the buffers for the actual meta data
                    MetaDataBufferIndicies = reader.Read(EntryType.ObjectMetaData_MetaDataBufferIndicies, 0, Count, Allocator.Persistent).Result.Reinterpret<int>();
                    // loop the array and get the total number of entries we need
                    int sum = 0;
                    for (int i = 0; i < MetaDataBufferIndicies.Count; i++)
                    {
                        if (MetaDataBufferIndicies[i] != -1)
                            sum++;
                    }

                    m_MetaDataBuffersReadOp = reader.AsyncReadDynamicSizedArray<byte>(EntryType.ObjectMetaData_MetaDataBuffer, 0, sum, Allocator.Persistent);
                }
            }

            public ILongIndexedContainer<byte> MetaData(int nativeObjectIndex)
            {
                if (MetaDataBufferIndicies.Count == 0) return default;
                var bufferIndex = MetaDataBufferIndicies[nativeObjectIndex];
                if (bufferIndex == -1) return default(DynamicArrayRef<byte>);

                return MetaDataBuffers[bufferIndex];
            }

            public void Dispose()
            {
                Count = 0;
                InstanceId.Dispose();
                Size.Dispose();
                NativeTypeArrayIndex.Dispose();
                HideFlags.Dispose();
                Flags.Dispose();
                NativeObjectAddress.Dispose();
                RootReferenceId.Dispose();
                ManagedObjectIndex.Dispose();
                RefCount.Dispose();
                ObjectName = null;
                NativeObjectAddressToInstanceId = null;
                RootReferenceIdToIndex = null;
                InstanceId2Index = null;
                MetaDataBufferIndicies.Dispose();
                if (m_MetaDataBuffersReadOp != null)
                {
                    MetaDataBuffers.Dispose();
                    m_MetaDataBuffersReadOp.Dispose();
                    m_MetaDataBuffersReadOp = null;
                }
            }
        }
        
        public class NativeCallstackSymbolEntriesCache : IDisposable
        {
            public long Count;
            public DynamicArray<ulong> Symbol = default;
            public string[] ReadableStackTrace;

            public NativeCallstackSymbolEntriesCache(ref IFileReader reader)
            {
                Count = reader.GetEntryCount(EntryType.NativeCallstackSymbol_Symbol);
                ReadableStackTrace = new string[Count];

                if (Count == 0)
                    return;

                Symbol = reader.Read(EntryType.NativeCallstackSymbol_Symbol, 0, Count, Allocator.Persistent).Result.Reinterpret<ulong>();
                using (DynamicArray<byte> tmp = new DynamicArray<byte>(0, Allocator.TempJob))
                {
                    var tmpSize = reader.GetSizeForEntryRange(EntryType.NativeCallstackSymbol_ReadableStackTrace, 0, Count);
                    tmp.Resize(tmpSize, false);
                    reader.Read(EntryType.NativeCallstackSymbol_ReadableStackTrace, tmp, 0, Count);
                    ConvertDynamicArrayByteBufferToManagedArray(tmp, ref ReadableStackTrace);
                }
            }

            public void Dispose()
            {
                Count = 0;
                Symbol.Dispose();
                ReadableStackTrace = null;
            }
        }

        public MemorySnapshot(IFileReader reader)
        {
            unsafe
            {
                Tools.VirtualMachineInformation vmInfo;
                reader.ReadUnsafe(EntryType.Metadata_VirtualMachineInformation, &vmInfo, UnsafeUtility.SizeOf<VirtualMachineInformation>(), 0, 1);

                if (!VMTools.ValidateVirtualMachineInfo(vmInfo))
                {
                    throw new UnityException("Invalid VM info. Snapshot file is corrupted.");
                }

                m_Reader = reader;
                long ticks;
                reader.ReadUnsafe(EntryType.Metadata_RecordDate, &ticks, UnsafeUtility.SizeOf<long>(), 0, 1);
                TimeStamp = new DateTime(ticks);

                VirtualMachineInformation = vmInfo;
                m_SnapshotVersion = reader.FormatVersion;

                MetaData = new MetaData(reader);

                NativeAllocationSites = new NativeAllocationSiteEntriesCache(ref reader);
                FieldDescriptions = new FieldDescriptionEntriesCache(ref reader);
                TypeDescriptions = new TypeDescriptionEntriesCache(ref reader, FieldDescriptions);
                NativeTypes = new NativeTypeEntriesCache(ref reader);
                NativeRootReferences = new NativeRootReferenceEntriesCache(ref reader);
                NativeObjects = new NativeObjectEntriesCache(ref reader);
                NativeMemoryRegions = new NativeMemoryRegionEntriesCache(ref reader);
                NativeMemoryLabels = new NativeMemoryLabelEntriesCache(ref reader, HasMemoryLabelSizesAndGCHeapTypes);
                NativeCallstackSymbols = new NativeCallstackSymbolEntriesCache(ref reader);
                NativeAllocations = new NativeAllocationEntriesCache(ref reader, NativeAllocationSites.Count != 0);
                ManagedStacks = new ManagedMemorySectionEntriesCache(ref reader, false, true);
                // ManagedHeapSections = new ManagedMemorySectionEntriesCache(ref reader, HasMemoryLabelSizesAndGCHeapTypes, false);
                // GcHandles = new GCHandleEntriesCache(ref reader);
                // Connections = new ConnectionEntriesCache(ref reader, NativeObjects, GcHandles.Count, HasConnectionOverhaul);
                // SceneRoots = new SceneRootEntriesCache(ref reader);
                // NativeGfxResourceReferences = new NativeGfxResourcReferenceEntriesCache(ref reader);
                // NativeAllocators = new NativeAllocatorEntriesCache(ref reader);
                //
                // SystemMemoryRegions = new SystemMemoryRegionEntriesCache(ref reader);
                // SystemMemoryResidentPages = new SystemMemoryResidentPagesEntriesCache(ref reader);
                //
                // SortedManagedObjects = new SortedManagedObjectsCache(this);
                //
                // SortedNativeRegionsEntries = new SortedNativeMemoryRegionEntriesCache(this);
                // SortedNativeAllocations = new SortedNativeAllocationsCache(this);
                // SortedNativeObjects = new SortedNativeObjectsCache(this);
                //
                // EntriesMemoryMap = new EntriesMemoryMapCache(this);
                //
                // CrawledData = new ManagedData(GcHandles.Count, Connections.Count);
                // if (MemoryProfilerSettings.FeatureFlags.GenerateTransformTreesForByStatusTable_2022_09)
                //     SceneRoots.CreateTransformTrees(this);
                // SceneRoots.GenerateGameObjectData(this);
            }
        }
        unsafe static void ConvertDynamicArrayByteBufferToManagedArray<T>(DynamicArray<byte> nativeEntryBuffer, ref T[] elements) where T : class
        {
            byte* binaryDataStream = nativeEntryBuffer.GetUnsafeTypedPtr();
            //jump over the offsets array
            long* binaryEntriesLength = (long*)binaryDataStream;
            binaryDataStream = binaryDataStream + sizeof(long) * (elements.Length + 1); //+1 due to the final element offset being at the end

            for (int i = 0; i < elements.Length; ++i)
            {
                byte* srcPtr = binaryDataStream + binaryEntriesLength[i];
                long actualLength = binaryEntriesLength[i + 1] - binaryEntriesLength[i];

                if (typeof(T) == typeof(string))
                {
                    var intLength = Convert.ToInt32(actualLength);
                    elements[i] = new string(unchecked((sbyte*)srcPtr), 0, intLength, System.Text.Encoding.UTF8) as T;
                }
                else if (typeof(T) == typeof(BitArray))
                {
                    byte[] temp = new byte[actualLength];
                    fixed (byte* dstPtr = temp)
                        UnsafeUtility.MemCpy(dstPtr, srcPtr, actualLength);

                    var arr = new BitArray(temp);
                    elements[i] = arr as T;
                }
                else
                {
                    Debug.LogError($"Use {nameof(NestedDynamicArray<byte>)} instead");
                    if (typeof(T) == typeof(byte[]))
                    {
                        var arr = new byte[actualLength];
                        fixed (void* dstPtr = arr)
                            UnsafeUtility.MemCpy(dstPtr, srcPtr, actualLength);
                        elements[i] = arr as T;
                    }
                    else if (typeof(T) == typeof(int[]))
                    {
                        var arr = new int[actualLength / UnsafeUtility.SizeOf<int>()];
                        fixed (void* dstPtr = arr)
                            UnsafeUtility.MemCpy(dstPtr, srcPtr, actualLength);
                        elements[i] = arr as T;
                    }
                    else if (typeof(T) == typeof(ulong[]))
                    {
                        var arr = new ulong[actualLength / UnsafeUtility.SizeOf<ulong>()];
                        fixed (void* dstPtr = arr)
                            UnsafeUtility.MemCpy(dstPtr, srcPtr, actualLength);
                        elements[i] = arr as T;
                    }
                    else if (typeof(T) == typeof(long[]))
                    {
                        var arr = new long[actualLength / UnsafeUtility.SizeOf<long>()];
                        fixed (void* dstPtr = arr)
                            UnsafeUtility.MemCpy(dstPtr, srcPtr, actualLength);
                        elements[i] = arr as T;
                    }
                    else
                    {
                        Debug.LogErrorFormat("Unsuported type provided for conversion, type name: {0}", typeof(T).FullName);
                    }
                }
            }
        }

    }
}