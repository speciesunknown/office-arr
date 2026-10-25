using TimeArr.Core;

namespace TimeArr.Tests.Core;

/// <summary>Checks CODING.md standard 3 for <see cref="ErrorCode"/>: values start at 1 and 0 or an unknown value is refused.</summary>
public class ErrorCodeGapTests
{
    [Fact]
    public void Every_error_code_is_one_or_more()
    {
        Assert.All(Enum.GetValues<ErrorCode>(), code => Assert.True((int)code >= 1, $"{code} is {(int)code}."));
    }

    [Fact]
    public void Error_codes_are_numbered_without_gaps_from_one()
    {
        var values = Enum.GetValues<ErrorCode>().Select(c => (int)c).Order().ToArray();

        Assert.Equal(Enumerable.Range(1, values.Length), values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void Error_refuses_zero_and_unknown_codes(int raw)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Error((ErrorCode)raw, "x"));
    }

    [Fact]
    public void Default_error_code_is_refused_everywhere_an_error_is_made()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Result.Fail(default(ErrorCode), "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Result<int>.Fail(default(ErrorCode), "x"));
    }

    [Fact]
    public void Every_defined_code_is_accepted()
    {
        Assert.All(Enum.GetValues<ErrorCode>(), code => Assert.Equal(code, new Error(code, "x").Code));
    }
}
