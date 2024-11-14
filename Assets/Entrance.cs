using System.Collections.Generic;
using System;

namespace MemAnalyzer
{
    public class Entrance
    {
        static Dictionary<string, string> GetInputCommand(string[] args)
        {
            Dictionary<string, string> infoData = new Dictionary<string, string>();
            string key = string.Empty;
            foreach (var item in args)
            {
                if (item.StartsWith("-"))
                {
                    key = item.Trim();
                }
                else if (!string.IsNullOrEmpty(item) && !string.IsNullOrEmpty(key) && !infoData.ContainsKey(key))
                {
                    infoData.Add(key, item.Trim());
                }
            }
            return infoData;
        }

        public static void StartServer()
        {
            Dictionary<string, string> commands = GetInputCommand(System.Environment.GetCommandLineArgs());

            try
            {
                MemProfiler.instance().AnalyzeStart(commands);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError(ex.ToString());
            }
        }
    }
}
