using TimeArr.Core;

namespace TimeArr.Tests.Core;

public class ResultTests
{
    [Fact]
    public void Ok_has_no_error()
    {
        var result = Result.Ok();

        Assert.True(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Fail_carries_its_error()
    {
        var result = Result.Fail(ErrorCode.InvalidInput, "nope");

        Assert.True(result.IsFailure);
        Assert.Equal(new Error(ErrorCode.InvalidInput, "nope"), result.Error);
    }

    [Fact]
    public void Value_of_a_failure_is_a_bug()
    {
        Result<int> result = new Error(ErrorCode.StorageBusy, "busy");

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ToResult_keeps_the_outcome()
    {
        Result<int> ok = 42;
        Result<int> fail = new Error(ErrorCode.Ipc, "down");

        Assert.True(ok.ToResult().IsSuccess);
        Assert.Equal(ErrorCode.Ipc, fail.ToResult().Error.Code);
    }
}
