namespace DrawArr.Commands;

/// <summary>Where a command came from, shown in the history and sent over IPC by number.</summary>
public enum CommandSource
{
    /// <summary>The drawing's window: the mouse, the keyboard or a menu.</summary>
    Window = 1,
    /// <summary>The draw-arr command line.</summary>
    Cli = 2,
    /// <summary>Another agent, through the broker.</summary>
    Agent = 3,
}
