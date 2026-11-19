namespace NotchPC.Core.Layout;

/// <summary>Moving one entry of a list next to another (drag and drop to reorder).</summary>
public static class ListOrder
{
    /// <summary>
    /// A copy of <paramref name="items"/> with <paramref name="item"/> moved before (or after) <paramref name="target"/>.
    /// Unknown entries leave the list as it was.
    /// </summary>
    public static List<T> Move<T>(IEnumerable<T> items, T item, T target, bool after, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var c = comparer ?? EqualityComparer<T>.Default;
        var list = items.ToList();
        var from = list.FindIndex(x => c.Equals(x, item));
        if (from < 0 || c.Equals(item, target) || !list.Any(x => c.Equals(x, target)))
        {
            return list;
        }

        var moved = list[from];
        list.RemoveAt(from);
        var to = list.FindIndex(x => c.Equals(x, target));
        list.Insert(after ? to + 1 : to, moved);
        return list;
    }

    /// <summary>Same, for lists of objects identified by a key (settings entries).</summary>
    public static List<T> MoveBy<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, TKey item, TKey target, bool after)
    {
        ArgumentNullException.ThrowIfNull(key);
        var list = items.ToList();
        var keys = Move(list.Select(key), item, target, after);
        return keys.Select(k => list.First(x => EqualityComparer<TKey>.Default.Equals(key(x), k))).ToList();
    }
}
