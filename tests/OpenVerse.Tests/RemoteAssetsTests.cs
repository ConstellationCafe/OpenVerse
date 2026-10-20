using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenVerse.Api;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// OpenVerseBE's /v1/assets/* and /v1/card-master, in memory
sealed class FakeAssetServer : HttpMessageHandler
{
    public readonly Dictionary<string, byte[]> Manifests = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentQueue<string> Requests = new();
    public byte[]? CardMaster;
    public string? RequiredCafeKey;
    public bool NotConfigured;
    public bool Down;
    public int BusyFor;           // answer this many requests with 503 first, as nginx's limiter does
    public int QueuedFor;         // answer this many file requests with 429 queued (position counting down) first
    public readonly ConcurrentQueue<string> DownloadIds = new();
    public TimeSpan FileDelay;
    public string[] ExtraListNames = [];
    int _inFlight;
    public int MaxInFlight;

    static string Md5(byte[] b) => Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (Down) throw new HttpRequestException("connection refused");
        // players' be_url is the shared domain's /openverse path; the server's own routes start after it
        var path = req.RequestUri!.AbsolutePath;
        if (path.StartsWith("/openverse/")) path = path["/openverse".Length..];
        Requests.Enqueue(path);
        if (RequiredCafeKey is not null &&
            (!req.Headers.TryGetValues("X-OV-Cafe-Key", out var k) || k.FirstOrDefault() != RequiredCafeKey))
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"bad_cafe_key\"}") };
        if (Interlocked.Decrement(ref BusyFor) >= 0)
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };

        if (path == "/v1/card-master")
            return CardMaster is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"card_master_not_configured\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(CardMaster) };

        if (NotConfigured)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"assets_not_configured\"}") };

        if (path == "/v1/assets/manifests")
        {
            var list = Manifests.Select(kv => (object)new { name = kv.Key, size = kv.Value.Length, md5 = Md5(kv.Value) })
                .Concat(ExtraListNames.Select(n => (object)new { name = n, size = 1, md5 = "00" }));
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { manifests = list }), Encoding.UTF8, "application/json") };
        }
        if (path.StartsWith("/v1/assets/manifests/"))
        {
            var name = Uri.UnescapeDataString(path["/v1/assets/manifests/".Length..]);
            return Manifests.TryGetValue(name, out var m)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(m) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"manifest_not_found\"}") };
        }
        if (path.StartsWith("/v1/assets/files/"))
        {
            if (req.Headers.TryGetValues("X-OV-Download-Id", out var ids)) DownloadIds.Enqueue(ids.First());
            var left = Interlocked.Decrement(ref QueuedFor);
            if (left >= 0)
                return new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    { error = "queued", position = Math.Min(left / 10 + 1, 9), active = 2, waiting = 3, max_active = 2 })),
                };
            var now = Interlocked.Increment(ref _inFlight);
            lock (this) MaxInFlight = Math.Max(MaxInFlight, now);
            try
            {
                if (FileDelay > TimeSpan.Zero) await Task.Delay(FileDelay, ct);
                var md5 = path["/v1/assets/files/".Length..];
                return Files.TryGetValue(md5, out var f)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(f) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"file_not_found\"}") };
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public class RemoteAssetsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"ov-ra-{Guid.NewGuid():N}");
    readonly FakeAssetServer _server = new();
    readonly List<string> _log = [];

    public RemoteAssetsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    RemoteAssets Client(string key = "", int parallel = RemoteAssets.DefaultParallel) =>
        new(new Uri("https://be.example/openverse/"), key, _server, parallel, _log.Add)
        {
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Attempts = 4,
            FileAttempts = 4,
            QueuePoll = TimeSpan.FromMilliseconds(1),
        };

    static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    static string Md5(byte[] b) => Convert.ToHexString(MD5.HashData(b)).ToLowerInvariant();
    string Cache => Path.Combine(_dir, "server-manifests");

    [Fact]
    public async Task Manifests_are_fetched_once_and_kept_until_the_server_changes_them()
    {
        _server.Manifests["moviemanifest_kor"] = B("movie_op,aaaa,movie,1,aaaa,1");
        _server.Manifests["card_assetmanifest"] = B("card_1,bbbb,card,1,bbbb,1");

        var first = await Client().LoadManifests(Cache);
        Assert.NotNull(first);
        Assert.Equal(B("movie_op,aaaa,movie,1,aaaa,1"), first!["moviemanifest_kor"]);
        Assert.Equal(2, _server.Requests.Count(r => r.StartsWith("/v1/assets/manifests/")));

        _server.Requests.Clear();
        _server.Manifests["card_assetmanifest"] = B("card_1,cccc,card,1,cccc,1");
        var second = await Client().LoadManifests(Cache);
        Assert.Equal(B("card_1,cccc,card,1,cccc,1"), second!["card_assetmanifest"]);
        // only the changed one was downloaded again
        Assert.Equal(["/v1/assets/manifests/card_assetmanifest"],
            _server.Requests.Where(r => r.StartsWith("/v1/assets/manifests/")).ToArray());
    }

    [Fact]
    public async Task A_server_without_game_files_or_out_of_reach_gives_none()
    {
        _server.NotConfigured = true;
        Assert.Null(await Client().LoadManifests(Cache));

        _server.NotConfigured = false;
        _server.Down = true;
        Assert.Null(await Client().LoadManifests(Cache));
        Assert.Contains(_log, l => l.Contains("giving up"));
    }

    [Fact]
    public async Task Names_that_would_leave_the_cache_folder_are_ignored()
    {
        _server.Manifests["ok_assetmanifest"] = B("x");
        _server.ExtraListNames = ["../evil", "a/b"];
        var got = await Client().LoadManifests(Cache);
        Assert.Equal(["ok_assetmanifest"], got!.Keys.ToArray());
        Assert.False(File.Exists(Path.Combine(_dir, "evil")));
    }

    [Fact]
    public async Task The_cafe_key_goes_with_every_request()
    {
        _server.RequiredCafeKey = "k";
        _server.Manifests["m"] = B("x");
        Assert.Null(await Client(key: "wrong").LoadManifests(Cache));
        Assert.NotNull(await Client(key: "k").LoadManifests(Cache));
    }

    static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    static byte[] Written(HttpContext ctx) => ((MemoryStream)ctx.Response.Body).ToArray();

    [Fact]
    public async Task A_missing_file_is_passed_through_from_the_server()
    {
        var bundle = B("a card bundle");
        _server.Files[Md5(bundle)] = bundle;
        var ctx = Context();
        Assert.True(await Client().Relay(Md5(bundle), ctx.Response, CancellationToken.None));
        Assert.Equal(bundle, Written(ctx));
        Assert.Equal(bundle.Length, ctx.Response.ContentLength);

        var none = Context();
        Assert.False(await Client().Relay("0123456789abcdef0123456789abcdef", none.Response, CancellationToken.None));
        Assert.Empty(Written(none));
        Assert.False(await Client().Relay("not-a-hash", none.Response, CancellationToken.None));
    }

    [Fact]
    public async Task A_busy_server_is_asked_again()
    {
        var bundle = B("voice");
        _server.Files[Md5(bundle)] = bundle;
        _server.BusyFor = 2;
        var ctx = Context();
        Assert.True(await Client().Relay(Md5(bundle), ctx.Response, CancellationToken.None));
        Assert.Equal(bundle, Written(ctx));
        Assert.Equal(3, _server.Requests.Count);
    }

    [Fact]
    public async Task Waiting_in_the_servers_line_never_gives_up()
    {
        var bundle = B("a fresh install's card art");
        _server.Files[Md5(bundle)] = bundle;
        // far more queued answers than FileAttempts: the line is not a failure
        _server.QueuedFor = 25;
        var client = Client();
        var ctx = Context();
        Assert.True(await client.Relay(Md5(bundle), ctx.Response, CancellationToken.None));
        Assert.Equal(bundle, Written(ctx));
        // every request carried the same id, so the server keeps this PC's place
        Assert.Equal(26, _server.DownloadIds.Count);
        Assert.All(_server.DownloadIds, id => Assert.Equal(client.DownloadId, id));
        // the position is shown when it changes, not on every poll, and the turn is announced
        Assert.Equal(3, _log.Count(l => l.Contains("다운로드 대기 중")));
        Assert.Contains(_log, l => l.Contains("차례가 왔습니다"));
    }

    [Fact]
    public async Task A_server_that_stays_down_is_given_up_on()
    {
        var bundle = B("x");
        _server.Files[Md5(bundle)] = bundle;
        _server.BusyFor = 100;
        var ctx = Context();
        Assert.False(await Client().Relay(Md5(bundle), ctx.Response, CancellationToken.None));
        Assert.Contains(_log, l => l.Contains("giving up after 4 tries"));
    }

    [Fact]
    public async Task No_more_than_two_downloads_run_at_once()
    {
        var files = Enumerable.Range(0, 6).Select(i => B($"file {i}")).ToList();
        foreach (var f in files) _server.Files[Md5(f)] = f;
        _server.FileDelay = TimeSpan.FromMilliseconds(80);
        var client = Client();
        var results = await Task.WhenAll(files.Select(async f =>
        {
            var ctx = Context();
            var ok = await client.Relay(Md5(f), ctx.Response, CancellationToken.None);
            return ok && Written(ctx).SequenceEqual(f);
        }));
        Assert.All(results, Assert.True);
        Assert.Equal(2, _server.MaxInFlight);
    }

    [Fact]
    public void Server_manifests_point_at_local_files_where_this_pc_has_them()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        var local = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(_dir, "a", "card_here"), local);
        var index = ManifestIndex.Build(_dir, Path.Combine(_dir, "sidecar.csv"));

        var server = new Dictionary<string, byte[]>
        {
            ["card_assetmanifest"] = B("card_here,1111,card,1,1111,1\ncard_missing,2222,common,1,2222,1"),
            ["moviemanifest_kor"] = B("op,3333,movie,1,3333,1"),
        };
        var pcOnly = new Dictionary<string, byte[]> { ["only_here"] = B("x") };

        var merged = RemoteAssets.Merge(pcOnly, server, index, out var localRows);
        var rows = Encoding.UTF8.GetString(merged["card_assetmanifest"]).Split('\n');
        Assert.StartsWith($"card_here,{Md5(local)},card,", rows[0]);
        // not here: the server's row, untouched (not moved out of the pre-fetch category), so it is fetched from there
        Assert.Equal("card_missing,2222,common,1,2222,1", rows[1]);
        Assert.Equal(B("op,3333,movie,1,3333,1"), merged["moviemanifest_kor"]);
        Assert.Equal(B("x"), merged["only_here"]);
        Assert.Equal(1, localRows);

        var noFolder = RemoteAssets.Merge(new Dictionary<string, byte[]>(), server, null, out _);
        Assert.Equal(server["card_assetmanifest"], noFolder["card_assetmanifest"]);
    }

    static byte[] Gz(int rows)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionMode.Compress))
            z.Write(B(string.Join('\n', Enumerable.Range(0, rows).Select(i => $"{i},card"))));
        return ms.ToArray();
    }

    [Fact]
    public async Task The_card_master_is_downloaded_and_checked()
    {
        var dest = Path.Combine(_dir, "data", CardMasterFile.FileName);
        _server.CardMaster = Gz(150);
        _server.RequiredCafeKey = "k";

        Assert.Null(await CardMasterFile.Download("https://be.example/openverse", "nope", dest, _log.Add, _server));
        Assert.Contains(_log, l => l.Contains("cafe key"));
        Assert.False(File.Exists(dest));

        var csv = await CardMasterFile.Download("https://be.example/openverse/", "k", dest, _log.Add, _server);
        Assert.NotNull(csv);
        Assert.Equal(_server.CardMaster, File.ReadAllBytes(dest));
        Assert.Contains("/v1/card-master", _server.Requests);

        // something that is not a card master never replaces the one in place
        _server.CardMaster = B("<html>login</html>");
        Assert.Null(await CardMasterFile.Download("https://be.example/openverse", "k", dest, _log.Add, _server));
        Assert.Equal(Gz(150).Length, File.ReadAllBytes(dest).Length);
        Assert.False(File.Exists(dest + ".part"));

        _server.CardMaster = null;
        Assert.Null(await CardMasterFile.Download("https://be.example/openverse", "k", dest, _log.Add, _server));
        Assert.Contains(_log, l => l.Contains("no card master"));

        _server.Down = true;
        Assert.Null(await CardMasterFile.Download("https://be.example/openverse", "k", dest, _log.Add, _server));
    }

    [Fact]
    public void Setting_files_skip_comments_and_blanks()
    {
        File.WriteAllText(Path.Combine(_dir, "be_url.txt"), "# where\n\n  https://x.example/openverse  \n");
        Assert.Equal("https://x.example/openverse", SettingFile.Read(_dir, "be_url.txt"));
        File.WriteAllText(Path.Combine(_dir, "cafe_key.txt"), "# empty\n");
        Assert.Null(SettingFile.Read(_dir, "cafe_key.txt"));
        Assert.Null(SettingFile.Read(_dir, "missing.txt"));
    }
}
