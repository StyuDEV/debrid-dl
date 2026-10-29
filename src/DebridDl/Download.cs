using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace DebridDl;

public enum State { Queued, Starting, Running, Paused, Done, Failed }

sealed class LinkExpired : Exception;

// Same files as the Python downloader (.telech + .telech.json), so either one can resume the other.
sealed record Saved(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("origine")] string Origin,
    [property: JsonPropertyName("taille")] long Size,
    [property: JsonPropertyName("morceau")] long Chunk,
    [property: JsonPropertyName("faits")] int[] Done);

public sealed class Download : INotifyPropertyChanged
{
    const long ChunkSize = 4L << 20;
    static readonly TimeSpan stall = TimeSpan.FromSeconds(60);
    static readonly HttpClient http = CreateClient();
    static readonly Brush blue = Frozen("#4C8DFF");
    static readonly Brush green = Frozen("#3DBE7A");
    static readonly Brush red = Frozen("#E5534B");
    static readonly Brush grey = Frozen("#5B6270");

    readonly object gate = new();
    readonly HashSet<int> done = [];
    readonly List<int> pending = [];
    readonly Queue<(DateTime At, long Bytes)> samples = new();
    readonly SemaphoreSlim renewing = new(1, 1);
    CancellationTokenSource? cts;
    Task? task;
    string url;
    string? path;
    string? failure;
    long chunk, received;
    bool ranges;
    volatile State state;

    public Download(string link, string folder, string? fileName = null, long size = 0, State state = State.Queued)
    {
        Link = link;
        Folder = folder;
        url = link;
        path = fileName is null ? null : System.IO.Path.Combine(folder, fileName);
        Size = size;
        this.state = state;
        if (state == State.Done)
        {
            received = size;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Link { get; }
    public string Folder { get; }
    public long Size { get; private set; }
    public State State => state;
    public string? Path => path;
    public string Name => path is null ? Link : System.IO.Path.GetFileName(path);
    public bool IsActive => state is State.Starting or State.Running;
    public double Speed { get; private set; }
    public double Progress => Size > 0 ? 100.0 * Interlocked.Read(ref received) / Size : 0;
    public string Details { get; private set; } = "";
    public string ActionGlyph => IsActive || state == State.Queued ? "" : "";
    public string ActionTip => IsActive || state == State.Queued ? "Pause" : "Reprendre";
    public Visibility ActionVisibility => state == State.Done ? Visibility.Collapsed : Visibility.Visible;
    public Brush Color => state switch
    {
        State.Done => green,
        State.Failed => red,
        State.Paused or State.Queued => grey,
        _ => blue,
    };
    string Temp => path + ".telech";
    string StatePath => path + ".telech.json";

    public Task Stopped => task ?? Task.CompletedTask;

    public void Start(int connections)
    {
        if (task is { IsCompleted: false })
        {
            return;
        }

        state = State.Starting;
        cts = new();
        var ct = cts.Token;
        task = Task.Run(() => Run(connections, ct));
    }

    public void Pause()
    {
        if (state == State.Queued)
        {
            state = State.Paused;
            return;
        }
        cts?.Cancel();
    }

    public void Requeue()
    {
        if (state is State.Paused or State.Failed)
        {
            state = State.Queued;
        }
    }

    public void DeletePartial()
    {
        if (path is null)
        {
            return;
        }
        File.Delete(Temp);
        File.Delete(StatePath);
    }

    public QueuedItem ToQueued()
    {
        var saved = state switch
        {
            State.Done => State.Done,
            State.Paused or State.Failed => State.Paused,
            _ => State.Queued,
        };
        return new(Link, Folder, path is null ? null : System.IO.Path.GetFileName(path), Size, saved);
    }

    public void SaveState()
    {
        lock (gate)
        {
            if (path is null || chunk == 0 || state == State.Done)
            {
                return;
            }
            var saved = new Saved(url, Link, Size, chunk, [.. done.Order()]);
            File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(saved));
            File.Move(StatePath + ".tmp", StatePath, true);
        }
    }

    public void Refresh()
    {
        var now = DateTime.UtcNow;
        var bytes = Interlocked.Read(ref received);
        if (state != State.Running)
        {
            samples.Clear();
        }
        samples.Enqueue((now, bytes));
        while (samples.Count > 10)
        {
            samples.Dequeue();
        }

        var (at, before) = samples.Peek();
        var seconds = (now - at).TotalSeconds;
        Speed = state == State.Running && seconds > 0 ? Math.Max(0, (bytes - before) / seconds) : 0;

        var amount = Size > 0 ? $"{Bytes(bytes)} / {Bytes(Size)}" : Bytes(bytes);
        Details = state switch
        {
            State.Queued => "En attente",
            State.Starting => "Déblocage du lien…",
            State.Running => $"{amount} · {Bytes(Speed)}/s · {Eta(Size - bytes)}",
            State.Paused => $"En pause · {amount}",
            State.Done => $"Terminé · {Bytes(Size)}",
            _ => $"Échec : {failure}",
        };
        PropertyChanged?.Invoke(this, new(null));
    }

    public static string Bytes(double b) => b switch
    {
        >= 1 << 30 => $"{b / (1 << 30):0.0} Go",
        >= 1 << 20 => $"{b / (1 << 20):0.0} Mo",
        >= 1 << 10 => $"{b / (1 << 10):0} Ko",
        _ => $"{b:0} o",
    };

    string Eta(long left)
    {
        if (Speed <= 0 || Size == 0)
        {
            return "--";
        }

        var t = TimeSpan.FromSeconds(left / Speed);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{t.Minutes} min {t.Seconds:00}";
    }

    async Task Run(int connections, CancellationToken ct)
    {
        failure = null;
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (await PrepareWithRetry(ct))
            {
                state = State.Done;
                return;
            }

            state = State.Running;
            var count = ranges ? connections : 1;
            await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Worker(abort)));
            SaveState();

            if (ct.IsCancellationRequested)
            {
                state = State.Paused;
                return;
            }
            if (failure is not null)
            {
                state = State.Failed;
                return;
            }

            File.Move(Temp, path!, true);
            File.Delete(StatePath);
            state = State.Done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SaveState();
            state = State.Paused;
        }
        catch (Exception e)
        {
            failure = e.Message;
            SaveState();
            state = State.Failed;
        }
    }

    async Task<bool> PrepareWithRetry(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await Prepare(ct);
            }
            catch (HttpRequestException) when (attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    async Task<bool> Prepare(CancellationToken ct)
    {
        var direct = new Uri(Link).Host.EndsWith("debrid.it");
        url = direct ? Link : await Debrid.Unlock(Link, ct) ?? Link;
        var name = await Probe(ct);
        path ??= System.IO.Path.Combine(Folder, name);

        if (File.Exists(path) && new FileInfo(path).Length == Size)
        {
            received = Size;
            return true;
        }

        Directory.CreateDirectory(Folder);
        var saved = File.Exists(Temp) && Load() is { } s && s.Size == Size && s.Chunk > 0 ? s : null;
        chunk = ranges && Size > 0 ? saved?.Chunk ?? ChunkSize : Math.Max(Size, 1);
        var count = (int)Math.Max(1, (Size + chunk - 1) / chunk);

        lock (gate)
        {
            done.Clear();
            pending.Clear();
            if (saved is not null && saved.Chunk == chunk)
            {
                done.UnionWith(saved.Done.Where(i => i < count));
            }
            pending.AddRange(Enumerable.Range(0, count).Where(i => !done.Contains(i)));
            received = done.Sum(Length);
        }

        await using (var file = new FileStream(Temp, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            if (Size > 0 && file.Length > Size)
            {
                file.SetLength(Size);
            }
        }

        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path))!;
        if (Size - new FileInfo(Temp).Length > new DriveInfo(root).AvailableFreeSpace)
        {
            throw new IOException("pas assez de place sur le disque");
        }

        SaveState();
        return false;
    }

    async Task<string> Probe(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
        }

        var headers = res.Content.Headers;
        ranges = res.StatusCode == HttpStatusCode.PartialContent;
        if (!ranges && headers.ContentType?.MediaType == "text/html")
        {
            var reason = Debrid.ApiKey == "" ? "lien non débloqué : ajoute ta clé AllDebrid" : "page web, pas un fichier";
            throw new IOException(reason);
        }

        url = res.RequestMessage!.RequestUri!.AbsoluteUri;
        Size = ranges ? headers.ContentRange?.Length ?? 0 : headers.ContentLength ?? 0;

        var name = headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"');
        name ??= Uri.UnescapeDataString(new Uri(url).AbsolutePath.Split('/')[^1]);
        name = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars()));
        return name == "" ? "fichier" : name;
    }

    async Task Worker(CancellationTokenSource abort)
    {
        await Task.Yield();
        var token = abort.Token;
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            int i;
            lock (gate)
            {
                if (pending.Count == 0)
                {
                    return;
                }
                i = pending[0];
                pending.RemoveAt(0);
            }

            var used = url;
            try
            {
                await Fetch(i, used, token);
                lock (gate)
                {
                    done.Add(i);
                }
                failures = 0;
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    pending.Insert(0, i);
                }
                if (token.IsCancellationRequested)
                {
                    return;
                }
                if (++failures >= 20)
                {
                    Abort(abort, $"20 échecs de suite ({e.Message})");
                    return;
                }

                try
                {
                    if (e is LinkExpired)
                    {
                        await Renew(used, abort);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * failures)), token);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    async Task Fetch(int i, string source, CancellationToken token)
    {
        var start = i * chunk;
        var expected = Length(i);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(stall);

        using var req = new HttpRequestMessage(HttpMethod.Get, source);
        if (ranges)
        {
            req.Headers.Range = new RangeHeaderValue(start, start + expected - 1);
        }
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)res.StatusCode is 401 or 403 or 404 or 410)
        {
            throw new LinkExpired();
        }
        if (!res.IsSuccessStatusCode || ranges && res.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
        }

        await using var body = await res.Content.ReadAsStreamAsync(timeout.Token);
        await using var file = new FileStream(Temp, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 16, true);
        file.Position = start;

        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        long got = 0;
        try
        {
            int n;
            while ((n = await body.ReadAsync(buffer, timeout.Token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), token);
                got += n;
                Interlocked.Add(ref received, n);
                timeout.CancelAfter(stall);
            }
            if (Size > 0 && got != expected)
            {
                throw new IOException("morceau coupé");
            }
        }
        catch
        {
            Interlocked.Add(ref received, -got);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    async Task Renew(string old, CancellationTokenSource abort)
    {
        await renewing.WaitAsync(abort.Token);
        try
        {
            if (url != old)
            {
                return;
            }

            var fresh = await Debrid.Unlock(Link, abort.Token);
            if (fresh is null)
            {
                Abort(abort, Debrid.ApiKey == "" ? "lien expiré : ajoute ta clé AllDebrid" : "lien expiré");
                return;
            }
            url = fresh;
        }
        catch (DebridException e)
        {
            Abort(abort, e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
        {
        }
        finally
        {
            renewing.Release();
        }
    }

    void Abort(CancellationTokenSource abort, string message)
    {
        failure ??= message;
        abort.Cancel();
    }

    long Length(int i) => Size > 0 ? Math.Min(chunk, Size - i * chunk) : 0;

    Saved? Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Saved>(File.ReadAllText(StatePath));
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 512,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    static Brush Frozen(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
