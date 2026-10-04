namespace Misscore;

/// <summary>How a list of nodes comes to a result.</summary>
public enum ListMode {
    /// <summary>Until one fails; succeeds when all succeed.</summary>
    Sequence,

    /// <summary>Until one succeeds; fails when all fail.</summary>
    Selector,
}