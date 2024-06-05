#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = System.Random;

[Flags]
public enum Axes
{
    None = 0,
    X = 1 << 0,
    Y = 1 << 1,
    Z = 1 << 2,
    W = 1 << 3,
    XY = X | Y,
    XZ = X | Z,
    XW = X | W,
    YZ = Y | Z,
    YW = Y | W,
    ZW = Z | W,
    XYZ = X | Y | Z,
    XYW = X | Y | W,
    XZW = X | Z | W,
    YZW = Y | Z | W,
    XYZW = X | Y | Z | W,
}

[Flags]
public enum Channels
{
    None = 0,
    R = 1 << 0,
    G = 1 << 1,
    B = 1 << 2,
    A = 1 << 3,
    RG = R | G,
    RB = R | B,
    RA = R | A,
    GB = G | B,
    GA = G | A,
    BA = B | A,
    RGB = R | G | B,
    RGA = R | G | A,
    RBA = R | B | A,
    GBA = G | B | A,
    RGBA = R | G | B | A
}

public enum Spaces
{
    Local,
    World
}

public static class Enum<T> where T : struct, Enum
{
    public static readonly string[] Names = Enum.GetNames(typeof(T));
    public static readonly T[] Values = (T[])Enum.GetValues(typeof(T));
    public static readonly Type Type = Enum.GetUnderlyingType(typeof(T));
}

public static class AxesExtensions
{
    public static bool HasAll(this Axes axes, Axes others) => (axes & others) == others;
    public static bool HasAny(this Axes axes, Axes others) => (axes & others) != 0;
    public static bool HasNone(this Axes axes, Axes others) => !axes.HasAny(others);
}

public static class ChannelsExtensions
{
    public static bool HasAll(this Channels channels, Channels others) => (channels & others) == others;
    public static bool HasAny(this Channels channels, Channels others) => (channels & others) != 0;
    public static bool HasNone(this Channels channels, Channels others) => !channels.HasAny(others);
}

public static class Extensions
{
    static readonly Random _random = new();
    static readonly Vector3[] _corners = new Vector3[4];

    public static bool Not(this bool value) => !value;

    public static ref T Swap<T>(ref this T source, ref T target) where T : struct
    {
        (target, source) = (source, target);
        return ref target;
    }

    public static T Set<T>(ref this T source, T target) where T : struct
    {
        (target, source) = (source, target);
        return target;
    }

    public static bool Change(ref this bool source, bool target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this byte source, byte target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this sbyte source, sbyte target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this short source, short target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this ushort source, ushort target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this int source, int target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this uint source, uint target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this ulong source, ulong target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this long source, long target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this float source, float target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this double source, double target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change<T>(ref this T source, in T target) where T : struct
    {
        var changed = !EqualityComparer<T>.Default.Equals(source, target);
        source = target;
        return changed;
    }

    public static bool Change(ref this bool? source, bool? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this byte? source, byte? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this sbyte? source, sbyte? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this short? source, short? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this ushort? source, ushort? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this int? source, int? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this uint? source, uint? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this ulong? source, ulong? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this long? source, long? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this float? source, float? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change(ref this double? source, double? target)
    {
        var changed = source != target;
        source = target;
        return changed;
    }

    public static bool Change<T>(ref this T? source, in T? target) where T : struct
    {
        var changed = !EqualityComparer<T?>.Default.Equals(source, target);
        source = target;
        return changed;
    }

    public static T Take<T>(ref this T source) where T : struct
    {
        var value = source;
        source = default;
        return value;
    }

    public static T? Take<T>(ref this T? source) where T : struct
    {
        var value = source;
        source = default;
        return value;
    }

    public static int Wrap(this int value, int bound) => (value < 0 ? (bound + value % bound) : value) % bound;
    public static long Wrap(this long value, long bound) => (value < 0 ? (bound + value % bound) : value) % bound;
    public static float Wrap(this float value, float bound) => (value < 0 ? (bound + value % bound) : value) % bound;
    public static double Wrap(this double value, double bound) => (value < 0 ? (bound + value % bound) : value) % bound;
    public static Vector2 Wrap(this Vector2 value, Vector2 bound) => new(value.x.Wrap(bound.x), value.y.Wrap(bound.y));
    public static Vector3 Wrap(this Vector3 value, Vector3 bound) => new(value.x.Wrap(bound.x), value.y.Wrap(bound.y), value.z.Wrap(bound.z));
    public static Vector4 Wrap(this Vector4 value, Vector4 bound) => new(value.x.Wrap(bound.x), value.y.Wrap(bound.y), value.z.Wrap(bound.z), value.w.Wrap(bound.w));
    public static Color Wrap(this Color value, Color bound) => new(value.r.Wrap(bound.r), value.g.Wrap(bound.g), value.b.Wrap(bound.b), value.a.Wrap(bound.a));

    public static double AsDouble(this float value) => value;
    public static double AsDouble(this short value) => value;
    public static double AsDouble(this ushort value) => value;
    public static double AsDouble(this int value) => value;
    public static double AsDouble(this uint value) => value;
    public static double AsDouble(this long value) => value;
    public static double AsDouble(this ulong value) => value;

    public static double Polarize(this double value, double amount = 1.0)
    {
        value = Math.Clamp(value, 0.0, 1.0) * 2.0 - 1.0;
        amount = Math.Clamp(amount, 0.0, 1.0) * 2.0;
        return value / (2.0 - amount + amount * Math.Abs(value)) + 0.5;
    }

    public static float Polarize(this float value, float amount = 1f) => (float)value.AsDouble().Polarize(amount);
    public static Color Polarize(this Color value, float amount = 1f) => new(value.r.Polarize(amount), value.g.Polarize(amount), value.b.Polarize(amount), value.a.Polarize(amount));

    public static Vector2 Clamp(in this Vector2 source, float minimum = 0f, float maximum = 1f) =>
        new(Mathf.Clamp(source.x, minimum, maximum), Mathf.Clamp(source.y, minimum, maximum));
    public static Color Clamp(in this Color source, float minimum = 0f, float maximum = 1f) =>
        new(Mathf.Clamp(source.r, minimum, maximum), Mathf.Clamp(source.g, minimum, maximum), Mathf.Clamp(source.b, minimum, maximum), Mathf.Clamp(source.a, minimum, maximum));
    public static Color Finite(in this Color source, Color fix = default) => new(
        float.IsFinite(source.r) ? source.r : fix.r,
        float.IsFinite(source.g) ? source.g : fix.g,
        float.IsFinite(source.b) ? source.b : fix.b,
        float.IsFinite(source.a) ? source.a : fix.a);

    public static Color With(in this Color color, float? r = null, float? g = null, float? b = null, float? a = null) =>
        new(r ?? color.r, g ?? color.g, b ?? color.b, a ?? color.a);
    public static Vector2 With(in this Vector2 vector, float? x = null, float? y = null) =>
        new(x ?? vector.x, y ?? vector.y);
    public static Vector3 With(in this Vector3 vector, Vector2 xy) =>
        new(xy.x, xy.y, vector.z);
    public static Vector3 With(in this Vector3 vector, float? x = null, float? y = null, float? z = null) =>
        new(x ?? vector.x, y ?? vector.y, z ?? vector.z);
    public static Vector4 With(in this Vector4 vector, float? x = null, float? y = null, float? z = null, float? w = null) =>
        new(x ?? vector.x, y ?? vector.y, z ?? vector.z, w ?? vector.w);
    public static Vector3 With(this Vector3 vector, Vector3 other, Axes axes)
    {
        if (axes.HasAll(Axes.X)) vector.x = other.x;
        if (axes.HasAll(Axes.Y)) vector.y = other.y;
        if (axes.HasAll(Axes.Z)) vector.z = other.z;
        return vector;
    }

    public static void Enable(this ParticleSystem system)
    {
        var emission = system.emission;
        emission.enabled = true;
    }

    public static void Disable(this ParticleSystem system)
    {
        var emission = system.emission;
        emission.enabled = false;
    }

    public static void Shift(this AudioSource source, TimeSpan delta)
    {
        if (source) source.time += (float)delta.TotalSeconds;
    }

    public static GameObject GameObject(this UnityEngine.Object instance) =>
        instance as GameObject ?? (instance as Component)!.gameObject;

    public static T Component<T>(this GameObject instance) where T : UnityEngine.Object =>
        instance as T ?? instance.GetComponent<T>();

    public static bool TryComponent<T>(this GameObject instance, out T component) where T : UnityEngine.Object =>
        component = instance.Component<T>();

    public static Color ShiftHue(this Color color, float shift) => color.MapHSV(hsv => ((hsv.h + shift) % 1f, hsv.s, hsv.v));
    public static Color ShiftSaturation(this Color color, float shift) => color.MapHSV(hsv => (hsv.h, hsv.s + shift, hsv.v));
    public static Color ShiftValue(this Color color, float shift) => color.MapHSV(hsv => (hsv.h, hsv.s, hsv.v + shift));

    public static Color MapHSV(this Color color, Func<(float h, float s, float v), (float h, float s, float v)> map)
    {
        Color.RGBToHSV(color, out var hue, out var saturation, out var value);
        (hue, saturation, value) = map((hue, saturation, value));
        return Color.HSVToRGB(hue, saturation, value);
    }

    public static T MinBy<T, TValue>(this IEnumerable<T> source, Func<T, TValue> by) where TValue : IComparable<TValue>
    {
        var first = true;
        var best = (item: default(T), value: default(TValue));
        foreach (var item in source)
        {
            var value = by(item);
            if (first.Change(false) || value.CompareTo(best.value!) < 0) best = (item, value);
        }
        return best.item!;
    }

    public static bool TryRandom<T>(this IReadOnlyList<T> list, out T value, Random? random = default)
    {
        if (list.Count == 0) { value = default!; return false; }
        else { value = list.Random(random); return true; }
    }

    public static T Random<T>(this IReadOnlyList<T> list, Random? random = default)
    {
        random ??= _random;
        return list[random.Next(0, list.Count)];
    }

    public static IEnumerable<T> Random<T>(this IReadOnlyList<T> list, int count, Random? random = default)
    {
        for (int i = 0; i < count; i++)
        {
            if (list.TryRandom(out var item, random)) yield return item;
            else yield break;
        }
    }

    public static void PlayWith(this AudioSource source, (float minimum, float maximum)? volume = null, (float minimum, float maximum)? pitch = null, Random? random = default)
    {
        random ??= _random;
        source.volume = volume switch { (float minimum, float maximum) => random.NextFloat(minimum, maximum), _ => source.volume };
        source.pitch = pitch switch { (float minimum, float maximum) => random.NextFloat(minimum, maximum), _ => source.pitch };
        source.Play();
    }

    public static HashSet<T> ToSet<T>(this IEnumerable<T> source) => new(source);

    public static TimeSpan Min(this TimeSpan time, TimeSpan value) => TimeSpan.FromTicks(Math.Min(time.Ticks, value.Ticks));
    public static TimeSpan Max(this TimeSpan time, TimeSpan value) => TimeSpan.FromTicks(Math.Max(time.Ticks, value.Ticks));
    public static TimeSpan Modulo(this TimeSpan time, TimeSpan value) => TimeSpan.FromTicks(time.Ticks % value.Ticks);
    public static TimeSpan Multiply(this TimeSpan time, double value) => TimeSpan.FromTicks((long)(time.Ticks * value));
    public static TimeSpan Divide(this TimeSpan time, double value) => TimeSpan.FromTicks((long)(time.Ticks / value));
    public static TimeSpan Negate(this TimeSpan time) => TimeSpan.FromTicks(-time.Ticks);

    public static float MapFloat(this Material material, string name, Func<float, float> map)
    {
        var source = material.GetFloat(name);
        var target = map(source);
        material.SetFloat(name, target);
        return target;
    }

    public static Color MapColor(this Material material, string name, Func<Color, Color> map)
    {
        var source = material.GetColor(name);
        var target = map(source);
        material.SetColor(name, target);
        return target;
    }

    public static string Clean(this string value) => value.Trim(' ', '~', '_', '\n', '\t', '\r', '\f', '\0').Split('_')[0];

    public static double NextDouble(this Random random, double minimum, double maximum) =>
        random.NextDouble() * (maximum - minimum) + minimum;
    public static float NextFloat(this Random random, float minimum, float maximum) =>
        (float)random.NextDouble(minimum, maximum);
    public static float NextFloat(this Random random) => (float)random.NextDouble();

    public static Vector2 Clamp(this Rect rectangle, Vector2 vector) => new(
        Mathf.Clamp(vector.x, rectangle.xMin, rectangle.xMax),
        Mathf.Clamp(vector.y, rectangle.yMin, rectangle.yMax));

    public static Vector3 Clamp(this Rect rectangle, Vector3 vector) => new(
        Mathf.Clamp(vector.x, rectangle.xMin, rectangle.xMax),
        Mathf.Clamp(vector.y, rectangle.yMin, rectangle.yMax),
        vector.z);

    public static Rect WorldRectangle(this RectTransform transform)
    {
        transform.GetWorldCorners(_corners);
        var (bottomLeft, topRight) = ((Vector2)_corners[0], (Vector2)_corners[2]);
        return Rect.MinMaxRect(bottomLeft.x, bottomLeft.y, topRight.x, topRight.y);
    }

    public static Rect LocalRectangle(this RectTransform transform)
    {
        transform.GetLocalCorners(_corners);
        var (bottomLeft, topRight) = ((Vector2)_corners[0], (Vector2)_corners[2]);
        return Rect.MinMaxRect(bottomLeft.x, bottomLeft.y, topRight.x, topRight.y);
    }

    public static List<T> Children<T>(this Component component, List<T> buffer, bool inactive = false)
    {
        component.GetComponentsInChildren(inactive, buffer);
        return buffer;
    }

    public static List<T> Children<T>(this GameObject component, List<T> buffer, bool inactive = false)
    {
        component.GetComponentsInChildren(inactive, buffer);
        return buffer;
    }

    public static IEnumerable<TTarget> TrySelect<TSource, TTarget>(this IEnumerable<TSource> source, Func<TSource, TTarget> select) =>
        source.Select(select).TryWhere();

    public static IEnumerable<T> TryWhere<T>(this IEnumerable<T> source)
    {
        var enumerator = source.GetEnumerator();
        while (true)
        {
            var success = false;
            try { success = enumerator.MoveNext(); }
            catch { }
            if (success) yield return enumerator.Current;
            else yield break;
        }
    }

    public static IEnumerable<(T item, int index)> Enumerate<T>(this IEnumerable<T> source) =>
        source.Select((item, index) => (item, index));

    public static (IEnumerable<T1>, IEnumerable<T2>) Unzip<T1, T2>(this IEnumerable<(T1, T2)> source)
    {
        var items = source.ToArray();
        return (items.Select(item => item.Item1), items.Select(item => item.Item2));
    }

    public static (IEnumerable<T1>, IEnumerable<T2>, IEnumerable<T3>) Unzip<T1, T2, T3>(this IEnumerable<(T1, T2, T3)> source)
    {
        var items = source.ToArray();
        return (items.Select(item => item.Item1), items.Select(item => item.Item2), items.Select(item => item.Item3));
    }

    public static (T1[], T2[]) ToArray<T1, T2>(this (IEnumerable<T1>, IEnumerable<T2>) source) =>
        (source.Item1.ToArray(), source.Item2.ToArray());
    public static (T1[], T2[], T3[]) ToArray<T1, T2, T3>(this (IEnumerable<T1>, IEnumerable<T2>, IEnumerable<T3>) source) =>
        (source.Item1.ToArray(), source.Item2.ToArray(), source.Item3.ToArray());

    public static bool TryAt<T>(this IReadOnlyList<T> list, int index, out T item)
    {
        if (index >= 0 && index < list.Count)
        {
            item = list[index];
            return true;
        }
        else
        {
            item = default!;
            return false;
        }
    }

    public static bool TryFirst<T>(this IReadOnlyList<T> list, out T item) => list.TryAt(0, out item);
    public static bool TryLast<T>(this IReadOnlyList<T> list, out T item) => list.TryAt(list.Count - 1, out item);

    public static bool TryPop<T>(this IList<T> list, out T item)
    {
        if (list.Count > 0)
        {
            item = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
            return true;
        }
        else
        {
            item = default!;
            return false;
        }
    }

    public static bool TryFind<T>(this IReadOnlyList<T> list, Func<T, bool> predicate, out T item)
    {
        for (int i = 0; i < list.Count; i++)
        {
            item = list[i];
            if (predicate(item)) return true;
        }
        item = default!;
        return false;
    }
}