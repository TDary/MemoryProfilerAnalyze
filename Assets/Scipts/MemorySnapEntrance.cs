using System;
using Scipts.AnalyzeDatas;
using Scipts.FileData;
using UnityEditor;
using UnityEngine;

namespace Scipts
{
    public class MemorySnapEntrance:EditorWindow
    {
        private static MemorySnapEntrance Instance;

        [MenuItem("Test/Analyze")]
        public static void MemoryAnalyze()
        {
            if (Instance == null)
            {
                Instance = ScriptableObject.CreateInstance<MemorySnapEntrance>();
            }
            Instance.Show();
        }

        private void OnGUI()
        {
            if (GUILayout.Button("测试一下解析Snap文件"))
            {
                string snapFilePath = "D:/405624-UI遍历-稳定性-初始-2080-1704229976.snap";
                var reader = new FileReader();
                ReadError err = reader.Open(snapFilePath);
                if (err != ReadError.Success)
                {
                    // Close and dispose the reader
                    reader.Close();
                    return;
                }
                
                var cachedSnapshot = new MemorySnapshot(reader);
                
            }
        }
    }
}