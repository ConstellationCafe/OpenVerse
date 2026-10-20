using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenVerse.Api;

// Game files this PC lacks, from OpenVerseBE (/v1/assets/*). The client asks this Api for every file its manifests
// name, by hash; whatever is not in the player's own Shadowverse folder is fetched from the server and passed straight
// through. The client saves what it receives, so each file crosses the network once per PC.
//
// The server sits on a home upload line, so a PC never has more than Parallel requests open there: the rest wait
// their turn here. A busy server (503 from the rate limiter) or a dropped connection is retried after a pause.
public sealed class RemoteAssets
{
    readonly HttpClient _http;
    readonly string _cafeKey;
    readonly SemaphoreSlim _gate;
    readonly Action<string> _log;

    public const int DefaultParallel = 2;
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public int Attempts { get; init; } = 6;
    // game files: a busy or briefly unreachable server is asked again for longer, with pauses up to MaxRetryDelay
    public int FileAttempts { get; init; } = 30;
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);
    // how often to ask again while in line, when the server does not say (Retry-After)
    public TimeSpan QueuePoll { get; init; } = TimeSpan.FromSeconds(5);

    // Who this PC is in the server's download line (one per launch). The line itself is the server's: while every
    // download place is taken, a game file is answered 429 {"error":"queued","position":n} and this keeps asking,
    // without giving up, until its turn comes
    public string DownloadId { get; } = Guid.NewGuid().ToString("N");
    int _lastPosition = -1;

    sealed record ManifestEntry(string Name, long Size, string? Md5);
    sealed record ManifestList(List<ManifestEntry> Manifests);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public RemoteAssets(Uri baseUrl, string cafeKey, HttpMessageHandler? handler = null, int parallel = DefaultParallel,
        Action<string>? log = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUrl;
        // a 100 MB bundle at the server's rate limit takes minutes; only the wait for headers is bounded (HeaderTimeout)
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _cafeKey = cafeKey;
        _gate = new SemaphoreSlim(Math.Max(1, parallel));
        _log = log ?? Console.WriteLine;
    }

    public static bool IsHash(string name) => name.Length == 32 && name.All(Uri.IsHexDigit);

    async Task<HttpResponseMessage> Get(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (_cafeKey.Length > 0) req.Headers.Add("X-OV-Cafe-Key", _cafeKey);
        req.Headers.Add("X-OV-Download-Id", DownloadId);
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(HeaderTimeout);
        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer from the OpenVerse server within {HeaderTimeout.TotalSeconds:0}s");
        }
    }

    static bool Retryable(HttpStatusCode code) =>
        code is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout;

    sealed record Queued(string? Error, int Position, int Active, int Waiting);

    // GET with the gate held and retries; the caller disposes. null = the server does not have it (or never will).
    // file = a game file: waiting in the server's line never counts as a try, and other trouble is tried for longer
    async Task<HttpResponseMessage?> GetWithRetry(string path, string what, CancellationToken ct, bool file = false)
    {
        var limit = file ? FileAttempts : Attempts;
        for (var attempt = 1; ; attempt++)
        {
            string why;
            try
            {
                var res = await Get(path, ct);
                if (res.IsSuccessStatusCode)
                {
                    if (file && Interlocked.Exchange(ref _lastPosition, -1) > 0)
                        _log("  [assets] 차례가 왔습니다. 다운로드를 시작합니다 (your turn, downloading)");
                    return res;
                }
                var code = res.StatusCode;
                var body = await res.Content.ReadAsStringAsync(ct);
                var retryAfter = res.Headers.RetryAfter?.Delta;
                res.Dispose();
                if (file && code == HttpStatusCode.TooManyRequests && TryQueued(body) is { } q)
                {
                    if (Interlocked.Exchange(ref _lastPosition, q.Position) != q.Position)
                        _log($"  [assets] 다운로드 대기 중: {q.Position}번째 (지금 받는 사람 {q.Active}명) " +
                             $"- waiting in line, position {q.Position}");
                    attempt = 0;
                    await Task.Delay(retryAfter is { } ra && ra > TimeSpan.Zero ? ra : QueuePoll, ct);
                    continue;
                }
                if (!Retryable(code) || body.Contains("assets_failed"))
                {
                    _log($"  [assets] {what}: server answered {(int)code} {Short(body)}");
                    return null;
                }
                why = $"{(int)code} {Short(body)}";
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException)
            {
                why = e.Message;
            }
            if (attempt >= limit)
            {
                _log($"  [assets] {what}: giving up after {attempt} tries ({why})");
                return null;
            }
            var pause = RetryDelay * attempt;
            await Task.Delay(pause < MaxRetryDelay ? pause : MaxRetryDelay, ct);
        }
    }

    static Queued? TryQueued(string body)
    {
        try
        {
            var q = JsonSerializer.Deserialize<Queued>(body, Json);
            return q is { Error: "queued", Position: > 0 } ? q : null;
        }
        catch (JsonException) { return null; }
    }

    static string Short(string s) => s.Length > 120 ? s[..120] : s;

    // The server's manifests, merged onto nothing yet: name -> bytes. Each is kept in cacheDir and fetched again only
    // when the server lists a different hash for it. null when the server has no game files or cannot be reached
    public async Task<Dictionary<string, byte[]>?> LoadManifests(string cacheDir, CancellationToken ct = default)
    {
        ManifestList? list;
        await _gate.WaitAsync(ct);
        try
        {
            using var res = await GetWithRetry("v1/assets/manifests", "manifest list", ct);
            if (res is null) return null;
            list = await res.Content.ReadFromJsonAsync<ManifestList>(Json, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException or JsonException)
        {
            _log($"  [assets] manifest list: {e.Message}");
            return null;
        }
        finally { _gate.Release(); }
        if (list?.Manifests is not { Count: > 0 } entries) return null;

        try { Directory.CreateDirectory(cacheDir); }
        catch (IOException) { }

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int fetched = 0, cached = 0;
        foreach (var m in entries)
        {
            // a name is only ever a file name: anything else would write outside the cache
            if (m.Name.Length == 0 || m.Name != Path.GetFileName(m.Name)) continue;
            var file = Path.Combine(cacheDir, m.Name);
            if (File.Exists(file))
            {
                var have = await File.ReadAllBytesAsync(file, ct);
                if (m.Md5 is not null && string.Equals(Md5(have), m.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    result[m.Name] = have;
                    cached++;
                    continue;
                }
            }
            byte[]? bytes = null;
            await _gate.WaitAsync(ct);
            try
            {
                using var res = await GetWithRetry("v1/assets/manifests/" + Uri.EscapeDataString(m.Name), m.Name, ct);
                if (res is not null) bytes = await res.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                _log($"  [assets] {m.Name}: {e.Message}");
            }
            finally { _gate.Release(); }

            if (bytes is not null)
            {
                result[m.Name] = bytes;
                fetched++;
                try { await File.WriteAllBytesAsync(file, bytes, ct); }
                catch (IOException) { }
            }
            // the server is unreachable right now but this one was fetched before: an older list beats no list
            else if (File.Exists(file)) result[m.Name] = await File.ReadAllBytesAsync(file, ct);
        }
        _log($"Assets: {result.Count} manifests from the OpenVerse server ({fetched} fetched, {cached} unchanged)");
        return result;
    }

    // Streams the file with this hash from the server into the response. false = the server does not have it, and
    // nothing has been written
    public async Task<bool> Relay(string md5, HttpResponse response, CancellationToken ct)
    {
        if (!IsHash(md5)) return false;
        await _gate.WaitAsync(ct);
        try
        {
            using var res = await GetWithRetry("v1/assets/files/" + md5.ToLowerInvariant(), md5, ct, file: true);
            if (res is null) return false;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/octet-stream";
            if (res.Content.Headers.ContentLength is { } len) response.ContentLength = len;
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            // a transfer cut off half way surfaces as an exception here: the response is aborted, so the client sees
            // a failed download (and asks again) rather than a short file it would keep
            await body.CopyToAsync(response.Body, ct);
            return true;
        }
        finally { _gate.Release(); }
    }

    static string Md5(byte[] b) => Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant();

    // Server manifests onto the ones from this PC: a row whose file is here points at the local copy (index), every
    // other row stays as the server wrote it, and a manifest only this PC has is kept
    public static Dictionary<string, byte[]> Merge(IReadOnlyDictionary<string, byte[]> local,
        IReadOnlyDictionary<string, byte[]> server, ManifestIndex? index, out int localRows)
    {
        localRows = 0;
        var merged = new Dictionary<string, byte[]>(local, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in server)
        {
            if (index is null) { merged[name] = bytes; continue; }
            merged[name] = index.Rewrite(bytes, out var r, out _, deferMissing: false);
            localRows += r;
        }
        return merged;
    }
}
