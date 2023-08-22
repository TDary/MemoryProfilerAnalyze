using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace MemoryAnalyze
{
    public partial class PackedMemorySnapshot
    {
        public PackedMemorySnapshotHeader header = new PackedMemorySnapshotHeader();

        // Descriptions of all the C++ unity types the profiled player knows about.
        public PackedNativeType[] nativeTypes = new PackedNativeType[0];

        // All native C++ objects that were loaded at time of the snapshot.
        public PackedNativeUnityEngineObject[] nativeObjects = new PackedNativeUnityEngineObject[0];

        // All GC handles in use in the memorysnapshot.
        public PackedGCHandle[] gcHandles = new PackedGCHandle[0];

        // The unmodified connections array of "from -> to" pairs that describe which things are keeping which other things alive.
        // connections 0..gcHandles.Length-1 represent connections FROM gchandles
        // connections gcHandles.Length..connections.Length-1 represent connections FROM native
        public PackedConnection[] connections = new PackedConnection[0];

        // Array of actual managed heap memory sections. These are sorted by address after snapshot initialization.
        public PackedMemorySection[] managedHeapSections = new PackedMemorySection[0];

        // Descriptions of all the managed types that were known to the virtual machine when the snapshot was taken.
        public PackedManagedType[] managedTypes = new PackedManagedType[0];

        // Information about the virtual machine running executing the managade code inside the player.
        public PackedVirtualMachineInformation virtualMachineInformation = new PackedVirtualMachineInformation();
        public static PackedMemorySnapshot GetMemoryProfilerData(PackedMemorySnapshotArgs args)
        {
            var source = args.source;

            var value = new PackedMemorySnapshot();
            try
            {
                VerifyMemoryProfilerSnapshot(source);
                value.header = PackedMemorySnapshotHeader.FromMemoryProfiler();
                value.nativeTypes = PackedNativeType.FromMemoryProfiler(source);
                value.nativeObjects = PackedNativeUnityEngineObject.FromMemoryProfiler(source);
                value.gcHandles = PackedGCHandle.FromMemoryProfiler(source);
                value.connections = PackedConnection.FromMemoryProfiler(source);
                value.managedHeapSections = PackedMemorySection.FromMemoryProfiler(source);
                value.managedTypes = PackedManagedType.FromMemoryProfiler(source);
                value.virtualMachineInformation = PackedVirtualMachineInformation.FromMemoryProfiler(source);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                value = null;
                throw;
            }
            return value;
        }
        static void VerifyMemoryProfilerSnapshot(UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot snapshot)
        {
            if (snapshot == null)
                throw new Exception("No snapshot was found.");

            if (snapshot.typeDescriptions == null || snapshot.typeDescriptions.GetNumEntries() == 0)
                throw new Exception("The snapshot does not contain any 'typeDescriptions'. This is a known issue when using .NET 4.x Scripting Runtime.\n(Case 1079363) PackedMemorySnapshot: .NET 4.x Scripting Runtime breaks memory snapshot");

            if (snapshot.managedHeapSections == null || snapshot.managedHeapSections.GetNumEntries() == 0)
                throw new Exception("The snapshot does not contain any 'managedHeapSections'. This is a known issue when using .NET 4.x Scripting Runtime.\n(Case 1079363) PackedMemorySnapshot: .NET 4.x Scripting Runtime breaks memory snapshot");

            if (snapshot.gcHandles == null || snapshot.gcHandles.GetNumEntries() == 0)
                throw new Exception("The snapshot does not contain any 'gcHandles'. This is a known issue when using .NET 4.x Scripting Runtime.\n(Case 1079363) PackedMemorySnapshot: .NET 4.x Scripting Runtime breaks memory snapshot");

            if (snapshot.nativeTypes == null || snapshot.nativeTypes.GetNumEntries() == 0)
                throw new Exception("The snapshot does not contain any 'nativeTypes'.");

            if (snapshot.nativeObjects == null || snapshot.nativeObjects.GetNumEntries() == 0)
                throw new Exception("The snapshot does not contain any 'nativeObjects'.");

            if (snapshot.connections == null || snapshot.connections.GetNumEntries() == 0)
                throw new Exception("The snapshot does not contain any 'connections'.");
        }
        public class PackedMemorySnapshotArgs
        {
            public UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot source;
        }
    }
}
