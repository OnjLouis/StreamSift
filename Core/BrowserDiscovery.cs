using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace StreamSift;

public sealed class BrowserDiscovery(HttpClient client)
{
    private const int MaxBrowserRequests = 72, MaxBrowserEvents = 180, MaxCandidates = 16, MaxDocument = 5 * 1024 * 1024,
        MaxReceived = 16 * 1024 * 1024;
    private static readonly TimeSpan BrowserTime = TimeSpan.FromSeconds(17), ResourceTime = TimeSpan.FromSeconds(4);
    private static readonly string[] BrowserResources = ["Document", "Script", "Stylesheet", "XHR", "Fetch", "Media", "Other"];
    private static readonly HashSet<string> ForwardHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept", "accept-language", "origin", "referer", "user-agent"
    };
    private static readonly HashSet<string> OmitHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "content-encoding", "content-length", "transfer-encoding", "set-cookie", "alt-svc"
    };

    public static bool IsAvailable => BrowserPath() != null;

    public async Task<SearchResult> FindAsync(Uri start, CancellationToken cancel, IProgress<int>? progress = null)
    {
        if (!PublicNetwork.TryUrl(start.AbsoluteUri, null, out start)) throw new ArgumentException("Invalid public address.");
        var executable = BrowserPath() ?? throw new InvalidOperationException("Chrome or Edge is not installed.");
        var profile = Directory.CreateTempSubdirectory("streamsift-browser-");
        var observed = new Dictionary<string, Uri>(StringComparer.Ordinal);
        int requests = 0, received = 0, events = 0;
        bool timedOut = false;
        try
        {
            // Chromium has no outward connection of its own. Intercepted requests are fulfilled
            // through PublicNetwork, which resolves, validates and pins the actual peer address.
            using var sink = new TcpListener(IPAddress.Loopback, 0);
            sink.Start(1);
            var proxyPort = ((IPEndPoint)sink.LocalEndpoint).Port;
            bool nativeAppleBrowser = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
                SupportsAppleSilicon();
            var launch = new ProcessStartInfo(nativeAppleBrowser ? "/usr/bin/arch" : executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            if (nativeAppleBrowser)
            {
                // A translated scanner need not run a universal browser under translation as well.
                launch.ArgumentList.Add("-arm64");
                launch.ArgumentList.Add(executable);
            }
            foreach (var arg in new[]
            {
                "--headless=new", "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
                "--disable-background-networking", "--disable-component-update", "--disable-sync", "--disable-extensions",
                "--no-first-run", "--no-default-browser-check", "--mute-audio", "--disable-quic", "--use-mock-keychain",
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                "--host-resolver-rules=MAP * ~NOTFOUND", "--proxy-bypass-list=<-loopback>",
                $"--proxy-server=http://127.0.0.1:{proxyPort}", $"--user-data-dir={profile.FullName}", "about:blank"
            }) launch.ArgumentList.Add(arg);

            using var browser = Process.Start(launch) ?? throw new InvalidOperationException("Could not start the browser.");
            try
            {
                using var attachedJob = OperatingSystem.IsWindows() ? BrowserJob.Attach(browser) : null;
                browser.BeginErrorReadLine();
                browser.BeginOutputReadLine();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(BrowserTime);
                var token = deadline.Token;
                using var transport = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
                var endpoint = await DevToolsPageAsync(profile.FullName, browser, transport, token);
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(endpoint, transport, token);
                using var devTools = new DevTools(socket, token);
                var activity = new List<Task>();
                var read = devTools.ReadAsync(OnEvent);
                try
                {
                    await devTools.CommandAsync("Page.enable");
                    await devTools.CommandAsync("Browser.setDownloadBehavior", w => w.WriteString("behavior", "deny"));
                    await devTools.CommandAsync("Network.setBypassServiceWorker", w => w.WriteBoolean("bypass", true));
                    await devTools.CommandAsync("Fetch.enable", w =>
                    {
                        w.WriteStartArray("patterns");
                        foreach (var kind in BrowserResources)
                        {
                            w.WriteStartObject();
                            w.WriteString("urlPattern", "*");
                            w.WriteString("resourceType", kind);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                    });
                    await devTools.CommandAsync("Page.navigate", w => w.WriteString("url", start.AbsoluteUri));
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                    bool empty;
                    lock (observed) empty = observed.Count == 0;
                    if (empty)
                        await devTools.CommandAsync("Runtime.evaluate", w =>
                        {
                            w.WriteString("expression", PlayOnce);
                            w.WriteBoolean("userGesture", true);
                        });
                    await Task.Delay(TimeSpan.FromSeconds(4), token);
                    try { await devTools.CommandAsync("Browser.close").WaitAsync(TimeSpan.FromMilliseconds(500), token); }
                    catch (Exception error) when (error is WebSocketException or TimeoutException or InvalidOperationException) { }
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { timedOut = true; }
                finally
                {
                    await deadline.CancelAsync();
                    await read;
                    await Task.WhenAll(activity);
                }

                void OnEvent(JsonElement message)
                {
                    if (!message.TryGetProperty("method", out var method)) return;
                    if (method.GetString() is not ("Fetch.requestPaused" or "Page.javascriptDialogOpening")) return;
                    if (++events > MaxBrowserEvents)
                    {
                        deadline.Cancel();
                        return;
                    }
                    if (method.GetString() == "Fetch.requestPaused" && message.TryGetProperty("params", out var data))
                        activity.Add(HandleAsync(data));
                    else if (method.GetString() == "Page.javascriptDialogOpening")
                        activity.Add(IgnoreEndingAsync(devTools.CommandAsync("Page.handleJavaScriptDialog",
                            w => w.WriteBoolean("accept", false))));
                }

                async Task HandleAsync(JsonElement args)
                {
                    string id = args.GetProperty("requestId").GetString()!;
                    var request = args.GetProperty("request");
                    string? verb = request.GetProperty("method").GetString();
                    string? kind = args.GetProperty("resourceType").GetString();
                    try
                    {
                        if (verb != "GET" || !BrowserResources.Contains(kind) ||
                            !PublicNetwork.TryUrl(request.GetProperty("url").GetString() ?? "", null, out var address) ||
                            Interlocked.Increment(ref requests) > MaxBrowserRequests)
                        {
                            await RejectAsync();
                            return;
                        }
                        if (kind == "Media" || MediaAddress(address))
                        {
                            Remember(address);
                            await RejectAsync();
                            return;
                        }

                        using var requestTime = CancellationTokenSource.CreateLinkedTokenSource(token);
                        requestTime.CancelAfter(ResourceTime);
                        using var outgoing = new HttpRequestMessage(HttpMethod.Get, address);
                        if (request.TryGetProperty("headers", out var headers))
                        {
                            foreach (var header in headers.EnumerateObject())
                                if (ForwardHeaders.Contains(header.Name) && header.Value.ValueKind == JsonValueKind.String)
                                    outgoing.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
                        }
                        using var response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, requestTime.Token);
                        var type = response.Content.Headers.ContentType?.MediaType;
                        if (response.IsSuccessStatusCode && type != null &&
                            (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                             type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || type == "application/ogg"))
                        {
                            Remember(address);
                            await RejectAsync();
                            return;
                        }

                        var body = Array.Empty<byte>();
                        if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is not > MaxDocument)
                        {
                            await using var data = await response.Content.ReadAsStreamAsync(requestTime.Token);
                            using var saved = new MemoryStream();
                            var chunk = new byte[8192];
                            while (true)
                            {
                                int count = await data.ReadAsync(chunk, requestTime.Token);
                                if (count == 0) break;
                                if (saved.Length + count > MaxDocument || Interlocked.Add(ref received, count) > MaxReceived)
                                {
                                    await RejectAsync();
                                    return;
                                }
                                saved.Write(chunk, 0, count);
                            }
                            body = saved.ToArray();
                        }
                        await devTools.CommandAsync("Fetch.fulfillRequest", w =>
                        {
                            w.WriteString("requestId", id);
                            w.WriteNumber("responseCode", (int)response.StatusCode);
                            w.WriteStartArray("responseHeaders");
                            foreach (var header in response.Headers.Concat(response.Content.Headers))
                            {
                                if (OmitHeaders.Contains(header.Key)) continue;
                                foreach (var value in header.Value)
                                {
                                    w.WriteStartObject();
                                    w.WriteString("name", header.Key);
                                    w.WriteString("value", value);
                                    w.WriteEndObject();
                                }
                            }
                            w.WriteEndArray();
                            w.WriteBase64String("body", body);
                        });
                    }
                    catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or WebSocketException or InvalidOperationException)
                    {
                        if (!token.IsCancellationRequested) await IgnoreEndingAsync(RejectAsync());
                    }

                    Task RejectAsync() => devTools.CommandAsync("Fetch.failRequest", w =>
                    {
                        w.WriteString("requestId", id);
                        w.WriteString("errorReason", "BlockedByClient");
                    });
                }
            }
            finally
            {
                if (!browser.HasExited)
                {
                    try { browser.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (browser.HasExited) { }
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await browser.WaitForExitAsync(cleanup.Token);
            }
        }
        finally
        {
            var info = new DirectoryInfo(profile.FullName);
            if (info.Exists && info.Name.StartsWith("streamsift-browser-", StringComparison.Ordinal) &&
                Path.GetFullPath(info.Parent!.FullName) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) &&
                !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                for (int attempt = 1; ; attempt++)
                {
                    try { info.Delete(recursive: true); break; }
                    catch (Exception error) when (attempt < 5 && error is IOException or UnauthorizedAccessException)
                    {
                        await Task.Delay(150 * attempt);
                    }
                }
            }
        }

        var verified = await new Discovery(client).VerifyObservedAsync(start, observed.Values, cancel, progress);
        return verified with { LimitReached = verified.LimitReached || timedOut ||
            requests > MaxBrowserRequests || received > MaxReceived || events > MaxBrowserEvents };

        void Remember(Uri address)
        {
            lock (observed)
                if (observed.Count < MaxCandidates) observed.TryAdd(address.AbsoluteUri, address);
        }
    }

    private static bool MediaAddress(Uri uri) => Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is
        ".mp4" or ".webm" or ".m3u8" or ".m3u" or ".pls" or ".xspf" or ".asx" or ".mp3" or ".mp2" or ".aac" or ".aacp" or ".flac" or ".ogg" or ".opus";

    private static string? BrowserPath()
    {
        if (OperatingSystem.IsMacOS())
            return new[] { "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge" }.FirstOrDefault(File.Exists);
        if (!OperatingSystem.IsWindows()) return null;
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
            .Select(Environment.GetFolderPath).Where(path => path.Length != 0)
            .SelectMany(path => new[] { Path.Combine(path, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(path, @"Microsoft\Edge\Application\msedge.exe") }).FirstOrDefault(File.Exists);
    }

    private static bool SupportsAppleSilicon()
    {
        nuint size = sizeof(int);
        return SysctlByName("hw.optional.arm64", out int supported, ref size, IntPtr.Zero, 0) == 0 && supported == 1;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out int value, ref nuint valueSize, IntPtr newValue, nuint newValueSize);

    private static async Task<Uri> DevToolsPageAsync(string profile, Process browser, HttpClient transport, CancellationToken token)
    {
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (browser.HasExited) throw new InvalidOperationException("The temporary browser stopped before it was ready.");
            if (File.Exists(portFile))
            {
                string[] lines = await File.ReadAllLinesAsync(portFile, token);
                if (lines.Length >= 2 && int.TryParse(lines[0], out int port) && port is > 0 and < 65536)
                {
                    try
                    {
                        using var response = await transport.GetAsync($"http://127.0.0.1:{port}/json/list", token);
                        response.EnsureSuccessStatusCode();
                        using var tabs = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
                        foreach (var tab in tabs.RootElement.EnumerateArray())
                        {
                            if (tab.GetProperty("type").GetString() != "page" ||
                                !tab.TryGetProperty("webSocketDebuggerUrl", out var url) ||
                                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var endpoint)) continue;
                            if (endpoint.Scheme == "ws" && endpoint.Host == "127.0.0.1" && endpoint.Port == port &&
                                endpoint.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal)) return endpoint;
                        }
                    }
                    catch (HttpRequestException) { }
                }
            }
            await Task.Delay(100, token);
        }
    }

    private static async Task IgnoreEndingAsync(Task task)
    {
        try { await task; }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or InvalidOperationException) { }
    }

    private const string PlayOnce = """
        (() => {
          const patterns = /^(play(?: live| radio| live radio| now)?|listen(?: live| now)?|lyssna|en vivo|ao vivo|ouvir ao vivo|escuchar en vivo|ecouter en direct)$/i;
          const control = [...document.querySelectorAll('button,[role="button"]')].find(el =>
            el.getClientRects().length && !el.matches(':disabled,[aria-disabled="true"]') &&
            [el.getAttribute('aria-label'),el.title,el.textContent].some(text => patterns.test((text || '').trim())));
          if (control) control.click();
          else {
            const audio = document.querySelector('audio, video');
            if (audio) { audio.muted = true; audio.play().catch(() => {}); }
          }
        })()
        """;

    private static class BrowserJob
    {
        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformation = 9;

        internal static SafeFileHandle Attach(Process process)
        {
            var job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
            var info = new ExtendedLimits { Basic = new BasicLimits { Flags = KillOnJobClose } };
            if (!job.IsInvalid && SetInformationJobObject(job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()) &&
                AssignProcessToJobObject(job, process.SafeHandle)) return job;
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            throw error;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateJobObjectW")]
        private static extern SafeFileHandle CreateJobObject(IntPtr attributes, IntPtr name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    }

    private sealed class DevTools(ClientWebSocket socket, CancellationToken token) : IDisposable
    {
        private readonly SemaphoreSlim writes = new(1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> pending = new();
        private int nextId;

        internal async Task CommandAsync(string method, Action<Utf8JsonWriter>? args = null)
        {
            int id = Interlocked.Increment(ref nextId);
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.TryAdd(id, reply);
            try
            {
                var data = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(data))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", id);
                    writer.WriteString("method", method);
                    if (args != null)
                    {
                        writer.WriteStartObject("params");
                        args(writer);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                await writes.WaitAsync(token);
                try { await socket.SendAsync(data.WrittenMemory, WebSocketMessageType.Text, true, token); }
                finally { writes.Release(); }
                if (!await reply.Task.WaitAsync(token)) throw new InvalidOperationException($"Browser did not accept {method}.");
            }
            finally { pending.TryRemove(id, out _); }
        }

        internal async Task ReadAsync(Action<JsonElement> onEvent)
        {
            var buffer = new byte[16 * 1024];
            var collected = new ArrayBufferWriter<byte>();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var read = await socket.ReceiveAsync(buffer.AsMemory(), token);
                    if (read.MessageType != WebSocketMessageType.Text || collected.WrittenCount + read.Count > 1024 * 1024) break;
                    collected.Write(buffer.AsSpan(0, read.Count));
                    if (!read.EndOfMessage) continue;
                    using var json = JsonDocument.Parse(collected.WrittenMemory);
                    var payload = json.RootElement;
                    if (payload.TryGetProperty("id", out var id) && pending.TryGetValue(id.GetInt32(), out var response))
                        response.TrySetResult(!payload.TryGetProperty("error", out _));
                    else onEvent(payload.Clone());
                    collected.Clear();
                }
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
            finally { foreach (var reply in pending.Values) reply.TrySetResult(false); }
        }

        public void Dispose() => writes.Dispose();
    }
}
