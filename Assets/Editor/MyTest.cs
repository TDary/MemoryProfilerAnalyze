using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.MemoryProfiler.Editor;
using UnityEditor;
using UnityEngine;

public class MyTest : EditorWindow
{
    private static MyTest instance;
    private MemoryProfilerNoWindow memPNW;
    [MenuItem("Tools/MyTest")]
    public static void GetInstance()
    {
        if (instance == null)
        {
            instance = ScriptableObject.CreateInstance<MyTest>();
        }
        instance.Show();
    }

    private void OnGUI()
    {
        if (GUILayout.Button("Run Test"))
        {
            string filePath = "D:\\Test\\jxsj4_2026-04-07_15-31-46.snap";
            memPNW = new MemoryProfilerNoWindow();
            memPNW.Init();
            memPNW.LoadedSnapshot(filePath);
            if (memPNW.IsLoadSuccess())
            {
                Debug.Log("Load完毕");
            }
        }
        if (GUILayout.Button("GenerateResultData"))
        {
            string AllMemoryDatajsonPath = "D:/allMemory.json";
            string stackDataPath = "D:/stackData.txt";
            string allMemoryJson = memPNW.BuildAllMemoryData();
            WriteResultFile(AllMemoryDatajsonPath, allMemoryJson);
            memPNW.BuildStackReferenceData(stackDataPath);
        }
        if (GUILayout.Button("GenerateUnityObjectsData"))
        {
            string unityObjectsJsonPath = "D:/unityObjects.csv";
            memPNW.BuildUnityObjectsData(unityObjectsJsonPath);
            Debug.Log("生成UnityObjectsData完毕");
        }
        if(GUILayout.Button("UnloadSnap"))
        {
            memPNW = null;
            Resources.UnloadUnusedAssets();
            GC.Collect();
            Debug.Log("UnloadSnap完毕");
        }
    }

    public void WriteResultFile(string filename, string data)
    {
        File.WriteAllText(filename, data);
    }
}
