using NotchPC.Core.Notifications;

namespace NotchPC.App.Notifications;

/// <summary>
/// The running Live Activities (timer, meeting, recording). Sources set and clear theirs; the notch shows the top
/// one in the closed pill. UI thread only.
/// </summary>
internal sealed class LiveActivityHub
{
    private readonly Dictionary<string, LiveActivity> _items = new(StringComparer.Ordinal);

    public static LiveActivityHub Current { get; } = new();

    public event EventHandler? Changed;

    public LiveActivity? Top => LiveActivities.Top(_items.Values);

    public IReadOnlyCollection<LiveActivity> All => _items.Values;

    public void Set(LiveActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (_items.TryGetValue(activity.Id, out var old) && old == activity)
        {
            return;
        }

        _items[activity.Id] = activity;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(string id)
    {
        if (_items.Remove(id))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
