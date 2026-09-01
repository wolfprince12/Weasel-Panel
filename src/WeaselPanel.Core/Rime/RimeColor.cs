//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  Rime 颜色字面量的解析与生成。
//
//  由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  RimeColor.swift 直译而来，随 Weasel Panel 以 GPL-3.0 分发。
//
//  Windows 侧增强：小狼毫的 weasel.yaml 支持 `style/color_format: abgr | argb | rgba`
//  全局切换颜色字节序（macOS 鼠须管硬编码 ABGR，无此键）。
//  因此本类把字节序做成参数，解析与生成均按当前 color_format 处理，
//  默认 abgr 与 macOS 版行为完全一致。
//
//  与 WPF 的 Color 互操作不放在本层（Core 禁止引用 System.Windows.*），
//  由 App 层扩展方法提供。

using System;
using System.Globalization;

namespace WeaselPanel.Core.Rime;

public enum RimeColorFormat
{
    /// <summary>0xAABBGGRR（小狼毫与鼠须管的默认，注意是 ABGR 倒序，不是常见的 ARGB）。</summary>
    Abgr,
    /// <summary>0xAARRGGBB。</summary>
    Argb,
    /// <summary>0xRRGGBBAA。</summary>
    Rgba
}

public static class RimeColorFormatExtensions
{
    public static RimeColorFormat FromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "argb" => RimeColorFormat.Argb,
        "rgba" => RimeColorFormat.Rgba,
        _ => RimeColorFormat.Abgr      // srgb 等未知值一律回退默认（与上游一致）
    };

    public static string ToConfigName(this RimeColorFormat format) => format switch
    {
        RimeColorFormat.Argb => "argb",
        RimeColorFormat.Rgba => "rgba",
        _ => "abgr"
    };
}

/// <summary>Rime 的 color_space 字段：srgb（默认）或 display_p3。</summary>
public enum RimeColorSpace
{
    Srgb,
    DisplayP3
}

public static class RimeColorSpaceExtensions
{
    public static RimeColorSpace FromName(string? name) =>
        string.Equals(name?.Trim(), "display_p3", StringComparison.OrdinalIgnoreCase)
            ? RimeColorSpace.DisplayP3
            : RimeColorSpace.Srgb;

    public static string ToConfigName(this RimeColorSpace space) =>
        space == RimeColorSpace.DisplayP3 ? "display_p3" : "srgb";
}

public readonly struct RimeColor : IEquatable<RimeColor>
{
    public double Red { get; }      // 0...1
    public double Green { get; }
    public double Blue { get; }
    public double Alpha { get; }

    public RimeColor(double red, double green, double blue, double alpha = 1)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
    }

    // MARK: - 解析

    /// <summary>
    /// 从 YAML 取到的原始值解析颜色。
    /// YAML 1.1 会把 0xF0E5F6FB 直接读成整数，所以这里同时接受字符串与整数。
    /// </summary>
    public static RimeColor? FromYamlValue(object? yamlValue, RimeColorFormat format = RimeColorFormat.Abgr)
    {
        switch (yamlValue)
        {
            case string text:
                return Parse(hexText: text, format: format);

            // 注意：C# 的 int 只有 32 位，而 Swift 原版的 Int 是 64 位。
            // 0xAABBCCDD 这类值在 C# 里会装箱成 uint（或负数 int），必须分别处理，
            // 否则按 int 取值会因符号扩展得到 0xFFFFFFFFAABBCCDD 之类的错误位型。
            case int number when number < 0:
                // 负数说明最高位为 1，必是含 alpha 的 8 位值
                return Parse(packed: unchecked((uint)number), hasAlpha: true, format: format);
            case int number:
                return Parse(packed: (ulong)number, hasAlpha: number > 0xFFFFFF, format: format);

            case uint u:
                return Parse(packed: u, hasAlpha: u > 0xFFFFFF, format: format);
            case long longNumber:
                return Parse(packed: (ulong)longNumber, hasAlpha: longNumber > 0xFFFFFF, format: format);
            case ulong ulongNumber:
                return Parse(packed: ulongNumber, hasAlpha: ulongNumber > 0xFFFFFF, format: format);

            default:
                return null;
        }
    }

    private static RimeColor? Parse(string hexText, RimeColorFormat format)
    {
        var trimmed = hexText.Trim();
        if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = trimmed.Substring(2);
        if (digits.Length != 6 && digits.Length != 8) return null;
        if (!ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return null;
        return Parse(packed, digits.Length == 8, format);
    }

    private static RimeColor Parse(ulong packed, bool hasAlpha, RimeColorFormat format)
    {
        var b0 = (packed >> 24) & 0xFF;   // 最高字节
        var b1 = (packed >> 16) & 0xFF;
        var b2 = (packed >> 8) & 0xFF;
        var b3 = packed & 0xFF;           // 最低字节

        double alpha, red, green, blue;
        if (format == RimeColorFormat.Rgba)
        {
            // 0xRRGGBBAA（8 位，alpha 在最低字节）/ 0xRRGGBB（6 位，无 alpha，各分量整体右移一字节）
            alpha = hasAlpha ? b3 / 255.0 : 1.0;
            red = (hasAlpha ? b0 : b1) / 255.0;
            green = (hasAlpha ? b1 : b2) / 255.0;
            blue = (hasAlpha ? b2 : b3) / 255.0;
        }
        else if (format == RimeColorFormat.Argb)
        {
            // 0xAARRGGBB / 0xRRGGBB —— alpha 恒在最高字节，RGB 分量位置不变
            alpha = hasAlpha ? b0 / 255.0 : 1.0;
            red = b1 / 255.0;
            green = b2 / 255.0;
            blue = b3 / 255.0;
        }
        else // Abgr
        {
            // 0xAABBGGRR / 0xBBGGRR —— 注意是 ABGR 倒序，alpha 恒在最高字节
            alpha = hasAlpha ? b0 / 255.0 : 1.0;
            blue = b1 / 255.0;
            green = b2 / 255.0;
            red = b3 / 255.0;
        }

        return new RimeColor(red, green, blue, alpha);
    }

    // MARK: - 生成

    /// <summary>输出 Rime 认识的字面量。透明度为 1 时省略 alpha 段。</summary>
    public string Literal(RimeColorFormat format = RimeColorFormat.Abgr)
    {
        var r = (int)Math.Round(Red * 255);
        var g = (int)Math.Round(Green * 255);
        var b = (int)Math.Round(Blue * 255);

        if (Alpha >= 0.999)
        {
            return format == RimeColorFormat.Argb || format == RimeColorFormat.Rgba
                ? string.Format(CultureInfo.InvariantCulture, "0x{0:X2}{1:X2}{2:X2}", r, g, b)
                : string.Format(CultureInfo.InvariantCulture, "0x{0:X2}{1:X2}{2:X2}", b, g, r);
        }

        var a = (int)Math.Round(Alpha * 255);
        return format switch
        {
            RimeColorFormat.Argb =>
                string.Format(CultureInfo.InvariantCulture, "0x{0:X2}{1:X2}{2:X2}{3:X2}", a, r, g, b),
            RimeColorFormat.Rgba =>
                string.Format(CultureInfo.InvariantCulture, "0x{0:X2}{1:X2}{2:X2}{3:X2}", r, g, b, a),
            _ =>
                string.Format(CultureInfo.InvariantCulture, "0x{0:X2}{1:X2}{2:X2}{3:X2}", a, b, g, r)
        };
    }

    public bool Equals(RimeColor other) =>
        Red.Equals(other.Red) && Green.Equals(other.Green) &&
        Blue.Equals(other.Blue) && Alpha.Equals(other.Alpha);

    public override bool Equals(object? obj) => obj is RimeColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(Red, Green, Blue, Alpha);
    public override string ToString() => Literal();
}
