namespace Misscore;

/// <summary>Result of a single node tick.</summary>
public enum MissStatus {
    Success = 0,
    Failure = 1,
    Running = 2,
}

public static class MissStatusExtensions {
    /// <summary>Sentinel used on the debug wire for "this node was not ticked in that frame".</summary>
    public const byte NotTicked = 0xFF;

    public static bool IsFinished(this MissStatus status) => status != MissStatus.Running;
}
