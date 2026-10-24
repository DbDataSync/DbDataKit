using System.Text;
using DbDataKit.Hosting.Local;

namespace DbDataKit.Hosting.Local.Tests;

public sealed class LocalChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbdata-local-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private LocalChannelOptions Options(string name = "test") => new(name + Guid.NewGuid().ToString("N")[..6]) { Directory = Path.Combine(_root, "run") };

    private static Task<byte[]> Echo(byte[] request, CancellationToken ct) => Task.FromResult(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(request)));

    [Fact]
    public async Task A_request_gets_its_answer_and_the_connection_serves_several()
    {
        var options = Options();
        await using var server = new LocalChannelServer(options, Echo);
        server.Start();
        await using var client = await LocalChannelClient.ConnectAsync(options);
        Assert.Equal("echo:one", Encoding.UTF8.GetString(await client.SendAsync("one"u8.ToArray())));
        Assert.Equal("echo:two", Encoding.UTF8.GetString(await client.SendAsync("two"u8.ToArray())));
    }

    [Fact]
    public async Task Several_clients_at_once_each_get_their_own_answers()
    {
        var options = Options();
        await using var server = new LocalChannelServer(options, Echo);
        server.Start();
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var c = await LocalChannelClient.ConnectAsync(options);
            return Encoding.UTF8.GetString(await c.SendAsync(Encoding.UTF8.GetBytes("n" + i)));
        }));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => "echo:n" + i), answers);
    }

    [Fact]
    public async Task A_message_over_the_limit_drops_that_connection_and_the_server_carries_on()
    {
        var options = Options() with { MaxMessageBytes = 64 };
        await using var server = new LocalChannelServer(options, Echo);
        server.Start();
        await using (var greedy = await LocalChannelClient.ConnectAsync(options))
            await Assert.ThrowsAsync<InvalidDataException>(() => greedy.SendAsync(new byte[65]));
        await using var raw = await LocalChannelClient.ConnectAsync(options);
        Assert.Equal("echo:ok", Encoding.UTF8.GetString(await raw.SendAsync("ok"u8.ToArray())));
    }

    [Fact]
    public async Task Nothing_listening_is_a_failure_to_connect_not_a_hang()
    {
        var options = Options();
        await Assert.ThrowsAnyAsync<Exception>(() => LocalChannelClient.ConnectAsync(options, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Framing_refuses_an_announced_length_beyond_the_limit_before_reading_it()
    {
        var stream = new MemoryStream([0x7f, 0xff, 0xff, 0xff]);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalChannelFraming.ReadAsync(stream, 1024));
    }

    [Fact]
    public async Task Framing_tells_a_clean_close_from_a_cut_message()
    {
        Assert.Null(await LocalChannelFraming.ReadAsync(new MemoryStream([])));
        await Assert.ThrowsAsync<EndOfStreamException>(() => LocalChannelFraming.ReadAsync(new MemoryStream([0, 0, 0, 5, 1, 2])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a b")]
    public void A_channel_name_cannot_be_a_path(string name) => Assert.Throws<ArgumentException>(() => new LocalChannelOptions(name).Endpoint);

    [Fact]
    public async Task On_Unix_the_folder_and_socket_are_closed_to_everyone_but_the_owner()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options();
        await using (var server = new LocalChannelServer(options, Echo))
        {
            server.Start();
            await using var c = await LocalChannelClient.ConnectAsync(options);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(options.Endpoint)!));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(options.Endpoint));
        }
        Assert.False(File.Exists(options.Endpoint));
    }

    [Fact]
    public async Task On_Unix_group_access_opens_the_socket_to_the_group_only()
    {
        if (OperatingSystem.IsWindows()) return;
        var options = Options() with { GroupAccess = true };
        await using var server = new LocalChannelServer(options, Echo);
        server.Start();
        await using var c = await LocalChannelClient.ConnectAsync(options);
        var mode = File.GetUnixFileMode(options.Endpoint);
        Assert.True(mode.HasFlag(UnixFileMode.GroupWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead) || mode.HasFlag(UnixFileMode.OtherWrite));
    }

    [Fact]
    public async Task On_Windows_a_pipe_with_an_access_list_still_serves_its_creator()
    {
        if (!OperatingSystem.IsWindows()) return;
        var options = Options() with { WindowsIdentities = ["SYSTEM"] };
        await using var server = new LocalChannelServer(options, Echo);
        server.Start();
        // The creator is always allowed (the server must open further instances); the list governs everyone else, which a single-user runner cannot exercise.
        await using var c = await LocalChannelClient.ConnectAsync(options);
        Assert.Equal("echo:x", Encoding.UTF8.GetString(await c.SendAsync("x"u8.ToArray())));
    }
}
