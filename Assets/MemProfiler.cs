using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.MemoryProfiler.Editor;
using System.IO;
using System.Threading;

namespace MemAnalyzer
{
    public class MemProfiler
    {
        string UUid;
        string SnapFilePath;
        string SummaryCsvPath;
        string UnityObjectsCsvPath;
        string AllMemoryDataCsvPath;
        string FunReferenceDataPath;
        private static MemProfiler _instance = null;
        static MemProfiler()
        {
            _instance = new MemProfiler();
        }
        public static MemProfiler instance()
        {
            return _instance;
        }
        private MemoryProfilerNoWindow mp_windows;
        //public string Appkey { get; set; }
        //public string ProjectId { get; set; }
        //public string Model { get; set; }
        //public string SceneName { get; set; }
        //public string CaseName { get; set; }
        //public string Quality { get; set; }
        //public string Tag { get; set; }
        //public string FileName { get; set; }
        //public string BuildTime { get; set; }
        //public string Version { get; set; }
        //public string Channel { get; set; }
        //public string Branch { get; set; }


        public void AnalyzeStart(Dictionary<string, string> comd)
        {
            Debug.Log("MemoryAnalyze Begin----");
            SnapFilePath = comd.ContainsKey("-SnapFilePath") ? comd["-SnapFilePath"] : "";
            UUid = comd.ContainsKey("-UUID") ? comd["-UUID"] : "";
            SummaryCsvPath = comd.ContainsKey("-SummaryCsv") ? comd["-SummaryCsv"] : "";
            UnityObjectsCsvPath = comd.ContainsKey("-UnityObjectsCsv") ? comd["-UnityObjectsCsv"] : "";
            AllMemoryDataCsvPath = comd.ContainsKey("-AllMemoryDataCsv") ? comd["-AllMemoryDataCsv"] : "";
            FunReferenceDataPath = comd.ContainsKey("-FunReferenceData") ? comd["-FunReferenceData"] : "";
            if (string.IsNullOrEmpty(UUid))
            {
                Debug.LogError("UUID为空----");
                return;
            }
            else if (string.IsNullOrEmpty(SummaryCsvPath))
            {
                Debug.LogError("SummaryCsvPath为空----");
                return;
            }
            else if (string.IsNullOrEmpty(UnityObjectsCsvPath))
            {
                Debug.LogError("UnityObjectsCsvPath为空----");
                return;
            }
            else if (string.IsNullOrEmpty(AllMemoryDataCsvPath))
            {
                Debug.LogError("AllMemoryDataCsvPath为空----");
                return;
            }
            else if (string.IsNullOrEmpty(SnapFilePath))
            {
                Debug.LogError("SnapFilePath为空----");
                return;
            }
            else if (string.IsNullOrEmpty(FunReferenceDataPath))
            {
                Debug.LogError("FunReferenceDataPath为空----");
                return;
            }
            else
            {
                //
            }
            Debug.Log($"SnapFilePath：{SnapFilePath}");
            Debug.Log($"UUID：{UUid}");
            Debug.Log($"SummaryCsvPath：{SummaryCsvPath}");
            Debug.Log($"UnityObjectsCsvPath：{UnityObjectsCsvPath}");
            Debug.Log($"AllMemoryDataCsvPath：{AllMemoryDataCsvPath}");
            Debug.Log($"FunReferenceDataPath：{FunReferenceDataPath}");
            // 初始化MemoryProfiler模块
            mp_windows = new MemoryProfilerNoWindow();
            mp_windows.Init();

            if (IsFileInUsed(SnapFilePath))
            {
                Debug.LogError($"{SnapFilePath}该文件被占用中----");
                return;
            }
            else if (!IsFileHasData(SnapFilePath))
            {
                Debug.LogError($"{SnapFilePath}该文件大小异常----");
                return;
            }

            mp_windows.LoadedSnapshot(SnapFilePath);

            while (true)
            {
                if (mp_windows.IsLoadSuccess())
                {
                    //Load完成，进行获取数据
                    break;
                }
                Thread.Sleep(1);
            }
            GetParseData();
        }

        public void WriteResultFile(string filename, string data)
        {
            File.WriteAllText(filename, data);
        }

        /// <summary>
        /// 输出结果数据
        /// </summary>
        void GetParseData()
        {
            Debug.Log("输出结果数据----");
            try
            {
                mp_windows.BuildAllData(SummaryCsvPath, UnityObjectsCsvPath, AllMemoryDataCsvPath);
                // mp_windows.BuildStackReferenceData(FunReferenceDataPath);
                Debug.Log("解析Memory完毕----");
            }
            catch (Exception e)
            {
                Debug.LogError(e.ToString());
            }
        }

        /// <summary>
        /// 判断文件是否被占用中
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        private bool IsFileInUsed(string filePath)
        {
            try
            {
                using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Debug.Log($"File {filePath} is not in use");
                    return false;
                }
            }
            catch (IOException)
            {
                Debug.LogError($"File {filePath} is in use");
                return true;
            }
        }

        /// <summary>
        /// 判断文件是否有数据，为0时证明没截到快照  进行跳过
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private bool IsFileHasData(string path)
        {
            FileInfo fileInfo = new FileInfo(path);

            if (fileInfo.Length == 0)
            {
                Debug.Log($"File {path} is empty");
                return false;
            }
            else
            {
                Debug.LogError($"File {path} is not empty");
                return true;
            }
        }

    }
}
