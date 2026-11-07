using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AfterHours;

internal sealed record BrowserMessage(string Browser, string Host, bool Focused, bool Video = false,
    bool Policy = false, bool OpenLimits = false);

internal static class BrowserBridge
{
    private const string HostName = "com.afterhours.local";
    private const int MaxReply = 900_000;
    private static string PipeName => "AfterHours.policy." + Environment.UserName + "." + Process.GetCurrentProcess().SessionId;

    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "BrowserExtension");

    public static void Register(string directory)
    {
        using var extension = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExtensionFolder, "manifest.json")));
        var bytes = Convert.FromBase64String(extension.RootElement.GetProperty("key").GetString()!);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        string id = string.Create(32, hash, (chars, data) =>
        {
            for (int i = 0; i < 16; i++)
            {
                chars[i * 2] = (char)('a' + (data[i] >> 4));
                chars[i * 2 + 1] = (char)('a' + (data[i] & 15));
            }
        });
        string path = Path.Combine(directory, "browser-host.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "AfterHours local website tracking",
            path = Environment.ProcessPath!,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{id}/" }
        }));
        foreach (var product in new[] { @"Google\Chrome", @"Microsoft\Edge" })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\{product}\NativeMessagingHosts\{HostName}");
            key.SetValue("", path);
        }
    }

    public static void Unregister()
    {
        foreach (var product in new[] { @"Google\Chrome", @"Microsoft\Edge" })
            Registry.CurrentUser.DeleteSubKey($@"Software\{product}\NativeMessagingHosts\{HostName}", false);
    }

    public static void RunHost()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        Span<byte> length = stackalloc byte[4];
        try
        {
            while (true)
            {
                input.ReadExactly(length);
                int size = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (size is < 1 or > 1024) return;
                var bytes = new byte[size];
                input.ReadExactly(bytes);
                byte[] response = "{\"connected\":false}"u8.ToArray();
                try
                {
                    var request = JsonSerializer.Deserialize<BrowserMessage>(bytes);
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                        PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                    client.Connect(450);
                    client.Write(length);
                    client.Write(bytes);
                    response = ReadReply(client, request?.Policy == true || request?.OpenLimits == true).GetAwaiter().GetResult();
                }
                catch (TimeoutException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                catch (JsonException) { }
                catch (OperationCanceledException) { }
                BinaryPrimitives.WriteInt32LittleEndian(length, response.Length);
                output.Write(length);
                output.Write(response);
                output.Flush();
            }
        }
        catch (IOException) { }
    }

    private static async Task<byte[]> ReadReply(Stream pipe, bool wantsPolicy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ack = new byte[1];
        await pipe.ReadExactlyAsync(ack, deadline.Token).ConfigureAwait(false);
        if (ack[0] != 1) return "{\"connected\":false}"u8.ToArray();
        if (!wantsPolicy) return "{\"connected\":true}"u8.ToArray();

        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaxReply) throw new IOException("Invalid browser reply.");
        var bytes = new byte[size];
        await pipe.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
        return bytes;
    }

    public static async Task Listen(Func<BrowserMessage, Task> receive, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var header = new byte[4];
                await server.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                int size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size is < 1 or > 1024) continue;
                var bytes = new byte[size];
                await server.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize<BrowserMessage>(bytes);
                if (message?.Host is null || message.Host.Length > 253 || message.Browser is not ("chrome" or "edge")) continue;
                await receive(message).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (message.Policy || message.OpenLimits)
                {
                    // Old companions use this response to clear rules from before the upgrade.
                    var payload = "{\"connected\":true,\"policy\":{\"expiresAt\":0,\"domains\":[]}}"u8.ToArray();
                    BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                    await server.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
                    await server.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                    await server.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                    continue;
                }
                await server.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (JsonException) { }
            catch (OperationCanceledException) { }
        }
    }
}
