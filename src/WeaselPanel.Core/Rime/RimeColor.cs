//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  Rime 颜色字面量的解析与生成。
//
//  由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  RimeColor.swift 直译而来，随 Weasel Panel 以 GPL-3.0 分发。
//
//  ── Windows 侧的字节序开关 ────────────────────────────────────────────
//  小狼毫支持 per-scheme 的颜色字节序开关，键为
//      preset_color_schemes/<scheme>/color_format: "argb" | "rgba" | "abgr"
//  （macOS 鼠须管硬编码 ABGR，无此键。）默认 abgr。
//
//  ⚠️ 注意：这是 **每套配色独立** 的键，不是全局的 `style/color_format`。
//     上游依据见 RimeWithWeasel/RimeWithWeasel.cpp 的 _RimeGetColor 调用点：
//         std::string prefix("preset_color_schemes/");
//         prefix += (color.empty()) ? buffer : color;
//         _RimeParseStringOptWithFallback(config,
//             (prefix + "/color_format").c_str(), fmt, _colorFmt, COLOR_ABGR);
//
//  ── 解析流水线（严格复刻上游，勿"优化"）────────────────────────────────
//  上游 _RimeGetColor 的最终产物是一个 **ABGR 布局（COLORREF，0xAABBGGRR）**
//  的 32 位值，再交给渲染层。本类复刻同一流水线：
//
//      1. parse_color_code：识别 `#` / `0x` / `0X` 前缀，
//         十六进制段长度必须是 3 / 4 / 6 / 8；3、4 位按「每位重复两次」扩展
//         成 6、8 位；超长截断至 8 位；任一字符非十六进制即判非法。
//      2. 6 位值补 alpha：fmt != rgba 时 `| 0xff000000`；
//         fmt == rgba 时 `(v << 8) | 0x000000ff`。
//      3. 按 color_format 归一到 ABGR：ARGB2ABGR / RGBA2ABGR。
//      4. `& 0xffffffff`。
//
//  第 3、4 步的位运算与上游宏逐位一致（见 RimeWithWeasel.cpp 第 16–21 行），
//  改错任何一位都会让「面板预览」与「实际候选窗」颜色不一致。
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
        _ => RimeColorFormat.Abgr      // 未知值一律回退默认（与上游 _RimeParseStringOptWithFallback 一致）
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
    public static RimeColor? FromYamlValue(object? yamlValue, RimeColorFormat format = RimeColorFormat.Abgr) =>
        TryParseAbgr(yamlValue, format, out var abgr) ? FromAbgr(abgr) : null;

    /// <summary>
    /// 直接解析到 ABGR 打包值（0xAABBGGRR）。
    /// 配色回退链全程按整数运算（上游 _RimeGetColor 的 value 就是 int），
    /// 走这条路可以避开 double 往返，与上游逐位一致。
    /// </summary>
    public static bool TryParseAbgr(object? yamlValue, RimeColorFormat format, out uint abgr)
    {
        var parsed = yamlValue switch
        {
            string text => ParseToAbgr(text, format),
            int number => ParseIntegerToAbgr(unchecked((ulong)number), number <= 0xFFFFFF, format),
            uint u => ParseIntegerToAbgr(u, u <= 0xFFFFFF, format),
            long longNumber => ParseIntegerToAbgr(unchecked((ulong)longNumber), longNumber <= 0xFFFFFF, format),
            ulong ulongNumber => ParseIntegerToAbgr(ulongNumber, ulongNumber <= 0xFFFFFF, format),
            _ => null
        };
        abgr = parsed ?? 0;
        return parsed.HasValue;
    }

    private static uint? ParseToAbgr(string hexText, RimeColorFormat format)
    {
        var hex = NormalizeColorCode(hexText);
        if (hex is null) return null;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return null;
        return Finalize(packed, hex.Length == 6, format);
    }

    private static uint? ParseIntegerToAbgr(ulong value, bool isSixDigitRange, RimeColorFormat format) =>
        // 上游：`else if (value > 0xffffffff) value &= 0xffffffff;`
        Finalize(unchecked((uint)value), isSixDigitRange, format);

    /// <summary>补 alpha + 归一到 ABGR。对应上游 _RimeGetColor 尾部的三段处理。</summary>
    private static uint Finalize(uint packed, bool isSixDigitRange, RimeColorFormat format)
    {
        // 6 位值补 alpha（与上游一致：仅对 6 位生效，8 位不动）
        if (isSixDigitRange)
        {
            packed = format != RimeColorFormat.Rgba
                ? (packed | 0xFF000000u)
                : ((packed << 8) | 0x000000FFu);
        }
        return ToAbgr(packed, format);
    }

    /// <summary>
    /// 复刻上游 parse_color_code：抽出并规范化十六进制段。
    /// </summary>
    /// <returns>归一化后的 6 或 8 位十六进制串；非法输入返回 null。</returns>
    internal static string? NormalizeColorCode(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return null;

        // 上游不 trim，但 YAML 标量偶有残留空白；这里多容忍一步，
        // 属于「上游不可达分支」的宽松处理，不会造成行为分歧。
        var str = rawText.Trim();
        if (str.Length == 0) return null;

        int start;
        if (str[0] == '#') start = 1;
        else if (str.Length >= 2 &&
                 (string.CompareOrdinal(str, 0, "0x", 0, 2) == 0 ||
                  string.CompareOrdinal(str, 0, "0X", 0, 2) == 0)) start = 2;
        else return null;

        var hexPart = str.Substring(start);
        if (hexPart.Length == 0) return null;
        if (hexPart.Length != 3 && hexPart.Length != 4 &&
            hexPart.Length != 6 && hexPart.Length != 8) return null;

        foreach (var c in hexPart)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return null;
        }

        // 3/4 位短格式：每位重复两次（abc → aabbcc，abcd → aabbccdd）
        return hexPart.Length switch
        {
            3 => string.Concat(Dup(hexPart[0]), Dup(hexPart[1]), Dup(hexPart[2])),
            4 => string.Concat(Dup(hexPart[0]), Dup(hexPart[1]), Dup(hexPart[2]), Dup(hexPart[3])),
            _ => hexPart
        };

        static string Dup(char c) => new string(c, 2);
    }

    /// <summary>按 color_format 归一到 ABGR 布局（上游 ARGB2ABGR / RGBA2ABGR 宏）。</summary>
    public static uint ToAbgr(uint value, RimeColorFormat format) => format switch
    {
        RimeColorFormat.Argb =>
            (value & 0xFF000000u) | ((value & 0x000000FFu) << 16) |
            (value & 0x0000FF00u) | ((value & 0x00FF0000u) >> 16),
        RimeColorFormat.Rgba =>
            ((value & 0x000000FFu) << 24) | ((value & 0xFF000000u) >> 24) |
            ((value & 0x00FF0000u) >> 8) | ((value & 0x0000FF00u) << 8),
        _ => value
    } & 0xFFFFFFFFu;

    /// <summary>从 ABGR 布局（0xAABBGGRR）解码。上游最终 value 即此布局。</summary>
    public static RimeColor FromAbgr(uint abgr) => new(
        red: (abgr & 0xFF) / 255.0,
        green: ((abgr >> 8) & 0xFF) / 255.0,
        blue: ((abgr >> 16) & 0xFF) / 255.0,
        alpha: ((abgr >> 24) & 0xFF) / 255.0);

    /// <summary>取 ABGR 打包值（供配色回退链的混合运算使用，通道语义见 FromAbgr）。</summary>
    public uint ToAbgr() =>
        ((uint)Math.Round(Alpha * 255) << 24) |
        ((uint)Math.Round(Blue * 255) << 16) |
        ((uint)Math.Round(Green * 255) << 8) |
        (uint)Math.Round(Red * 255);

    // MARK: - 生成

    /// <summary>输出 Rime 认识的字面量。透明度为 1 时省略 alpha 段。</summary>
    public string Literal(RimeColorFormat format = RimeColorFormat.Abgr)
    {
        var r = (int)Math.Round(Red * 255);
        var g = (int)Math.Round(Green * 255);
        var b = (int)Math.Round(Blue * 255);

        if (Alpha >= 0.999)
        {
            // 6 位字面量的低 24 位：abgr 存 BBGGRR，argb / rgba 存 RRGGBB
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
