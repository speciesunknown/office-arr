namespace TimeArr.Commands;

/// <summary>Which timer a command is about. Names and ids are separate: a name is never read as an id.</summary>
public abstract record TimerRef
{
    private TimerRef() { }

    /// <summary>The timer with this id.</summary>
    public sealed record ById(long Id) : TimerRef
    {
        public override string ToString() => $"timer #{Id}";
    }

    /// <summary>The timer with this name (ignoring ASCII case).</summary>
    public sealed record ByName(string Name) : TimerRef
    {
        public override string ToString() => $"timer '{Name}'";
    }

    /// <summary>No timer given: the one named <see cref="DefaultName"/>, if there is one.</summary>
    public sealed record Default : TimerRef
    {
        public override string ToString() => "the default timer";
    }

    /// <summary>The timer <see cref="Default"/> stands for: the one a new database starts with.</summary>
    public const string DefaultName = TimeArr.Core.TimerName.Default;
}
