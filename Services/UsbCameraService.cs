using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AForge.Video;
using AForge.Video.DirectShow;

namespace PcbInspection.Services
{
    // 相机设备信息
    public class CameraDeviceInfo
    {
        public string Name { get; set; }
        public string MonikerString { get; set; }
    }
    // Camera Watchdog Test
    public class UsbCameraService : ICameraService
    {
        public event Action<BitmapSource> ImageCaptured;
        private VideoCaptureDevice _videoSource;
        // 最新一帧
        private BitmapSource _currentFrame;
        private readonly object _frameLock = new object();
        private readonly object _cameraLock = new object();
        private bool _isClosing = false;
        // ==============================
        // 看门狗
        // ==============================
        private CancellationTokenSource _watchdogCts;
        // 最近一次收到 NewFrame 的时间
        private DateTime _lastFrameTime = DateTime.MinValue;
        // 是否正在自动重启
        private int _isRestarting = 0;
        // 最近一次打开相机使用的 Moniker
        private string _cameraMoniker;
        // 最近一次打开相机的设备名称
        private string _cameraName;
        // 看门狗参数
        private const int WatchdogIntervalMs = 2000;
        // 超过 5 秒没有收到新帧，认为相机卡死
        private const int FrameTimeoutSeconds = 5;
        // 自动重启失败后等待时间
        private const int RestartDelayMs = 2000;
      
        // 获取所有 USB 相机     
        public static List<CameraDeviceInfo> GetAvailableCameras()
        {
            var cameraList = new List<CameraDeviceInfo>();
            try
            {
                var videoDevices = new FilterInfoCollection( FilterCategory.VideoInputDevice);
                foreach (FilterInfo device in videoDevices)
                {
                    cameraList.Add(new CameraDeviceInfo
                    {
                        Name = device.Name,
                        MonikerString = device.MonikerString
                    });
                }
            } 
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine( $"扫描相机失败：{ex.Message}");
            }
            return cameraList;
        }
   
        // 根据 Moniker 打开相机   
        public bool OpenCameraByMoniker(string monikerString)
        {
            if (string.IsNullOrWhiteSpace(monikerString))
                return false;
            try
            {
                CloseCamera();   // 先停止旧相机 重新启动
                lock (_cameraLock)//对象锁
                {
                    _cameraMoniker = monikerString;
                    _isClosing = false;
                    _lastFrameTime = DateTime.Now;
                    _videoSource =new VideoCaptureDevice(monikerString);
                    _videoSource.NewFrame += OnNewFrame;
                }
                System.Diagnostics.Debug.WriteLine($"相机已打开：{monikerString}");
                StartWatchdog(); // 启动看门狗 
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"打开相机失败：{ex.Message}");
                CloseCamera();
                return false;
            }
        }
        
        // 打开第一台相机 
       
        public bool OpenCamera()
        {
            var list = GetAvailableCameras();
            if (list == null || list.Count == 0)
                return false;
            _cameraName = list[0].Name;
            return OpenCameraByMoniker(list[0].MonikerString);
        }
        
        // 开始采集      
        public void StartGrabbing()
        {
            try
            {
                lock (_cameraLock)
                {
                    if (_videoSource == null)
                        return;
                    _isClosing = false;
                    _lastFrameTime = DateTime.Now;
                    if (!_videoSource.IsRunning)
                    {
                        System.Diagnostics.Debug.WriteLine( "开始启动相机采集");
                        _videoSource.Start();
                    }
                }
                StartWatchdog();// 确保看门狗运行
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine( $"启动相机采集失败：{ex.Message}");
            }
        }
       
        // 停止采集     
        public void StopGrabbing()
        {
            VideoCaptureDevice source = null;
            lock (_cameraLock)
            {
                source = _videoSource;
                if (source == null)
                    return;
                _isClosing = true;
                try
                {
                    if (source.IsRunning)
                    {
                        source.SignalToStop();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine( $"发送停止信号失败：{ex.Message}");
                }
            }
            Task.Run(() =>
            {
                try
                {
                    if (source != null)
                    {
                        source.WaitForStop();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine( $"等待相机停止失败：{ex.Message}");
                }
            });
        }
        // 关闭相机    
        public void CloseCamera()
        {
            VideoCaptureDevice source = null;
            StopWatchdog(); //停止看门狗 
            lock (_cameraLock)
            {
                _isClosing = true;
                source = _videoSource; 
                _videoSource = null; 
                if (source == null)
                    return;
                try
                {
                    source.NewFrame -= OnNewFrame;
                }
                catch {  }
            }
            try
            {
                if (source.IsRunning)
                {
                    source.SignalToStop();
                }
            }
            catch{ }
            // 后台等待
            Task.Run(() =>
            {
                try
                {
                    if (source.IsRunning)
                    {
                        source.WaitForStop();
                    }
                }
                catch { }
            });
            lock (_frameLock)
            {
                _currentFrame = null;
            }
            _lastFrameTime = DateTime.MinValue;
        }
        // =========================================================
        // 获取最新帧
        // =========================================================
        public BitmapSource GrabSingleFrame()
        {
            lock (_frameLock)
            {
                return _currentFrame;
            }
        }
        // =========================================================
        // AForge 新帧
        // =========================================================
        private void OnNewFrame( object sender, NewFrameEventArgs eventArgs)
        {
            if (_isClosing)
                return;
            try
            {
                // 非常重要：
                // 一收到 NewFrame 就更新时间
                _lastFrameTime = DateTime.Now;
                using (Bitmap frameBitmap = (Bitmap)eventArgs.Frame.Clone())
                {
                    BitmapSource bitmapSource = ConvertBitmapToBitmapSource(frameBitmap);
                    if (bitmapSource == null)
                        return;
                    // 保存最新帧
                    lock (_frameLock)
                    {
                        _currentFrame = bitmapSource;
                    }
                    // 注意：
                    // 这里不要执行耗时操作 
                    try
                    {
                        ImageCaptured?.Invoke(bitmapSource);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine( $"ImageCaptured 回调异常：{ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine( $"相机帧处理异常：{ex.Message}");
            }
        }
        // Bitmap → BitmapSource      
        private BitmapSource ConvertBitmapToBitmapSource( Bitmap bitmap)
        {
            if (bitmap == null)
                return null;
            try
            {
                using (MemoryStream stream =  new MemoryStream())
                {
                    bitmap.Save( stream, ImageFormat.Bmp);
                    stream.Position = 0;
                    BitmapImage result =  new BitmapImage();
                    result.BeginInit();
                    result.CacheOption = BitmapCacheOption.OnLoad;
                    result.StreamSource = stream;
                    result.EndInit();
                    result.Freeze();
                    return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine( $"Bitmap 转 BitmapSource 失败：{ex.Message}");
                return null;
            }
        }
        // 启动看门狗
        private void StartWatchdog()
        {
            lock (_cameraLock)
            {
                if (_watchdogCts != null)
                    return;
                _watchdogCts = new CancellationTokenSource();
            }
            CancellationToken token = _watchdogCts.Token;
            Task.Run(async () =>
            {
                System.Diagnostics.Debug.WriteLine( "相机看门狗启动");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay( WatchdogIntervalMs, token);
                        if (token.IsCancellationRequested)
                            break;
                        CheckCameraHealth();
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine( $"相机看门狗异常：{ex.Message}");
                    }
                }
                System.Diagnostics.Debug.WriteLine( "相机看门狗停止");
            }, token);
        }
        // =========================================================
        // 停止看门狗  
        // =========================================================
        private void StopWatchdog()
        {
            CancellationTokenSource cts;
            lock (_cameraLock)
            {
                cts = _watchdogCts;
                _watchdogCts = null;
            }
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch  { }
            }
        }
        // 检查相机是否正常
        private void CheckCameraHealth()
        {
            if (_isClosing)
                return;
            VideoCaptureDevice source;
            lock (_cameraLock)
            {
                source = _videoSource;
            }
            if (source == null)
                return;
            bool running = false;
            try
            {
                running = source.IsRunning;
            }
            catch  {  }
            TimeSpan noFrameTime = DateTime.Now - _lastFrameTime;
            // 情况1：
            // 相机线程已经停止
            if (!running)
            {
                System.Diagnostics.Debug.WriteLine( "⚠ 相机 IsRunning=false，准备自动重启");
                RestartCameraAsync();
                return;
            }
            // 情况2：
            // IsRunning=true
            // 但是很长时间没有收到 NewFrame
            if (noFrameTime.TotalSeconds > FrameTimeoutSeconds)
            {
                System.Diagnostics.Debug.WriteLine(  $"⚠ 相机疑似卡死："  + $" {noFrameTime.TotalSeconds:F1} 秒没有新帧");
                RestartCameraAsync();
                return;
            }
        }
        // 自动重启相机
        private void RestartCameraAsync()
        {
            // 防止多个看门狗同时重启
            if (Interlocked.Exchange( ref _isRestarting, 1) == 1)
            {
                return;
            }
            Task.Run(async () =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine( "================================");
                    System.Diagnostics.Debug.WriteLine( "⚠ 开始自动重启 USB 相机");
                    System.Diagnostics.Debug.WriteLine( "================================");
                    // 暂时停止看门狗
                    StopWatchdog();
                    VideoCaptureDevice oldSource = null;
                    lock (_cameraLock)
                    {
                        oldSource = _videoSource;
                        _videoSource = null;
                        _isClosing = true;
                        if (oldSource != null)
                        {
                            try
                            {
                                oldSource.NewFrame -= OnNewFrame;
                            }
                            catch { }
                        }
                    }
                    // 停止旧相机
                    if (oldSource != null)
                    {
                        try
                        {
                            if (oldSource.IsRunning)
                            {
                                oldSource.SignalToStop();
                                //这里是在后台线程     
                                //可以安全 WaitForStop 
                                oldSource.WaitForStop();
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine( $"停止旧相机异常：{ex.Message}");
                        }
                    }
                    // 等待 USB/DirectShow 释放
                    await Task.Delay(RestartDelayMs);
                    if (string.IsNullOrWhiteSpace( _cameraMoniker))
                    {
                        System.Diagnostics.Debug.WriteLine( "❌ 没有保存相机 Moniker，无法自动重启");
                        return;
                    }
                    // 重新创建相机  
                    VideoCaptureDevice newSource = null;
                    try
                    {
                        newSource = new VideoCaptureDevice( _cameraMoniker);
                        newSource.NewFrame += OnNewFrame;
                        lock (_cameraLock)
                        {
                            _videoSource = newSource;
                            _isClosing = false;
                            _lastFrameTime =DateTime.Now;
                        }
                        newSource.Start();
                        System.Diagnostics.Debug.WriteLine( "✅ 相机自动重启成功");
                        // 重新启动看门狗 
                        StartWatchdog();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine( $"❌ 相机重新启动失败：{ex.Message}");
                        try
                        {
                            if (newSource != null)
                            {
                                newSource.NewFrame -= OnNewFrame;
                                if (newSource.IsRunning)
                                {
                                    newSource.SignalToStop();
                                    newSource.WaitForStop();
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(  $"❌ 自动重启相机异常：{ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange( ref _isRestarting, 0);
                }
            });
        }
    }
}