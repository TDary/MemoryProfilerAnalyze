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
            string filePath = "D:\\Test\\5449d8f2971946f588561077dbf7b12f.snap";
            memPNW = new MemoryProfilerNoWindow();
            memPNW.Init();
            memPNW.LoadedSnapshot(filePath);
            if (memPNW.IsLoadSuccess())
            {
                Debug.Log("LoadÍê±Ï");
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
    }

    public void WriteResultFile(string filename, string data)
    {
        File.WriteAllText(filename, data);
    }
}
