namespace NotchPC.Core.Widgets;

public enum FocusPhase
{
    Focus,
    Break,
}

public enum FocusState
{
    /// <summary>Not started: shows the full length of the current phase.</summary>
    Idle,
    Running,
    Paused,
}

/// <summary>
/// Pomodoro-style timer (focus, then break, then focus …). Pure logic: time is always passed in,
/// so the UI only needs a one-shot timer for the end of a phase and a 1 Hz refresh while it is visible.
/// </summary>
public sealed class FocusTimer
{
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinLength = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(120);

    private DateTimeOffset _endsAt;
    private TimeSpan _remainingWhenStopped;

    public FocusTimer(TimeSpan focusLength, TimeSpan breakLength)
    {
        FocusLength = Clamp(focusLength);
        BreakLength = Clamp(breakLength);
        _remainingWhenStopped = FocusLength;
    }

    public TimeSpan FocusLength { get; private set; }

    public TimeSpan BreakLength { get; private set; }

    public FocusPhase Phase { get; private set; } = FocusPhase.Focus;

    public FocusState State { get; private set; } = FocusState.Idle;

    /// <summary>When the running phase ends (only meaningful while running).</summary>
    public DateTimeOffset EndsAt => _endsAt;

    public TimeSpan PhaseLength => Phase == FocusPhase.Focus ? FocusLength : BreakLength;

    public TimeSpan Remaining(DateTimeOffset now)
    {
        if (State != FocusState.Running)
        {
            return _remainingWhenStopped;
        }

        var left = _endsAt - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>0 at the start of the phase, 1 at its end.</summary>
    public double Progress(DateTimeOffset now) =>
        PhaseLength <= TimeSpan.Zero ? 0 : Math.Clamp(1 - (Remaining(now) / PhaseLength), 0, 1);

    public void Start(DateTimeOffset now)
    {
        if (State == FocusState.Running)
        {
            return;
        }

        _endsAt = now + _remainingWhenStopped;
        State = FocusState.Running;
    }

    public void Pause(DateTimeOffset now)
    {
        if (State != FocusState.Running)
        {
            return;
        }

        _remainingWhenStopped = Remaining(now);
        State = FocusState.Paused;
    }

    public void Toggle(DateTimeOffset now)
    {
        if (State == FocusState.Running)
        {
            Pause(now);
        }
        else
        {
            Start(now);
        }
    }

    /// <summary>Back to an idle focus phase.</summary>
    public void Reset()
    {
        Phase = FocusPhase.Focus;
        State = FocusState.Idle;
        _remainingWhenStopped = FocusLength;
    }

    /// <summary>
    /// The −/+ buttons: change the length of the current phase by <paramref name="delta"/>.
    /// A running or paused timer keeps its elapsed time; remaining time never drops below one minute.
    /// </summary>
    public void Adjust(TimeSpan delta, DateTimeOffset now)
    {
        var oldLength = PhaseLength;
        var newLength = Clamp(oldLength + delta);
        if (Phase == FocusPhase.Focus)
        {
            FocusLength = newLength;
        }
        else
        {
            BreakLength = newLength;
        }

        var applied = newLength - oldLength;
        if (State == FocusState.Idle)
        {
            _remainingWhenStopped = newLength;
            return;
        }

        var remaining = Remaining(now) + applied;
        if (remaining < TimeSpan.FromMinutes(1))
        {
            remaining = TimeSpan.FromMinutes(1);
        }

        if (State == FocusState.Running)
        {
            _endsAt = now + remaining;
        }
        else
        {
            _remainingWhenStopped = remaining;
        }
    }

    /// <summary>
    /// Call when the end time has passed: the end of a focus starts the break; the end of the break stops the timer
    /// (ready for the next focus, started by the user, so it does not cycle forever).
    /// Returns the phase that just finished, or null if nothing finished.
    /// </summary>
    public FocusPhase? CompleteIfDue(DateTimeOffset now)
    {
        if (State != FocusState.Running || now < _endsAt)
        {
            return null;
        }

        var finished = Phase;
        if (finished == FocusPhase.Break)
        {
            Reset();
            return finished;
        }

        Phase = Phase == FocusPhase.Focus ? FocusPhase.Break : FocusPhase.Focus;
        _endsAt = _endsAt + PhaseLength;
        if (_endsAt <= now)
        {
            // The PC slept through more than one phase: restart the new phase from now.
            _endsAt = now + PhaseLength;
        }

        return finished;
    }

    /// <summary>"25:00", "4:59", "1:05:00".</summary>
    public static string Format(TimeSpan t)
    {
        var total = (int)Math.Ceiling(Math.Max(0, t.TotalSeconds));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    private static TimeSpan Clamp(TimeSpan t) => t < MinLength ? MinLength : t > MaxLength ? MaxLength : t;
}
