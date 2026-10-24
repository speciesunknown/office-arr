using TimeArr.Core;

namespace TimeArr.Tests.Core;

/// <summary>Edge cases for <see cref="TimerName"/> not covered by <see cref="TimerNameTests"/>.</summary>
public class TimerNameGapTests
{
    [Fact]
    public void Unicode_whitespace_is_trimmed()
    {
        // NBSP and ideographic space are whitespace to string.Trim.
        Assert.Equal("work", TimerName.Validate(" work　").Value);
    }

    [Fact]
    public void Leading_dash_after_trimming_is_rejected()
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("   -x").Error.Code);
    }

    [Theory]
    [InlineData("a-b")]
    [InlineData("x-")]
    [InlineData("−x")] // MINUS SIGN is not '-'
    public void Dash_elsewhere_is_fine(string name)
    {
        Assert.True(TimerName.Validate(name).IsSuccess);
    }

    [Theory]
    [InlineData("a\u007fb")] // DEL
    [InlineData("a\u0085b")] // NEL (C1)
    [InlineData("a\u009bb")] // CSI (C1)
    [InlineData("a\u001bb")] // ESC
    public void Other_control_characters_are_rejected(string name)
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate(name).Error.Code);
    }

    [Fact]
    public void Length_limit_applies_after_trimming()
    {
        var name = "  " + new string('x', TimerName.MaxLength) + "  ";

        Assert.Equal(TimerName.MaxLength, TimerName.Validate(name).Value.Length);
    }

    [Fact]
    public void Length_limit_applies_after_normalization()
    {
        // 64 decomposed "é" are 128 chars in, 64 composed chars out.
        var name = string.Concat(Enumerable.Repeat("é", TimerName.MaxLength));

        Assert.Equal(TimerName.MaxLength, TimerName.Validate(name).Value.Length);
    }

    [Fact]
    public void Length_counts_utf16_units_so_32_emoji_fit_but_33_do_not()
    {
        // Pins current behaviour: the limit is in UTF-16 code units, not user-visible characters.
        var fits = string.Concat(Enumerable.Repeat("\U0001F600", 32));
        var tooLong = string.Concat(Enumerable.Repeat("\U0001F600", 33));

        Assert.True(TimerName.Validate(fits).IsSuccess);
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate(tooLong).Error.Code);
    }

    [Fact]
    public void Lone_low_surrogate_is_rejected()
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("ab\udc00").Error.Code);
    }

    [Fact]
    public void Validated_name_is_stable_when_validated_again()
    {
        var once = TimerName.Validate("  Café  ").Value;

        Assert.Equal(once, TimerName.Validate(once).Value);
    }
}
