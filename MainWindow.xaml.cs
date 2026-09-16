using PcbInspection.Pages;
using PcbInspection.Services;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace PcbInspection
{
    public partial class MainWindow : Window
    {
        // 目标宽高比：1450 / 1200
        private const double TargetAspectRatio = 1450.0 / 1200.0;
        private const int WM_SIZING = 0x0214;
        // ==================== 全局热键 ====================

        // Windows 全局热键消息
        private const int WM_HOTKEY = 0x0312;

        // 防止按住按键时重复触发
        private const uint MOD_NOREPEAT = 0x4000;

        // 全局热键ID
        private const int HOTKEY_1 = 1001;
        private const int HOTKEY_NUMPAD1 = 1002;

        // Windows虚拟键码
        private const uint VK_1 = 0x31;       // 键盘上方数字1
        private const uint VK_NUMPAD1 = 0x61; // 小键盘数字1

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(
            IntPtr hWnd,
            int id);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // 1. 全局唯一的相机服务对象
        public static ICameraService CameraService { get; private set; }

        private readonly DetectPage _detectPage;
        private readonly ConfigPage _configPage;
        private readonly DataPage _dataPage;

        public MainWindow(string userRole)
        {
            InitializeComponent();
            // 2. 初始化全局相机服务
            CameraService = new UsbCameraService();
            _detectPage = new DetectPage();
            _configPage = new ConfigPage(userRole);
            _dataPage = new DataPage();
            TxtCurrentUser.Text = userRole;
            // 默认加载检测界面
            MainFrame.Navigate(_detectPage);
            string modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "best.onnx");
            VisionInspectionService.InitYoloModel(modelPath);
            // 3. 只有当整个主软件彻底关闭时，才释放相机资源
            //this.Closed += (s, e) => CameraService?.CloseCamera();
            this.Closed += MainWindow_Closed;
        }
        private void MainWindow_Closed(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HOTKEY_1);  // 注销全局热键 
            UnregisterHotKey(hwnd, HOTKEY_NUMPAD1);
            CameraService?.CloseCamera(); // 释放相机
        }
        /// <summary>
        /// 挂载 Windows 原生消息钩子，实现鼠标拖拉窗口时锁定 1450:1200 宽高比
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null)
            {
                source.AddHook(WindowProc);
            }
            RegisterGlobalHotKeys(hwnd); //注册全局数字1
        }
        /// <summary>
        /// 注册全局键盘快捷键
        /// </summary>
        private void RegisterGlobalHotKeys(IntPtr hwnd)
        {
            //键盘上方的数字 1
            bool result1 = RegisterHotKey( hwnd, HOTKEY_1, MOD_NOREPEAT, VK_1);
            //小键盘的数字 1
            bool result2 = RegisterHotKey( hwnd, HOTKEY_NUMPAD1,  MOD_NOREPEAT, VK_NUMPAD1);
            if (!result1)
            {
                int error = Marshal.GetLastWin32Error();
                MessageBox.Show( $"注册全局数字1失败！\r\n错误代码：{error}", "全局热键",  MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (!result2)
            {
                int error = Marshal.GetLastWin32Error();
                MessageBox.Show( $"注册小键盘1失败！\r\n错误代码：{error}","全局热键", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {  // =========================================================
           // 1. 全局键盘热键
           // =========================================================
            if (msg == WM_HOTKEY)
            {
                int hotkeyId = wParam.ToInt32();
                // 数字1/小键盘1
                if (hotkeyId == HOTKEY_1 || hotkeyId == HOTKEY_NUMPAD1)
                {
                    // 只有当前处于DetectPage才执行检测
                    if (MainFrame.Content == _detectPage)
                    {
                        _detectPage.TriggerStartDetect();
                    }
                    // 表示这个消息已经处理
                    handled = true;
                }

                return IntPtr.Zero;
            }
            if (msg == WM_SIZING)
            {
                RECT rect = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT));
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                // 根据当前拉伸的主方向，动态锁定纵横比
                if (width / (double)height > TargetAspectRatio)
                {
                    rect.Right = rect.Left + (int)(height * TargetAspectRatio);
                }
                else
                {
                    rect.Bottom = rect.Top + (int)(width / TargetAspectRatio);
                }
                Marshal.StructureToPtr(rect, lParam, true);
            }
            return IntPtr.Zero;//0
        }
        private void RadioButton_Checked(object sender, RoutedEventArgs e) { }
        private void NavDetect_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_detectPage);
        private void NavConfig_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_configPage);
        private void NavData_Click(object sender, RoutedEventArgs e) => MainFrame.Navigate(_dataPage);
    }
}