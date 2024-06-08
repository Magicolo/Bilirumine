#nullable enable

using System;
using System.Collections.Concurrent;

public static class Pool<T>
{
    static readonly ConcurrentDictionary<int, ConcurrentBag<T[]>> _free = new();
    static readonly ConcurrentDictionary<object, ConcurrentBag<T[]>> _reserve = new();

    public static T[] Take(int count)
    {
        if (count == 0) return Array.Empty<T>();
        var bag = _free.GetOrAdd(count, _ => new());
        var items = bag.TryTake(out var value) ? value : new T[count];
        _reserve.TryAdd(items, bag);
        return items;
    }

    public static bool Put(ref T[] items)
    {
        if (items.Length == 0) return false;
        else if (_reserve.TryRemove(items, out var bag))
        {
            items = Array.Empty<T>();
            bag.Add(items);
            return true;
        }
        else return false;
    }
}
