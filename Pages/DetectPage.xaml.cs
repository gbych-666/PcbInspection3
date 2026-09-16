using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using PcbInspection.Models;
using PcbInspection.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;

namespace PcbInspection.Pages
{
    public partial class DetectPage : Page
    {
        private ICameraService CameraService => MainWindow.CameraService;
        private static bool _isLiveStreamRunning = false;
        private InspectionProject _currentProject;
        private BitmapSource _currentCapturedFrame;
        // ==============================
        // 实时画面优化
       // ==============================
        // 永远只保留最新一帧
        private BitmapSource _pendingLiveFrame;
        // 防止每一帧都向 Dispatcher 队列添加任务
        private bool _liveRenderScheduled = false;
        // 实时帧锁
        private readonly object _liveFrameLock = new object();
        // ==============================
        // 检测结果
        // ==============================
        private class RoiResultInfo
        {
            public RoiRect PixelRoi { get; set; }
            public bool IsPassed { get; set; }
            public double DiffRatio { get; set; }
            public string Message { get; set; }
        }
        private List<RoiResultInfo> _lastRoiResults =  new List<RoiResultInfo>();
        private double ManualOffsetX = 0.0;
        private double ManualOffsetY = 0.0; 
        // =====================================================
        // 当日统计
        // =====================================================
        private DailyStatistics _dailyStatistics = new DailyStatistics();
        /// <summary>
        /// 统计文件根目录
        /// </summary>
        private string StatisticsRootDirectory =>  Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "Data");
        public DetectPage()
        {
            InitializeComponent();
            Loaded += DetectPage_Loaded;
            Unloaded += DetectPage_Unloaded;
            // 键盘快捷键
            // PreviewKeyDown += DetectPage_PreviewKeyDown;
        }
        /// <summary>
        /// 提供给 MainWindow 调用的“开始检测”入口
        /// 键盘、按钮、外部触发都统一从这里进入
        /// </summary>
        public void TriggerStartDetect()
        {
            // 确保在 UI 线程执行
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(TriggerStartDetect));
                return;
            }
            // 直接调用原来的检测按钮事件
            BtnStartDetect_Click(BtnStartDetect, new RoutedEventArgs());
        }
        // =====================================================
        // 键盘快捷键
        // 数字键 1 = 开始检测
        // =====================================================
        private void DetectPage_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // 主键盘数字 1
            if (e.Key == System.Windows.Input.Key.D1)
            {
                e.Handled = true;
                // 如果当前正在检测，不重复触发
                if (!BtnStartDetect.IsEnabled)
                    return;
                BtnStartDetect_Click( BtnStartDetect, new RoutedEventArgs(Button.ClickEvent));
            }
        }
        // =====================================================
        // 页面加载
        // =====================================================
        private void DetectPage_Loaded(object sender, RoutedEventArgs e)
        {
            CameraService.ImageCaptured -= OnCameraImageCaptured;
            CameraService.ImageCaptured += OnCameraImageCaptured;
            UpdateBtnState(_isLiveStreamRunning);
            LoadProjectList();
            // 加载当天检测统计
            LoadDailyStatistics();
        }
        // =====================================================
        // 页面卸载
        // =====================================================
        private void DetectPage_Unloaded(object sender, RoutedEventArgs e)
        {
            CameraService.ImageCaptured -= OnCameraImageCaptured;
        }
        // =====================================================
        // 加载检测项目
        // =====================================================
        private void LoadProjectList()
        {
            try
            {
                var projects = ProjectManagerService.GetProjectList();
                CmbProjects.ItemsSource = projects;
                if (projects != null && projects.Count > 0)
                {
                    CmbProjects.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine( $"加载项目列表失败：{ex.Message}");
            }
        }
        // =====================================================
        // 接收到相机图像
        // 注意：
        // 此方法运行在 AForge 相机采集线程
        // 非常重要：
        // 这里绝对不能执行耗时操作
        // 这里只做：
        // 1. 保存最新帧
        // 2. 通知 WPF 刷新
        // 不执行：
        // ❌ OpenCV
        // ❌ 保存图片
        // ❌ ROI检测
        // ❌ MessageBox
        // ❌ Dispatcher.Invoke
        // =====================================================
        private void OnCameraImageCaptured(BitmapSource frame)
        {
            if (frame == null)
                return;
            try
            {
                // =================================================
                // BitmapSource 已经在 UsbCameraService 中 Freeze
                // 这里再保险处理一次
                // =================================================
                if (frame.CanFreeze && !frame.IsFrozen)
                {
                    frame.Freeze();
                }
            }
            catch
            {
                // Freeze失败不影响实时显示
            }
            lock (_liveFrameLock)
            {
                // =================================================
                // 永远只保存最新一帧
                // 如果UI处理不过来：
                // Frame1
                // Frame2
                // Frame3
                // Frame4
                // 最终只保留 Frame4
                // 不会堆积1000张图片
                // =================================================
                _pendingLiveFrame = frame;
                // =================================================
                // 如果已经有一个UI刷新任务
                // 就不要重复添加
                // =================================================
                if (_liveRenderScheduled)
                    return;
                _liveRenderScheduled = true;
            }
            try
            {
                // =================================================
                // 非阻塞提交UI
                // 注意：
                // 必须 BeginInvoke
                // 不要使用 Dispatcher.Invoke
                // =================================================
                Dispatcher.BeginInvoke( System.Windows.Threading.DispatcherPriority.Render,new Action(UpdateLiveImage));
            }
            catch
            {
                lock (_liveFrameLock)
                {
                    _liveRenderScheduled = false;
                }
            }
        }
        // =====================================================
        // UI线程显示最新画面
        // 特点：
        // 永远显示最新帧
        // 不堆积 Dispatcher 任务
        // =====================================================
        private void UpdateLiveImage()
        {
            try
            {
                BitmapSource latestFrame = null;
                lock (_liveFrameLock)
                {
                    latestFrame = _pendingLiveFrame;
                    // 取走当前最新帧
                    _pendingLiveFrame = null;
                    // 当前UI任务完成
                    _liveRenderScheduled = false;
                }
                // =================================================
                // 显示最新帧
                // =================================================
                if (latestFrame != null)
                {
                    ImgLive.Source = latestFrame;
                }
                // =================================================
                // 检查UI刷新过程中是否又来了新帧
                // =================================================
                bool needUpdateAgain = false;
                lock (_liveFrameLock)
                {
                    if (_pendingLiveFrame != null && !_liveRenderScheduled)
                    {
                        _liveRenderScheduled = true;
                        needUpdateAgain = true;
                    }
                }
                // =================================================
                // 如果又来了新帧，再提交一次UI刷新
                // =================================================
                if (needUpdateAgain)
                {
                    Dispatcher.BeginInvoke(  System.Windows.Threading.DispatcherPriority.Render, new Action(UpdateLiveImage));
                }
            }
            catch (Exception ex)
            {
                lock (_liveFrameLock)
                {
                    _liveRenderScheduled = false;
                }
                Debug.WriteLine( $"更新实时画面失败：{ex.Message}");
            }
        }
        // =====================================================
        // 开始 / 停止实时采集
        // =====================================================
        private void BtnToggleLive_Click( object sender, RoutedEventArgs e)
        {
            try
            {
                // =================================================
                // 开始采集
                // =================================================
                if (!_isLiveStreamRunning)
                {
                    string savedMoniker = AppConfig.GetCameraMoniker();
                    bool openSuccess = false;
                    // =================================================
                    // 优先打开配置保存的相机
                    // =================================================
                    if (!string.IsNullOrWhiteSpace(savedMoniker))
                    {
                        if (CameraService is UsbCameraService usbCamera)
                        {
                            try
                            {
                                openSuccess = usbCamera.OpenCameraByMoniker( savedMoniker);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine( $"按Moniker打开相机失败：{ex.Message}");
                            }
                        }
                    }
                    // =================================================
                    // 配置相机失败
                    // 使用第一台相机
                    // =================================================
                    if (!openSuccess)
                    {
                        try
                        {
                            openSuccess =  CameraService.OpenCamera();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine( $"默认打开相机失败：{ex.Message}");
                        }
                    }
                    // =================================================
                    // 相机打开失败
                    // =================================================
                    if (!openSuccess)
                    {
                        MessageBox.Show( "打开相机失败！请前往【配置调试】界面检查或扫描相机。", "错误",  MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    // =================================================
                    // 开始采集
                    // UsbCameraService 内部会启动自己的看门狗
                    // =================================================
                    CameraService.StartGrabbing();
                    // =================================================
                    // 标记实时采集状态
                    // =================================================
                    _isLiveStreamRunning = true;
                    // =================================================
                    // 清除旧帧
                    // =================================================
                    lock (_liveFrameLock)
                    {
                        _pendingLiveFrame = null;
                        _liveRenderScheduled = false;
                    }
                    // =================================================
                    // 清除旧的拍照画面
                    // =================================================
                    _currentCapturedFrame = null;
                    // 注意：                               
                    // 这里不要再设置 _lastLiveFrameTime   
                    // 不需要 DetectPage 自己监控相机      
                    // UsbCameraService 会自己监控 NewFrame
                    // =================================================
                    UpdateBtnState(true);
                    Debug.WriteLine("[DetectPage] 开始实时采集");
                }
                else
                {
                    // =================================================
                    // 停止采集  
                    // =================================================
                    CameraService.StopGrabbing();
                    _isLiveStreamRunning = false;
                    // =================================================
                    // 清除实时帧缓存
                    // =================================================
                    lock (_liveFrameLock)
                    {
                        _pendingLiveFrame = null;
                        _liveRenderScheduled = false;
                    }
                    UpdateBtnState(false);
                    Debug.WriteLine( "[DetectPage] 停止实时采集");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show( $"操作相机失败：{ex.Message}", "错误",  MessageBoxButton.OK,  MessageBoxImage.Error);
            }
        }
        // =====================================================
        // 更新采集按钮状态 update staute
        // =====================================================
        private void UpdateBtnState(bool isRunning)
        {
            if (isRunning)
            {
                BtnLive.Content = "停止采集";
                BtnLive.Background = new SolidColorBrush(Colors.IndianRed);
            }
            else
            {
                BtnLive.Content = "开始采集";
                BtnLive.Background = (Brush)new BrushConverter()  .ConvertFrom("#2ECC71");
            }
        }
        // =====================================================
        // 抓取当前帧 
        // =====================================================
        private bool ExecuteCapture()
        {
            BitmapSource capturedFrame = CameraService.GrabSingleFrame();
            if (capturedFrame == null)
            {
                MessageBox.Show( "尚未接收到画面，请先点击【开始采集】！", "提示",  MessageBoxButton.OK,  MessageBoxImage.Information);
                return false;
            }
            _currentCapturedFrame = capturedFrame;
            ImgCapture.Source = _currentCapturedFrame;
            CanvasDetectRoi.Children.Clear();
            _lastRoiResults.Clear();
            return true;
        }
        // =====================================================
        // 点击开始检测  
        // =====================================================
        private async void BtnStartDetect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CmbProjects.SelectedItem == null)
                {
                    MessageBox.Show( "请先选择要调用的检测项目配方！","提示",  MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string selectedProjectName = CmbProjects.SelectedItem.ToString();
                _currentProject = ProjectManagerService.LoadProject( selectedProjectName);
                if (_currentProject == null || string.IsNullOrWhiteSpace( _currentProject.TemplateImagePath) || !File.Exists( _currentProject.TemplateImagePath))
                {
                    string s = "";                
                    if (!File.Exists(_currentProject.TemplateImagePath))
                    {
                        s += "3";
                    }
                    MessageBox.Show(s+ "项目模板图片丢失或未配置，请先在【配置调试】中上传模板并保存项目！",  "错误",  MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (_currentProject.RoiList == null ||  _currentProject.RoiList.Count == 0)
                {
                    MessageBox.Show( "当前项目未绘制任何 ROI 框，请先在【配置调试】中进行框选并保存！","提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // 在 UI 线程抓取当前画面
                if (!ExecuteCapture())
                    return;
                BtnStartDetect.IsEnabled = false;
                BtnStartDetect.Content = "检测中...";
                // =================================================
                // OpenCV 算法放后台线程 
                // 不阻塞实时画面 UI 
                // =================================================
                InspectionRunResult runResult =
                    await Task.Run(() =>
                    {
                        return RunInspectionAlgorithmBackground( _currentProject, _currentCapturedFrame);
                    });
                // 回到 UI 线程更新结果
                ApplyInspectionResult(runResult);
            }
            catch (Exception ex)
            {
                MessageBox.Show( $"检测流程异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnStartDetect.IsEnabled = true;
                BtnStartDetect.Content = "开始检测";
            }
        }
        // =====================================================
        // 后台检测结果对象
        // =====================================================
        private class InspectionRunResult
        {
            public bool OverallPassed { get; set; }
            public List<RoiResultInfo> RoiResults { get; set; }
            public List<string> Logs { get; set; }
            public DateTime DetectTime { get; set; }
            public string ErrorMessage { get; set; }
            // 最终对齐后的图片
            public BitmapSource AlignedImage { get; set; }
        }
        // =====================================================
        // 当日检测统计
        // =====================================================
        private class DailyStatistics
        {
            /// <summary>
            /// 统计日期
            /// </summary>
            public string Date { get; set; }
            /// <summary>
            /// 检测总数
            /// </summary>
            public int TotalCount { get; set; }
            /// <summary>
            /// 良品数
            /// </summary>
            public int PassCount { get; set; }
            /// <summary>
            /// 不良品数
            /// </summary>
            public int NgCount { get; set; }
            /// <summary>
            /// 良品率
            /// </summary>
            public double PassRate
            {
                get
                {
                    if (TotalCount <= 0)
                        return 0;
                    return (double)PassCount / TotalCount * 100.0;
                }
            }
        }
        /// <summary>
        /// 将待测图对齐到模板图坐标系
        /// 1. 尺寸统一
        /// 2. ECC自动平移对齐 
        /// 3. 手动X/Y补偿
        /// </summary>
        private Mat AlignSampleToTemplate( Mat template, Mat sample,  out double alignmentScore)
        {
            alignmentScore = 0;
            if (template == null || template.Empty())
                throw new ArgumentException("模板图为空");
            if (sample == null || sample.Empty())
                throw new ArgumentException("待测图为空");
            // =====================================================
            // 1. 尺寸统一
            // =====================================================
            Mat resized = new Mat();
            if (sample.Size() != template.Size())
            {
                Cv2.Resize( sample, resized, template.Size(),  0,  0, InterpolationFlags.Linear);
            }
            else
            {
                sample.CopyTo(resized);
            }
            // =====================================================
            // 2. 转灰度  to gary 
            // =====================================================
            using Mat grayTemplate = new Mat();
            using Mat graySample = new Mat();
            Cv2.CvtColor( template, grayTemplate, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor( resized,  graySample,   ColorConversionCodes.BGR2GRAY);
            // =====================================================
            // 3. 中间区域用于 ECC 对齐
            // 避免边缘背景、胶带、黑边干扰
            // =====================================================
            int marginX = (int)(template.Width * 0.08);
            int marginY = (int)(template.Height * 0.08);
            int roiW = template.Width - marginX * 2;
            int roiH = template.Height - marginY * 2;
            if (roiW < 50 || roiH < 50)
            {
                resized.Dispose();
                throw new Exception("图像尺寸过小，无法进行对齐。");
            }
            OpenCvSharp.Rect alignRect = new OpenCvSharp.Rect( marginX, marginY, roiW, roiH);
            using Mat tplPart = new Mat(grayTemplate, alignRect);
            using Mat smpPart = new Mat(graySample, alignRect);         
            // =====================================================
            // 4. (降噪)高斯模糊
            // =====================================================
            Cv2.GaussianBlur(  tplPart, tplPart,  new OpenCvSharp.Size(5, 5), 0);
            Cv2.GaussianBlur( smpPart, smpPart, new OpenCvSharp.Size(5, 5), 0);
            // =====================================================
            // 5. ECC 平移模型 incrarvalybal
            // =====================================================
            using Mat warpMatrix = Mat.Eye( 2, 3, MatType.CV_32FC1);
            warpMatrix.Set(0, 0, 1.0f);
            warpMatrix.Set(0, 1, 0.0f);
            warpMatrix.Set(1, 0, 0.0f);
            warpMatrix.Set(1, 1, 1.0f);
            warpMatrix.Set(0, 2, 0.0f);
            warpMatrix.Set(1, 2, 0.0f);
            TermCriteria criteria = new TermCriteria( CriteriaTypes.Eps | CriteriaTypes.Count, 100, 0.001);
            try
            {
                alignmentScore = Cv2.FindTransformECC( tplPart, smpPart, warpMatrix,  MotionTypes.Translation, criteria, null, 5);
            }
            catch (Exception ex)
            {
                resized.Dispose();
                throw new Exception($"ECC图像对齐失败：{ex.Message}");
            }
            float dx = warpMatrix.At<float>(0, 2);
            float dy = warpMatrix.At<float>(1, 2);
            // =====================================================
            // 6. 防止对齐算法跑飞  
            // =====================================================
            if (float.IsNaN(dx) || float.IsNaN(dy) || float.IsInfinity(dx) || float.IsInfinity(dy))
            {
                resized.Dispose();
                throw new Exception("ECC计算得到无效位移。");
            }
            if (Math.Abs(dx) > template.Width * 0.15 || Math.Abs(dy) > template.Height * 0.15)
            {
                resized.Dispose();
                throw new Exception( $"自动对齐位移异常：X={dx:F1}px，Y={dy:F1}px");
            }
            // =====================================================
            // 7. ECC自动对齐 
            // =====================================================
            Mat aligned = new Mat();
            Cv2.WarpAffine( resized, aligned,  warpMatrix, template.Size(), InterpolationFlags.Linear, BorderTypes.Constant,  Scalar.Black);
            resized.Dispose();
            // =====================================================
            // 8. 手动补偿 
            // =====================================================
            if (Math.Abs(ManualOffsetX) > 0.001 || Math.Abs(ManualOffsetY) > 0.001)
            {
                using Mat manualMatrix = Mat.Eye( 2, 3, MatType.CV_64FC1);
                manualMatrix.Set(0, 0, 1.0);
                manualMatrix.Set(0, 1, 0.0);
                manualMatrix.Set(1, 0, 0.0);
                manualMatrix.Set(1, 1, 1.0);
                // X > 0 向右
                manualMatrix.Set( 0, 2,  ManualOffsetX);
                // Y > 0 向下
                manualMatrix.Set( 1, 2, ManualOffsetY);
                Mat compensated = new Mat();
                Cv2.WarpAffine( aligned, compensated,  manualMatrix, template.Size(), InterpolationFlags.Linear, BorderTypes.Constant,  Scalar.Black);
                aligned.Dispose();
                aligned = compensated;
            }
            // =====================================================
            // 9. 防止最终图变成大面积黑图
            // =====================================================
            using Mat checkGray = new Mat();
            Cv2.CvtColor( aligned, checkGray, ColorConversionCodes.BGR2GRAY);
            double meanBrightness = Cv2.Mean(checkGray).Val0;
            if (meanBrightness < 10)
            {
                aligned.Dispose();
                throw new Exception(  $"对齐结果异常，图像平均亮度仅 {meanBrightness:F1}，" +  $"疑似发生过大位移。");
            }
            Debug.WriteLine( $"图像对齐：ECC={alignmentScore:F4}, " + $"AutoX={dx:F1}, AutoY={dy:F1}, " + $"ManualX={ManualOffsetX:F1}, " + $"ManualY={ManualOffsetY:F1}");
            return aligned;
        }
        private InspectionRunResult RunInspectionAlgorithmBackground( InspectionProject project, BitmapSource capturedFrame)
        {
            var runResult = new InspectionRunResult
            {
                OverallPassed = true,
                RoiResults = new List<RoiResultInfo>(),
                Logs = new List<string>(),
                DetectTime = DateTime.Now
            };
            try
            {
                // =====================================================
                // 1. 读取模板w
                // =====================================================
                using Mat matTemplate = Cv2.ImRead( project.TemplateImagePath, ImreadModes.Color);
                // =====================================================
                // 2. 当前拍摄图
                // =====================================================
                using Mat rawSample = BitmapSourceToMat(capturedFrame);
                if (matTemplate == null || matTemplate.Empty())
                {
                    throw new Exception("模板图片读取失败！");
                }
                if (rawSample == null || rawSample.Empty())
                {
                    throw new Exception("采集图片转换失败！");
                }
                // =====================================================
                // 3. Resize + ECC自动对齐 + 手动补偿
                // =====================================================
                double alignmentScore;
                using Mat alignedSample =  AlignSampleToTemplate( matTemplate, rawSample, out alignmentScore);
                BitmapSource alignedBitmap =
                BitmapSourceConverter.ToBitmapSource( alignedSample);
                alignedBitmap.Freeze();
                runResult.AlignedImage = alignedBitmap;
                runResult.Logs.Add(
                    $"{DateTime.Now:HH:mm:ss} " +
                    $"[ALIGN] 图像对齐完成，" +
                    $"ECC={alignmentScore:F2}，" +
                    $"手动补偿 X={ManualOffsetX:F1}px，" +
                    $"Y={ManualOffsetY:F1}px");
                // =====================================================
                // 4. 检测所有 ROI
                // =====================================================
                for (int i = 0; i < project.RoiList.Count; i++)
                {
                    var roi = project.RoiList[i];
                    if (roi == null)
                        continue;
                    OpenCvSharp.Rect cvRect =
                        new OpenCvSharp.Rect(
                            (int)Math.Round(roi.X),
                            (int)Math.Round(roi.Y),
                            (int)Math.Round(roi.Width),
                            (int)Math.Round(roi.Height));
                    // =================================================
                    // ROI边界保护
                    // =================================================
                    cvRect.X =
                        Math.Max(
                            0,
                            Math.Min(
                                cvRect.X,
                                matTemplate.Width - 1));

                    cvRect.Y =
                        Math.Max(
                            0,
                            Math.Min(
                                cvRect.Y,
                                matTemplate.Height - 1));

                    cvRect.Width =
                        Math.Min(
                            cvRect.Width,
                            matTemplate.Width - cvRect.X);

                    cvRect.Height =
                        Math.Min(
                            cvRect.Height,
                            matTemplate.Height - cvRect.Y);

                    if (cvRect.Width <= 0 ||
                        cvRect.Height <= 0)
                    {
                        runResult.Logs.Add(
                            $"{DateTime.Now:HH:mm:ss} " +
                            $"[ERROR] ROI-{i + 1} 区域无效");

                        continue;
                    }

                    // =================================================
                    // 5. 执行视觉算法
                    //
                    // 注意：
                    // 这里的 alignedSample 已经和模板处于同一坐标系
                    // =================================================
                    InspectionResult result = VisionInspectionService.ExecuteInspection(  matTemplate,  alignedSample,  cvRect, roi);
                    if (!result.IsPassed)
                    {
                        runResult.OverallPassed = false;
                    }
                    string roiName = string.IsNullOrWhiteSpace(roi.DisplayName) ? $"ROI-{i + 1}" : roi.DisplayName;
                    string statusTag =  result.IsPassed  ? "[OK]"  : "[NG]";
                    string msg = $"{DateTime.Now:HH:mm:ss} " +  $"{statusTag} " + $"{roiName}: " + $"{result.Message}";
                    runResult.Logs.Add(msg);//  
                    // =================================================
                    // 注意：                   
                    // DetectPage 不保存/不回显 result.mat
                    // 只保存 ROI 判定信息    
                    // =================================================
                    runResult.RoiResults.Add( 
                        new RoiResultInfo
                        {
                            PixelRoi = roi,
                            IsPassed = result.IsPassed,
                            DiffRatio = result.Score,
                            Message = msg
                        });
                }
            }
            catch (Exception ex)
            {
                runResult.OverallPassed = false;
                runResult.ErrorMessage =  ex.Message;
                runResult.Logs.Add( $"{DateTime.Now:HH:mm:ss} " + $"[ERROR] {ex.Message}");
            }
            return runResult;
        }
        // =====================================================
        // UI线程显示检测结果
        // =====================================================
        private void ApplyInspectionResult( InspectionRunResult result)
        {
            if (result == null)
                return;
            if (!string.IsNullOrWhiteSpace( result.ErrorMessage))
            {
                MessageBox.Show( $"检测过程中出现异常：{result.ErrorMessage}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            _lastRoiResults = result.RoiResults ?? new List<RoiResultInfo>();
            // =====================================================
            // 显示最终真正参与检测的“对齐后图片”
            // =====================================================
            if (result.AlignedImage != null)
            {
                ImgCapture.Source = result.AlignedImage;
            }
            TxtResultTime.Text = result.DetectTime.ToString( "yyyy-MM-dd HH:mm:ss");
            TxtDetectLogs.Text = string.Join( Environment.NewLine, result.Logs);
            if (result.OverallPassed)
            {
                BorderResultStatus.Background = new SolidColorBrush( (Color)ColorConverter.ConvertFromString( "#2ECC71"));
                TxtResultStatus.Text = "PASS";
            }
            else
            {
                BorderResultStatus.Background = new SolidColorBrush( (Color)ColorConverter.ConvertFromString( "#E74C3C")); TxtResultStatus.Text = "NG";
            }
            // =====================================================
            // 当次检测完成 → 更新当日统计
            // =====================================================
            AddInspectionStatistics(  result.OverallPassed, result.DetectTime);
            // 重绘 ROI
            RedrawRoiOverlays();
            // 确保 ROI 已经完成渲染
            Dispatcher.Invoke( System.Windows.Threading.DispatcherPriority.Render,  new Action(() => { }));
            // 保存带 ROI 框的检测图
            SaveUIElementAsImage(  GridCaptureContainer,_currentProject.ProjectName, result.OverallPassed, result.DetectTime);
        }

        // =====================================================
        // 图片尺寸变化时重新绘制 ROI
        // =====================================================
        private void ImgCapture_SizeChanged( object sender, SizeChangedEventArgs e)
        {
            RedrawRoiOverlays();
        }
        // =====================================================
        // 重绘 ROI 
        // Stretch="Fill" 专用版本
        // Image 和 Canvas 位于同一个 Grid
        // Image 控件和 Canvas 控件尺寸完全一致
        // 因此不需要 Uniform 的 offsetX / offsetY
        // =====================================================
        private void RedrawRoiOverlays()
        {
            CanvasDetectRoi.Children.Clear();
            if (_lastRoiResults == null || _lastRoiResults.Count == 0)
            {
                return;
            }
            if (ImgCapture == null || ImgCapture.Source == null)
            {
                return;
            }
            // =====================================================
            // 1. 获取 Image 控件实际显示尺寸
            // =====================================================
            double ctrlW = ImgCapture.ActualWidth;
            double ctrlH = ImgCapture.ActualHeight;
            if (ctrlW <= 0 || ctrlH <= 0)
            {
                return;
            }
            // =====================================================
            // 2. 获取实际图片像素尺寸
            // ROI保存的是 OpenCV 原图像素坐标
            // 所以必须拿 PixelWidth / PixelHeight
            // =====================================================
            double imgW;
            double imgH;
            if (ImgCapture.Source is BitmapSource bitmapSource)
            {
                imgW = bitmapSource.PixelWidth;
                imgH = bitmapSource.PixelHeight;
            }
            else
            {
                imgW = ImgCapture.Source.Width;
                imgH = ImgCapture.Source.Height;
            }
            if (imgW <= 0 || imgH <= 0)
            {
                return;
            }
            // =====================================================
            // 3. Stretch="Fill"
            // 图片被强制拉伸到整个 Image 控件
            // 所以 X、Y 分别计算缩放比例
            // =====================================================
            double scaleX = ctrlW / imgW;
            double scaleY = ctrlH / imgH;
            // =====================================================
            // 4. 绘制 ROI
            // =====================================================
            foreach (var result in _lastRoiResults)
            {
                if (result?.PixelRoi == null)
                    continue;
                RoiRect roi = result.PixelRoi;
                // =================================================
                // OpenCV像素坐标
                // ↓
                // Canvas显示坐标
                // =================================================
                double drawX = roi.X * scaleX;
                double drawY = roi.Y * scaleY;
                double drawW = roi.Width * scaleX;
                double drawH = roi.Height * scaleY;
                // =================================================
                // 根据检测结果设置颜色
                // =================================================
                Brush strokeBrush =  result.IsPassed ? Brushes.Lime : Brushes.Red;
                var rect =
                    new System.Windows.Shapes.Rectangle
                    {
                        Stroke = strokeBrush,
                        StrokeThickness = 2,
                        Width = drawW,
                        Height = drawH,
                        Fill =  result.IsPassed ? new SolidColorBrush( Color.FromArgb( 30, 0,  255,  0)) : new SolidColorBrush( Color.FromArgb(  50, 255, 0,  0))
                    };
                // =================================================
                // Canvas坐标
                // =================================================
                Canvas.SetLeft( rect, drawX);
                Canvas.SetTop( rect, drawY);
                CanvasDetectRoi.Children.Add(rect);//
                
            }
        }
        // =====================================================
        // 保存检测结果图片
        // =====================================================
        private void SaveUIElementAsImage( FrameworkElement element, string projectName, bool isPass, DateTime time)
        {
            try
            {
                if (element == null)
                    return;
                int width = (int)Math.Ceiling( element.ActualWidth);
                int height = (int)Math.Ceiling( element.ActualHeight);
                if (width <= 0 || height <= 0)
                    return;
                var renderTargetBitmap =  new RenderTargetBitmap(
                        width,
                        height,
                        96,
                        96,
                        PixelFormats.Pbgra32);
                renderTargetBitmap.Render(element);
                var pngEncoder = new PngBitmapEncoder();
                pngEncoder.Frames.Add( BitmapFrame.Create( renderTargetBitmap));
                string yearStr = time.ToString("yyyy");
                string dateStr = time.ToString("MMdd");
                string targetDir =  Path.Combine( AppDomain.CurrentDomain.BaseDirectory, "SavedImages", projectName, yearStr, dateStr);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                string statusTag = isPass ? "PASS" : "NG";
                string fileName = $"{time:HH-mm-ss_fff}_{statusTag}.png";
                string fullPath = Path.Combine( targetDir, fileName);
                using FileStream fs =
                    new FileStream(
                        fullPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);
                pngEncoder.Save(fs);
            }
            catch (Exception ex) 
            {
                System.Diagnostics.Debug.WriteLine( "保存检测图片失败：" + ex.Message);
            }
        }
        // =====================================================  
        // BitmapSource 转 OpenCV Mat
        // =====================================================
        private Mat BitmapSourceToMat( BitmapSource source)
        {
            return source.ToMat();
        }
        // =====================================================
        //   加载当天统计数据  
        // =====================================================
        private void LoadDailyStatistics()
        {
            try
            {
                DateTime today = DateTime.Now;
                string yearStr = today.ToString("yyyy");
                string dateStr = today.ToString("MMdd");
                string targetDir = Path.Combine(  StatisticsRootDirectory, yearStr, dateStr);
                string filePath = Path.Combine( targetDir, "statistics.json");
                // 如果今天还没有统计文件
                if (!File.Exists(filePath))
                {
                    _dailyStatistics = new DailyStatistics
                    {
                        Date = today.ToString("yyyy-MM-dd"),
                        TotalCount = 0,
                        PassCount = 0,
                        NgCount = 0
                    };
                    UpdateDailyStatisticsUI();
                    return;
                }
                string json = File.ReadAllText(filePath);
                DailyStatistics statistics = JsonSerializer.Deserialize<DailyStatistics>(json);
                if (statistics == null)
                {
                    statistics = new DailyStatistics();
                }
                _dailyStatistics = statistics;
                // 防止跨天后读取到旧日期
                if (_dailyStatistics.Date != today.ToString("yyyy-MM-dd"))
                {
                    _dailyStatistics = new DailyStatistics
                    {
                        Date = today.ToString("yyyy-MM-dd"),
                        TotalCount = 0,
                        PassCount = 0,
                        NgCount = 0
                    };
                }
                UpdateDailyStatisticsUI();
            }
            catch (Exception ex)
            {
                Debug.WriteLine( $"加载当天统计失败：{ex.Message}");
                _dailyStatistics = new DailyStatistics
                {
                    Date = DateTime.Now.ToString("yyyy-MM-dd")
                };
                UpdateDailyStatisticsUI();
            }
        }   
        // =====================================================
        // 更新当日统计 UI
        // =====================================================
        private void UpdateDailyStatisticsUI()
        {
            if (TxtStatisticsDate == null ||TxtTotalCount == null ||  TxtPassCount == null ||  TxtNgCount == null || TxtPassRate == null)
            {
                return;
            }
            DateTime today = DateTime.Now;
            TxtStatisticsDate.Text = today.ToString("yyyy/MM/dd");
            TxtTotalCount.Text = _dailyStatistics.TotalCount.ToString();
            TxtPassCount.Text = _dailyStatistics.PassCount.ToString();
            TxtNgCount.Text =  _dailyStatistics.NgCount.ToString();
            TxtPassRate.Text = $"{_dailyStatistics.PassRate:F2}%";
        }
        // =====================================================
        // 统计一次检测结果
        // =====================================================
        private void AddInspectionStatistics( bool isPassed,  DateTime detectTime)
        {
            try
            {
                // =================================================
                // 防止程序跨天运行时仍然使用昨天的数据
                // =================================================
                string todayString = detectTime.ToString("yyyy-MM-dd");
                if (_dailyStatistics == null || _dailyStatistics.Date != todayString)
                {
                    // 新的一天
                    LoadDailyStatistics();
                }
                // =================================================
                // 总数 +1
                // =================================================
                _dailyStatistics.TotalCount++;
                // =================================================
                // PASS / NG
                // =================================================
                if (isPassed)
                {
                    _dailyStatistics.PassCount++;
                }
                else
                {
                    _dailyStatistics.NgCount++;
                }
                // =================================================
                // 保存到本地
                // =================================================
                SaveDailyStatistics();

                // 更新界面

                UpdateDailyStatisticsUI();
                Debug.WriteLine( $"当日统计：总数={_dailyStatistics.TotalCount}，" + $"PASS={_dailyStatistics.PassCount}，" + $"NG={_dailyStatistics.NgCount}，" + $"良品率={_dailyStatistics.PassRate:F2}%");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"统计检测结果失败：{ex.Message}");
            }
        }
 
        // =====================================================
        // 保存当天统计数据
        // =====================================================
        private void SaveDailyStatistics()
        {
            try
            {
                DateTime today = DateTime.Now;

                string yearStr = today.ToString("yyyy");
                string dateStr = today.ToString("MMdd");

                string targetDir = Path.Combine(
                    StatisticsRootDirectory,
                    yearStr,
                    dateStr);

                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                string filePath = Path.Combine(
                    targetDir,
                    "statistics.json");

                string json = JsonSerializer.Serialize(
                    _dailyStatistics,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                File.WriteAllText(
                    filePath,
                    json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"保存当天统计失败：{ex.Message}");
            }
        }

    }
}