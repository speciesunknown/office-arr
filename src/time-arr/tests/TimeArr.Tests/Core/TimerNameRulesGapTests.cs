using TimeArr.Core;

namespace TimeArr.Tests.Core;

/// <summary>Edge cases for the stricter name rules (forbidden categories, digit-only names) added after M1 review.</summary>
public class TimerNameRulesGapTests
{
    [Fact]
    public void Unassigned_code_point_is_rejected()
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("a͸b").Error.Code);
    }

    [Fact]
    public void Plain_emoji_outside_the_bmp_is_accepted()
    {
        // A valid surrogate pair must not trip the Surrogate category check.
        Assert.Equal("focus \U0001F3AF", TimerName.Validate("focus \U0001F3AF").Value);
    }

    [Fact]
    public void Emoji_with_variation_selector_is_accepted()
    {
        Assert.True(TimerName.Validate("❤️ project").IsSuccess);
    }

    [Fact]
    public void Zwj_emoji_sequence_is_rejected_as_documented()
    {
        // Family emoji: man ZWJ woman ZWJ girl. ZWJ is Format (Cf).
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("\U0001F468‍\U0001F469‍\U0001F467").Error.Code);
    }

    [Fact]
    public void Byte_order_mark_is_rejected()
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("﻿work").Error.Code);
    }

    [Theory]
    [InlineData("  42  ")]
    [InlineData(" 42　")]
    [InlineData("0")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000001")]
    public void Digit_only_names_are_rejected_after_trimming(string name)
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate(name).Error.Code);
    }

    [Theory]
    [InlineData("４２")] // fullwidth 42: not ASCII digits, and NFC leaves them alone
    [InlineData("4 2")]
    [InlineData("42.")]
    [InlineData("+42")]
    [InlineData("0x2a")]
    public void Names_that_merely_contain_digits_are_accepted(string name)
    {
        Assert.Equal(name, TimerName.Validate(name).Value);
    }

    [Fact]
    public void Digit_check_comes_after_the_dash_check()
    {
        // "-42" fails on the dash; either way it must be InvalidInput.
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("-42").Error.Code);
    }
}
