using System;
using System.IO;

namespace PcbInspection
{
    public static class AppConfig
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");

        // 保存相机 ID 到本地文本文件
        public static void SaveCameraMoniker(string moniker)
        {
            try
            {
                File.WriteAllText(ConfigPath, moniker);
            }
            catch { }
        }

        // 读取保存的相机 ID
        public static string GetCameraMoniker()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    return File.ReadAllText(ConfigPath);
                }
            }
            catch { }
            return string.Empty;
        }
    }
}