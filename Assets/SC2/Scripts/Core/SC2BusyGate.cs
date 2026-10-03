/// <summary>
/// The life cycle of a "busy" plate that is shown before a long main-thread job: the plate has to be on screen BEFORE the job
/// blocks the frame loop, or the editor looks frozen with nothing explaining why. The job is therefore not started when it is
/// requested but only once the plate has been drawn by enough repaints to have been presented, or after a short time limit when
/// nothing repaints (a hidden Game view). Pure, so the rules can be tested.
/// </summary>
public sealed class SC2BusyGate
{
    public enum Phase { Idle, Showing, Running }

    /// <summary>Repaints of the plate that must have happened before the job starts: one to draw it and one more so the first
    /// has certainly been presented.</summary>
    public const int RepaintsBeforeStart = 2;

    /// <summary>Longest the plate waits for those repaints before the job starts anyway.</summary>
    public const double MaxShowSeconds = 1.0;

    public Phase State { get; private set; } = Phase.Idle;
    public string Title { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public bool IsActive => State != Phase.Idle;

    private int _repaints;
    private double _requestedAt;
    private double _startedAt;

    /// <summary>Starts showing the plate; false while another job already owns it.</summary>
    public bool Begin(string title, string detail, double now)
    {
        if (State != Phase.Idle) return false;
        State = Phase.Showing;
        Title = title ?? "";
        Detail = detail ?? "";
        _repaints = 0;
        _requestedAt = now;
        return true;
    }

    /// <summary>The plate was drawn by a repaint.</summary>
    public void Presented()
    {
        if (State == Phase.Showing) _repaints++;
    }

    public bool ReadyToStart(double now) =>
        State == Phase.Showing && (_repaints >= RepaintsBeforeStart || now - _requestedAt >= MaxShowSeconds);

    public void MarkRunning(double now)
    {
        if (State != Phase.Showing) return;
        State = Phase.Running;
        _startedAt = now;
    }

    /// <summary>How long the job has been running, 0 before it starts.</summary>
    public double RunningSeconds(double now) => State == Phase.Running ? now - _startedAt : 0.0;

    public void End()
    {
        State = Phase.Idle;
        Title = "";
        Detail = "";
        _repaints = 0;
    }
}
