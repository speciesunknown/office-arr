using TimeArr.Core;

namespace TimeArr.Tests.Core;

public class TimerNameTests
{
    [Theory]
    [InlineData("work", "work")]
    [InlineData("  padded  ", "padded")]
    [InlineData("Ünïcödé ⏱", "Ünïcödé ⏱")]
    [InlineData("e\u0301", "\u00e9")] // decomposed é becomes the composed form
    [InlineData("42 things", "42 things")]
    [InlineData("v2", "v2")]
    [InlineData("١٢", "١٢")]          // Arabic-Indic digits: not ASCII, so not an id
    [InlineData("\u2028trimmed\u2029", "trimmed")]
    public void Valid_names_are_trimmed_and_normalized(string input, string expected)
    {
        Assert.Equal(expected, TimerName.Validate(input).Value);
    }

    [Fact]
    public void Longest_allowed_name_is_accepted()
    {
        Assert.True(TimerName.Validate(new string('x', TimerName.MaxLength)).IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("new\nline")]
    [InlineData("nul\0")]
    [InlineData("-flag")]
    [InlineData("line\u2028break")]
    [InlineData("para\u2029break")]
    [InlineData("work\u200b")]      // zero-width space: looks like "work"
    [InlineData("\u202eevil")]      // right-to-left override
    [InlineData("soft\u00adhyphen")]
    [InlineData("private\ue000use")]
    [InlineData("42")]
    [InlineData("007")]
    public void Invalid_names_are_rejected(string? input)
    {
        var result = TimerName.Validate(input);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error.Code);
    }

    [Fact]
    public void Lone_surrogate_is_rejected()
    {
        // Not an InlineData case: xUnit's test-case serialization replaces lone surrogates.
        var result = TimerName.Validate("ab\ud800cd");

        Assert.Equal(ErrorCode.InvalidInput, result.Error.Code);
    }

    [Fact]
    public void Too_long_name_is_rejected()
    {
        Assert.True(TimerName.Validate(new string('x', TimerName.MaxLength + 1)).IsFailure);
    }
}
