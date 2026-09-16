using System;
using System.Collections.Generic;

namespace PcbInspection.Models
{
   

    public class InspectionProject
    {
        public string ProjectName { get; set; }
        public string TemplateImagePath { get; set; }

        // ROI 集合（现在每个 RoiRect 对象内部都包含了独立参数）
        public List<RoiRect> RoiList { get; set; } = new List<RoiRect>();

        public double Confidence { get; set; } = 0.8;
    }
}