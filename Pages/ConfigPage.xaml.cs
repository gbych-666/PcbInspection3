using Microsoft.Win32;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using PcbInspection.Models;
using PcbInspection.Services;
using System;
using System;
using System.Collections.Generic;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;



namespace PcbInspection.Pages
{
    public partial class ConfigPage : Page
    {
        private class RoiResultInfo
        {
            public RoiRect PixelRoi { get; set; }
            public bool IsPassed { get; set; }
            public double DiffRatio { get; set; }
            public string Message { get; set; }
            // 新增：保存转化后的图像，用于 Canvas 渲染
            public BitmapSource RoiImageSource { get; set; }
        }
        private System.Windows.Point _startPoint;
        private System.Windows.Shapes.Rectangle _currentRect;
        private bool _isDrawing = false;
        // 内存中的 ROI 集合，直接绑定到 UI 上的 DataGrid (DgRoiParams)
        private List<RoiRect> _currentRoiList = new List<RoiRect>();
        private BitmapSource _currentTemplateImage;
        private string _testTemplatePath = "";
        private string _testSamplePath = "";
        private InspectionProject _currentTestProject;
        private BitmapSource _currentCapturedFrame;
       
        // --- 单步调试控制变量 ---
        private List<RoiResultInfo> _lastRoiResults = new List<RoiResultInfo>();
        // 当前待测图是否正在显示最终检测结果
        private bool _isShowingTestResults = false;
        // =========================================================
        // 手动位置补偿参数
        // 单位：OpenCV 像素
        //
        // X > 0：待测图向右移动
        // X < 0：待测图向左移动
        // Y > 0：待测图向下移动
        // Y < 0：待测图向上移动
        // =========================================================
        private double ManualOffsetX = 0.0;
        private double ManualOffsetY = 0.0;

        // ----------------------------------------------------
        // 1. 构造函数 传入用户角色，应用权限控制
        // ----------------------------------------------------
        public ConfigPage() : this("管理员")
        {

        }
        public ConfigPage(string userRole)
        {
            InitializeComponent();
            LoadAndScanCameras();
            RefreshProjectComboBox();
            ApplyPermissions(userRole);
            RefreshTestProjectComboBox();
            this.Loaded += ConfigPage_Loaded;
        }
        #region 
        // 在 DataGrid 准备单元格时绑定下拉选项  
        private void RefreshRoiDataGrid()
        {
            DgRoiParams.ItemsSource = null;
            DgRoiParams.ItemsSource = _currentRoiList;
        }     
        private T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                if (child is T tChild)
                {
                    return tChild;
                }
                T childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                {
                    return childOfChild;
                }
            }
            return null;
        }
        #endregion
        private void ConfigPage_Loaded(object sender, RoutedEventArgs e) 
        {
            RefreshTestProjectComboBox();
        }  
        // 模板图 ROI
        private void ImgTemplate_SizeChanged( object sender, SizeChangedEventArgs e)
        {
            if (ImgTemplate == null || ImgTemplate.Source == null)
            {
                return;
            }
            RedrawRois();
        }
        // 测试模板图 ROI
        private void ImgTestTemplate_SizeChanged( object sender, SizeChangedEventArgs e)
        {
            if (_currentTestProject == null || ImgTestTemplate == null || ImgTestTemplate.Source == null)
            {
                return;
            }
            if (ImgTestTemplate.ActualWidth <= 0 || ImgTestTemplate.ActualHeight <= 0)
            {
                return;
            }
            DrawRoiOnCanvas( CanvasTestTemplateRoi,  ImgTestTemplate, _currentTestProject.RoiList, Brushes.Lime);
        }
        // =========================================================
        // 测试图片 ROI
        // =========================================================
        private void ImgTestSample_SizeChanged( object sender, SizeChangedEventArgs e)
        {
            if (_currentTestProject == null || ImgTestSample == null || ImgTestSample.Source == null)
            {
                return;
            }
            if (ImgTestSample.ActualWidth <= 0 || ImgTestSample.ActualHeight <= 0)
            {
                return;
            }
            // 正在显示检测结果
            if (_isShowingTestResults)
            {
                RedrawRoiOverlays();
                return;
            }
            // 普通状态
            DrawRoiOnCanvas( CanvasTestSampleRoi, ImgTestSample,_currentTestProject.RoiList, Brushes.Yellow);
        }
        // 2. 坐标转换与图像处理核心逻辑  
        private Mat BitmapSourceToMat(BitmapSource source)
        {
            if (source == null)
                return null;
            // 统一转换为 BGR24
            // WPF:
            // Bgr24 = B G R
            // OpenCV:
            // CV_8UC3 = B G R
            // 后面所有算法统一使用 3 通道 BGR
            FormatConvertedBitmap converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = source;
            converted.DestinationFormat = PixelFormats.Bgr24;
            converted.EndInit();
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            // BGR 3通道
            int stride = width * 3;
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels,stride,0);
            Mat mat = Mat.FromPixelData(height,width,MatType.CV_8UC3,pixels,stride);
            // Clone 一份独立数据，避免 pixels 生命周期问题 life cytue issues 
            return mat.Clone();
        }
       
        /// <summary>
        /// 将 Canvas 坐标转换成原始图像像素坐标。
        /// Stretch="Fill" 模式：
        /// Canvas 和 Image 控件完全重合，图片完全铺满控件。
        /// 因此：
        /// X像素 = CanvasX × 原图宽 / Image控件宽
        /// Y像素 = CanvasY × 原图高 / Image控件高
        /// 不再存在 offsetX / offsetY。
        /// </summary>
        public RoiRect CanvasToPixel( double canvasX,  double canvasY, double canvasW, double canvasH, Image imageControl)
        {
            if (imageControl == null || imageControl.Source == null || imageControl.ActualWidth <= 0 || imageControl.ActualHeight <= 0)
            {
                return new RoiRect
                {
                    X = canvasX,
                    Y = canvasY,
                    Width = canvasW,
                    Height = canvasH
                };
            }
            double ctrlW = imageControl.ActualWidth;
            double ctrlH = imageControl.ActualHeight;
            double imgW;
            double imgH; 
            if (imageControl.Source is BitmapSource bitmapSource)
            {
                imgW = bitmapSource.PixelWidth;
                imgH = bitmapSource.PixelHeight;
            }
            else
            {
                imgW = imageControl.Source.Width;  // 宽度
                imgH = imageControl.Source.Height; // 高度
            }
            if (imgW <= 0 || imgH <= 0)
            {
                return new RoiRect
                {
                    X = canvasX,
                    Y = canvasY,
                    Width = canvasW,
                    Height = canvasH
                };
            }
            // Stretch="Fill"
            // X、Y分别独立缩放
            double scaleX = imgW / ctrlW;
            double scaleY = imgH / ctrlH;
            double pixelX = canvasX * scaleX;
            double pixelY = canvasY * scaleY;
            double pixelW = canvasW * scaleX;
            double pixelH = canvasH * scaleY;
            // 限制到原始图像范围
            pixelX = Math.Max(0, Math.Min(pixelX, imgW - 1));
            pixelY = Math.Max(0, Math.Min(pixelY, imgH - 1));
            pixelW = Math.Max(  1,  Math.Min(pixelW, imgW - pixelX));
            pixelH = Math.Max(  1,  Math.Min(pixelH, imgH - pixelY));
            return new RoiRect
            {
                X = pixelX,
                Y = pixelY,
                Width = pixelW,
                Height = pixelH
            };
        }
        /// <summary>
        /// 截取指定 WPF 控件当前实际显示的内容，转换为 OpenCV BGR Mat
        /// </summary>
        private Mat CaptureControlToMat(FrameworkElement element)
        {
            if (element == null)
                return null;
            int width = (int)Math.Round(element.ActualWidth);
            int height = (int)Math.Round(element.ActualHeight);
            if (width <= 0 || height <= 0)
                return null;
            // 确保控件已经完成布局
            element.UpdateLayout();
            // 截取控件当前显示结果
            RenderTargetBitmap rtb = new RenderTargetBitmap( width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(element);
            // 转换为 BGRA32
            FormatConvertedBitmap converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = rtb;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels,stride, 0);
            using Mat bgra = Mat.FromPixelData(
                height,
                width,
                MatType.CV_8UC4,
                pixels,
                stride);
            Mat bgr = new Mat();
            Cv2.CvtColor(
                bgra,
                bgr,
                ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        /// <summary>
        /// 将原始图像像素 ROI 转换成 Image/Canvas 上的显示坐标。
        ///
        /// 当前 Image 使用 Stretch="Fill"，
        /// 因此 Image 和 Canvas 坐标完全一致。
        /// </summary>
        private OpenCvSharp.Rect GetDisplayRoiRect( Image imageControl, RoiRect roi)
        {
            if (imageControl == null || imageControl.Source == null)
            {
                throw new Exception("图像控件或图像源为空");
            }
            double ctrlW = imageControl.ActualWidth;
            double ctrlH = imageControl.ActualHeight;

            double imgW;
            double imgH;
            if (imageControl.Source is BitmapSource bitmapSource)
            {
                imgW = bitmapSource.PixelWidth;  //宽度 
                imgH = bitmapSource.PixelHeight; //高度 
            }
            else
            {
                imgW = imageControl.Source.Width;
                imgH = imageControl.Source.Height;
            }
            if (ctrlW <= 0 || ctrlH <= 0 || imgW <= 0 || imgH <= 0)
            {
                throw new Exception("图像尺寸无效");
            }
            // =========================================================
            // Stretch="Fill"
            // 原图像素 -> WPF控件坐标
            // =========================================================
            double scaleX = ctrlW / imgW;
            double scaleY = ctrlH / imgH;
            double drawX = roi.X * scaleX;
            double drawY = roi.Y * scaleY;
            double drawW = roi.Width * scaleX;
            double drawH = roi.Height * scaleY;
            int x = (int)Math.Round(drawX);
            int y = (int)Math.Round(drawY);
            int w = (int)Math.Round(drawW);
            int h = (int)Math.Round(drawH);
            // =========================================================
            // 边界保护
            // =========================================================
            x = Math.Max( 0, Math.Min( x,(int)ctrlW - 1));
            y = Math.Max( 0, Math.Min( y,(int)ctrlH - 1));
            w = Math.Max( 1, Math.Min( w,(int)ctrlW - x));
            h = Math.Max( 1, Math.Min( h, (int)ctrlH - y));
            return new OpenCvSharp.Rect( x, y, w,  h);
        }

        /// <summary>
        /// 按照 Image 当前显示坐标裁剪 ROI。
        /// Stretch="Fill" 模式下：
        /// Canvas 坐标和 Image 显示坐标一致。
        /// </summary>
        private Mat CaptureRoiFromImageControl( Image imageControl, RoiRect roi)
        {
            using Mat controlMat = CaptureControlToMat(imageControl);
            if (controlMat == null || controlMat.Empty())
            {
                throw new Exception("控件截图失败");
            }
            OpenCvSharp.Rect displayRect = GetDisplayRoiRect( imageControl, roi);
            int x = Math.Max( 0, Math.Min( displayRect.X, controlMat.Width - 1));
            int y = Math.Max( 0, Math.Min( displayRect.Y, controlMat.Height - 1));
            int w = Math.Max(1, Math.Min( displayRect.Width, controlMat.Width - x));
            int h = Math.Max( 1, Math.Min(  displayRect.Height, controlMat.Height - y));
            OpenCvSharp.Rect safeRect =  new OpenCvSharp.Rect(  x, y,  w, h);
            return new Mat( controlMat, safeRect).Clone();
        }


        /// <summary>
        /// 将原始像素 ROI 绘制到 WPF Canvas。
        ///
        /// Image = Stretch.Fill
        /// Canvas = 与 Image 完全重合
        /// 所以只需要 X/Y 分别缩放。
        /// </summary>
        private void DrawRoiOnCanvas( Canvas canvas,  Image imageControl, List<RoiRect> rois, System.Windows.Media.Brush color)
        {
            canvas.Children.Clear();
            if (rois == null || rois.Count == 0 || imageControl == null || imageControl.Source == null)
            {
                return;
            }
            double ctrlW = imageControl.ActualWidth;
            double ctrlH = imageControl.ActualHeight;
            if (ctrlW <= 0 || ctrlH <= 0)
            {
                return;
            }
            double imgW;
            double imgH;
            if (imageControl.Source is BitmapSource bitmapSource)
            {
                imgW = bitmapSource.PixelWidth;
                imgH = bitmapSource.PixelHeight;
            }
            else
            {
                imgW = imageControl.Source.Width;
                imgH = imageControl.Source.Height;
            }
            if (imgW <= 0 || imgH <= 0)
            {
                return;
            }
            // =========================================================
            // Stretch="Fill" 
            // =========================================================
            double scaleX = ctrlW / imgW;
            double scaleY = ctrlH / imgH;
            for (int i = 0; i < rois.Count; i++)
            {
                RoiRect roi = rois[i];
                double drawX = roi.X * scaleX;
                double drawY = roi.Y * scaleY;
                double drawW = roi.Width * scaleX;
                double drawH = roi.Height * scaleY;
                System.Windows.Shapes.Rectangle rect =
                    new System.Windows.Shapes.Rectangle
                    {
                        Stroke = color,
                        StrokeThickness = 2,
                        Width = drawW,
                        Height = drawH,
                        Fill = color == Brushes.Lime ? new SolidColorBrush( System.Windows.Media.Color.FromArgb( 40,  0, 255, 0)) : null
                    };
                Canvas.SetLeft(rect, drawX);
                Canvas.SetTop(rect, drawY);
                canvas.Children.Add(rect);
            }
        }

        //重新绘制ROI区域框
        private void RedrawRois()
        {
            DrawRoiOnCanvas(CanvasRoi, ImgTemplate, _currentRoiList, Brushes.Lime);
        }
        private OpenCvSharp.Rect ScaleWpfRectToCv(RoiRect pixelRoi, Mat cvMat)
        {
            //ROI区域
            return new OpenCvSharp.Rect(
                (int)Math.Round(pixelRoi.X),
                (int)Math.Round(pixelRoi.Y),
                (int)Math.Round(pixelRoi.Width),
                (int)Math.Round(pixelRoi.Height)
            );
        }
        private OpenCvSharp.Rect ScaleWpfRectToCv1(RoiRect roi, Mat mat)
        {
            // 如果 roi 里的 X,Y,Width,Height 本身就是像素坐标：
            return  new OpenCvSharp.Rect(
                (int)roi.X,
                (int)roi.Y,
                (int)roi.Width,
                (int)roi.Height
            );
        }
        // ----------------------------------------------------
        // 3. 鼠标交互框选 ROI 与 DataGrid 控件同步
        // ----------------------------------------------------
        private void CanvasRoi_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && ImgTemplate.Source != null)
            {
                _isDrawing = true;
                _startPoint = e.GetPosition(CanvasRoi);

                _currentRect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = Brushes.Lime,
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0, 255, 0))
                };

                Canvas.SetLeft(_currentRect, _startPoint.X);
                Canvas.SetTop(_currentRect, _startPoint.Y);
                CanvasRoi.Children.Add(_currentRect);
            }
        }

        private void CanvasRoi_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDrawing && _currentRect != null)
            {
                System.Windows.Point pos = e.GetPosition(CanvasRoi);
                double x = Math.Min(pos.X, _startPoint.X);
                double y = Math.Min(pos.Y, _startPoint.Y);
                double w = Math.Abs(pos.X - _startPoint.X);
                double h = Math.Abs(pos.Y - _startPoint.Y);

                Canvas.SetLeft(_currentRect, x);
                Canvas.SetTop(_currentRect, y);
                _currentRect.Width = w;
                _currentRect.Height = h;
            }
        }

        private void CanvasRoi_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawing && _currentRect != null)
            {
                _isDrawing = false;
                if (_currentRect.Width > 5 && _currentRect.Height > 5)
                {
                    double canvasX = Canvas.GetLeft(_currentRect);
                    double canvasY = Canvas.GetTop(_currentRect);
                    double canvasW = _currentRect.Width;
                    double canvasH = _currentRect.Height;
                     
                    // 转换为基于原始像素坐标系的 ROI
                    RoiRect pixelRoi = CanvasToPixel(canvasX, canvasY, canvasW, canvasH, ImgTemplate);//临时模板图片

                    // 自动生成编号显示名称
                    pixelRoi.DisplayName = $"ROI_{_currentRoiList.Count + 1}";

                    _currentRoiList.Add(pixelRoi);

                    // 刷新 UI 界面与表格数据
                    RefreshRoiDataGrid();
                    RedrawRois();
                }
                else
                {
                    CanvasRoi.Children.Remove(_currentRect);
                }
                _currentRect = null;
            }
        }

        private void BtnClearRoi_Click(object sender, RoutedEventArgs e)
        {
            // =========================================================
            // 1. 必须先选中 DataGrid 中的一行
            // =========================================================
            if (DgRoiParams.SelectedItem == null)
            {
                MessageBox.Show( "请先在 ROI 参数列表中选择一个 ROI！", "提示",  MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // =========================================================
            // 2. 获取当前选中的 ROI
            // =========================================================
            RoiRect selectedRoi = DgRoiParams.SelectedItem as RoiRect;
            if (selectedRoi == null)
            {
                MessageBox.Show( "无法获取当前选中的 ROI！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // =========================================================
            // 3. 从 ROI 集合中删除选中的 ROI
            // =========================================================
            bool removed = _currentRoiList.Remove(selectedRoi);
            if (!removed)
            {
                MessageBox.Show( "未找到对应的 ROI，无法删除！",  "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // =========================================================
            // 4. 重新编号 ROI
            // =========================================================
            for (int i = 0; i < _currentRoiList.Count; i++)
            {
                _currentRoiList[i].DisplayName = $"ROI_{i + 1}";
            }
            // =========================================================
            // 5. 刷新 DataGrid
            // =========================================================
            RefreshRoiDataGrid();
            // =========================================================
            // 6. 重新绘制 Canvas
            //    剩余 ROI 会保留，被删除的 ROI 不再绘制
            // =========================================================
            RedrawRois();
            // =========================================================
            // 7. 清除 DataGrid 当前选中状态
            // =========================================================
            DgRoiParams.SelectedItem = null;
        }
        private void ClearRoiCanvas()//清除ROI区
        {
            _currentRoiList.Clear();
            RefreshRoiDataGrid();
            CanvasRoi.Children.Clear();
        }
        // ----------------------------------------------------
        // 4. 项目配方保存与加载
        // ----------------------------------------------------
        private void BtnSaveProject_Click(object sender, RoutedEventArgs e)
        {
            if (CmbProjects.SelectedItem == null)
            {
                MessageBox.Show("请先选择或添加一个项目！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 提交 DataGrid 中可能正在编辑的单元格数据
            DgRoiParams.CommitEdit();
            string projName = CmbProjects.SelectedItem.ToString();//保存项目数据名称
            string imgPath = string.Empty;
            if (_currentTemplateImage != null)
            {
                imgPath = ProjectManagerService.SaveTemplateImage(projName, _currentTemplateImage);
            }
            var project = new InspectionProject
            {
                ProjectName = projName,
                TemplateImagePath = imgPath,
                RoiList = _currentRoiList, // 保存包含独立参数的 ROI 列表   
                Confidence = 0.8
            };
            ProjectManagerService.SaveProject(project);
            MessageBox.Show($"检测项目【{projName}】配方保存成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshTestProjectComboBox();
            if (CmbTestProjects.SelectedItem != null && CmbTestProjects.SelectedItem.ToString() == projName)
            {
                _currentTestProject = ProjectManagerService.LoadProject(projName);
                if (_currentTestProject != null)
                {
                    DrawRoiOnCanvas(CanvasTestTemplateRoi, ImgTestTemplate, _currentTestProject.RoiList, Brushes.Lime);
                    DrawRoiOnCanvas(CanvasTestSampleRoi, ImgTestSample, _currentTestProject.RoiList, Brushes.Yellow);
                }
            }
        }
        private void LoadSelectedProject(string projectName)
        {
            var proj = ProjectManagerService.LoadProject(projectName);
            if (proj == null) return;
            _currentRoiList = proj.RoiList ?? new List<RoiRect>();
            // 为缺少 DisplayName 的旧数据重新校准 DisplayName 
            for (int i = 0; i < _currentRoiList.Count; i++)
            {
                if (string.IsNullOrEmpty(_currentRoiList[i].DisplayName))
                {
                    _currentRoiList[i].DisplayName = $"ROI_{i + 1}";
                }
            }
            RefreshRoiDataGrid();//刷新 
            if (!string.IsNullOrEmpty(proj.TemplateImagePath) && File.Exists(proj.TemplateImagePath))
            {
                BitmapImage bmp = LoadBitmapWithoutLock(proj.TemplateImagePath);
                ImgTemplate.Source = bmp;
                _currentTemplateImage = bmp;
            }
            else
            {
                ImgTemplate.Source = null;
                _currentTemplateImage = null;
            }
            RedrawRois();// 
        }

        private void RefreshProjectComboBox(string selectedName = null)
        {
            CmbProjects.SelectionChanged -= CmbProjects_SelectionChanged;
            var projects = ProjectManagerService.GetProjectList();
            CmbProjects.ItemsSource = projects;
            if (projects.Count > 0)
            {
                CmbProjects.SelectedItem = selectedName ?? projects[0];
            }
            else
            {
                CmbProjects.SelectedItem = null;
                ClearUiDisplay();
            }
            CmbProjects.SelectionChanged += CmbProjects_SelectionChanged;
            if (CmbProjects.SelectedItem != null)
            {
                LoadSelectedProject(CmbProjects.SelectedItem.ToString());
            }
        }
        private void CmbProjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbProjects.SelectedItem != null)
            {
                LoadSelectedProject(CmbProjects.SelectedItem.ToString());
            }
        }

        private void BtnAddProject_Click(object sender, RoutedEventArgs e)
        {
            string inputName = Microsoft.VisualBasic.Interaction.InputBox("请输入新检测项目名称：", "添加项目", "PCB_Model_1");
            inputName = inputName.Trim();

            if (string.IsNullOrEmpty(inputName)) return;

            var list = ProjectManagerService.GetProjectList();
            if (list.Contains(inputName))
            {
                MessageBox.Show("该项目名称已存在！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var newProj = new InspectionProject { ProjectName = inputName };
            ProjectManagerService.SaveProject(newProj);
            RefreshProjectComboBox(inputName);
            RefreshTestProjectComboBox();
        }

        private void BtnDeleteProject_Click(object sender, RoutedEventArgs e)
        {
            if (CmbProjects.SelectedItem == null) return;
            //判定非空 多选参数 
            string currName = CmbProjects.SelectedItem.ToString();
            if (MessageBox.Show($"确定要彻底删除项目【{currName}】及其模板资源吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                ImgTemplate.Source = null;
                _currentTemplateImage = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                ProjectManagerService.DeleteProject(currName);

                RefreshProjectComboBox();
                RefreshTestProjectComboBox();
            }
        }
        // ----------------------------------------------------
        // 5. 右侧手动比对测试逻辑（采用 ROI 独立阈值和占比上限）内联算法实现与单步/全量检测逻辑
        // ----------------------------------------------------
        private void RefreshTestProjectComboBox()
        {
            var projects = ProjectManagerService.GetProjectList();
            CmbTestProjects.ItemsSource = projects;
            if (projects.Count > 0)
            {
                if (CmbProjects.SelectedItem != null && projects.Contains(CmbProjects.SelectedItem.ToString()))
                {
                    CmbTestProjects.SelectedItem = CmbProjects.SelectedItem.ToString();
                }
                else
                {
                    CmbTestProjects.SelectedIndex = 0;
                }
            }
            else
            {
                _currentTestProject = null; 
                CanvasTestTemplateRoi.Children.Clear();
                CanvasTestSampleRoi.Children.Clear();
            }
        }
        private void CmbTestProjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbTestProjects.SelectedItem == null) return;
            string projName = CmbTestProjects.SelectedItem.ToString();
            _currentTestProject = ProjectManagerService.LoadProject(projName);//项目文件 
            if (_currentTestProject != null)
            {
                if (File.Exists(_currentTestProject.TemplateImagePath))
                {
                    _testTemplatePath = _currentTestProject.TemplateImagePath;
                    ImgTestTemplate.Source = LoadBitmapWithoutLock(_testTemplatePath);
                }
                DrawRoiOnCanvas(CanvasTestTemplateRoi, ImgTestTemplate, _currentTestProject.RoiList, Brushes.Lime);
                DrawRoiOnCanvas(CanvasTestSampleRoi, ImgTestSample, _currentTestProject.RoiList, Brushes.Yellow);
            }
            // 切换项目时重置单步索引    
            _lastRoiResults.Clear();
        }
        /// <summary> 
        /// 将待测图平移对齐到模板图 
        /// 注意：FindTransformECC 得到的 warpMatrix 直接用于 WarpAffine，
        /// 不要再手动把 dx/dy 取负。
        /// </summary>
        private Mat AlignSampleToTemplate( Mat template, Mat sample, out double alignmentScore)
        {
            alignmentScore = 0;
            if (template == null || template.Empty())
                throw new ArgumentException("模板图为空");
            if (sample == null || sample.Empty())
                throw new ArgumentException("待测图为空");
            // =========================================================
            // 1. 尺寸统一
            // =========================================================
            Mat resized = new Mat();
            if (sample.Size() != template.Size())
            {
                Cv2.Resize( sample, resized, template.Size(), 0, 0, InterpolationFlags.Linear);
            }
            else
            {
                resized = sample.Clone();
            }
            // =========================================================
            // 2. 灰度
            // =========================================================
            using Mat grayTemplate = new Mat();
            using Mat graySample = new Mat();
            Cv2.CvtColor(  template, grayTemplate, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor( resized, graySample, ColorConversionCodes.BGR2GRAY);
            // =========================================================
            // 3. 使用 PCB 中间区域做定位
            // =========================================================
            int marginX = (int)(template.Width * 0.08);
            int marginY = (int)(template.Height * 0.08);
            int roiW = template.Width - marginX * 2;
            int roiH = template.Height - marginY * 2;
            if (roiW < 50 || roiH < 50)
            {
                resized.Dispose();
                throw new Exception("图像太小，无法进行图像对齐");
            }
            OpenCvSharp.Rect alignRect = new OpenCvSharp.Rect( marginX, marginY, roiW, roiH);
            using Mat tplPart = new Mat(grayTemplate, alignRect);
            using Mat smpPart = new Mat(graySample, alignRect);
            // =========================================================
            // 4. 轻微模糊，减少噪声和光照影响
            // =========================================================
            Cv2.GaussianBlur( tplPart,  tplPart,  new OpenCvSharp.Size(5, 5),  0);
            Cv2.GaussianBlur( smpPart, smpPart,  new OpenCvSharp.Size(5, 5), 0);
            // =========================================================
            // 5. ECC 平移模型
            // =========================================================
            Mat warpMatrix = Mat.Eye(
                2,
                3,
                MatType.CV_32FC1);

            warpMatrix.Set(0, 0, 1.0f);
            warpMatrix.Set(0, 1, 0.0f);
            warpMatrix.Set(1, 0, 0.0f);
            warpMatrix.Set(1, 1, 1.0f);
            warpMatrix.Set(0, 2, 0.0f);
            warpMatrix.Set(1, 2, 0.0f);

            TermCriteria criteria = new TermCriteria(
                CriteriaTypes.Eps | CriteriaTypes.Count,
                100,
                0.001);

            try
            {
                // =====================================================
                // FindTransformECC：
                // warpMatrix 就是“把待测图向模板坐标系变换”的矩阵
                // =====================================================
                alignmentScore = Cv2.FindTransformECC(
                    tplPart,
                    smpPart,
                    warpMatrix,
                    MotionTypes.Translation,
                    criteria,
                    null,
                    5);
            }
            catch (Exception ex)
            {
                resized.Dispose();

                throw new Exception(
                    $"ECC 图像对齐失败：{ex.Message}");  
            }
            // =========================================================
            // 6. 获取平移量，仅用于检查
            // =========================================================
            float dx = warpMatrix.At<float>(0, 2);
            float dy = warpMatrix.At<float>(1, 2);

            // =========================================================
            // 7. 防止 ECC 跑飞
            // =========================================================
            if (float.IsNaN(dx) ||
                float.IsNaN(dy) ||
                float.IsInfinity(dx) ||
                float.IsInfinity(dy))
            {
                resized.Dispose();

                throw new Exception("ECC 计算得到无效位移");
            }

            // 工业现场通常不应该突然移动几百像素
            if (Math.Abs(dx) > template.Width * 0.15 ||
                Math.Abs(dy) > template.Height * 0.15)
            {
                resized.Dispose();

                throw new Exception(
                    $"检测到异常图像位移：X={dx:F1}px，Y={dy:F1}px");
            }

            // =========================================================
            // 8. 关键：
            //
            // 不要再构造 inverseWarp！
            // 直接使用 FindTransformECC 返回的 warpMatrix。
            // =========================================================
            Mat aligned = new Mat();

            Cv2.WarpAffine(
                resized,
                aligned,
                warpMatrix,
                template.Size(),
                InterpolationFlags.Linear,
                BorderTypes.Constant,
                Scalar.Black);

            // =========================================================
            // 8. 手动位置补偿
            //
            // 自动对齐完成后，再额外进行人工 X/Y 补偿。
            // =========================================================
            if (Math.Abs(ManualOffsetX) > 0.001 || Math.Abs(ManualOffsetY) > 0.001)
            {
                Mat manualMatrix = Mat.Eye( 2, 3, MatType.CV_64FC1);
                manualMatrix.Set(0, 0, 1.0);
                manualMatrix.Set(0, 1, 0.0);
                manualMatrix.Set(1, 0, 0.0);
                manualMatrix.Set(1, 1, 1.0);
                // X 正数 = 向右
                manualMatrix.Set( 0, 2, ManualOffsetX);
                // Y 正数 = 向下
                manualMatrix.Set( 1, 2, ManualOffsetY);
                Mat compensated = new Mat();
                Cv2.WarpAffine( aligned, compensated, manualMatrix, template.Size(),InterpolationFlags.Linear,  BorderTypes.Constant, Scalar.Black);
                aligned.Dispose();
                aligned = compensated;
            }
            resized.Dispose();
            return aligned;
        }
        private void BtnRunTest_Click(object sender, RoutedEventArgs e)
        {
            _lastRoiResults.Clear();         
            if (string.IsNullOrEmpty(_testTemplatePath) || _currentCapturedFrame == null) return;
            if (_currentTestProject == null || _currentTestProject.RoiList.Count == 0) return;
            try
            {
                using (Mat matTemplate = Cv2.ImRead(_testTemplatePath, ImreadModes.Color))
                using (Mat matSample = BitmapSourceToMat(_currentCapturedFrame))
                {
                    // =========================================================
                    // 关键修改：
                    // 以前只是 Resize，现在必须先进行位置对齐
                    // =========================================================
                    double alignmentScore;
                    Mat resizedSample = AlignSampleToTemplate(  matTemplate, matSample, out alignmentScore);
                    // =========================================================
                    // 对齐质量检查
                    // =========================================================
                    if (alignmentScore < 0.60)
                    {
                        resizedSample.Dispose();
                        MessageBox.Show( $"模板与待测图对齐失败！\n\n" +
                            $"ECC匹配度：{alignmentScore:F3}\n\n" +
                            $"请检查模板图和待测图是否来自同一位置。",
                            "图像对齐失败",
                            MessageBoxButton.OK,  MessageBoxImage.Warning);
                        return;
                    }
                    // =========================================================
                    // 防止对齐后出现大面积黑图
                    // =========================================================
                    using (Mat checkGray = new Mat())
                    {
                        Cv2.CvtColor(
                            resizedSample,
                            checkGray,
                            ColorConversionCodes.BGR2GRAY);

                        Scalar mean = Cv2.Mean(checkGray);

                        if (mean.Val0 < 10)
                        {
                            resizedSample.Dispose();

                            MessageBox.Show(
                                $"图像对齐异常！\n\n" +
                                $"对齐后平均亮度：{mean.Val0:F1}\n" +
                                $"当前图像疑似被移出画面。",
                                "图像对齐失败",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);

                            return;
                        }
                    }
                    // =========================================================
                    // 非常重要：
                    // 把「已经对齐后的图」显示出来
                    //
                    // 这样下面绘制的 ROI 坐标和实际检测坐标
                    // 就属于完全相同的坐标系。
                    // =========================================================
                    BitmapSource alignedSource = OpenCvSharp.WpfExtensions.BitmapSourceConverter.ToBitmapSource(resizedSample);
                    alignedSource.Freeze();
                    ImgTestSample.Source = alignedSource;
                    // =========================================================
                    // 后面的检测继续使用 resizedSample
                    // =========================================================
                    bool isNG = false;
                    List<string> logs = new List<string>();
                    logs.Add($"图像对齐完成，匹配度：{alignmentScore:P2}");               
                    for (int i = 0; i < _currentTestProject.RoiList.Count; i++)
                    {
                        var roi = _currentTestProject.RoiList[i];
                        OpenCvSharp.Rect cvRect = ScaleWpfRectToCv(roi, matTemplate);
                        InspectionResult res = VisionInspectionService.ExecuteInspection(matTemplate, resizedSample, cvRect, roi);
                        // 1. 将 OpenCvSharp 的 Mat 转换为 WPF 支持的 BitmapSource 
                        BitmapSource roiBmp = null;
                        if (res.mat != null && !res.mat.IsDisposed)
                        {
                            // 使用 OpenCvSharp.WpfExtensions 库进行转码
                            roiBmp = OpenCvSharp.WpfExtensions.BitmapSourceConverter.ToBitmapSource(res.mat);
                            // 转码完成后及时释放 Mat 内存
                            res.mat.Dispose();
                        }
                        string roiName = string.IsNullOrEmpty(roi.DisplayName) ? $"ROI_{i + 1}" : roi.DisplayName;
                        if (!res.IsPassed)
                        {
                            isNG = true;
                            logs.Add($"❌ [{roiName}] 异常: {res.Message}");
                        }
                        else
                        {
                            logs.Add($"  [{roiName}] 正常: {res.Message}");
                        }
                        _lastRoiResults.Add(new RoiResultInfo
                        {
                            PixelRoi = roi,
                            IsPassed = res.IsPassed,
                            DiffRatio = res.Score,
                            Message = res.Message,
                            RoiImageSource = roiBmp // 保存转换后的图像
                        });
                    }
                    // resizedSample.Dispose();
                    // 更新 UI 结果文本
                    if (isNG)
                    {
                        BorderTestResult.Background = new SolidColorBrush(Colors.Red);
                        TxtTestResult.Text = "RESULT: NG";
                        TxtTestDetails.Text = string.Join("\n", logs);
                    }
                    else
                    {
                        BorderTestResult.Background = new SolidColorBrush(Colors.Green);
                        TxtTestResult.Text = "RESULT: PASS";
                        TxtTestDetails.Text = "✅ 所有 ROI 视觉算法检测项全部合格！";
                        TxtTestDetails.Text += string.Join("\n", logs);
                    }
                    _isShowingTestResults = true;
                    Dispatcher.BeginInvoke( System.Windows.Threading.DispatcherPriority.Render,
                        new Action(() =>
                        {
                            RedrawRoiOverlays();            
                            resizedSample.Dispose(); // 检测结果已经画完，再释放
                        }));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检测异常: {ex.Message}");
            }
        }

        private void RedrawRoiOverlays()
        {
            if (!_isShowingTestResults)
                return;
            if (ImgTestSample == null ||
                ImgTestSample.Source == null)
                return;
            if (_lastRoiResults == null ||
                _lastRoiResults.Count == 0)
                return;
            DrawRoiResultsOnCanvas( CanvasTestSampleRoi,ImgTestSample, _lastRoiResults);
        }
        /// <summary>
        /// 在待测图上绘制最终检测结果。
        ///
        /// ROIImageSource：检测算法产生的 ROI 结果图 
        /// IsPassed：PASS / NG
        /// </summary>
        private void DrawRoiResultsOnCanvas( Canvas canvas,  Image imageControl, List<RoiResultInfo> roiResults)
        {
            canvas.Children.Clear();
            if (roiResults == null || roiResults.Count == 0 || imageControl == null || imageControl.Source == null)
            {
                return;
            }
            double ctrlW = imageControl.ActualWidth;
            double ctrlH = imageControl.ActualHeight;
            if (ctrlW <= 0 || ctrlH <= 0)
            {
                return;
            }
            double imgW;
            double imgH;
            if (imageControl.Source is BitmapSource bitmapSource)
            {
                imgW = bitmapSource.PixelWidth;
                imgH = bitmapSource.PixelHeight;
            }
            else
            {
                imgW = imageControl.Source.Width;
                imgH = imageControl.Source.Height;
            }
            if (imgW <= 0 || imgH <= 0)
            {
                return;
            }
            // =========================================================
            // Stretch="Fill"
            // =========================================================
            double scaleX = ctrlW / imgW;
            double scaleY = ctrlH / imgH;
            foreach (var result in roiResults)
            {
                RoiRect roi = result.PixelRoi;
                double drawX = roi.X * scaleX;
                double drawY = roi.Y * scaleY;
                double drawW = roi.Width * scaleX;
                double drawH = roi.Height * scaleY;
                // =====================================================
                // 1. 绘制算法输出图
                // =====================================================
                if (result.RoiImageSource != null)
                {
                    System.Windows.Controls.Image imgRoi =
                        new System.Windows.Controls.Image
                        {
                            Source = result.RoiImageSource,
                            Width = drawW,
                            Height = drawH,
                            Stretch = Stretch.Fill
                        };
                    Canvas.SetLeft(imgRoi, drawX);
                    Canvas.SetTop(imgRoi, drawY);
                    canvas.Children.Add(imgRoi);
                }
                // =====================================================
                // 2. 绘制 PASS / NG 外框
                // =====================================================
                System.Windows.Media.Brush borderColor = result.IsPassed ? Brushes.Lime : Brushes.Red;
                System.Windows.Shapes.Rectangle rect =
                    new System.Windows.Shapes.Rectangle
                    {
                        Stroke = borderColor,
                        StrokeThickness = 2,
                        Width = drawW,
                        Height = drawH,
                        Fill = Brushes.Transparent
                    };
                Canvas.SetLeft(rect, drawX);
                Canvas.SetTop(rect, drawY);
                canvas.Children.Add(rect);
            }
        }

        // ----------------------------------------------------
        // 6. 辅助方法与硬件交互  
        // ----------------------------------------------------

        private void ImgTestTemplate_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                string path = SelectLocalImage();
                if (!string.IsNullOrEmpty(path))
                {
                    _testTemplatePath = path;
                    ImgTestTemplate.Source = LoadBitmapWithoutLock(path);
                }
            }
        }

        private void ImgTestSample_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                string path = SelectLocalImage();
                if (!string.IsNullOrEmpty(path))
                {
                    _testSamplePath = path;
                    ImgTestSample.Source = LoadBitmapWithoutLock(path);
                    if (_currentTestProject != null)
                    {
                        DrawRoiOnCanvas(CanvasTestSampleRoi, ImgTestSample, _currentTestProject.RoiList, Brushes.Yellow);
                    }
                }
            }
        }
       
       
        private string SelectLocalImage()
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "图像文件|*.jpg;*.jpeg;*.png;*.bmp"
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private BitmapImage LoadBitmapWithoutLock(string path)
        {
            BitmapImage bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void BtnSnapTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (MainWindow.CameraService == null) return;
            BitmapSource snap = MainWindow.CameraService.GrabSingleFrame();
            if (snap != null)
            {
                ImgTemplate.Source = snap;
                _currentTemplateImage = snap;
                ClearRoiCanvas();
                MessageBox.Show("模板图像已抓取！请在图像上按住左键拖拽框选关键检测区域。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("无法获取相机画面，请检查相机是否开启拉流！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void BtnTestSample_Click(object sender, RoutedEventArgs e)
        {
            if (MainWindow.CameraService == null)
                return;
            BitmapSource snap = MainWindow.CameraService.GrabSingleFrame();
            if (snap != null)
            {
                // =====================================================
                // 新图片来了，重新进入“普通 ROI 显示状态”
                // =====================================================
                _isShowingTestResults = false;
                _currentCapturedFrame = snap;
                _lastRoiResults.Clear();
                ImgTestSample.Source = snap;
                CanvasTestSampleRoi.Children.Clear();
                TxtTestResult.Text = "待测图已拍摄，请点击【执行比对检测】";
                BorderTestResult.Background = new SolidColorBrush(Colors.Gray);
                // 普通状态显示黄色 ROI
                if (_currentTestProject != null)
                {
                    DrawRoiOnCanvas( CanvasTestSampleRoi, ImgTestSample,  _currentTestProject.RoiList, Brushes.Yellow);
                }
            }
            else
            {
                MessageBox.Show("无法获取相机画面，请检查相机是否开启拉流！","错误",MessageBoxButton.OK,MessageBoxImage.Error);
            }
        }
        private void ClearUiDisplay()
        {
            ImgTemplate.Source = null;
            _currentTemplateImage = null;
            ClearRoiCanvas();
        }

        private void ApplyPermissions(string role)
        {
            if (role == "操作员")
            {
                ConfigContainer.IsEnabled = false;
                ProjectContainer.IsEnabled = false;
                BorderRoleTip.Visibility = Visibility.Visible;
            }
        }

        private void LoadAndScanCameras()
        {
            List<CameraDeviceInfo> cameras = UsbCameraService.GetAvailableCameras();
            CmbCameras.ItemsSource = cameras;
            if (cameras.Count > 0)
            {
                string savedMoniker = AppConfig.GetCameraMoniker();
                int index = cameras.FindIndex(c => c.MonikerString == savedMoniker);
                CmbCameras.SelectedIndex = index >= 0 ? index : 0;
            }
        }
        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadAndScanCameras();
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (CmbCameras.SelectedValue != null)
            {
                ManualOffsetX = Convert.ToInt32(FixX.Text);//保存手动X轴偏移量
                ManualOffsetY = Convert.ToInt32(FixY.Text);//保存手动Y轴偏移量
                AppConfig.SaveCameraMoniker(CmbCameras.SelectedValue.ToString());
                if (Par1.IsChecked == true)
                {
                    //开启调试落盘（可在运行目录下 debug_rois 文件夹查看传给模型的图片）
                    VisionInspectionService.IsDebugSaveRoi = true;
                }
                else
                {
                    //开启调试落盘（可在运行目录下 debug_rois 文件夹查看传给模型的图片）
                    VisionInspectionService.IsDebugSaveRoi = false;
                }
                MessageBox.Show("相机配置保存成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        private void BtnTestSingleRoi(object sender, RoutedEventArgs e)
        {     
        }

        private void FixX_TextChanged(object sender, TextChangedEventArgs e)
        {
        }
    }
}