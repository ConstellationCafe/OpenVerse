using System.IO.Compression;
using System.Text;

namespace OpenVerse.Common;

// One-line settings beside the exe (be_url.txt, cafe_key.txt). Elevation goes through ShellExecute, which drops the
// environment, so a file survives where an environment variable would not
public static class SettingFile
{
    public static string? Read(string dir, string name)
    {
        var f = Path.Combine(dir, name);
        try
        {
            return File.Exists(f)
                ? File.ReadLines(f).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
                : null;
        }
        catch (IOException) { return null; }
    }
}

// card_master_full.csv.gz: a gzip'd CSV, the same for every player. Since the service ended a PC that never cached it
// cannot get one from the client, so OpenVerseBE hands out the one its battle server uses
public static class CardMasterFile
{
    public const string FileName = "card_master_full.csv.gz";

    // set to 1 to never ask the server (CI checks what happens without one)
    public const string NoServerEnv = "OPENVERSE_NO_SERVER_DOWNLOAD";

    public static bool ServerAllowed => Environment.GetEnvironmentVariable(NoServerEnv) != "1";

    // the CSV if this is a usable card master (more than a hundred rows), else null
    public static string? Read(string path, Action<string>? log = null)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            var csv = sr.ReadToEnd();
            if (csv.Split('\n').Length > 100) return csv;
            log?.Invoke($"card_master: {path} has too few rows to be a card master");
            return null;
        }
        catch (Exception e)
        {
            log?.Invoke($"card_master: {path} is not a usable card master ({e.Message})");
            return null;
        }
    }

    // Fetches GET {beUrl}/v1/card-master into dest, replacing it only once the download is checked. The CSV, or null
    public static async Task<string?> Download(string beUrl, string? cafeKey, string dest, Action<string> log,
        HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var part = dest + ".part";
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = timeout ?? TimeSpan.FromMinutes(2);
            using var req = new HttpRequestMessage(HttpMethod.Get, beUrl.TrimEnd('/') + "/v1/card-master");
            if (!string.IsNullOrEmpty(cafeKey)) req.Headers.Add("X-OV-Cafe-Key", cafeKey);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode)
            {
                log(res.StatusCode switch
                {
                    System.Net.HttpStatusCode.Forbidden => "card_master: the server refused the cafe key (check cafe_key.txt)",
                    System.Net.HttpStatusCode.NotFound => "card_master: the server has no card master to hand out",
                    _ => $"card_master: the server answered {(int)res.StatusCode}",
                });
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            await using (var fs = File.Create(part))
                await res.Content.CopyToAsync(fs);
            var csv = Read(part, log);
            if (csv is null) return null;
            File.Move(part, dest, overwrite: true);
            log($"card_master: downloaded from {beUrl} -> {dest}");
            return csv;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UriFormatException
                                       or InvalidOperationException)
        {
            log($"card_master: could not download from {beUrl} ({e.Message})");
            return null;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
        }
    }
}
