using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using PcbInspection.Models;

namespace PcbInspection.Services
{
    public class ProjectManagerService
    {
        private static readonly string ProjectDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects");

        // 配置 JSON 序列化参数：将 Enum 转换为字符串保存，增强可读性与解析兼容性
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        };

        static ProjectManagerService()
        {
            if (!Directory.Exists(ProjectDir))
            {
                Directory.CreateDirectory(ProjectDir);
            }
        }

        // 获取所有已保存的项目名称
        public static List<string> GetProjectList()
        {
            var list = new List<string>();
            var files = Directory.GetFiles(ProjectDir, "*.json");
            foreach (var file in files)
            {
                list.Add(Path.GetFileNameWithoutExtension(file));
            }
            return list;
        }
        // 保存检测项目（带 Enum 文本序列化） 
        public static void SaveProject(InspectionProject project)
        {
            if (project == null || string.IsNullOrWhiteSpace(project.ProjectName)) return;

            string filePath = Path.Combine(ProjectDir, $"{project.ProjectName}.json");
            string json = JsonConvert.SerializeObject(project, JsonSettings);
            File.WriteAllText(filePath, json);
        }
        // 加载指定项目
        public static InspectionProject LoadProject(string projectName)
        {
            string filePath = Path.Combine(ProjectDir, $"{projectName}.json");
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<InspectionProject>(json, JsonSettings);
            }
            return null;
        }

        // 删除项目及其关联文件（包括模板图片）
        public static void DeleteProject(string projectName)
        {
            string jsonPath = Path.Combine(ProjectDir, $"{projectName}.json");//
            if (File.Exists(jsonPath)) File.Delete(jsonPath);

            string imgPath = Path.Combine(ProjectDir, $"{projectName}_template.png");
            if (File.Exists(imgPath))
            {
                try
                {
                    File.Delete(imgPath);
                }
                catch (IOException)
                {
                    // 如果个别情况下文件仍被延迟占用，强制 GC 后重试一次
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    if (File.Exists(imgPath)) File.Delete(imgPath);
                }
            }
        }

        // 保存模板图片文件 roi 
        public static string SaveTemplateImage(string projectName, System.Windows.Media.Imaging.BitmapSource image)
        {
            if (image == null) return string.Empty;

            string imgPath = Path.Combine(ProjectDir, $"{projectName}_template.png");
            using (var fileStream = new FileStream(imgPath, FileMode.Create))
            {
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                encoder.Save(fileStream);
            }
            return imgPath;
        }
    }
}