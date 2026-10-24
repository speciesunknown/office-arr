using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using TimeArr.App.Ipc;
using TimeArr.Commands;
using TimeArr.Core;
using TimeArr.Data;
using TimeArr.Tests.Commands;
using TimeArr.ViewModels;

namespace TimeArr.Tests;

/// <summary>Checks CODING.md #4 for bad user input arriving through the CLI, IPC frames and UI fields.</summary>
public sealed class UserInputGapTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    // Short, because the socket path must stay under ~107 bytes.
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ta-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly FakeClock _clock = new(T0);

    public UserInputGapTests() => Assert.True(InstanceChannel.PrepareFolder(_folder).IsSuccess);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private TimerCommandHandler Handler() => new(new SqliteTimerStorage(Path.Combine(_folder, "time-arr.db")), _clock);

    /// <summary>Runs a command line the way the IPC server does, failing the test if anything throws.</summary>
    private static IpcReply Cli(ITimerCommands commands, string[] args)
    {
        IpcReply? reply = null;
        var thrown = Record.Exception(() => reply = IpcServer.Handle(args, commands, () => { }));
        Assert.True(thrown is null, $"time-arr {Show(args)} threw {thrown}");
        return reply!;
    }

    private static string Show(string[] args) => string.Join(' ', args.Select(a => $"[{a}]"));

    private static void AssertIsAMessage(IpcReply reply, string[] args)
    {
        Assert.True(reply.ExitCode is 1 or 2, $"time-arr {Show(args)} gave exit code {reply.ExitCode}: {reply.Output}");
        Assert.False(string.IsNullOrWhiteSpace(reply.Output), $"time-arr {Show(args)} failed without a message.");
        Assert.DoesNotContain("Exception", reply.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", reply.Output, StringComparison.Ordinal);
    }

    // --- CLI ---

    private static readonly string[][] BadLines =
    [
        [""],
        [" "],
        ["\0"],
        ["frobnicate", "--id", "1"],
        ["add"],
        ["add", ""],
        ["add", "   "],
        ["add", "a\0b"],
        ["add", "line\nbreak"],
        ["add", "-flag"],
        ["add", "‮evil"],
        ["add", "work​"],
        ["add", new string('x', TimerName.MaxLength + 1)],
        ["add", new string('x', 10_000)],
        ["add", string.Concat(Enumerable.Repeat("\U0001F600", 33))],
        ["add", "default"],
        ["add", "DEFAULT"],
        ["start", ""],
        ["start", "nonexistent"],
        ["start", "--id"],
        ["start", "--id", ""],
        ["start", "--id", "0"],
        ["start", "--id", "-5"],
        ["start", "--id", "1e5"],
        ["start", "--id", "9223372036854775808"],
        ["start", "--id", "999999"],
        ["start", "--bogus"],
        ["status", "a", "b"],
        ["rename", "default"],
        ["rename", "default", ""],
        ["rename", "default", "-x"],
        ["rename", "nonexistent", "x"],
        ["rename", "--id", "1", "a\tb"],
        ["delete"],
        ["delete", "--id", "abc"],
        ["list", "extra"],
        ["help", "extra"],
        ["add", "a", "b", "c", "d", "e", "f", "g", "h", "i"],
    ];

    public static TheoryData<string[]> BadCommandLines => new(BadLines);

    [Theory]
    [MemberData(nameof(BadCommandLines))]
    public void Bad_command_lines_give_a_message_and_never_throw(string[] args)
    {
        AssertIsAMessage(Cli(Handler(), args), args);
    }

    [Fact]
    public void Lone_surrogates_on_the_command_line_give_a_message()
    {
        // Built here rather than in TheoryData, because xUnit's serialization would replace lone surrogates.
        var handler = Handler();
        string[][] cases = [["add", "ab\uD800"], ["add", "\uDC00x"], ["start", "\uD800"], ["rename", "default", "x\uD800"]];

        foreach (var args in cases)
            AssertIsAMessage(Cli(handler, args), args);
    }

    [Fact]
    public void Random_command_lines_never_throw()
    {
        var handler = Handler();
        var random = new Random(4242);
        string[] tokens =
        [
            "add", "start", "pause", "reset", "status", "rename", "delete", "list", "show", "help",
            "--id", "--help", "-h", "--", "-", "default", "DEFAULT", "0", "1", "-1", "007", "99999999999999999999",
            "", " ", "\0", "\n", "‮", "​", "é", "é", "\U0001F600", "x", "work",
        ];

        for (var i = 0; i < 500; i++)
        {
            var args = new string[random.Next(0, 6)];
            for (var j = 0; j < args.Length; j++)
                args[j] = random.Next(4) == 0 ? RandomText(random) : tokens[random.Next(tokens.Length)];

            var reply = Cli(handler, args);

            Assert.True(reply.ExitCode is 0 or 1 or 2, $"time-arr {Show(args)} gave exit code {reply.ExitCode}.");
            if (reply.ExitCode != 0)
                AssertIsAMessage(reply, args);
        }
    }

    private static string RandomText(Random random)
    {
        var chars = new char[random.Next(0, 80)];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)random.Next(0, 0x10000);
        return new string(chars);
    }

    // --- IPC frames ---

    [Fact]
    public void Random_ipc_frames_are_refused_or_handled_and_never_throw()
    {
        var socketPath = InstanceChannel.SocketPath(_folder).Value;
        using var listener = InstanceChannel.Listen(socketPath).Value;
        var handler = Handler();
        var random = new Random(777);
        string[] shapes =
        [
            """{"args":["add","x"]}""", """{"args":[]}""", """{"args":null}""", """{"args":[null]}""",
            """{"args":[1]}""", """{"args":{}}""", """{"Args":["list"]}""", """{"args":["list"],"args":["add"]}""",
            """{"args":["list"]""", """[]""", """null""", "\"args\"", "{}",
        ];

        for (var i = 0; i < 200; i++)
        {
            byte[] body = random.Next(3) switch
            {
                0 => Encoding.UTF8.GetBytes(shapes[random.Next(shapes.Length)]),
                1 => RandomBytes(random),
                _ => Encoding.UTF8.GetBytes($$"""{"args":["{{Escape(RandomText(random))}}"]}"""),
            };
            var frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteInt32BigEndian(frame, random.Next(10) == 0 ? random.Next(int.MinValue, int.MaxValue) : body.Length);
            body.CopyTo(frame, 4);

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var server = listener.Accept();
            server.ReceiveTimeout = 2000;
            client.Send(frame);
            client.Shutdown(SocketShutdown.Send);

            Result<IpcRequest>? request = null;
            Assert.Null(Record.Exception(() => request = IpcProtocol.ReadRequest(server)));
            if (request!.IsFailure)
            {
                Assert.Equal(ErrorCode.Ipc, request.Error.Code);
                continue;
            }
            var reply = Cli(handler, request.Value.Args);
            Assert.True(reply.ExitCode is 0 or 1 or 2);
        }
    }

    private static byte[] RandomBytes(Random random)
    {
        var bytes = new byte[random.Next(1, 200)];
        random.NextBytes(bytes);
        return bytes;
    }

    private static string Escape(string text)
    {
        var json = new StringBuilder();
        foreach (var c in text)
            json.Append(c is '"' or '\\' || c < ' ' || char.IsSurrogate(c) ? $"\\u{(int)c:x4}" : c.ToString());
        return json.ToString();
    }

    // --- UI fields (through the viewmodels) ---

    public static TheoryData<string?> BadNames => new()
    {
        null, "", "   ", "-x", "a\0b", "tab\there", "‮evil", new string('x', 65), "DEFAULT",
    };

    [Theory]
    [MemberData(nameof(BadNames))]
    public void Bad_text_in_the_add_box_shows_a_message_and_never_throws(string? name)
    {
        var vm = new MainViewModel(Handler(), _clock);

        // A cleared TextBox can push null through the binding even though the property is non-nullable.
        vm.NewTimerName = name!;
        Assert.Null(Record.Exception(() => vm.AddCommand.Execute(null)));

        Assert.True(vm.HasMessage);
        Assert.False(string.IsNullOrWhiteSpace(vm.Message));
        Assert.Single(vm.Timers);
    }

    [Theory]
    [MemberData(nameof(BadNames))]
    public void Bad_text_in_the_rename_box_shows_a_message_and_never_throws(string? name)
    {
        var vm = new MainViewModel(Handler(), _clock);
        vm.AddCommand.Execute(null);
        vm.NewTimerName = "other";
        vm.AddCommand.Execute(null);
        var row = vm.Timers.Single(t => t.Name == "other");
        row.BeginRenameCommand.Execute(null);

        row.EditName = name!;
        Assert.Null(Record.Exception(() => row.CommitRenameCommand.Execute(null)));

        Assert.True(vm.HasMessage);
        Assert.False(string.IsNullOrWhiteSpace(vm.Message));
        Assert.True(row.IsEditing);
        Assert.Equal("other", row.Name);
    }
}
