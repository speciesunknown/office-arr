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
    [InlineData("42", "42")]
    [InlineData(" 007 ", "007")]
    [InlineData("١٢", "١٢")]          // Arabic-Indic digits
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
    public void Invalid_names_are_rejected(string? input)
    {
        var result = TimerName.Validate(input);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCode.InvalidInput, result.Error.Code);
    }

    private static string U(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));

    // Redteam M1 round 2 #2: invisible characters outside the forbidden categories.
    // Code points rather than literals, so editors and tools can't silently change them.
    [Theory]
    [InlineData(0x3164)]   // HANGUL FILLER, alone: displays as empty
    [InlineData(0xFFA0)]   // HALFWIDTH HANGUL FILLER
    [InlineData(0x115F)]   // HANGUL CHOSEONG FILLER
    [InlineData(0x2800)]   // BRAILLE PATTERN BLANK
    [InlineData(0x034F)]   // COMBINING GRAPHEME JOINER
    [InlineData(0xFE00)]   // VARIATION SELECTOR-1, alone
    [InlineData(0x0301)]   // lone combining acute
    public void Invisible_names_are_rejected(int codePoint)
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate(U(codePoint)).Error.Code);
    }

    [Theory]
    [InlineData(0x034F)]   // CGJ
    [InlineData(0xFE0F)]   // VS16 after a letter
    [InlineData(0x180B)]   // MONGOLIAN FVS1
    [InlineData(0xE0100)]  // VARIATION SELECTOR-17
    public void Look_alikes_of_default_are_rejected(int invisible)
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("default" + U(invisible)).Error.Code);
    }

    [Theory]
    [InlineData(0x3164)]
    [InlineData(0x034F)]
    public void Invisible_prefix_cannot_smuggle_a_leading_dash(int invisible)
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate(U(invisible) + "-rm").Error.Code);
    }

    [Fact]
    public void Zalgo_text_is_rejected_but_a_few_marks_are_fine()
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("default" + string.Concat(Enumerable.Repeat(U(0x0301), 20))).Error.Code);
        // Four marks in a row are allowed, and real text with marks (Vietnamese) is fine.
        Assert.True(TimerName.Validate("t" + U(0x0323, 0x0302, 0x0301, 0x0300)).IsSuccess);
        Assert.True(TimerName.Validate("Tiếng Việt").IsSuccess);
    }

    [Fact]
    public void Emoji_with_variation_selector_is_fine()
    {
        Assert.True(TimerName.Validate("love " + U(0x2764, 0xFE0F)).IsSuccess);
    }

    [Theory]
    [InlineData('1')]
    [InlineData('#')]
    [InlineData('*')]
    public void Keycap_emoji_are_fine(char key)
    {
        // Redteam M1 round 3 #3.
        Assert.True(TimerName.Validate("step " + key + U(0xFE0F, 0x20E3)).IsSuccess);
    }

    [Fact]
    public void Variation_selector_after_a_digit_without_keycap_is_rejected()
    {
        Assert.Equal(ErrorCode.InvalidInput, TimerName.Validate("1" + U(0xFE0F)).Error.Code);
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
