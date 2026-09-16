using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using PcbInspection.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PcbInspection.Services
{
    public static class VisionInspectionService
    {
        // 是否开启 ROI 落盘调试（生产环境设为 false 以免影响性能）
        public static bool IsDebugSaveRoi { get; set; } = false;

        // ==================== 1. ONNX 模型静态单例 ====================
        private static InferenceSession _onnxSession;
        private static readonly object _sessionLock = new object();

        // 2. 类别名称（顺序必须和 data.yaml 保持一致：0: miss, 1: class2, 2: class1）
        private static readonly string[] ClassNames = new string[]
        {
            "miss",   // Index 0  缺焊
            "class2", // Index 1  黑色
            "class1"  // Index 2  橙色
        };
        /// <summary>
        /// 初始化 ONNX 模型（上位机启动或切换方案时调用一次即可）
        /// </summary>
        public static void InitYoloModel(string modelPath)
        {
            lock (_sessionLock)
            {
                if (_onnxSession == null)
                {
                    var options = new SessionOptions();
                    // 如配置 GPU，可取消下行注释
                    // options.AppendExecutionProvider_CUDA();
                    _onnxSession = new InferenceSession(modelPath, options);
                }
            }
        }
        /// <summary>
        /// 视觉检测总入口
        /// </summary>
        public static InspectionResult ExecuteInspection(Mat matTemplate, Mat matSample, Rect cvRect, RoiRect roi)
        {      
            // 边界安全裁剪
            cvRect.X = Math.Max(0, Math.Min(cvRect.X, matTemplate.Width - 1));
            cvRect.Y = Math.Max(0, Math.Min(cvRect.Y, matTemplate.Height - 1));
            cvRect.Width = Math.Min(cvRect.Width, matTemplate.Width - cvRect.X);
            cvRect.Height = Math.Min(cvRect.Height, matTemplate.Height - cvRect.Y);

            if (cvRect.Width <= 0 || cvRect.Height <= 0)
            {
                return new InspectionResult { IsPassed = false, Message = "ROI 区域超出图像范围" };
            }
            using (Mat roiTpl = new Mat(matTemplate, cvRect))
            using (Mat roiSmp = new Mat(matSample, cvRect))
            {             
                switch (roi.Algorithm)//识别算法 
                {
                    case InspectionAlgorithm.FindLW1:
                        return FindLW1(roiSmp, Convert.ToInt16(roi.ParamThreshold), Convert.ToInt16(roi.ParamRatio));
                    case InspectionAlgorithm.FindLW:
                        return FindLW(roiSmp, Convert.ToInt16(roi.ParamThreshold), roi.ParamRatio);
                    case InspectionAlgorithm.YoloOnnx1:
                        return RunYoloOnnxInspection(roiSmp, "class1", Convert.ToInt16(roi.ParamThreshold), Convert.ToInt16(roi.ParamRatio));
                    case InspectionAlgorithm.YoloOnnx2:
                        return RunYoloOnnxInspection(roiSmp, "class2", Convert.ToInt16(roi.ParamThreshold), Convert.ToInt16(roi.ParamRatio));
                    case InspectionAlgorithm.TemplateDiff:
                        return InspectSolderBrightRing(roiSmp, Convert.ToInt16(roi.ParamThreshold), roi.ParamRatio);
                    case InspectionAlgorithm.TemplateDiff1:
                        return InspectSolderBrightRing1(roiSmp, Convert.ToInt16(roi.ParamThreshold), roi.ParamRatio);
                    case InspectionAlgorithm.TemplateDiff2:
                        return RunTemplateDiff2(roiTpl, roiSmp, roi.ParamThreshold, roi.ParamRatio);
                    case InspectionAlgorithm.EdgeDetection:
                        return RunEdgeDetection(roiTpl, roiSmp, roi.ParamThreshold, roi.ParamRatio);
                    case InspectionAlgorithm.MeanBrightness:
                        return RunBrightnessCheck(roiSmp, roi.ParamThreshold, roi.ParamRatio);
                    case InspectionAlgorithm.PyramidTemplate:
                        return RunPyramidMatching(roiTpl, roiSmp, (int)roi.ParamThreshold, roi.ParamRatio);
                    case InspectionAlgorithm.SolderAreaCompare:
                        return RunSolderAreaCompare(roiTpl, roiSmp, roi.ParamThreshold, roi.ParamRatio);
                    case InspectionAlgorithm.SolderContourDetection:
                        return RunSolderContourDetection(roiTpl, roiSmp, roi.ParamThreshold, roi.ParamRatio);
                    case InspectionAlgorithm.SolderHsvMaskCompare:
                        return RunSolderHsvMaskCompare(roiTpl, roiSmp, roi.ParamThreshold, roi.ParamRatio);
                    default:                       
                        return RunTemplateDiff(roiTpl, roiSmp, roi.ParamThreshold, roi.ParamRatio);//阈值分割值
                }
            }
        }
        private static InspectionResult FindLW1(Mat smpRoi, int ParamThreshold = 100, int ParamNum = 3)
        {
            // 1. 空图防呆判断
            if (smpRoi == null || smpRoi.Empty())
            {
                return new InspectionResult
                {
                    mat = new Mat(),
                    IsPassed = false,
                    ScoreOrRatio = 0,
                    Score = 0,
                    Message = "NG：检测区域为空"
                };
            }
            // 2. 落盘调试开关
            if (IsDebugSaveRoi)
            {
                string debugDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_rois");
                Directory.CreateDirectory(debugDir);
                Cv2.ImWrite(Path.Combine(debugDir, $"{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"), smpRoi);
            }
            // 3. 连通域分析核心逻辑（移植自 btnTest1_Click）
            using (Mat gray = new Mat())
            using (Mat binary = new Mat())
            using (Mat labels = new Mat())
            using (Mat stats = new Mat())
            using (Mat centroids = new Mat())
            {
                // 转灰度图
                if (smpRoi.Channels() > 1)
                    Cv2.CvtColor(smpRoi, gray, ColorConversionCodes.BGR2GRAY);
                else
                    smpRoi.CopyTo(gray);
                // 固定阈值二值化 (128)
                Cv2.Threshold(gray, binary, ParamThreshold, 255, ThresholdTypes.Binary);
                // 连通域分析
                int numLabels = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids);
                // 准备绘制结果图像
                Mat resMat = smpRoi.Channels() == 1 ? new Mat() : smpRoi.Clone();
                if (smpRoi.Channels() == 1)
                    Cv2.CvtColor(smpRoi, resMat, ColorConversionCodes.GRAY2BGR);
                int minArea = 10; // 过滤最小面积
                int validCount = 0;//检测到连通个数
                for (int i = 1; i < numLabels; i++)
                {
                    int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area < minArea) continue;
                    validCount++;
                    int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
                    int y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
                    int w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
                    int h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
                    // 在图上标记矩形框
                    Cv2.Rectangle(resMat, new Rect(x, y, w, h), Scalar.Yellow, 1);
                }
                // 判断规则（示例：有效连通域数量 >= 1 即为 PASS)
                bool pass = validCount >= ParamNum;
                // 图上绘制数量                                                             
                Cv2.PutText(resMat, $"Count: {validCount}", new Point(10, 30), HersheyFonts.HersheySimplex, 0.8, pass ? Scalar.Lime : Scalar.Red, 2);
                // 返回统一格式结果
                return new InspectionResult
                {
                    mat = resMat,
                    IsPassed = pass,
                    ScoreOrRatio = validCount,
                    Score = pass ? 100 : 0,
                    Message = pass ? $"PASS（检测到连通域数量：{validCount}）" : $"NG（未达到有效连通域要求，数量：{validCount}）"//返回结果数据参数信息
                };
            }
        }
        //检测螺纹 检测螺纹黑白纹理  
        private static InspectionResult FindLW( Mat smpRoi, double minTextureScore = 100, double minEdgeRatio = 0.10)
        {
            // 判断依据：
            //     TextureScore >= 100
            //     EdgeRatio    >= 0.10
            // 两个条件同时满足 => PASS
            if (smpRoi == null || smpRoi.Empty())  // 防止传入空图
            {
                return new InspectionResult
                {
                    mat = new Mat(),
                    IsPassed = false,
                    ScoreOrRatio = 0,
                    Score = 0,
                    Message = "NG：检测区域为空"
                };
            }

            // 【修改点 2】：用开关控制落盘，避免上位机卡顿
            if (IsDebugSaveRoi && smpRoi != null && !smpRoi.Empty())
            {
                string debugDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_rois");
                Directory.CreateDirectory(debugDir);
                Cv2.ImWrite(Path.Combine(debugDir, $"{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")}.png"), smpRoi);
            }
            using (Mat blurred = new Mat())
            using (Mat redChannel = new Mat())
            using (Mat enhanced = new Mat())
            using (Mat gradX = new Mat())
            using (Mat gradY = new Mat())
            using (Mat gradient = new Mat())
            using (Mat edges = new Mat())
            using (Mat binary = new Mat())
            {              
               
                Cv2.GaussianBlur( smpRoi, blurred, new OpenCvSharp.Size(5, 5), 0);// 1. 高斯滤波
                if (blurred.Channels() >= 3)// 2. 提取 R 通道
                {
                    Cv2.ExtractChannel( blurred, redChannel, 2);
                }
                else
                {
                    blurred.CopyTo(redChannel);
                }
              
                // 3. CLAHE 局部对比度增强
                // 螺纹可能存在： 
                // 黑 → 白 → 黑 → 白
                // 如果整体亮度比较接近，
                // CLAHE 可以把局部纹理增强出来。         
                using (CLAHE clahe = Cv2.CreateCLAHE( clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8)))
                {
                    clahe.Apply( redChannel, enhanced);
                }
                Cv2.Sobel( enhanced, gradX, MatType.CV_32F, 1, 0, 3);// 4. Sobel X
                Cv2.Sobel( enhanced, gradY, MatType.CV_32F, 0, 1, 3); // 5. Sobel Y            
                // 6. 计算梯度幅值
                // gradient = sqrt(Gx² + Gy²)
                // 纹理越明显：
                // 黑白变化越明显
                // 梯度越大
                // TextureScore 越高
                Cv2.Magnitude( gradX, gradY, gradient);
                Scalar meanGradient = Cv2.Mean(gradient);
                double textureScore = meanGradient.Val0;
                // 7. Canny 边缘检测
                // 用来计算：EdgeRatio =
                // 边缘像素数量 / ROI总像素数量  螺纹黑白交替明显时，EdgeRatio 通常会比较高。
                Cv2.Canny( enhanced, edges, 50, 150);
                int totalPixels = enhanced.Rows * enhanced.Cols;
                double edgePixels = 0;
                if (totalPixels > 0)
                {
                    edgePixels = Cv2.CountNonZero(edges);
                }
                double edgeRatio = totalPixels > 0 ? edgePixels / totalPixels: 0;            
                // 8. OTSU 二值化
                // 这里只用于辅助分析和显示，
                // 不直接决定 PASS / NG。            
                double otsuThreshold = Cv2.Threshold( enhanced, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                double whiteRatio = 0;
                if (totalPixels > 0)
                {
                    whiteRatio = (double)Cv2.CountNonZero(binary) / totalPixels;
                }
                // 9. 黑白跳变辅助检测
                // 不要求固定有几条螺纹。
                //
                // 因为实际螺纹可能：
                // - 倾斜
                // - 弯曲
                // - 数量不同
                // - 四指小手形状
                //
                // 所以这里只作为辅助指标，
                // 不参与最终 PASS / NG。
                double transitionScore = 0;
                int[] scanRows =
                {
                    (int)(binary.Rows * 0.20),
                    (int)(binary.Rows * 0.40),
                    (int)(binary.Rows * 0.60),
                    (int)(binary.Rows * 0.80)
                };
                int validScanCount = 0;
                double transitionTotal = 0;
                foreach (int row in scanRows)
                {
                    if (row < 0 || row >= binary.Rows)
                    {
                        continue;
                    }
                    int transitions = 0;
                    byte previous = binary.At<byte>(row, 0);
                    for (int x = 1; x < binary.Cols; x++)
                    {
                        byte current = binary.At<byte>(row, x);
                        if (current != previous)
                        {
                            transitions++;
                        }
                        previous = current;
                    }
                    transitionTotal += transitions;
                    validScanCount++;
                }
                if (validScanCount > 0)
                {
                    transitionScore =  transitionTotal / validScanCount;
                }
                // ========================================================
                // 10. 最终判断
                // 纹理强度：>= 100   边缘比例：  >= 10% 两个条件同时满足才 PASS
                // ========================================================
              
                bool textureOK = textureScore >= minTextureScore;
                bool edgeOK =  edgeRatio >= minEdgeRatio;
                bool pass = textureOK && edgeOK;
                // ========================================================
                // 11. 制作调试显示图
                // 返回的 mat 不再是原来的“亮块二值图”，而是：
                //
                //  增强后的灰度图  + Canny边缘  +  PASS/NG  +  TextureScore   + EdgeRatio

                // 方便你现场调试。
                // ========================================================
                using (Mat debugImage = new Mat())
                {
                    Cv2.CvtColor( enhanced, debugImage,  ColorConversionCodes.GRAY2BGR);
                    // ====================================================
                    // 将边缘叠加到图像上
                    //
                    // PASS：绿色
                    // NG：红色
                    // ====================================================
                    Scalar edgeColor = pass ? new Scalar(0, 255, 0)  : new Scalar(0, 0, 255);
                    debugImage.SetTo(  edgeColor, edges);
                    // ====================================================
                    // 文字
                    // ====================================================
                    //string resultText =  pass ? "THREAD PASS" : "THREAD NG";
                    //string textureText = $"Texture: {textureScore:F2}";
                    //string edgeText = $"Edge: {edgeRatio:P2}";
                    //string transitionText = $"Transition: {transitionScore:F2}";
                    //string whiteText = $"White: {whiteRatio:P2}";
                    //Cv2.PutText( debugImage, resultText, new Point(10, 25), HersheyFonts.HersheySimplex,  0.65, edgeColor, 2);
                    //Cv2.PutText( debugImage, textureText, new Point(10, 50), HersheyFonts.HersheySimplex, 0.55, new Scalar(255, 255, 0), 1);
                    //Cv2.PutText( debugImage, edgeText,  new Point(10, 73), HersheyFonts.HersheySimplex, 0.55, new Scalar(255, 255, 0), 1);
                    //Cv2.PutText( debugImage, transitionText, new Point(10, 96), HersheyFonts.HersheySimplex,  0.55, new Scalar(255, 255, 0),  1);
                    //Cv2.PutText( debugImage, whiteText, new Point(10, 119),HersheyFonts.HersheySimplex, 0.55, new Scalar(255, 255, 0), 1);
                    // Clone 一份给返回值
                    Mat resultMat = debugImage.Clone();
                    // ====================================================
                    // 12. 返回原来的 InspectionResult 格式
                    // ====================================================
                    //                                            越大代表
                    //TextureScore      整体亮暗变化强不强       纹理越明显           ⭐⭐⭐⭐⭐
                    //EdgeRatio         黑白边界多不多           边缘 / 纹理越丰富    ⭐⭐⭐⭐⭐
                    //TransitionScore   横向黑白切换次数         黑白交替越明显       ⭐⭐⭐
                    //WhiteRatio        整个区域白色占多少       白色面积越多         ⭐

                    return new InspectionResult
                    {
                        mat = resultMat,
                        IsPassed = pass,
                        // 原来这里存的是：
                        // maxAreaRatio
                        // 现在为了不改变字段，
                        // 改成存 EdgeRatio
                        ScoreOrRatio = edgeRatio,
                        Score = pass ? 100 : 0,
                        Message = pass? $"PASS（螺纹纹理正常，Texture={textureScore:F2}，Edge={edgeRatio:P2}）,Transition: {transitionScore:F2},White: {whiteRatio:P2}" : $"NG（螺纹纹理不足，Texture={textureScore:F2}/{minTextureScore:F0}，Edge={edgeRatio:P2}/{minEdgeRatio:P0}）,Transition: {transitionScore:F2},White: {whiteRatio:P2}"
                    };
                }
            }
        }
        //检测圆型焊点
        private static InspectionResult InspectSolderBrightRing(Mat smpRoi, int brightThreshold = 180, double maxSingleAreaRatio = 0.03)
        {
            using (Mat blurred = new Mat())  
            using (Mat redChannel = new Mat())
            using (Mat binary = new Mat())
            {
                Cv2.GaussianBlur(smpRoi, blurred, new OpenCvSharp.Size(5, 5), 0);
                Cv2.ExtractChannel(blurred, redChannel, 2);
                Cv2.Threshold(redChannel, binary, brightThreshold, 255, ThresholdTypes.Binary);
                Cv2.FindContours( binary,out Point[][] contours,out HierarchyIndex[] _,RetrievalModes.External,ContourApproximationModes.ApproxSimple);
                double maxContourArea = 0;
                foreach (var contour in contours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area > maxContourArea)
                    {
                        maxContourArea = area;
                    }
                }
                int totalRoiArea = smpRoi.Width * smpRoi.Height;
                double maxAreaRatio = totalRoiArea > 0 ? (maxContourArea / totalRoiArea) : 0;
                bool pass = maxAreaRatio <= maxSingleAreaRatio;
                return new InspectionResult
                {
                    mat = binary.Clone(),
                    IsPassed = pass,
                    ScoreOrRatio = maxAreaRatio,
                    Score = pass ? 100 : 0,
                    Message = pass ? $"PASS (最大亮块: {maxAreaRatio:P1})" : $"检测到刺眼亮环/月牙，最大连通块占比 {maxAreaRatio:P1} (上限 {maxSingleAreaRatio:P1})"
                };
            }
        }
        //检测芯片
        private static InspectionResult InspectSolderBrightRing1( Mat smpRoi, int brightThreshold = 180, double maxSingleAreaRatio = 0.03)
        {
            using (Mat blurred = new Mat())
            using (Mat greenChannel = new Mat())
            using (Mat binary = new Mat())
            {
   
                Cv2.GaussianBlur( smpRoi,blurred, new OpenCvSharp.Size(5, 5), 0); // 1. 高斯滤波，降低噪声 
  
                Cv2.ExtractChannel( blurred, greenChannel, 1);  // 2. 提取绿色通道     OpenCV颜色顺序：B=0，G=1，R=2

                // 3. 阈值分割               
                // 绿色通道 > brightThreshold  -> 白色 255
                // 绿色通道 <= brightThreshold -> 黑色 0               
                // 因此 binary 中的黑色区域就是我们需要统计的区域
                Cv2.Threshold( greenChannel, binary, brightThreshold, 255, ThresholdTypes.Binary);
                // =========================================================
                // 4. 统计黑色像素面积
                // binary:
                // 白色 = 255
                // 黑色 = 0
                // CountNonZero(binary)
                // 得到的是白色像素数量
                // ROI总像素 - 白色像素
                // = 黑色像素数量 
                // =========================================================
                int totalPixels = binary.Rows * binary.Cols;
                int whitePixels = Cv2.CountNonZero(binary);
                int blackPixels = totalPixels - whitePixels;
                // =========================================================
                // 5. 计算黑色面积占比
                // =========================================================
                double blackAreaRatio = totalPixels > 0 ? (double)blackPixels / totalPixels : 0;
                // =========================================================
                // 6. 判定 
                // 黑色面积占比 <= 最大允许比例    PASS            0.45  45%
                // 黑色面积占比 >  最大允许比例    NG          
                // =========================================================
                bool pass = blackAreaRatio >= maxSingleAreaRatio;
                // =========================================================
                // 7. 返回结果 
                // =========================================================
                return new InspectionResult 
                {
                    mat = binary.Clone(), 
                    IsPassed = pass, 
                    // ScoreOrRatio 返回黑色面积占比   
                    ScoreOrRatio = blackAreaRatio,   
                    Score = pass ? 100 : 0,   
                    Message = pass ? $"PASS (黑色面积: {blackAreaRatio:P1})" : $"检测到黑色异常区域，黑色面积占比 {blackAreaRatio:P1} (上限 {maxSingleAreaRatio:P1})"
                };
            }
        }
        #region YOLO ONNX 核心检测逻辑

        private static InspectionResult RunYoloOnnxInspection( Mat smpRoi, string className, float confThreshold = 0.4f, float iouThreshold = 0.4f)
        {
            confThreshold = confThreshold * 0.01f;
            iouThreshold = iouThreshold * 0.01f;
            // 【修改点 2】：用开关控制落盘，避免上位机卡顿
            if (IsDebugSaveRoi && smpRoi != null && !smpRoi.Empty())
            {
                string debugDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_rois");
                Directory.CreateDirectory(debugDir);
                Cv2.ImWrite(Path.Combine(debugDir, $"{className}_{DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss")}.png"), smpRoi);
            }             
            if (smpRoi == null || smpRoi.Empty())
            {
                string emptyMsg = $"NG (图像为空) [目标类别: {className}]";
                return new InspectionResult { mat = null, IsPassed = false, Score = 0, Message = emptyMsg };
            }
            if (_onnxSession == null)
            {
                string noModelMsg = $"NG (ONNX模型未初始化) [目标类别: {className}]";
                return new InspectionResult { mat = smpRoi.Clone(), IsPassed = false, Score = 0, Message = noModelMsg };
            }
            // ==================== 1. 恢复 3 通道 BGR 彩色图 ====================
            using Mat colorRoi = new Mat();
            if (smpRoi.Channels() == 1)
            {
                Cv2.CvtColor(smpRoi, colorRoi, ColorConversionCodes.GRAY2BGR);
            }
            else
            {
                smpRoi.CopyTo(colorRoi);
            }
            int origWidth = colorRoi.Width;
            int origHeight = colorRoi.Height;
            // 【修改点 3】：动态读取模型的实际输入宽高，防止模型尺寸改变导致报错
            int targetWidth = 128;
            int targetHeight = 128;
            var inputMeta = _onnxSession.InputMetadata.Values.FirstOrDefault();
            if (inputMeta != null && inputMeta.Dimensions.Length == 4)
            {
                if (inputMeta.Dimensions[2] > 0) targetHeight = inputMeta.Dimensions[2];
                if (inputMeta.Dimensions[3] > 0) targetWidth = inputMeta.Dimensions[3];
            }
            // ==================== 2. Letterbox 等比例缩放 + 灰色填充 ====================
            float scale = Math.Min((float)targetWidth / origWidth, (float)targetHeight / origHeight);
            int newWidth = (int)Math.Round(origWidth * scale);
            int newHeight = (int)Math.Round(origHeight * scale);
            int padW = (targetWidth - newWidth) / 2;
            int padH = (targetHeight - newHeight) / 2;
            using Mat resized = new Mat();
            Cv2.Resize(colorRoi, resized, new Size(newWidth, newHeight));
            using Mat rgbResized = new Mat();
            Cv2.CvtColor(resized, rgbResized, ColorConversionCodes.BGR2RGB);//转为红色通道 图像
            using Mat letterboxMat = new Mat(targetHeight, targetWidth, MatType.CV_8UC3, new Scalar(114, 114, 114));
            Rect roiArea = new Rect(padW, padH, newWidth, newHeight);
            rgbResized.CopyTo(letterboxMat[roiArea]);
            // ==================== 3. 构建 NCHW Tensor [1, 3, H, W] ====================
            using Mat floatMat = new Mat();
            letterboxMat.ConvertTo(floatMat, MatType.CV_32FC3, 1.0 / 255.0);
            Mat[] splitChannels = Cv2.Split(floatMat);
            float[] tensorBuffer = new float[1 * 3 * targetHeight * targetWidth];
            int channelSize = targetHeight * targetWidth;
            for (int c = 0; c < 3; c++)
            {
                System.Runtime.InteropServices.Marshal.Copy(  splitChannels[c].Data,  tensorBuffer, c * channelSize,  channelSize); splitChannels[c].Dispose();
            }
            var inputTensor = new DenseTensor<float>(tensorBuffer, new[] { 1, 3, targetHeight, targetWidth });
            // ==================== 4. 执行 ONNX 推理 ====================
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_onnxSession.InputMetadata.Keys.First(), inputTensor)
            };
            List<YoloPrediction> allPredictions = new List<YoloPrediction>();
            float rawMaxConfidenceForTarget = 0f;
            int targetClassIndex = Array.IndexOf(ClassNames, className);
            using (var results = _onnxSession.Run(inputs))
            {
                var outputTensor = results.First().AsTensor<float>();
                int numClasses = ClassNames.Length;
                int anchors = outputTensor.Dimensions[2];
                for (int i = 0; i < anchors; i++)
                {
                    if (targetClassIndex >= 0)
                    {
                        float targetScore = outputTensor[0, 4 + targetClassIndex, i];
                        if (targetScore > rawMaxConfidenceForTarget)
                        {
                            rawMaxConfidenceForTarget = targetScore;
                        }
                    }
                    float maxScore = 0f;
                    int maxClassId = -1;
                    for (int c = 0; c < numClasses; c++)
                    {
                        float score = outputTensor[0, 4 + c, i];
                        if (score > maxScore)
                        {
                            maxScore = score;
                            maxClassId = c;
                        }
                    }
                    if (maxScore >= confThreshold)
                    {
                        // 坐标还原
                        float cx = (outputTensor[0, 0, i] - padW) / scale;
                        float cy = (outputTensor[0, 1, i] - padH) / scale;
                        float w = outputTensor[0, 2, i] / scale;
                        float h = outputTensor[0, 3, i] / scale;
                        int x = (int)(cx - w / 2);
                        int y = (int)(cy - h / 2);
                        allPredictions.Add(new YoloPrediction
                        {
                            Box = new Rect(x, y, (int)w, (int)h),
                            Confidence = maxScore,
                            ClassId = maxClassId,
                            Label = ClassNames[maxClassId]
                        });
                    }
                }
            }

            // ==================== 5. 【修改点 4】按类别独立 NMS 与筛选 ==================== 
            List<YoloPrediction> nmsPredictions = ApplyPerClassNMS(allPredictions, iouThreshold);//

            var matchedTargets = nmsPredictions
                .Where(p => string.Equals(p.Label, className, StringComparison.OrdinalIgnoreCase))
                .ToList();

            float finalMaxConfidence = matchedTargets.Count > 0
                ? matchedTargets.Max(p => p.Confidence)
                : rawMaxConfidenceForTarget;

            // ==================== 6. 结果绘制与 PASS/NG 判定 ====================
            Mat displayMat = smpRoi.Clone();
            if (displayMat.Channels() == 1)
            {
                Cv2.CvtColor(displayMat, displayMat, ColorConversionCodes.GRAY2BGR);
            }

            foreach (var pred in matchedTargets)
            {
                bool isDefect = pred.Label.StartsWith("miss", StringComparison.OrdinalIgnoreCase);
                Scalar boxColor = isDefect ? new Scalar(0, 0, 255) : new Scalar(0, 255, 0);

                Cv2.Rectangle(displayMat, pred.Box, boxColor, 2);
                string text = $"{pred.Label} {pred.Confidence:P1}";
                Cv2.PutText(displayMat, text, new Point(Math.Max(0, pred.Box.X), Math.Max(15, pred.Box.Y - 5)),
                    HersheyFonts.HersheySimplex, 0.4, boxColor, 1);
            }

            bool isDefectType = className.StartsWith("miss", StringComparison.OrdinalIgnoreCase);
            bool pass = isDefectType ? (matchedTargets.Count == 0) : (matchedTargets.Count > 0);
            float re = 0;
         
            if(!pass)
            {
                re = RunYoloOnnxInspectionMiss(smpRoi, 0.35f, 0.35f);
                float con = confThreshold / 2;
                if (re < 0.3 && finalMaxConfidence> con)
                {
                    
                    pass = true;
                }
            }


            string statusStr = pass ? "PASS" : "NG";
            string logMessage = $"[{statusStr}]目标:'{className}'|miss:{re:P2}|置信度:{finalMaxConfidence:P2}(阈值: {confThreshold:P2})|数量: {matchedTargets.Count}";



           // Debug.WriteLine($"[YOLO_AOI] {logMessage}");

            return new InspectionResult
            {
                mat = displayMat,
                IsPassed = pass,
                Score = (int)(finalMaxConfidence * 100),
                ScoreOrRatio = finalMaxConfidence,
                Message = logMessage
            };
        }
        #endregion
        private static float RunYoloOnnxInspectionMiss( Mat smpRoi, float confThreshold = 0.4f, float iouThreshold = 0.45f)
        {
            string className = "miss";
            // ==================== 1. 恢复 3 通道 BGR 彩色图 ====================
            using Mat colorRoi = new Mat();
            if (smpRoi.Channels() == 1)
            {
                Cv2.CvtColor(smpRoi, colorRoi, ColorConversionCodes.GRAY2BGR);
            }
            else
            {
                smpRoi.CopyTo(colorRoi);
            }
            int origWidth = colorRoi.Width;
            int origHeight = colorRoi.Height;
            // 【修改点 3】：动态读取模型的实际输入宽高，防止模型尺寸改变导致报错
            int targetWidth = 128;
            int targetHeight = 128;
            var inputMeta = _onnxSession.InputMetadata.Values.FirstOrDefault();//
            if (inputMeta != null && inputMeta.Dimensions.Length == 4)
            {
                if (inputMeta.Dimensions[2] > 0) targetHeight = inputMeta.Dimensions[2];
                if (inputMeta.Dimensions[3] > 0) targetWidth = inputMeta.Dimensions[3];
            }

            // ==================== 2. Letterbox 等比例缩放 + 灰色填充 ====================
            float scale = Math.Min((float)targetWidth / origWidth, (float)targetHeight / origHeight);
            int newWidth = (int)Math.Round(origWidth * scale);
            int newHeight = (int)Math.Round(origHeight * scale);
            
            int padW = (targetWidth - newWidth) / 2;
            int padH = (targetHeight - newHeight) / 2;

            using Mat resized = new Mat();
            Cv2.Resize(colorRoi, resized, new Size(newWidth, newHeight));

            using Mat rgbResized = new Mat();
            Cv2.CvtColor(resized, rgbResized, ColorConversionCodes.BGR2RGB);

            using Mat letterboxMat = new Mat(targetHeight, targetWidth, MatType.CV_8UC3, new Scalar(114, 114, 114));
            Rect roiArea = new Rect(padW, padH, newWidth, newHeight);
            rgbResized.CopyTo(letterboxMat[roiArea]);

            // ==================== 3. 构建 NCHW Tensor [1, 3, H, W] ====================
            using Mat floatMat = new Mat();
            letterboxMat.ConvertTo(floatMat, MatType.CV_32FC3, 1.0 / 255.0);

            Mat[] splitChannels = Cv2.Split(floatMat);
            float[] tensorBuffer = new float[1 * 3 * targetHeight * targetWidth];
            int channelSize = targetHeight * targetWidth;

            for (int c = 0; c < 3; c++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    splitChannels[c].Data,
                    tensorBuffer,
                    c * channelSize,
                    channelSize);
                splitChannels[c].Dispose();
            }

            var inputTensor = new DenseTensor<float>(tensorBuffer, new[] { 1, 3, targetHeight, targetWidth });

            // ==================== 4. 执行 ONNX 推理 ====================natwdbb#
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_onnxSession.InputMetadata.Keys.First(), inputTensor)
            };

            List<YoloPrediction> allPredictions = new List<YoloPrediction>();//
            float rawMaxConfidenceForTarget = 0f;
            int targetClassIndex = Array.IndexOf(ClassNames, className);

            using (var results = _onnxSession.Run(inputs))
            {
                var outputTensor = results.First().AsTensor<float>();
                int numClasses = ClassNames.Length;
                int anchors = outputTensor.Dimensions[2];

                for (int i = 0; i < anchors; i++)
                {
                    if (targetClassIndex >= 0)
                    {
                        float targetScore = outputTensor[0, 4 + targetClassIndex, i];
                        if (targetScore > rawMaxConfidenceForTarget)
                        {
                            rawMaxConfidenceForTarget = targetScore;
                        }
                    }
                    float maxScore = 0f;
                    int maxClassId = -1;
                    for (int c = 0; c < numClasses; c++)
                    {
                        float score = outputTensor[0, 4 + c, i]; 
                        if (score > maxScore)
                        {
                            maxScore = score;
                            maxClassId = c;
                        }
                    }
                    if (maxScore >= confThreshold)
                    {
                        // 坐标还原
                        float cx = (outputTensor[0, 0, i] - padW) / scale;
                        float cy = (outputTensor[0, 1, i] - padH) / scale;
                        float w = outputTensor[0, 2, i] / scale;
                        float h = outputTensor[0, 3, i] / scale;

                        int x = (int)(cx - w / 2);
                        int y = (int)(cy - h / 2);

                        allPredictions.Add(new YoloPrediction
                        {
                            Box = new Rect(x, y, (int)w, (int)h),
                            Confidence = maxScore,
                            ClassId = maxClassId,
                            Label = ClassNames[maxClassId]
                        });
                    }
                }
            }
            // ==================== 5. 【修改点 4】按类别独立 NMS 与筛选 ====================
            List<YoloPrediction> nmsPredictions = ApplyPerClassNMS(allPredictions, iouThreshold);
            var matchedTargets = nmsPredictions.Where(p => string.Equals(p.Label, className, StringComparison.OrdinalIgnoreCase)).ToList();
            float finalMaxConfidence = matchedTargets.Count > 0
                ? matchedTargets.Max(p => p.Confidence)
                : rawMaxConfidenceForTarget;
            // ====================  6. 结果绘制与 PASS/NG 判定  ====================
            Mat displayMat = smpRoi.Clone();//拷贝ROI图片 
            if (displayMat.Channels() == 1)
            {
                Cv2.CvtColor(displayMat, displayMat, ColorConversionCodes.GRAY2BGR);
            }
            foreach (var pred in matchedTargets)
            {
                bool isDefect = pred.Label.StartsWith("miss", StringComparison.OrdinalIgnoreCase);
                Scalar boxColor = isDefect ? new Scalar(0, 0, 255) : new Scalar(0, 255, 0);

                Cv2.Rectangle(displayMat, pred.Box, boxColor, 2);
                string text = $"{pred.Label} {pred.Confidence:P1}";
                Cv2.PutText(displayMat, text, new Point(Math.Max(0, pred.Box.X), Math.Max(15, pred.Box.Y - 5)),
                    HersheyFonts.HersheySimplex, 0.4, boxColor, 1);
            }

            bool isDefectType = className.StartsWith("miss", StringComparison.OrdinalIgnoreCase);
            bool pass = isDefectType ? (matchedTargets.Count == 0) : (matchedTargets.Count > 0);

            string statusStr = pass ? "PASS" : "NG";
            string logMessage = $"[{statusStr}] 目标: '{className}' | 最高置信度: {finalMaxConfidence:P2} (阈值: {confThreshold:P2}) | 数量: {matchedTargets.Count}";

            //Debug.WriteLine($"[YOLO_AOI] {logMessage}");
            //return new InspectionResult
            //{
            //    mat = displayMat,
            //    IsPassed = pass,
            //    Score = (int)(finalMaxConfidence * 100),
            //    ScoreOrRatio = finalMaxConfidence,
            //    Message = logMessage
            //};
            return finalMaxConfidence;
        }
        #region NMS 辅助类与算法

        private class YoloPrediction
        {
            public Rect Box { get; set; }
            public float Confidence { get; set; }
            public int ClassId { get; set; }
            public string Label { get; set; }
        }

        /// <summary>
        /// 按类别独立做 NMS（防止缺件 miss 和正常器件 class1 互相吃掉对方的检测框）
        /// </summary>
        private static List<YoloPrediction> ApplyPerClassNMS(List<YoloPrediction> predictions, float iouThreshold)
        {
            var result = new List<YoloPrediction>();
            var groupedByClass = predictions.GroupBy(p => p.ClassId);

            foreach (var group in groupedByClass)
            {
                var sorted = group.OrderByDescending(p => p.Confidence).ToList();

                while (sorted.Count > 0)
                {
                    var current = sorted[0];
                    result.Add(current);
                    sorted.RemoveAt(0);

                    for (int i = sorted.Count - 1; i >= 0; i--)
                    {
                        if (ComputeIoU(current.Box, sorted[i].Box) > iouThreshold)
                        {
                            sorted.RemoveAt(i);
                        }
                    }
                }
            }

            return result;
        }

        private static float ComputeIoU(Rect box1, Rect box2)
        {
            int x1 = Math.Max(box1.X, box2.X);
            int y1 = Math.Max(box1.Y, box2.Y);
            int x2 = Math.Min(box1.Right, box2.Right);
            int y2 = Math.Min(box1.Bottom, box2.Bottom);

            int intersectionArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            int box1Area = box1.Width * box1.Height;
            int box2Area = box2.Width * box2.Height;

            int unionArea = box1Area + box2Area - intersectionArea;
            return unionArea <= 0 ? 0 : (float)intersectionArea / unionArea;
        }

        #endregion


        // 算法 1：差值比对
        private static InspectionResult RunTemplateDiff1(Mat tpl, Mat smp, double threshold, double maxRatio)
                {
                    using (Mat grayTpl = new Mat())
                    using (Mat graySmp = new Mat())
                    using (Mat diff = new Mat())
                    {
                        Cv2.CvtColor(tpl, grayTpl, ColorConversionCodes.BGR2GRAY);
                        Cv2.CvtColor(smp, graySmp, ColorConversionCodes.BGR2GRAY);
                        Cv2.Absdiff(grayTpl, graySmp, diff);
                        Cv2.Threshold(diff, diff, threshold, 255, ThresholdTypes.Binary);
                        int nonZeroCount = Cv2.CountNonZero(diff);
                        double ratio = (double)nonZeroCount / (tpl.Width * tpl.Height);
                        bool pass = ratio <= maxRatio;
                        return new InspectionResult
                        {
                            IsPassed = pass,
                            ScoreOrRatio = ratio,
                            Score = ratio, // 补全赋值
                            Message = pass ? "PASS" : $"差值占比 {ratio:P1} 超过上限 {maxRatio:P1}"
                        };
                    }
                }
        // 算法 2：Canny 边缘差异检测
        private static InspectionResult RunEdgeDetection(Mat tpl, Mat smp, double threshold, double maxRatio)
        {
            using (Mat grayTpl = new Mat())
            using (Mat graySmp = new Mat())
            using (Mat edgeTpl = new Mat())
            using (Mat edgeSmp = new Mat())
            using (Mat diff = new Mat())
            {
                Cv2.CvtColor(tpl, grayTpl, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(smp, graySmp, ColorConversionCodes.BGR2GRAY);
                Cv2.Canny(grayTpl, edgeTpl, threshold / 2, threshold);
                Cv2.Canny(graySmp, edgeSmp, threshold / 2, threshold);
                Cv2.Absdiff(edgeTpl, edgeSmp, diff);
                int nonZeroCount = Cv2.CountNonZero(diff);//计算边缘差异像素数量
                double ratio = (double)nonZeroCount / (tpl.Width * tpl.Height);
                bool pass = ratio <= maxRatio; 
                return new InspectionResult
                {
                    IsPassed = pass,
                    ScoreOrRatio = ratio,
                    Score = ratio, //补全赋值 
                    Message = pass ? "PASS" : $"边缘缺失/变形占比 {ratio:P1}" 
                };
            }
        } 
        // 算法 3：平均灰度/亮度检测 
        private static InspectionResult RunBrightnessCheck(Mat smp, double minGray, double maxGray)
        {
            using (Mat graySmp = new Mat())
            {
                Cv2.CvtColor(smp, graySmp, ColorConversionCodes.BGR2GRAY);//颜色rgb  
                Scalar mean = Cv2.Mean(graySmp);
                double avgVal = mean.Val0;//平均灰度值
                bool pass = avgVal >= minGray && avgVal <= maxGray;//最大灰度值
                return new InspectionResult
                {
                    IsPassed = pass,
                    ScoreOrRatio = avgVal,
                    Score = avgVal, // 平均灰度值  
                    Message = pass ? "PASS" : $"平均灰度 {avgVal:F1} 不在 [{minGray}-{maxGray}] 范围内"//indexsim
                };
            }
        }
        // 算法 4：金字塔加速模板匹配
        private static InspectionResult RunPyramidMatching(Mat tpl, Mat smp, int maxLevels, double minScore)
        {
            /*
               参数 1（金字塔层数）：建议输入 2 或 3。
               参数 2（匹配得分下限）：建议输入 0.80 ~ 0.90（全符合为 1.0）。 相似度 
             */
            // 防护：防止金字塔层数过大导致图像尺寸小于 1  3
            maxLevels = Math.Max(1, Math.Min(maxLevels, 5));
            using (Mat grayTpl = new Mat())
            using (Mat graySmp = new Mat())
            {
                Cv2.CvtColor(tpl, grayTpl, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(smp, graySmp, ColorConversionCodes.BGR2GRAY);
                Mat currentTpl = grayTpl.Clone();
                Mat currentSmp = graySmp.Clone();
                // 1. 构建下采样金字塔（缩减图像尺寸加速计算）
                for (int level = 0; level < maxLevels - 1; level++) 
                {
                    if (currentTpl.Width / 2 < 8 || currentTpl.Height / 2 < 8) break;
                    Mat nextTpl = new Mat();
                    Mat nextSmp = new Mat();
                    Cv2.PyrDown(currentTpl, nextTpl);  
                    Cv2.PyrDown(currentSmp, nextSmp);  
                    currentTpl.Dispose();    
                    currentSmp.Dispose();   
                    currentTpl = nextTpl;
                    currentSmp = nextSmp;
                }
                // 2. 顶层匹配（归一化相关系数匹配）dingjisoo
                using (Mat result = new Mat())
                {
                    Cv2.MatchTemplate(currentSmp, currentTpl, result, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);
                    currentTpl.Dispose();
                    currentSmp.Dispose();
                    bool pass = maxVal >= minScore;
                    return new InspectionResult 
                    {
                        IsPassed = pass,
                        ScoreOrRatio = maxVal,
                        Score = maxVal,
                        Message = pass ? "PASS" : $"匹配得分 {maxVal:F2} 未达到阈值 {minScore:F2}"

                    };
                }
            }
        }

        /// <summary>
        /// 算法 5：焊锡面积对比
        ///
        /// ParamThreshold：二值化阈值
        /// ParamRatio：当前焊锡面积 / 模板焊锡面积 的最小允许比例
        ///
        /// 例如：
        /// Threshold = 150
        /// MinRatio = 0.75
        ///
        /// 当前焊锡面积达到模板的 75% 以上 = PASS 
        /// 当前焊锡面积低于模板的 75% = NG
        /// </summary>
        private static InspectionResult RunSolderAreaCompare( Mat tpl, Mat smp, double threshold, double minRatio)
        {
            try
            {
                using Mat grayTpl = new Mat();
                using Mat graySmp = new Mat();
                using Mat binaryTpl = new Mat();
                using Mat binarySmp = new Mat();
                // =========================
                // 1. 转灰度
                // =========================
                Cv2.CvtColor( tpl,  grayTpl, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor( smp, graySmp,  ColorConversionCodes.BGR2GRAY);
                // =========================
                // 2. 轻微高斯滤波
                // 去除相机噪声
                // =========================
                Cv2.GaussianBlur(  grayTpl, grayTpl, new Size(3, 3), 0);
                Cv2.GaussianBlur(  graySmp, graySmp, new Size(3, 3), 0);
                // =========================
                // 3. 二值化
                // 灰度 >= threshold 阈值
                // 认为是焊锡亮区域
                // =========================
                Cv2.Threshold( grayTpl,  binaryTpl, threshold,  255, ThresholdTypes.Binary);
                Cv2.Threshold(  graySmp, binarySmp, threshold, 255, ThresholdTypes.Binary);
                // =========================
                // 4. 形态学处理
                // Open：去除小噪点
                // Close：填补焊锡内部小孔
                // =========================
                using Mat kernel = Cv2.GetStructuringElement(  MorphShapes.Ellipse,  new Size(3, 3));
                Cv2.MorphologyEx(  binaryTpl,  binaryTpl,  MorphTypes.Open,  kernel);// 开运算
                Cv2.MorphologyEx(  binaryTpl, binaryTpl, MorphTypes.Close, kernel);  // 闭运算
                Cv2.MorphologyEx( binarySmp, binarySmp, MorphTypes.Open,  kernel);   // 开运算
                Cv2.MorphologyEx(  binarySmp,  binarySmp, MorphTypes.Close, kernel); // 闭运算
                // =========================
                // 5. 计算焊锡像素面积
                // =========================
                int templateSolderArea = Cv2.CountNonZero(binaryTpl);
                int sampleSolderArea = Cv2.CountNonZero(binarySmp);
                // =========================
                // 6. 模板保护
                // =========================
                if (templateSolderArea <= 0)
                {
                    return new InspectionResult
                    {
                        IsPassed = false,
                        Score = 0,
                        ScoreOrRatio = 0,
                        Message =
                            $"NG，模板焊锡面积为 0，请检查 ROI 或 Threshold={threshold:F0}"
                    };
                }
                // =========================
                // 7. 当前面积 / 模板面积
                // =========================
                double areaRatio =(double)sampleSolderArea / templateSolderArea;//白色占比
                // =========================
                // 8. 判断
                // =========================
                bool pass = areaRatio >= minRatio;
                // =========================
                // 9. 返回结果
                // =========================
                string message;
                if (pass)
                {
                    message =$"PASS，焊锡面积比={areaRatio:P1}，" +$"当前={sampleSolderArea}px，" + $"模板={templateSolderArea}px";
                }
                else
                {
                    message =  $"NG，焊锡面积不足，" +  $"面积比={areaRatio:P1}，" + $"要求≥{minRatio:P1}，" + $"当前={sampleSolderArea}px，" + $"模板={templateSolderArea}px";
                }
                return new InspectionResult//回传检测结果到界面
                {
                    IsPassed = pass,
                    Score = areaRatio,
                    ScoreOrRatio = areaRatio,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                return new InspectionResult
                {
                    IsPassed = false,
                    Score = 0,
                    ScoreOrRatio = 0,
                    Message = $"焊锡面积检测异常：{ex.Message}"
                };
            }
        }
        /// <summary>
        /// 算法 6：焊锡轮廓面积检测
        ///
        /// ParamThreshold：二值化阈值
        /// ParamRatio：当前最大焊锡轮廓面积 / 模板最大焊锡轮廓面积
        ///             的最小允许比例
        ///
        /// 例如：
        /// Threshold = 150
        /// MinRatio = 0.70
        ///
        /// 当前主要焊锡轮廓达到模板的 70% = PASS
        /// 否则 = NG
        /// </summary>
        private static InspectionResult RunSolderContourDetection(  Mat tpl, Mat smp, double threshold, double minRatio)
        {
            try
            {
                // =========================
                // 模板图处理
                // =========================
                using Mat grayTpl = new Mat();
                using Mat binaryTpl = new Mat();
                Cv2.CvtColor( tpl,  grayTpl, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur( grayTpl, grayTpl, new Size(3, 3),  0);
                Cv2.Threshold(  grayTpl,  binaryTpl, threshold, 255,  ThresholdTypes.Binary);
                // =========================
                // 当前图处理
                // =========================
                using Mat graySmp = new Mat();
                using Mat binarySmp = new Mat();
                Cv2.CvtColor(  smp, graySmp, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(  graySmp, graySmp,  new Size(3, 3),  0);
                Cv2.Threshold(  graySmp, binarySmp, threshold, 255,  ThresholdTypes.Binary);
                // =========================
                // 形态学去噪
                // =========================
                using Mat kernel = Cv2.GetStructuringElement( MorphShapes.Ellipse, new Size(3, 3));
                Cv2.MorphologyEx( binaryTpl, binaryTpl,  MorphTypes.Open, kernel);
                Cv2.MorphologyEx( binaryTpl, binaryTpl, MorphTypes.Close, kernel);
                Cv2.MorphologyEx( binarySmp, binarySmp, MorphTypes.Open, kernel);
                Cv2.MorphologyEx( binarySmp, binarySmp, MorphTypes.Close, kernel);
                // =========================
                // 找模板最大轮廓
                // =========================
                Cv2.FindContours( binaryTpl, out Point[][] templateContours,  out HierarchyIndex[] _,  RetrievalModes.External,  ContourApproximationModes.ApproxSimple);
                double templateMaxArea = 0;
                foreach (Point[] contour in templateContours)
                {
                    double area =  Cv2.ContourArea(contour);
                    if (area > templateMaxArea)
                    {
                        templateMaxArea = area;
                    }
                }
                // 找当前图最大轮廓
                Cv2.FindContours( binarySmp,  out Point[][] sampleContours,  out HierarchyIndex[] _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                double sampleMaxArea = 0;
                foreach (Point[] contour in sampleContours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area > sampleMaxArea)
                    {
                        sampleMaxArea = area;
                    }
                }
                // 模板保护
                if (templateMaxArea <= 0)//未检测到模板焊锡轮廓
                {
                    return new InspectionResult
                    {
                        IsPassed = false,
                        Score = 0,
                        ScoreOrRatio = 0,
                        Message = $"NG，未检测到模板焊锡轮廓，请检查 ROI 或 Threshold={threshold:F0}"
                    };
                }
                // 当前轮廓面积 / 模板轮廓面积
                double contourRatio =  sampleMaxArea / templateMaxArea;
                // 判断
                bool pass = contourRatio >= minRatio;
                // 结果信息
                string message;
                if (pass)
                {
                    message = $"PASS，焊锡轮廓面积比={contourRatio:P1}，" + $"当前={sampleMaxArea:F0}px²，" + $"模板={templateMaxArea:F0}px²";
                }
                else
                {
                    message = $"NG，焊锡轮廓面积不足，" + $"面积比={contourRatio:P1}，" + $"要求≥{minRatio:P1}，" + $"当前={sampleMaxArea:F0}px²，" + $"模板={templateMaxArea:F0}px²";
                }
                return new InspectionResult
                {
                    IsPassed = pass, 
                    Score = contourRatio,
                    ScoreOrRatio = contourRatio,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                return new InspectionResult
                {
                    IsPassed = false,
                    Score = 0,
                    ScoreOrRatio = 0,
                    Message = $"焊锡轮廓检测异常：{ex.Message}"
                };
            }
        }
        /// <summary>
        /// 算法：HSV 焊锡 Mask 对比
        /// ParamThreshold：最大饱和度 S
        /// ParamRatio：最小重叠比例
        /// 建议初始：
        /// ParamThreshold = 100
        /// ParamRatio = 0.70
        /// </summary>
        private static InspectionResult RunSolderHsvMaskCompare(  Mat tpl,  Mat smp, double maxSaturation, double minIou)
        {
            try
            {
                using Mat hsvTpl = new Mat();//色彩空间 模板
                using Mat hsvSmp = new Mat();//色彩空间 待测
                // 1. BGR 转 HSV
                Cv2.CvtColor( tpl, hsvTpl, ColorConversionCodes.BGR2HSV);
                Cv2.CvtColor( smp, hsvSmp, ColorConversionCodes.BGR2HSV);
                // 2. 提取焊锡 Mask 
                // 灰白色/银色：
                // S 较低
                // V 有一定亮度
                using Mat maskTpl = new Mat();
                using Mat maskSmp = new Mat();
                Scalar lower = new Scalar( 0,   0,  60);
                Scalar upper = new Scalar(  180, maxSaturation, 255);
                Cv2.InRange(  hsvTpl, lower,  upper, maskTpl);
                Cv2.InRange( hsvSmp, lower, upper,  maskSmp);
                // 3. 形态学处理 开闭运算
                using Mat kernel = Cv2.GetStructuringElement( MorphShapes.Ellipse, new Size(3, 3));
                Cv2.MorphologyEx( maskTpl, maskTpl, MorphTypes.Open, kernel);
                Cv2.MorphologyEx( maskTpl, maskTpl, MorphTypes.Close, kernel);
                Cv2.MorphologyEx( maskSmp, maskSmp, MorphTypes.Open, kernel);
                Cv2.MorphologyEx( maskSmp, maskSmp, MorphTypes.Close,  kernel);          
                // 4. 模板面积             
                int tplArea =  Cv2.CountNonZero(maskTpl);
                int smpArea =   Cv2.CountNonZero(maskSmp);
                if (tplArea <= 0)
                {
                    return new InspectionResult
                    {
                        IsPassed = false,
                        Score = 0,
                        ScoreOrRatio = 0,
                        Message = "NG，模板未检测到焊锡区域"
                    };
                }
             
                // 5. 计算面积比 
                // ============================
                double areaRatio = (double)smpArea / tplArea;
                // ============================
                // 6. 计算 IoU
                // Intersection / Union
                // ============================
                using Mat intersection = new Mat();
                using Mat union = new Mat();
                Cv2.BitwiseAnd(  maskTpl,  maskSmp,  intersection);
                Cv2.BitwiseOr(  maskTpl, maskSmp,  union);
                int intersectionArea = Cv2.CountNonZero(intersection);
                int unionArea =  Cv2.CountNonZero(union);
                double iou = unionArea > 0  ? (double)intersectionArea / unionArea : 0;
                // ============================
                // 7. 判断
                // 同时判断：
                // 面积不能明显减少
                // Mask 重叠率必须足够高
                // ============================
                bool areaPass =  areaRatio >= minIou;
                bool iouPass =  iou >= minIou;
                bool pass =  areaPass && iouPass;
                string message;
                if (pass)
                {
                    message = $"PASS，面积比={areaRatio:P1}，" + $"IoU={iou:P1}";
                }
                else
                {
                    message = $"NG，面积比={areaRatio:P1}，" + $"IoU={iou:P1}，" + $"要求≥{minIou:P1}";
                }
                return new InspectionResult
                {
                    IsPassed = pass,
                    // Score 主要显示 IoU 
                    Score = iou,
                    ScoreOrRatio = iou,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                return new InspectionResult
                {
                    IsPassed = false,
                    Score = 0,
                    ScoreOrRatio = 0,
                    Message = $"HSV 焊锡检测异常：{ex.Message}"
                };
            }
        }     
        private static InspectionResult RunTemplateDiff( Mat tpl, Mat smp, double threshold, double maxRatio)
        {
            // 参数
            // 固定模板焊点 Mask
            // 连通区域最小面积
            const int minSolderComponentArea = 50;
            // 模板焊点 Mask 形态学处理
            const int solderCloseIterations = 2;
            const int solderOpenIterations = 1;
            // 缺失焊锡检测
            // 模板比待测亮多少，认为该位置可能缺少焊锡
            // 例如：
            // 模板 = 180
            // 待测 = 130
            // 差值 = 50
            const double missingDarkDiffThreshold = 35;
            // 缺失区域最小面积
            const int minMissingAreaPixels = 15;
            // 所有缺失区域总面积最大比例
            const double maxMissingRatio = 0.08;
            // 单个最大缺失区域最大比例
            const double maxSingleMissingRatio = 0.04;
            // 内部黑洞检测
            const double darkThreshold = 75;
            // 黑洞最小面积
            const int minHoleAreaPixels = 10;
            // 最大单个黑洞比例
            const double maxHoleRatio = 0.03;
            // 所有黑洞总面积比例
            const double maxTotalHoleRatio = 0.05;
            // 向内腐蚀次数
            const int innerErodeIterations = 2;
            // 亮白环检测
            // 待测比模板亮多少，认为可能是异常反光/亮白环
            const double brightDiffThreshold = 35;
            // 亮白区域最小面积
            const int minBrightAreaPixels = 15;//原10
            // 亮白区域最大总比例
            const double maxBrightRingRatio = 0.18;//0.45  45%
            // 单个最大亮白区域比例
            const double maxSingleBrightRingRatio = 0.10; //10%
            // 输出开始
            Debug.WriteLine("");
            Debug.WriteLine("==============================================================");
            Debug.WriteLine("【焊点综合检测开始 - 固定模板Mask版本】");
            Debug.WriteLine("==============================================================");
            Debug.WriteLine($"图像尺寸:");
            Debug.WriteLine($"  模板 tpl = {tpl.Width} x {tpl.Height}, 通道={tpl.Channels()}");
            Debug.WriteLine($"  待测 smp = {smp.Width} x {smp.Height}, 通道={smp.Channels()}");
            Debug.WriteLine("");
            Debug.WriteLine("【当前检测参数】");
            Debug.WriteLine($"  模板差异 threshold                = {threshold:F2}");
            Debug.WriteLine($"  模板差异 maxRatio(仅参考)         = {maxRatio:P2}");
            Debug.WriteLine($"  缺失灰度差阈值                    = {missingDarkDiffThreshold:F2}");
            Debug.WriteLine($"  最小缺失面积                      = {minMissingAreaPixels}");
            Debug.WriteLine($"  最大总缺失比例                    = {maxMissingRatio:P2}");
            Debug.WriteLine($"  最大单块缺失比例                  = {maxSingleMissingRatio:P2}");
            Debug.WriteLine($"  黑洞灰度阈值                      = {darkThreshold:F2}");
            Debug.WriteLine($"  最小黑洞面积                      = {minHoleAreaPixels}");
            Debug.WriteLine($"  最大单块黑洞比例                  = {maxHoleRatio:P2}");
            Debug.WriteLine($"  最大总黑洞比例                    = {maxTotalHoleRatio:P2}");
            Debug.WriteLine($"  亮白差异阈值                      = {brightDiffThreshold:F2}");
            Debug.WriteLine($"  最小亮白区域面积                  = {minBrightAreaPixels}");
            Debug.WriteLine($"  最大总亮白比例                    = {maxBrightRingRatio:P2}");
            Debug.WriteLine($"  最大单块亮白比例                  = {maxSingleBrightRingRatio:P2}");
            Debug.WriteLine($"  焊点内部腐蚀次数                  = {innerErodeIterations}");
            Debug.WriteLine("--------------------------------------------------------------");
            // ================================================================
            // 基本检查
            // ================================================================
            if (tpl == null || tpl.Empty())
                throw new ArgumentException("模板图 tpl 不能为空");
            if (smp == null || smp.Empty())
                throw new ArgumentException("待测图 smp 不能为空");
            if (tpl.Width != smp.Width || tpl.Height != smp.Height)
            {
                throw new ArgumentException( $"tpl 和 smp 尺寸必须相同。"+$"tpl={tpl.Width}x{tpl.Height}，"+ $"smp={smp.Width}x{smp.Height}");
            }
            // ================================================================
            // 图像处理 圆形阈值分割处理
            // ================================================================
            using (Mat grayTpl = new Mat())
            using (Mat graySmp = new Mat())
            using (Mat diff = new Mat())
            using (Mat kernel3 = Cv2.GetStructuringElement( MorphShapes.Ellipse, new Size(3, 3)))
            using (Mat kernel5 = Cv2.GetStructuringElement( MorphShapes.Ellipse, new Size(5, 5)))
            {
                // 1. 转灰度
                if (tpl.Channels() == 1)
                {
                    tpl.CopyTo(grayTpl);
                }
                else if (tpl.Channels() == 3)
                {
                    Cv2.CvtColor( tpl, grayTpl, ColorConversionCodes.BGR2GRAY);
                }
                else if (tpl.Channels() == 4)
                {
                    Cv2.CvtColor( tpl, grayTpl, ColorConversionCodes.BGRA2GRAY);
                }
                else
                {
                    throw new ArgumentException( $"tpl 不支持的通道数: {tpl.Channels()}");//通道数不支持 异常返回
                }
                if (smp.Channels() == 1)
                {
                    smp.CopyTo(graySmp);
                }
                else if (smp.Channels() == 3)
                {
                    Cv2.CvtColor(  smp, graySmp, ColorConversionCodes.BGR2GRAY);
                }
                else if (smp.Channels() == 4)
                {
                    Cv2.CvtColor( smp,  graySmp, ColorConversionCodes.BGRA2GRAY);
                }
                else
                {
                    throw new ArgumentException( $"smp 不支持的通道数: {smp.Channels()}");
                }
                // 灰度信息 Cv2.Min
                Cv2.MinMaxLoc( grayTpl, out double tplMin,out double tplMax);
                Cv2.MinMaxLoc( graySmp, out double smpMin, out double smpMax);
                Debug.WriteLine("");
                Debug.WriteLine("【灰度图信息】");
                Debug.WriteLine($"  模板灰度范围 = {tplMin:F0} ~ {tplMax:F0}");
                Debug.WriteLine($"  待测灰度范围 = {smpMin:F0} ~ {smpMax:F0}");
                // ============================================================
                // ① 模板整体差异
                // 注意：
                // 根据刚才5个焊点测试结果：
                // 正常焊点与漏焊焊点的差异比例存在重叠
                // 所以这里只记录数据，不直接决定最终NG
                // ============================================================
                
                Debug.WriteLine("");
                Debug.WriteLine("【① 模板整体差异检测 - 仅参考】");
                Cv2.Absdiff( grayTpl, graySmp, diff);
                Cv2.MinMaxLoc( diff, out double diffMinBefore, out double diffMaxBefore);
                Scalar diffMeanBefore = Cv2.Mean(diff);
                Debug.WriteLine("  Absdiff后:");
                Debug.WriteLine( $"    差异灰度范围 = {diffMinBefore:F0} ~ {diffMaxBefore:F0}");
                Debug.WriteLine( $"    平均差异值   = {diffMeanBefore.Val0:F2}");
                Cv2.Threshold( diff, diff, threshold, 255, ThresholdTypes.Binary);
                int diffCountAfterThreshold = Cv2.CountNonZero(diff);
                Cv2.MorphologyEx( diff, diff,  MorphTypes.Open, kernel3);
                int diffCount =  Cv2.CountNonZero(diff);
                double totalPixels =  tpl.Width * tpl.Height;
                double diffRatio = totalPixels > 0 ? diffCount / totalPixels : 0;
                bool templateDiffPassed =  diffRatio <= maxRatio;
                Debug.WriteLine( $"  Threshold({threshold:F0})后:");
                Debug.WriteLine( $"    差异像素数量 = {diffCountAfterThreshold}");
                Debug.WriteLine("  Open(3x3)后:");
                Debug.WriteLine( $"    最终差异像素 = {diffCount}");
                Debug.WriteLine( $"    总像素       = {totalPixels:F0}");
                Debug.WriteLine( $"    差异比例     = {diffRatio:P4}");
                Debug.WriteLine( $"    参数上限     = {maxRatio:P4}");
                Debug.WriteLine( $"    参考结果     = {(templateDiffPassed ? "PASS" : "NG")}");
                Debug.WriteLine( "    注意：模板整体差异不参与最终判定");
                // ==============================================
                // ② 建立固定模板焊点 Mask
                //
                // 核心：
                // 只从模板中找一次焊点
                // 后面待测图不再重新找“最大区域”
                // ===============================================
                Debug.WriteLine("【② 建立固定模板焊点Mask】");
                using (Mat tplSolderMask = new Mat())
                using (Mat tplLabels = new Mat())
                using (Mat tplStats = new Mat())
                using (Mat tplCentroids = new Mat())
                {
                    double tplOtsuThreshold =  Cv2.Threshold( grayTpl, tplSolderMask,  0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                    Debug.WriteLine( $"  模板Otsu自动阈值 = {tplOtsuThreshold:F2}");//
                    // 填补焊点内部小孔xiyoioh
                    Cv2.MorphologyEx( tplSolderMask, tplSolderMask,  MorphTypes.Close, kernel5, iterations: solderCloseIterations);
                    // 去掉小噪声
                    Cv2.MorphologyEx( tplSolderMask, tplSolderMask, MorphTypes.Open, kernel3, iterations: solderOpenIterations);
                    int tplLabelCount = Cv2.ConnectedComponentsWithStats(
                            tplSolderMask,
                            tplLabels,
                            tplStats,
                            tplCentroids,
                            PixelConnectivity.Connectivity8,
                            MatType.CV_32S);
                    Debug.WriteLine( $"  模板连通区域数量 = {tplLabelCount - 1}");
                    // ROI中心
                    double imageCenterX =  tpl.Width / 2.0;
                    double imageCenterY =  tpl.Height / 2.0;
                    int selectedTplLabel = -1;
                    int selectedTplArea = 0;
                    // 优先选择“靠近ROI中心”的区域
                    double bestDistance = double.MaxValue;
                    for (int i = 1; i < tplStats.Rows; i++)
                    {
                        int area = tplStats.At<int>( i,(int)ConnectedComponentsTypes.Area);
                        int left = tplStats.At<int>( i,(int)ConnectedComponentsTypes.Left);
                        int top = tplStats.At<int>( i, (int)ConnectedComponentsTypes.Top);
                        int width =  tplStats.At<int>( i, (int)ConnectedComponentsTypes.Width);
                        int height = tplStats.At<int>( i, (int)ConnectedComponentsTypes.Height);
                        double centerX = tplCentroids.At<double>(i, 0);
                        double centerY = tplCentroids.At<double>(i, 1);
                        double distance = Math.Sqrt( Math.Pow(centerX - imageCenterX, 2) +  Math.Pow(centerY - imageCenterY, 2));
                        Debug.WriteLine(
                            $"    区域[{i}] 面积={area}, " +
                            $"位置=({left},{top}), " +
                            $"尺寸={width}x{height}, " +
                            $"中心=({centerX:F1},{centerY:F1}), " +
                            $"距ROI中心={distance:F1}");
                        // 太小的区域忽略
                        if (area < minSolderComponentArea)
                            continue;
                        // 选择最靠近ROI中心的区域
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            selectedTplLabel = i;
                            selectedTplArea = area;
                        }
                    }
                    // 如果没有找到合格区域，退化为最大区域
                    if (selectedTplLabel < 0)
                    {
                        Debug.WriteLine( "  警告：没有找到足够大的中心区域，退化为最大区域");
                        int maxArea = 0;
                        for (int i = 1; i < tplStats.Rows; i++)
                        {
                            int area = tplStats.At<int>( i, (int)ConnectedComponentsTypes.Area);
                            if (area > maxArea)
                            {
                                maxArea = area;
                                selectedTplLabel = i;
                                selectedTplArea = area;
                            }
                        }
                    }
                    // 清空
                    using (Mat fixedTplMask = Mat.Zeros(  tplSolderMask.Size(), MatType.CV_8UC1))
                    {
                        if (selectedTplLabel > 0)
                        {
                            Cv2.Compare( tplLabels, selectedTplLabel, fixedTplMask, CmpTypes.EQ);
                            fixedTplMask.CopyTo(tplSolderMask);
                        }
                        else
                        {
                            tplSolderMask.SetTo(Scalar.Black);
                        }
                        double templateSolderArea = Cv2.CountNonZero(tplSolderMask);
                        Debug.WriteLine("");
                        Debug.WriteLine("  【固定模板Mask结果】");
                        Debug.WriteLine( $"    选中区域Label = {selectedTplLabel}");
                        Debug.WriteLine( $"    选中区域面积  = {selectedTplArea}");
                        Debug.WriteLine( $"    固定Mask面积  = {templateSolderArea:F0}");
                        Debug.WriteLine( $"    距ROI中心     = {bestDistance:F2}");
                        if (templateSolderArea <= 0)
                        {
                            return new InspectionResult
                            {
                                IsPassed = false,
                                ScoreOrRatio = 1,
                                Score = 1,
                                Message = "NG：无法从模板建立有效焊点Mask"
                            };
                        }
                        // ====================================================
                        // ③ 建立焊点内部 Mask
                        //
                        // 用于：
                        // 缺失检测
                        // 黑洞检测
                        //
                        // 去掉焊点边缘，减少边缘位置偏差影响
                        // ====================================================
                        using (Mat innerMask = tplSolderMask.Clone())
                        {
                            if (innerErodeIterations > 0)
                            {
                                Cv2.Erode(
                                    innerMask,
                                    innerMask,
                                    kernel3,
                                    iterations: innerErodeIterations);
                            }
                            double innerArea = Cv2.CountNonZero(innerMask);
                            Debug.WriteLine("");
                            Debug.WriteLine("【③ 焊点内部固定检测区域】");///检测固定AOI
                            Debug.WriteLine( $"  模板焊点Mask面积 = {templateSolderArea:F0}");
                            Debug.WriteLine( $"  腐蚀次数         = {innerErodeIterations}");
                            Debug.WriteLine( $"  最终内部面积     = {innerArea:F0}");
                            if (innerArea <= 0)
                            {
                                return new InspectionResult
                                {
                                    IsPassed = false,
                                    ScoreOrRatio = 1,
                                    Score = 1,
                                    Message = "NG：焊点内部Mask面积为0"
                                };
                            }
                            // ================================================
                            // ④ 缺失焊锡检测
                            //
                            // 模板 - 待测
                            //
                            // 只有待测明显比模板变暗才标记
                            // ================================================
                            Debug.WriteLine("");
                            Debug.WriteLine("【④ 焊锡缺失面积检测】");
                            using (Mat missingDiff = new Mat())
                            using (Mat missingMask = new Mat())
                            using (Mat missingLabels = new Mat())
                            using (Mat missingStats = new Mat())
                            using (Mat missingCentroids = new Mat())
                            {
                                // grayTpl - graySmp
                                // OpenCV Subtract 会自动饱和到 0
                                //
                                // 模板180，待测100
                                // => 80
                                //
                                // 模板100，待测180
                                // => 0
                                Cv2.Subtract( grayTpl, graySmp, missingDiff);
                                Cv2.Threshold(  missingDiff,  missingMask, missingDarkDiffThreshold,  255,  ThresholdTypes.Binary);
                                // 只检测固定焊点内部
                                Cv2.BitwiseAnd( missingMask, innerMask, missingMask);
                                int missingBeforeMorphology =  Cv2.CountNonZero(missingMask);
                                // 去掉小噪声
                                Cv2.MorphologyEx(  missingMask,  missingMask,  MorphTypes.Open, kernel3);
                                int missingAfterMorphology = Cv2.CountNonZero(missingMask);
                                Debug.WriteLine(
                                    $"  模板-待测差值阈值 = {missingDarkDiffThreshold:F0}");
                                Debug.WriteLine(
                                    $"  形态学前缺失像素 = {missingBeforeMorphology}");
                                Debug.WriteLine(
                                    $"  形态学后缺失像素 = {missingAfterMorphology}");
                                int missingLabelCount =  Cv2.ConnectedComponentsWithStats(   missingMask,  missingLabels,
                                        missingStats,
                                        missingCentroids,
                                        PixelConnectivity.Connectivity8,
                                        MatType.CV_32S);
                                Debug.WriteLine(
                                    $"  缺失连通区域数量 = {missingLabelCount - 1}");


                                double totalValidMissingArea = 0;
                                double maxMissingArea = 0;


                                for (int i = 1;
                                     i < missingStats.Rows;
                                     i++)
                                {
                                    int area =
                                        missingStats.At<int>(
                                            i,
                                            (int)ConnectedComponentsTypes.Area);

                                    int left =
                                        missingStats.At<int>(
                                            i,
                                            (int)ConnectedComponentsTypes.Left);

                                    int top =
                                        missingStats.At<int>(
                                            i,
                                            (int)ConnectedComponentsTypes.Top);

                                    int width =
                                        missingStats.At<int>(
                                            i,
                                            (int)ConnectedComponentsTypes.Width);

                                    int height = missingStats.At<int>(
                                            i,
                                            (int)ConnectedComponentsTypes.Height);
                                    Debug.WriteLine(
                                        $"    缺失[{i}] " +
                                        $"面积={area}, " +
                                        $"位置=({left},{top}), " +
                                        $"尺寸={width}x{height}" +
                                        (area < minMissingAreaPixels
                                            ? " → 忽略(面积太小)"
                                            : ""));
                                    if (area < minMissingAreaPixels)
                                        continue;
                                    totalValidMissingArea += area;
                                    if (area > maxMissingArea)
                                        maxMissingArea = area;
                                }
                                double missingRatio =  totalValidMissingArea / innerArea;
                                double singleMissingRatio = maxMissingArea / innerArea;//最大单块缺失面积占内部焊点面积的比例
                                bool missingPassed = missingRatio <= maxMissingRatio && singleMissingRatio <= maxSingleMissingRatio;
                                Debug.WriteLine("");
                                Debug.WriteLine("  【缺失检测结果】");
                                Debug.WriteLine( $"    有效缺失总面积   = {totalValidMissingArea:F0}");
                                Debug.WriteLine( $"    最大单块缺失面积 = {maxMissingArea:F0}");
                                Debug.WriteLine( $"    内部焊点面积     = {innerArea:F0}");
                                Debug.WriteLine( $"    总缺失比例       = {missingRatio:P4}");
                                Debug.WriteLine( $"    最大单块比例     = {singleMissingRatio:P4}");
                                Debug.WriteLine( $"    总缺失允许上限   = {maxMissingRatio:P4}");
                                Debug.WriteLine( $"    单块允许上限     = {maxSingleMissingRatio:P4}");
                                Debug.WriteLine( $"    结果             = {(missingPassed ? "PASS" : "NG")}");
                                // ================================================
                                // ⑤ 内部黑洞检测
                                // ================================================
                                Debug.WriteLine("");
                                Debug.WriteLine("【⑤ 内部黑洞检测】");
                                using (Mat darkMask = new Mat())
                                using (Mat holeMask = new Mat())
                                using (Mat holeLabels = new Mat())
                                using (Mat holeStats = new Mat())
                                using (Mat holeCentroids = new Mat())
                                {
                                    // 小于 darkThreshold
                                    // 认为是黑色候选
                                    Cv2.Threshold( graySmp, darkMask, darkThreshold,  255, ThresholdTypes.BinaryInv);
                                    // 只保留固定焊点内部
                                    Cv2.BitwiseAnd( darkMask, innerMask, holeMask);
                                    int holeBeforeMorphology = Cv2.CountNonZero(holeMask);
                                    Cv2.MorphologyEx( holeMask, holeMask, MorphTypes.Open, kernel3);
                                    int holeAfterMorphology = Cv2.CountNonZero(holeMask);
                                    Debug.WriteLine( $"  黑洞阈值         = {darkThreshold:F0}");
                                    Debug.WriteLine( $"  形态学前黑洞像素 = {holeBeforeMorphology}");
                                    Debug.WriteLine( $"  形态学后黑洞像素 = {holeAfterMorphology}");
                                    int holeLabelCount = Cv2.ConnectedComponentsWithStats( holeMask, holeLabels, holeStats, holeCentroids, PixelConnectivity.Connectivity8, MatType.CV_32S);
                                    Debug.WriteLine( $"  黑洞连通区域数量 = {holeLabelCount - 1}");
                                    double totalHoleArea = 0;//
                                    double maxHoleArea = 0;//
                                    for (int i = 1;  i < holeStats.Rows;i++)
                                    {
                                        int holeArea = holeStats.At<int>( i,(int)ConnectedComponentsTypes.Area);
                                        int left = holeStats.At<int>( i, (int)ConnectedComponentsTypes.Left);
                                        int top =  holeStats.At<int>( i, (int)ConnectedComponentsTypes.Top);
                                        int width = holeStats.At<int>(  i, (int)ConnectedComponentsTypes.Width);
                                        int height =  holeStats.At<int>( i, (int)ConnectedComponentsTypes.Height);
                                        Debug.WriteLine( $"    黑色洞[{i}] " + $"面积={holeArea}, " + $"位置=({left},{top}), " + $"尺寸={width}x{height}" + (holeArea < minHoleAreaPixels ? " → 忽略(面积太小)" : ""));
                                        if (holeArea < minHoleAreaPixels)
                                            continue;
                                        totalHoleArea += holeArea;
                                        if (holeArea > maxHoleArea)
                                            maxHoleArea = holeArea;
                                    }
                                    double holeRatio = maxHoleArea / innerArea;
                                    double totalHoleRatio = totalHoleArea / innerArea;
                                    bool holePassed =  holeRatio <= maxHoleRatio &&  totalHoleRatio <= maxTotalHoleRatio;
                                    Debug.WriteLine("");
                                    Debug.WriteLine("  【黑洞检测结果】");
                                    Debug.WriteLine( $"    最大单块黑洞面积 = {maxHoleArea:F0}");
                                    Debug.WriteLine( $"    有效黑洞总面积   = {totalHoleArea:F0}");
                                    Debug.WriteLine( $"    内部焊点面积     = {innerArea:F0}");
                                    Debug.WriteLine( $"    最大单块比例     = {holeRatio:P4}");
                                    Debug.WriteLine( $"    总黑洞比例       = {totalHoleRatio:P4}");
                                    Debug.WriteLine( $"    单块允许上限     = {maxHoleRatio:P4}");
                                    Debug.WriteLine( $"    总黑洞允许上限   = {maxTotalHoleRatio:P4}");
                                    Debug.WriteLine( $"    结果             = {(holePassed ? "PASS" : "NG")}");
                                    // ============================================
                                    // ⑥ 亮白环检测
                                    //
                                    // 待测 - 模板
                                    //
                                    // 待测比模板明显亮
                                    // ============================================
                                    Debug.WriteLine("");
                                    Debug.WriteLine("【⑥ 亮白环/异常高亮检测】");
                                    using (Mat brightDiff = new Mat())
                                    using (Mat brightMask = new Mat())
                                    using (Mat brightLabels = new Mat())
                                    using (Mat brightStats = new Mat())
                                    using (Mat brightCentroids = new Mat())
                                    {
                                        // 待测 - 模板
                                        Cv2.Subtract(
                                            graySmp,
                                            grayTpl,
                                            brightDiff);
                                        Cv2.Threshold(
                                            brightDiff,
                                            brightMask,
                                            brightDiffThreshold,
                                            255,
                                            ThresholdTypes.Binary);
                                        // 限制在模板焊点区域内
                                        Cv2.BitwiseAnd(
                                            brightMask,
                                            tplSolderMask,
                                            brightMask);
                                        int brightBeforeMorphology = Cv2.CountNonZero(brightMask);
                                        Cv2.MorphologyEx(
                                            brightMask,
                                            brightMask,
                                            MorphTypes.Open,
                                            kernel3);
                                        int brightAfterMorphology =
                                            Cv2.CountNonZero(brightMask);
                                        Debug.WriteLine(
                                            $"  待测-模板亮度差阈值 = {brightDiffThreshold:F0}");
                                        Debug.WriteLine(
                                            $"  形态学前亮白像素   = {brightBeforeMorphology}");
                                        Debug.WriteLine(
                                            $"  形态学后亮白像素   = {brightAfterMorphology}");
                                        int brightLabelCount =
                                            Cv2.ConnectedComponentsWithStats(
                                                brightMask,
                                                brightLabels,
                                                brightStats,
                                                brightCentroids,
                                                PixelConnectivity.Connectivity8,
                                                MatType.CV_32S);
                                        Debug.WriteLine(
                                            $"  亮白连通区域数量 = {brightLabelCount - 1}");
                                        double totalBrightArea = 0;
                                        double maxBrightArea = 0;
                                        for (int i = 1; i < brightStats.Rows; i++)
                                        {
                                            int brightArea = brightStats.At<int>( i,(int)ConnectedComponentsTypes.Area);
                                            int left = brightStats.At<int>( i, (int)ConnectedComponentsTypes.Left);
                                            int top =  brightStats.At<int>( i, (int)ConnectedComponentsTypes.Top);
                                            int width = brightStats.At<int>( i, (int)ConnectedComponentsTypes.Width);
                                            int height = brightStats.At<int>( i, (int)ConnectedComponentsTypes.Height);
                                            Debug.WriteLine( $"    亮白[{i}] " +  $"面积={brightArea}, " +  $"位置=({left},{top}), " +
                                                $"尺寸={width}x{height}" + (brightArea < minBrightAreaPixels  ? " → 忽略(面积太小)"  : ""));
                                            if (brightArea < minBrightAreaPixels)
                                                continue;
                                            totalBrightArea += brightArea;
                                            if (brightArea > maxBrightArea)
                                                maxBrightArea = brightArea;
                                        }
                                        double brightRatio =  totalBrightArea / templateSolderArea;
                                        double singleBrightRatio =  maxBrightArea / templateSolderArea;
                                        bool brightPassed =  brightRatio <= maxBrightRingRatio &&  singleBrightRatio <= maxSingleBrightRingRatio;
                                        Debug.WriteLine("");
                                        Debug.WriteLine("  【亮白环检测结果】");
                                        Debug.WriteLine( $"    有效亮白总面积   = {totalBrightArea:F0}");
                                        Debug.WriteLine( $"    最大单块亮白面积 = {maxBrightArea:F0}");
                                        Debug.WriteLine( $"    模板焊点面积     = {templateSolderArea:F0}");
                                        Debug.WriteLine( $"    总亮白比例       = {brightRatio:P4}");
                                        Debug.WriteLine( $"    最大单块比例     = {singleBrightRatio:P4}");
                                        Debug.WriteLine( $"    总亮白允许上限   = {maxBrightRingRatio:P4}");
                                        Debug.WriteLine( $"    单块允许上限     = {maxSingleBrightRingRatio:P4}");
                                        Debug.WriteLine( $"    结果             = {(brightPassed ? "PASS" : "NG")}");
                                        // ========================================
                                        // 最终判断
                                        //
                                        // 模板整体差异不参与最终判断
                                        // ========================================
                                        bool pass = missingPassed && holePassed && brightPassed;
                                        Debug.WriteLine("");
                                        Debug.WriteLine("==============================================================");
                                        Debug.WriteLine("【最终检测结果】");
                                        Debug.WriteLine("--------------------------------------------------------------");
                                        Debug.WriteLine(
                                            $"  ① 模板整体差异(参考) : " +
                                            $"{(templateDiffPassed ? "PASS" : "NG")}");
                                        Debug.WriteLine(
                                            $"     实际={diffRatio:P4}, 参数上限={maxRatio:P4}");
                                        Debug.WriteLine(
                                            $"     不参与最终判定");
                                        Debug.WriteLine(
                                            $"  ② 焊锡缺失 : " +
                                            $"{(missingPassed ? "PASS" : "NG")}");
                                        Debug.WriteLine(
                                            $"     总缺失={missingRatio:P4}, " +
                                            $"最大单块={singleMissingRatio:P4}");
                                        Debug.WriteLine(
                                            $"  ③ 内部黑洞 : " +
                                            $"{(holePassed ? "PASS" : "NG")}");
                                        Debug.WriteLine(
                                            $"     最大单块={holeRatio:P4}, " +
                                            $"总黑洞={totalHoleRatio:P4}");
                                        Debug.WriteLine(
                                            $"  ④ 亮白环 : " +
                                            $"{(brightPassed ? "PASS" : "NG")}");
                                        Debug.WriteLine(
                                            $"     总亮白={brightRatio:P4}, " +
                                            $"最大单块={singleBrightRatio:P4}");
                                        Debug.WriteLine("--------------------------------------------------------------");
                                        Debug.WriteLine(
                                            $"  ★ 最终结果 : {(pass ? "PASS" : "NG")}");
                                        Debug.WriteLine("==============================================================");
                                        Debug.WriteLine("");
                                        // ========================================
                                        // 返回结果
                                        // ========================================
                                        string message;
                                        if (pass)
                                        {
                                            message = $"PASS | " + $"缺失={missingRatio:P2} | " + $"黑洞={totalHoleRatio:P2} | " +  $"亮白={brightRatio:P2}";
                                        }
                                        else
                                        {
                                            List<string> ngMessages =  new List<string>();
                                            if (!missingPassed)
                                            {
                                                ngMessages.Add( $"焊锡缺失NG(" + $"总={missingRatio:P2}," +  $"单块={singleMissingRatio:P2})");
                                            }
                                            if (!holePassed)
                                            {
                                                ngMessages.Add(
                                                    $"黑洞NG(" +
                                                    $"最大={holeRatio:P2}," +
                                                    $"总={totalHoleRatio:P2})");
                                            }
                                            if (!brightPassed)
                                            {
                                                ngMessages.Add(
                                                    $"亮白环NG(" +
                                                    $"总={brightRatio:P2}," +
                                                    $"单块={singleBrightRatio:P2})");
                                            }
                                            message =  string.Join("；", ngMessages);
                                        }
                                        // Score 使用最严重缺陷比例
                                        double score =  Math.Max( missingRatio,  Math.Max( totalHoleRatio, brightRatio));
                                        return new InspectionResult
                                        {
                                            IsPassed = pass,
                                            ScoreOrRatio = score,
                                            Score = score,
                                            Message = message
                                        };
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        //检测元件
        private static InspectionResult RunTemplateDiff2( Mat tpl, Mat smp, double threshold, double maxRatio)
        {
            using (Mat grayTpl = new Mat())
            using (Mat graySmp = new Mat())
            using (Mat blurTpl = new Mat())
            using (Mat blurSmp = new Mat())
            using (Mat mask = new Mat())
            using (Mat sampleMask = new Mat())
            using (Mat innerMask = new Mat( tpl.Rows, tpl.Cols, MatType.CV_8UC1, Scalar.Black))
            {
                // 1. 转灰度
                Cv2.CvtColor( tpl, grayTpl, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor( smp, graySmp, ColorConversionCodes.BGR2GRAY);
                // 2. 轻微高斯模糊
                // 消除相机噪声、纹理、小亮点影响
                Cv2.GaussianBlur( grayTpl, blurTpl, new OpenCvSharp.Size(3, 3), 0);
                Cv2.GaussianBlur( graySmp, blurSmp, new OpenCvSharp.Size(3, 3), 0);
                // 3. 创建有效检测区域
                // 去掉 ROI 四周一部分区域
                // 避免白色框线、丝印边缘影响
                int marginX = Math.Max( 2, (int)(tpl.Cols * 0.12));
                int marginY = Math.Max( 2, (int)(tpl.Rows * 0.08));
                int innerX = marginX;
                int innerY = marginY;
                int innerW = tpl.Cols - marginX * 2;
                int innerH = tpl.Rows - marginY * 2;
                if (innerW <= 0 || innerH <= 0)
                {
                    return new InspectionResult
                    {
                        IsPassed = false,
                        ScoreOrRatio = 1,
                        Score = 1,
                        Message = "ROI 尺寸过小，无法检测"
                    };
                }
                Cv2.Rectangle(innerMask,new OpenCvSharp.Rect(innerX,innerY,innerW,innerH),Scalar.White,-1);
                // 4. 从模板自动获取元件亮区阈值
                // 使用 Otsu 自动分析模板
                double otsuThreshold = Cv2.Threshold( blurTpl, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                // 防止 Otsu 阈值过低
                // threshold 参数可以作为最低亮度限制
                double brightThreshold =  Math.Max(otsuThreshold, threshold);
                // 重新生成模板亮区 Mask
                Cv2.Threshold( blurTpl, mask, brightThreshold, 255, ThresholdTypes.Binary);
                // 5. 去掉 ROI 边缘
                // 只保留内部真正需要检测的位置
                Cv2.BitwiseAnd(mask,innerMask,mask);
                // 6. 形态学处理
                // 去除小噪点
                // 连接元件内部亮区
                using (Mat kernel = Cv2.GetStructuringElement( MorphShapes.Rect, new OpenCvSharp.Size(3, 3)))
                {
                    Cv2.MorphologyEx(mask,mask,MorphTypes.Open, kernel);
                    Cv2.MorphologyEx(mask,mask,MorphTypes.Close,kernel);
                }
                // 7. 检查模板中是否成功找到亮区
                int templateBrightCount = Cv2.CountNonZero(mask);
                if (templateBrightCount < 5)
                {
                    return new InspectionResult
                    {
                        IsPassed = false,
                        ScoreOrRatio = 1,
                        Score = 1,
                        Message = "模板中未找到有效元件亮区"
                    };
                }
                // 8. 使用同一个阈值检测当前图片
                Cv2.Threshold( blurSmp, sampleMask, brightThreshold, 255, ThresholdTypes.Binary);
                // 只保留模板中原本应该有元件的位置
                Cv2.BitwiseAnd( sampleMask, mask, sampleMask);
                // 9. 计算当前元件存在比例
                //
                // 模板亮区有多少
                // 当前相同区域还有多少亮区
                int currentBrightCount = Cv2.CountNonZero(sampleMask);
                double existRatio = (double)currentBrightCount / templateBrightCount;
                // 10. 同时计算亮度平均值
                // 防止面积接近但实际颜色已经变化
                Scalar templateMean =  Cv2.Mean(blurTpl, mask);
                Scalar sampleMean = Cv2.Mean(blurSmp, mask);
                double brightnessRatio = templateMean.Val0 <= 0  ? 0  : sampleMean.Val0 / templateMean.Val0;
                // 11. 综合评分
                // existRatio：
                // 元件亮区存在面积比例
                // brightnessRatio：
                // 元件区域平均亮度比例
                // 以面积比例作为主要判定
                bool pass = existRatio >= maxRatio;
                // 如果面积看起来正常，但亮度下降太多
                // 同样判 NG
                if (brightnessRatio < 0.65)
                {
                    pass = false;
                }
                string message;
                if (pass)
                {
                    message = $"PASS | 存在面积={existRatio:P1} | " + $"亮度比例={brightnessRatio:P1}";
                }
                else
                {
                    message = $"NG | 存在面积={existRatio:P1} | " +  $"亮度比例={brightnessRatio:P1}";
                }
                return new InspectionResult
                {
                    IsPassed = pass,
                    Score = existRatio,// Score 越大表示越正常
                    ScoreOrRatio = existRatio,
                    Message = message
                };
            }
        }
        /// <summary>
        /// 将任意常见图像转换为8位单通道灰度图
        /// 支持 Gray / BGR / BGRA
        /// </summary>
        private static Mat ToGray8(Mat src)
        {
            if (src == null || src.Empty())
                throw new ArgumentException("输入图像为空");
            Mat gray = new Mat();
            // 根据通道数转换
            switch (src.Channels())
            {
                case 1:
                    src.CopyTo(gray);
                    break;
                case 3:
                    Cv2.CvtColor(
                        src,
                        gray,
                        ColorConversionCodes.BGR2GRAY);
                    break;

                case 4:
                    Cv2.CvtColor(
                        src,
                        gray,
                        ColorConversionCodes.BGRA2GRAY);
                    break;

                default:
                    throw new NotSupportedException(
                        $"不支持的图像通道数: {src.Channels()}");
            }

            // CLAHE要求8U或16U
            // 统一转换为8位
            if (gray.Depth() != MatType.CV_8U)
            {
                Mat gray8 = new Mat();

                // 自动归一化到0~255
                Cv2.Normalize(
                    gray,
                    gray8,
                    0,
                    255,
                    NormTypes.MinMax);

                gray.Dispose();
                gray = new Mat();

                gray8.ConvertTo(gray, MatType.CV_8U);

                gray8.Dispose();
            }

            return gray;
        }
    }
}