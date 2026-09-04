using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace WeaselPanel.App.Services;

/// <summary>
/// 从 C# 嵌入式资源读取 logo 等图片，绕开 pack://application:,,,/...
/// 在 PublishSingleFile 自包含发布下会触发 NullReference 的坑。
///
/// manifest 资源命名规则：默认命名空间 + 目录 + 文件名（目录分隔符换为 .）。
/// 例：Resources/logo.png  在 WeaselPanel.App 命名空间下，叫
///     "WeaselPanel.App.Resources.logo.png"。
/// </summary>
public static class EmbeddedAssets
{
    private const string DefaultNamespace = "WeaselPanel.App";

    /// <summary>
    /// 读 logo.png（Resources 目录下）。
    /// 找不到或 IO 出错时返回 null，调用方负责回退到纯文本占位。
    /// </summary>
    public static BitmapImage? TryLoadLogo()
    {
        return TryLoadBitmap($"{DefaultNamespace}.Resources.logo.png");
    }

    /// <summary>
    /// 读爻知云公众号二维码（Resources 目录下，复用鼠须管面板同款推广图）。
    /// 缺失或 IO 出错时返回 null，调用方负责保持推广区只显示文字。
    /// </summary>
    public static BitmapImage? TryLoadYaozhiQR()
    {
        return TryLoadBitmap($"{DefaultNamespace}.Resources.YaozhiQRCode.png");
    }

    /// <summary>
    /// 通用嵌入式 BitmapImage 读取。
    /// </summary>
    public static BitmapImage? TryLoadBitmap(string manifestResourceName)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(manifestResourceName);
            if (stream is null) return null;

            // 直接用 BitmapImage + 缓存模式：避免锁定外部文件（embedded 不会锁）
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = stream;
            img.EndInit();
            img.Freeze();   // 跨线程安全 + 释放中间缓冲
            return img;
        }
        catch
        {
            return null;
        }
    }
}
