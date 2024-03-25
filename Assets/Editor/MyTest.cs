using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading;
using DataAO;
using LitJson;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public class MyTest:EditorWindow
    {
        private static MyTest Instance;
        private string filePath;
        private Action<string, bool, DebugScreenCapture> screenshotCaptureFunc = null;
        [MenuItem("Tool/MemorySnap")]
        public static void GetMemory()
        {
            if (Instance == null)
            {
                Instance = ScriptableObject.CreateInstance<MyTest>();
            }
            Instance.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("请输入要读取的快照文件路径：");
            filePath = EditorGUILayout.TextField(filePath);
            
            if (GUILayout.Button("Import内存快照"))
            { 
                EditorApplication.ExecuteMenuItem("Window/Analysis/Memory Profiler");
                var windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();
                windows.SnapshotDataService.Import(filePath);
                windows.SnapshotDataService.Load(filePath);
                if (windows.SnapshotDataService.Base.Valid)
                {
                    Debug.Log("Yes");
                }
                else
                {
                    Debug.Log("No");
                }
            }

            if (GUILayout.Button("Load内存快照"))
            {
                EditorApplication.ExecuteMenuItem("Window/Analysis/Memory Profiler");
                var windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();
                windows.SnapshotDataService.Load(filePath);
                if (windows.SnapshotDataService.Base.Valid)
                {
                    Debug.Log("Yes");
                }
                else
                {
                    Debug.Log("No");
                }
            }

            if (GUILayout.Button("导出Summary数据"))
            {
                var windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();
                var data = windows.ProfilerViewController.GetMemoryData("summary");
                WriteToFile("D:/SummaryData.json", data);
            }

            if (GUILayout.Button("导出UnityObjects数据"))
            {
                var windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();
                var data = windows.ProfilerViewController.GetMemoryData("unityObjects");
                WriteToFile("D:/UnityObjects.json", data);
            }

            if (GUILayout.Button("导出所有MemoryData数据"))
            {
                var windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();
                var data = windows.ProfilerViewController.GetMemoryData("allMemory");
                WriteToFile("D:/AllMemoryData.json", data);
            }

            if (GUILayout.Button("切换界面"))
            {
                var windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();  //切换界面以更新这些数据
                windows.ProfilerViewController.SetTabBarView(2);
                windows.ProfilerViewController.SetTabBarView(1);
            }

            if (GUILayout.Button("UnLoad"))
            {
                var windows =  EditorWindow.GetWindow<Unity.MemoryProfiler.Editor.MemoryProfilerWindow>();
                windows.SnapshotDataService.UnloadAll();
            }

            if (GUILayout.Button("Test"))
            {
                string test = "D:\\TestSnap\\Memory-6196";
                Debug.Log(Path.GetDirectoryName(test));
            }

            if (GUILayout.Button("反序列化测试"))
            {
                var nums =new []{-2,1,-3,4,-1,2,1,-5,4};
                int pre = 0, maxAns = nums[0];
                foreach (int x in nums) {
                    pre = Math.Max(pre + x, x);
                    maxAns = Math.Max(maxAns, pre);
                }

                Debug.Log(maxAns);
            }

            if (GUILayout.Button("测试"))
            {
                screenshotCaptureFunc = (string path, bool result, DebugScreenCapture screenCapture) =>
                {
                    if (screenCapture.RawImageDataReference.Length == 0)
                        return;
                
                    if (Path.HasExtension(path))
                    {
                        path = Path.ChangeExtension(path, ".png");
                    }
                
                    Texture2D tex = new Texture2D(screenCapture.Width, screenCapture.Height, screenCapture.ImageFormat, false);
                    CopyDataToTexture(tex, screenCapture.RawImageDataReference);
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    if (Application.isPlaying)
                        UnityEngine.Object.Destroy(tex);
                    else
                        UnityEngine.Object.DestroyImmediate(tex);
                };
                Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot("D:/newTest.snap", MemorySnapshotCallback,
                    screenshotCaptureFunc,Unity.Profiling.Memory.CaptureFlags.ManagedObjects | 
                                          Unity.Profiling.Memory.CaptureFlags.NativeObjects | 
                                          Unity.Profiling.Memory.CaptureFlags.NativeAllocations | 
                                          Unity.Profiling.Memory.CaptureFlags.NativeAllocationSites | 
                                          Unity.Profiling.Memory.CaptureFlags.NativeStackTraces);
            }
        }

        void CopyDataToTexture(Texture2D tex, NativeArray<byte> byteArray)
        {
            unsafe
            {
                void* srcPtr = NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(byteArray);
                void* dstPtr = tex.GetRawTextureData<byte>().GetUnsafeReadOnlyPtr();
                UnsafeUtility.MemCpy(dstPtr, srcPtr, byteArray.Length * sizeof(byte));
            }
        }
        
        private void MemorySnapshotCallback(string str, bool bl)
        {
            if (bl)
            {
                UnityEngine.Debug.Log("截取快照完成："+str);
            }
        }
        
        void WriteToFile(string fileName,string data)
        {
            File.WriteAllText(fileName,data);
        }
        
        private string UnicodeDecode(string unicodeStr)
        {
            if (string.IsNullOrWhiteSpace(unicodeStr) || (!unicodeStr.Contains("\\u") && !unicodeStr.Contains("\\U")))
            {
                return unicodeStr;
            }

            string newStr = Regex.Replace(unicodeStr, @"\\[uU](.{4})", (m) =>
            {
                string unicode = m.Groups[1].Value;
                if (int.TryParse(unicode, System.Globalization.NumberStyles.HexNumber, null, out int temp))
                {
                    return ((char)temp).ToString();
                }
                else
                {
                    return m.Groups[0].Value;
                }
            }, RegexOptions.Singleline);

            return newStr;
        }
    }
}