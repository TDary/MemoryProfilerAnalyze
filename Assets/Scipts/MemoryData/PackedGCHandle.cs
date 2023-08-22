using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MemoryAnalyze
{
    public struct PackedGCHandle
    {
        // The address of the managed object that the GC handle is referencing.
        public System.UInt64 target;

        [NonSerialized] public System.Int32 gcHandlesArrayIndex;
        [NonSerialized] public System.Int32 managedObjectsArrayIndex;

        const System.Int32 k_Version = 1;

        public static void Write(System.IO.BinaryWriter writer, PackedGCHandle[] value)
        {
            writer.Write(k_Version);
            writer.Write(value.Length);

            for (int n = 0, nend = value.Length; n < nend; ++n)
            {
                writer.Write(value[n].target);
            }
        }

        public static void Read(System.IO.BinaryReader reader, out PackedGCHandle[] value, out string stateString)
        {
            value = new PackedGCHandle[0];
            stateString = "";

            var version = reader.ReadInt32();
            if (version >= 1)
            {
                var length = reader.ReadInt32();
                stateString = string.Format("Loading {0} GC Handles", length);
                value = new PackedGCHandle[length];

                for (int n = 0, nend = value.Length; n < nend; ++n)
                {
                    value[n].target = reader.ReadUInt64();
                    value[n].gcHandlesArrayIndex = n;
                    value[n].managedObjectsArrayIndex = -1;
                }
            }
        }

        public static PackedGCHandle[] FromMemoryProfiler(UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot snapshot)
        {
            var source = snapshot.gcHandles;
            var value = new PackedGCHandle[source.GetNumEntries()];

            var sourceTargets = new ulong[source.target.GetNumEntries()];
            source.target.GetEntries(0, source.target.GetNumEntries(), ref sourceTargets);

            for (int n = 0, nend = value.Length; n < nend; ++n)
            {
                value[n] = new PackedGCHandle
                {
                    target = sourceTargets[n],
                    gcHandlesArrayIndex = n,
                    managedObjectsArrayIndex = -1,
                };
            }
            return value;
        }
    }
}
