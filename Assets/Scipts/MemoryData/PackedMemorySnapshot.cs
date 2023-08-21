using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MemoryAnalyze
{
    public class PackedMemorySnapshot
    {
        /// <summary>
        /// An array of 4096bytes aligned memory sections. These appear to me the actual managed memory sections.
        /// non-aligned sections seem to be internal / MonoMemPool sections.
        /// </summary>
        /// <see cref="https://forum.unity.com/threads/wip-heap-explorer-memory-profiler-debugger-and-analyzer-for-unity.527949/page-3#post-3902371"/>
        public PackedMemorySection[] alignedManagedHeapSections = new PackedMemorySection[0];

        /// <summary>
        /// An array of extracted managed objects from the managed heap memory.
        /// </summary>
        public PackedManagedObject[] managedObjects = new PackedManagedObject[0];

        /// <summary>
        /// An array of managed static fields.
        /// </summary>
        public PackedManagedStaticField[] managedStaticFields = new PackedManagedStaticField[0];

        /// <summary>
        /// Indices into the managedTypes array of static types.
        /// </summary>
        public int[] managedStaticTypes = new int[0];

        /// <summary>
        /// Indices into the connections array.
        /// </summary>
        public int[] connectionsToMonoScripts = new int[0];

        /// <summary>
        /// CoreTypes is a helper class that contains indices to frequently used classes, such as MonoBehaviour.
        /// </summary>
        public PackedCoreTypes coreTypes = new PackedCoreTypes();
    }
}
