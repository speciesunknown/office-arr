using Microsoft.Data.Sqlite;
using TimeArr.Core;
using TimeArr.Data;

namespace TimeArr.Tests.Data;

/// <summary>Edge cases for <see cref="SqliteTimerStorage"/> not covered by <see cref="SqliteTimerStorageTests"/>.</summary>
public sealed class SqliteTimerStorageGapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "time-arr-tests", Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_root, "time-arr.db");

    public SqliteTimerStorageGapTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Default_storage_points_at_the_default_path_without_opening_it()
    {
        Assert.Equal(SqliteTimerRepository.DefaultPath().Value, SqliteTimerStorage.Default().Value.Location);
    }

    [Fact]
    public void Location_is_the_path_given()
    {
        Assert.Equal(DbPath, new SqliteTimerStorage(DbPath).Location);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Bad_busy_timeout_is_reported_on_open(int seconds)
    {
        Assert.Equal(ErrorCode.InvalidInput, new SqliteTimerStorage(DbPath, seconds).Open().Error.Code);
    }

    [Fact]
    public void Each_open_gives_a_repository_on_the_same_data()
    {
        var storage = new SqliteTimerStorage(DbPath);
        var created = storage.Open().Value.Create("shared").Value;

        Assert.Equal(created, storage.Open().Value.FindByName("shared").Value);
    }

    [Fact]
    public void Foreign_database_is_reported_as_foreign_and_left_alone()
    {
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE notes (x TEXT);";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(DbPath);

        Assert.Equal(ErrorCode.StorageForeign, new SqliteTimerStorage(DbPath).Open().Error.Code);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
    }
}
