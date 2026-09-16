using System;
using System.Windows.Media.Imaging;

namespace PcbInspection.Services
{
    public interface ICameraService
    {
        // 实时图像回调事件（推流给 UI 渲染）
        event Action<BitmapSource> ImageCaptured;

        bool OpenCamera();
        void CloseCamera();
        void StartGrabbing();
        void StopGrabbing();
        BitmapSource GrabSingleFrame(); // 抓取单帧（拍照）
    }
}