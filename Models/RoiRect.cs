using OpenCvSharp;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PcbInspection.Models
{
    // 支持的视觉检测算法类型
    public enum InspectionAlgorithm
    {
        [Description("检测螺纹-连通个数")]
         FindLW1,

        [Description("检测螺纹")]
         FindLW,

        [Description("检测圆型焊点")]
        TemplateDiff,

        [Description("检测缺元件")]
        YoloOnnx,

        [Description("检测橙色元件")]
        YoloOnnx1,
        [Description("检测黑色元件")]
        YoloOnnx2,
     
        [Description("检测芯片")]
        TemplateDiff1,
        [Description("检测元件缺失2")]
        TemplateDiff2,

        [Description("边缘轮廓对比 (Canny)")]
        EdgeDetection,

        [Description("平均亮度/灰度检验")]
        MeanBrightness,

        [Description("金字塔模板匹配")]
        PyramidTemplate, 
               
        [Description("焊锡面积比较")]
        SolderAreaCompare,

        [Description("检测最大焊锡轮廓面积")]
        SolderContourDetection,

        [Description("过滤背景+面积比较")]
        SolderHsvMaskCompare
    }

    public class RoiRect : INotifyPropertyChanged
    {
        public string DisplayName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }

        // 选中的视觉检测算法
        private InspectionAlgorithm _algorithm = InspectionAlgorithm.TemplateDiff;
        public InspectionAlgorithm Algorithm
        {
            get => _algorithm;
            set
            {
                _algorithm = value;
                OnPropertyChanged();
                // 算法切换时，通知界面更新参数名称
                OnPropertyChanged(nameof(Param1Name));
                OnPropertyChanged(nameof(Param2Name));
            }
        }
        // --- 动态中文参数名称 ---
        [Newtonsoft.Json.JsonIgnore] // 序列化时无需保存
        public string Param1Name
        {
            get
            {
                switch (Algorithm)
                {
                    case InspectionAlgorithm.FindLW1:
                        return "阈值分割";
                    case InspectionAlgorithm.FindLW:
                        return "最小纹理分数";
                    case InspectionAlgorithm.YoloOnnx1:
                        return "置信度阈值(%)";
                    case InspectionAlgorithm.YoloOnnx2:
                        return "置信度阈值(%)";
                    case InspectionAlgorithm.TemplateDiff:
                        return "二值化阈值";
                    case InspectionAlgorithm.TemplateDiff2:
                        return "最低亮度阈值";
                    case InspectionAlgorithm.EdgeDetection:
                        return "Canny高阈值";
                    case InspectionAlgorithm.MeanBrightness:
                        return "灰度下限(Min)";
                    case InspectionAlgorithm.PyramidTemplate:
                        return "金字塔层数(Levels)"; // ParamThreshold 用作层数（如 2 或 3）
                    default:
                        return "阈值/参数1";//默认参数
                }
            }
        }

        [Newtonsoft.Json.JsonIgnore]
        public string Param2Name
        {
            get
            {
                switch (Algorithm)
                {
                    case InspectionAlgorithm.FindLW1:
                        return "最小连通个数";
                    case InspectionAlgorithm.FindLW:
                        return "最小边缘比例";
                    case InspectionAlgorithm.YoloOnnx1:
                        return "IoU 重叠度阈值";
                    case InspectionAlgorithm.YoloOnnx2:
                        return "IoU 重叠度阈值";
                    case InspectionAlgorithm.TemplateDiff:
                        return "容忍上限占比";
                    case InspectionAlgorithm.TemplateDiff2:
                        return "元件存在比例下限";
                    case InspectionAlgorithm.EdgeDetection:
                        return "变形上限占比";
                    case InspectionAlgorithm.MeanBrightness:
                        return "灰度上限(Max)";
                    case InspectionAlgorithm.PyramidTemplate:
                        return "匹配得分下限(0~1)"; // ParamRatio 用作最小匹配度阈值（如 0.8）
                    default:
                        return "上限/参数2";//默认参数
                }
            }
        }
        // --- 算法通用参数集 ---
        // 参数1: 阈值 (用于差值二值化 / Canny高阈值 / 灰度下限)
        private double _paramThreshold = 128;
        public double ParamThreshold
        {
            get => _paramThreshold;
            set { _paramThreshold = value; OnPropertyChanged(); }
        }

        // 参数2: 比例或上限 (用于缺陷占比 / 灰度上限)
        private double _paramRatio = 0.05;
        public double ParamRatio
        {
            get => _paramRatio;
            set { _paramRatio = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler PropertyChanged;//
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    // 统一算法检测结果
    public class InspectionResult
    {
        public bool IsPassed { get; set; }
        public double ScoreOrRatio { get; set; }
        public string Message { get; set; }
        public double Score { get; set; } // <--- 添加这一行
        public Mat mat { get; set; }
    }
    public class InspectionStep
    {
        public int StepIndex { get; set; }          // 步骤序号 
        public string StepName { get; set; }       // 步骤名称（如：灰度化、Canny边缘检测）
        public Mat StepMat { get; set; }           // 当前步骤生成的 Mat 图像（注意销毁）
        public string InputParams { get; set; }     // 输入参数描述   
        public string OutputData { get; set; }      // 输出数据/计算结果描述   
    }
}