using TimeArr.Core;

namespace TimeArr.Tests.Core;

/// <summary>Edge cases for <see cref="Result"/> and <see cref="Result{T}"/> not covered by <see cref="ResultTests"/>.</summary>
public class ResultGapTests
{
    [Fact]
    public void Fail_with_null_error_is_a_bug()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Fail(null!));
        Assert.Throws<ArgumentNullException>(() => Result<int>.Fail(null!));
    }

    [Fact]
    public void Error_of_a_successful_value_result_is_a_bug()
    {
        Result<string> ok = "x";

        Assert.Throws<InvalidOperationException>(() => ok.Error);
    }

    [Fact]
    public void Implicit_error_conversion_makes_a_failure()
    {
        Result result = new Error(ErrorCode.Conflict, "taken");

        Assert.Equal(ErrorCode.Conflict, result.Error.Code);
    }

    [Fact]
    public void Ok_can_carry_null_for_reference_types()
    {
        var result = Result<string?>.Ok(null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ToString_names_the_outcome()
    {
        Assert.Equal("Ok", Result.Ok().ToString());
        Assert.Equal("Ok(5)", Result<int>.Ok(5).ToString());
        Assert.Equal("Fail(NotFound: gone)", Result.Fail(ErrorCode.NotFound, "gone").ToString());
        Assert.Equal("Fail(NotFound: gone)", Result<int>.Fail(ErrorCode.NotFound, "gone").ToString());
    }

    [Fact]
    public void Value_failure_message_includes_the_error()
    {
        var result = Result<int>.Fail(ErrorCode.StorageBusy, "locked");

        var e = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("locked", e.Message, StringComparison.Ordinal);
    }
}
