using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using static MemoryAnalyze.PackedMemorySnapshot;

namespace MemoryAnalyze
{
    public class AnalyzeEntrance:UnityEditor.Editor
    {
        private string SnapFilePath;
        [MenuItem("Test/Memory")]
        static void Init()
        {
            string snapFilePath = "E:\\MemoryFroGitlab\\MemoryClient\\MemoryCaptures\\332785-格纳库-1080.snap";
            var args = new PackedMemorySnapshotArgs();
            args.source = UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot.Load(snapFilePath);
            var heap = PackedMemorySnapshot.GetMemoryProfilerData(args);
        }
    }
}
