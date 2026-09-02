//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  补丁值的类型封装。用它而不是 object，是为了让「有没有改动」可以直接用 Equals 判断，
//  同时保证写进 YAML 的类型正确（Rime 对 bool 与字符串 "true" 的处理并不相同）。
//
//  由 Squirrel Panel (https://github.com/wolfprince12/squirrel-Panel) 的
//  PatchValue.swift 直译而来，随 Weasel Panel 以 GPL-3.0 分发。

using System;
using System.Collections.Generic;
using System.Linq;

namespace WeaselPanel.Core.Rime;

/// <summary>Rime 配置树里的动态值（对标 Swift 的 <c>[String: Any]</c>）。</summary>
public static class RimeValue
{
    /// <summary>
    /// 深比较任意两个 Rime 值。支持的形态：字符串、数值、布尔、
    /// 列表（<see cref="IReadOnlyList{T}"/>）与映射（<see cref="Dictionary{TKey,TValue}"/>），可任意嵌套。
    /// </summary>
    public static bool ValueEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;

        switch (a, b)
        {
            case (string s1, string s2):
                return string.Equals(s1, s2, StringComparison.Ordinal);
            case (bool b1, bool b2):
                return b1 == b2;
        }

        // 数值统一按 decimal 归一化比较，抹平 int / long / double / float 的类型差异
        if (TryNumber(a, out var na) && TryNumber(b, out var nb)) return na == nb;

        if (a is IReadOnlyList<object?> la && b is IReadOnlyList<object?> lb)
        {
            if (la.Count != lb.Count) return false;
            for (var i = 0; i < la.Count; i++)
                if (!ValueEquals(la[i], lb[i])) return false;
            return true;
        }
        // 可能的 IList<object> / object[] 等其它序列形态
        if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb &&
            a is not string && b is not string)
        {
            var xa = ea.Cast<object?>().ToList();
            var xb = eb.Cast<object?>().ToList();
            if (xa.Count != xb.Count) return false;
            for (var i = 0; i < xa.Count; i++)
                if (!ValueEquals(xa[i], xb[i])) return false;
            return true;
        }

        if (a is Dictionary<string, object?> da && b is Dictionary<string, object?> db)
            return ListOfMapsEquals(new[] { da }, new[] { db });

        if (a is IReadOnlyDictionary<string, object?> ra && b is IReadOnlyDictionary<string, object?> rb)
        {
            if (ra.Count != rb.Count) return false;
            foreach (var (k, v) in ra)
                if (!rb.TryGetValue(k, out var other) || !ValueEquals(v, other)) return false;
            return true;
        }

        return Equals(a, b);
    }

    public static bool ListOfMapsEquals(
        IReadOnlyList<Dictionary<string, object?>> a,
        IReadOnlyList<Dictionary<string, object?>> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Count != y.Count) return false;
            foreach (var (key, vx) in x)
            {
                if (!y.TryGetValue(key, out var vy)) return false;
                if (!ValueEquals(vx, vy)) return false;
            }
        }
        return true;
    }

    private static bool TryNumber(object o, out decimal value)
    {
        switch (o)
        {
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short s: value = s; return true;
            case byte by: value = by; return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d): value = (decimal)d; return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f): value = (decimal)f; return true;
            case decimal m: value = m; return true;
            default: value = 0; return false;
        }
    }
}

public enum PatchValueKind
{
    Bool, Int, Double, String,
    StringList, SchemaList, KeyBindings, Punctuation, MapList, Dictionary
}

/// <summary>
/// 补丁值。对标 Swift 的 <c>PatchValue</c> 枚举；用抽象基类 + 子类实现关联值语义。
/// 相等性为深比较（见 <see cref="RimeValue.ValueEquals"/>）。
/// </summary>
public abstract class PatchValue : IEquatable<PatchValue>
{
    public abstract PatchValueKind Kind { get; }

    /// <summary>标量（bool/int/double/string）——写后校验会对标量做严格值比对。</summary>
    public virtual bool IsScalar => false;

    /// <summary>转换成可交给 YAML 发射器的对象。</summary>
    public abstract object? ToYamlObject();

    public abstract bool ValueEquals(PatchValue other);

    public bool Equals(PatchValue? other) => other is not null && ValueEquals(other);
    public override bool Equals(object? obj) => obj is PatchValue p && Equals(p);
    public override int GetHashCode() => Kind.GetHashCode();

    // MARK: 工厂

    public static PatchValue Of(bool v) => new BoolValue(v);
    public static PatchValue Of(int v) => new IntValue(v);
    public static PatchValue Of(double v) => new DoubleValue(v);
    public static PatchValue Of(string v) => new StringValue(v);
    public static PatchValue StringList(IEnumerable<string> v) => new StringListValue(v.ToList());
    public static PatchValue SchemaList(IEnumerable<string> v) => new SchemaListValue(v.ToList());

    public static PatchValue KeyBindings(IEnumerable<Dictionary<string, object?>> v) =>
        new KeyBindingsValue(v.ToList());

    public static PatchValue Punctuation(Dictionary<string, object?> v) => new PunctuationValue(v);

    public static PatchValue MapList(IEnumerable<Dictionary<string, object?>> v) =>
        new MapListValue(v.ToList());

    public static PatchValue Dictionary(Dictionary<string, object?> v) => new DictionaryValue(v);

    public sealed class BoolValue : PatchValue
    {
        public bool Value { get; }
        public BoolValue(bool value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.Bool;
        public override bool IsScalar => true;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) => other is BoolValue o && o.Value == Value;
        public override string ToString() => Value ? "true" : "false";
    }

    public sealed class IntValue : PatchValue
    {
        public int Value { get; }
        public IntValue(int value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.Int;
        public override bool IsScalar => true;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) => other is IntValue o && o.Value == Value;
        public override string ToString() => Value.ToString();
    }

    public sealed class DoubleValue : PatchValue
    {
        public double Value { get; }
        public DoubleValue(double value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.Double;
        public override bool IsScalar => true;
        // 整数值写成整数，避免 16.0 这种冗余写法
        public override object? ToYamlObject() =>
            Value == Math.Round(Value) && Math.Abs(Value) < 1e9 ? (int)Value : Value;
        public override bool ValueEquals(PatchValue other) => other is DoubleValue o && o.Value.Equals(Value);
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class StringValue : PatchValue
    {
        public string Value { get; }
        public StringValue(string value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.String;
        public override bool IsScalar => true;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) => other is StringValue o && o.Value == Value;
        public override string ToString() => Value;
    }

    public sealed class StringListValue : PatchValue
    {
        public IReadOnlyList<string> Value { get; }
        public StringListValue(IReadOnlyList<string> value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.StringList;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) =>
            other is StringListValue o && Value.SequenceEqual(o.Value);
    }

    public sealed class SchemaListValue : PatchValue
    {
        public IReadOnlyList<string> Value { get; }
        public SchemaListValue(IReadOnlyList<string> value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.SchemaList;
        public override object? ToYamlObject() =>
            Value.Select(s => new Dictionary<string, object?> { ["schema"] = s }).ToList();
        public override bool ValueEquals(PatchValue other) =>
            other is SchemaListValue o && Value.SequenceEqual(o.Value);
    }

    /// <summary>Rime 的 key_bindings 列表（每个元素是映射）。</summary>
    public sealed class KeyBindingsValue : PatchValue
    {
        public IReadOnlyList<Dictionary<string, object?>> Value { get; }
        public KeyBindingsValue(IReadOnlyList<Dictionary<string, object?>> value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.KeyBindings;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) =>
            other is KeyBindingsValue o && RimeValue.ListOfMapsEquals(Value, o.Value);
    }

    /// <summary>标点符号映射表（punctuator/full_shape 与 punctuator/half_shape）。</summary>
    public sealed class PunctuationValue : PatchValue
    {
        public Dictionary<string, object?> Value { get; }
        public PunctuationValue(Dictionary<string, object?> value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.Punctuation;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) =>
            other is PunctuationValue o && RimeValue.ValueEquals(Value, o.Value);
    }

    /// <summary>任意「列表的映射」结构（如 rime_ice.custom.yaml 的 switches 整段）。</summary>
    public sealed class MapListValue : PatchValue
    {
        public IReadOnlyList<Dictionary<string, object?>> Value { get; }
        public MapListValue(IReadOnlyList<Dictionary<string, object?>> value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.MapList;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) =>
            other is MapListValue o && RimeValue.ListOfMapsEquals(Value, o.Value);
    }

    /// <summary>单个映射结构（如 preset_color_schemes/&lt;id&gt; 配色定义）。</summary>
    public sealed class DictionaryValue : PatchValue
    {
        public Dictionary<string, object?> Value { get; }
        public DictionaryValue(Dictionary<string, object?> value) => Value = value;
        public override PatchValueKind Kind => PatchValueKind.Dictionary;
        public override object? ToYamlObject() => Value;
        public override bool ValueEquals(PatchValue other) =>
            other is DictionaryValue o && RimeValue.ValueEquals(Value, o.Value);
    }
}

/// <summary>一组待写入的补丁：值为 null 表示「移除该键」（对标 Swift 的 <c>PatchSet</c>）。</summary>
public sealed class PatchSet
{
    private readonly Dictionary<string, PatchValue?> _items = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, PatchValue?> Items => _items;
    public int Count => _items.Count;

    public void Set(string key, PatchValue? value) => _items[key] = value;

    /// <summary>标记删除该键。</summary>
    public void Remove(string key) => _items[key] = null;

    public IEnumerable<KeyValuePair<string, PatchValue?>> Enumerate()
    {
        // 按键排序，保证写入顺序确定（等价于 Swift 版字典遍历后再由 Yams sortKeys 归一）
        return _items.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 按**值**比较两份补丁集（脏值判断要用）。
    ///
    /// 同一个键在两边都是 null（删键）算相等；一边 null 一边有值算不等。
    /// 键的数量不同直接判不等 —— 少了某个键意味着那一项不会写盘，语义完全不同。
    /// </summary>
    public bool ValueEquals(PatchSet other)
    {
        if (Count != other.Count) return false;

        foreach (var (key, value) in _items)
        {
            if (!other._items.TryGetValue(key, out var otherValue)) return false;

            if (value is null || otherValue is null)
            {
                if (value is not null || otherValue is not null) return false;
                continue;
            }

            if (!value.ValueEquals(otherValue)) return false;
        }

        return true;
    }
}
