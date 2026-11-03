namespace DbdPresence.Core.Presence;

/// <summary>Activity types Discord accepts through <c>SET_ACTIVITY</c> (FEASIBILITY D4).</summary>
public enum ActivityKind
{
    /// <summary>"Playing …" (default).</summary>
    Playing = 0,

    /// <summary>"Listening to …".</summary>
    Listening = 2,

    /// <summary>"Watching …".</summary>
    Watching = 3,

    /// <summary>"Competing in …".</summary>
    Competing = 5,
}

/// <summary>Which field Discord shows in the member list (<c>status_display_type</c>; FR-RPC-11).</summary>
public enum StatusDisplay
{
    /// <summary>Show the application name.</summary>
    Name = 0,

    /// <summary>Show the state line.</summary>
    State = 1,

    /// <summary>Show the details line.</summary>
    Details = 2,
}
