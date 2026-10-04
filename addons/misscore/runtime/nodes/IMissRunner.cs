namespace Misscore;

/// <summary>
/// What every Missfits runner has in common — the scene node that drives a behavior tree, a state
/// machine and so on. It is what a node reaches through <see cref="MissContext.Runner"/>, so an
/// action can stop whatever is running it without knowing which addon that is.
/// </summary>
public interface IMissRunner {
    /// <summary>
    /// Whether the runner is running. Switching it off stops it and interrupts whatever was running;
    /// switching it back on resumes.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Stops the runner; same as setting <see cref="Enabled"/> to false. Safe to call from inside a
    /// tick: the current tick finishes first, and the interrupt follows right after it.
    /// </summary>
    void Stop();
}