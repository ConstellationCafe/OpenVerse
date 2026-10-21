using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OpenVerse.Installer;

// One exe a player downloads from Releases: fetches the newest OpenVerse.zip, unpacks it into one folder, gets the
// card master in place, and puts a shortcut on the desktop. Running it again updates in place and keeps the player's
// card master and settings. It installs per user (%LocalAppData%), so it needs no admin rights itself; the launcher
// still asks for them each time it runs, for hosts and the certificate.
static class Program
{
    const string DefaultRepo = "ConstellationCafe/OpenVerse";
    const string ZipAsset = "OpenVerse.zip";
    const string CardMaster = "card_master_full.csv.gz";
    const string VersionFile = "installed-version.txt";

    sealed record Options(
        string Dir, string Repo, string? Zip, string Tag, string? CardMasterPath,
        bool Force, bool Yes, bool NoShortcut, string? ShortcutDir);

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        // removal, from '앱 및 기능' (the copy registered there runs with --uninstall) or by hand
        if (args.Contains("--uninstall-system")) return Uninstall.SystemOnly();
        if (args.Contains("--uninstall"))
        {
            Options u;
            try { u = Parse(args); }
            catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }
            int code;
            try { code = Uninstall.Run(u.Dir, u.Yes, u.ShortcutDir); }
            catch (Exception e) { Console.WriteLine($"제거에 실패했습니다: {e.Message}"); code = 1; }
            if (!u.Yes) Pause();
            return code;
        }

        Options o;
        try { o = Parse(args); }
        catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 1; }

        Console.WriteLine("OpenVerse 설치 / 업데이트");
        Console.WriteLine($"설치 위치: {o.Dir}");
        Console.WriteLine();

        try
        {
            var code = Run(o);
            if (!o.Yes) Pause();
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine();
            Console.WriteLine($"설치에 실패했습니다: {e.Message}");
            if (!o.Yes) Pause();
            return 1;
        }
    }

    static int Run(Options o)
    {
        var running = new[] { "openverse-launcher", "OpenVerse.Api", "OpenVerse.Battle" }
            .Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
        if (running.Count > 0)
            throw new InvalidOperationException(
                $"OpenVerse가 실행 중입니다 ({string.Join(", ", running)}). 게임과 런처를 닫고 다시 실행해 주세요.");

        // 1. what to install
        string tag;
        string zipPath;
        string? downloaded = null;
        if (o.Zip is not null)
        {
            tag = o.Tag;
            zipPath = Path.GetFullPath(o.Zip);
            if (!File.Exists(zipPath)) throw new FileNotFoundException($"zip이 없습니다: {zipPath}");
        }
        else
        {
            Console.WriteLine("최신 버전 확인 중...");
            var (latestTag, url) = LatestRelease(o.Repo);
            tag = latestTag;
            Console.WriteLine($"  최신 버전: {tag}");
            var installed = InstalledVersion(o.Dir);
            if (installed == tag && !o.Force && IsInstalled(o.Dir))
            {
                Console.WriteLine($"  이미 최신 버전입니다 ({installed}).");
                zipPath = "";
            }
            else
            {
                downloaded = Path.Combine(Path.GetTempPath(), $"openverse-{Guid.NewGuid():N}.zip");
                Download(url, downloaded);
                zipPath = downloaded;
            }
        }

        try
        {
            // 2. unpack (keeping what the player put there)
            if (zipPath.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("파일 설치 중...");
                Install(zipPath, o.Dir);
                File.WriteAllText(Path.Combine(o.Dir, VersionFile), tag);
                Console.WriteLine($"  설치 완료 ({tag})");
            }
        }
        finally
        {
            if (downloaded is not null) try { File.Delete(downloaded); } catch { }
        }

        // 3. '앱 및 기능' entry, so it can be removed like any other program
        Uninstall.Register(o.Dir, tag, o.NoShortcut ? null : o.ShortcutDir);

        // 4. card master
        Console.WriteLine();
        var haveMaster = EnsureCardMaster(o);

        // 5. shortcut
        if (!o.NoShortcut && OperatingSystem.IsWindows())
        {
            var lnk = CreateShortcut(o.Dir, o.ShortcutDir);
            if (lnk is not null) Console.WriteLine($"바탕화면 바로가기를 만들었습니다: {lnk}");
        }

        Console.WriteLine();
        if (!haveMaster)
        {
            Console.WriteLine("카드 마스터가 없어서 아직 플레이할 수 없습니다.");
            Console.WriteLine($"인터넷 연결을 확인하고 인스톨러를 다시 실행하거나, 카페에서 받은 {CardMaster} 파일을 아래 폴더에 넣으세요.");
            Console.WriteLine($"  {Path.Combine(o.Dir, "server", "data")}");
            return 2;
        }

        Console.WriteLine("설치가 끝났습니다. 바탕화면의 OpenVerse 바로가기로 실행하세요.");
        Console.WriteLine("(실행할 때 뜨는 관리자 권한 창은 '예'를 눌러야 합니다)");
        if (!o.Yes && OperatingSystem.IsWindows() && AskYesNo("지금 실행할까요? [Y/n] ", true))
            Process.Start(new ProcessStartInfo(Path.Combine(o.Dir, "openverse-launcher.exe"))
            { UseShellExecute = true, WorkingDirectory = o.Dir });
        return 0;
    }

    // ---------- release lookup / download ----------

    static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenVerse-Installer");
        // only CI sets this: unauthenticated GitHub API calls are rate-limited per IP, and runners share IPs
        if (Environment.GetEnvironmentVariable("OPENVERSE_GITHUB_TOKEN") is { Length: > 0 } token)
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    static (string Tag, string Url) LatestRelease(string repo)
    {
        using var http = Http();
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var res = http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest").GetAwaiter().GetResult();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub에서 최신 버전 정보를 받지 못했습니다 (HTTP {(int)res.StatusCode}). 인터넷 연결을 확인해 주세요.");
        using var doc = JsonDocument.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "unknown";
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString() == ZipAsset)
                return (tag, a.GetProperty("browser_download_url").GetString()!);
        throw new InvalidOperationException($"최신 릴리스({tag})에 {ZipAsset}이 없습니다.");
    }

    static void Download(string url, string to)
    {
        using var http = Http();
        using var res = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"다운로드 실패 (HTTP {(int)res.StatusCode})");
        var total = res.Content.Headers.ContentLength;
        using var src = res.Content.ReadAsStream();
        using var dst = File.Create(to);
        var buf = new byte[1 << 16];
        long done = 0;
        var lastShown = -1;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            dst.Write(buf, 0, n);
            done += n;
            if (total is > 0)
            {
                var pct = (int)(done * 100 / total.Value);
                if (pct / 5 != lastShown / 5)
                {
                    lastShown = pct;
                    Console.Write($"\r  다운로드 중... {pct,3}% ({done / 1048576} / {total.Value / 1048576} MB)");
                }
            }
        }
        Console.WriteLine();
    }

    // ---------- install ----------

    static bool IsInstalled(string dir) =>
        File.Exists(Path.Combine(dir, "openverse-launcher.exe")) && File.Exists(Path.Combine(dir, "server", "OpenVerse.Api.exe"));

    static string? InstalledVersion(string dir)
    {
        var f = Path.Combine(dir, VersionFile);
        return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
    }

    // Files the player owns: kept across updates. be_url.txt is NOT one of them, it follows the release
    static readonly string[] KeepIfFilled = ["username.txt", "cafe_key.txt"];

    static void Install(string zipPath, string dir)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"openverse-stage-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging);
            if (!File.Exists(Path.Combine(staging, "openverse-launcher.exe")) ||
                !File.Exists(Path.Combine(staging, "server", "OpenVerse.Api.exe")))
                throw new InvalidOperationException("받은 zip이 OpenVerse 배포본이 아닙니다.");

            Directory.CreateDirectory(dir);

            // what the player has, before the server folder is replaced
            var master = Path.Combine(dir, "server", "data", CardMaster);
            byte[]? keptMaster = File.Exists(master) ? File.ReadAllBytes(master) : null;
            var keptSettings = KeepIfFilled
                .Select(f => (f, path: Path.Combine(dir, f)))
                .Where(x => File.Exists(x.path) && HasValue(x.path))
                .ToDictionary(x => x.f, x => File.ReadAllText(x.path));

            // the old server folder goes as a whole, so no stale files from a previous build stay behind
            var server = Path.Combine(dir, "server");
            if (Directory.Exists(server))
            {
                try { Directory.Delete(server, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"이전 버전 파일을 지울 수 없습니다. OpenVerse를 모두 닫고 다시 실행해 주세요. ({e.Message})");
                }
            }

            CopyTree(staging, dir);

            if (keptMaster is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(master)!);
                File.WriteAllBytes(master, keptMaster);
                Console.WriteLine("  기존 카드 마스터를 유지했습니다.");
            }
            foreach (var (f, text) in keptSettings)
            {
                File.WriteAllText(Path.Combine(dir, f), text);
                Console.WriteLine($"  기존 {f} 설정을 유지했습니다.");
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    static void CopyTree(string from, string to)
    {
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }

    // a setting file counts as filled in when it has a line that is not a # comment
    static bool HasValue(string path) =>
        File.ReadLines(path).Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));

    // ---------- card master ----------

    static bool EnsureCardMaster(Options o)
    {
        var dataDir = Path.Combine(o.Dir, "server", "data");
        var target = Path.Combine(dataDir, CardMaster);
        Console.WriteLine("카드 마스터 확인 중...");

        if (o.CardMasterPath is not null)
        {
            Place(o.CardMasterPath, target);
            Console.WriteLine($"  지정한 파일을 사용했습니다: {o.CardMasterPath}");
            return true;
        }
        if (IsCardMaster(target))
        {
            Console.WriteLine("  이미 있습니다.");
            return true;
        }

        // the player's own client cache, else the OpenVerse server (be_url.txt), through the setup that ships in the zip
        var setup = Path.Combine(o.Dir, "openverse-setup.exe");
        if (OperatingSystem.IsWindows() && File.Exists(setup))
        {
            Console.WriteLine("  셰도우버스 클라이언트 캐시에서 추출하고, 없으면 OpenVerse 서버에서 받습니다...");
            try
            {
                using var p = Process.Start(new ProcessStartInfo(setup, "--quiet")
                { UseShellExecute = false, WorkingDirectory = o.Dir })!;
                p.WaitForExit();
            }
            catch (Exception e) { Console.WriteLine($"  setup 실행 실패: {e.Message}"); }
            if (IsCardMaster(target))
            {
                Console.WriteLine("  준비했습니다.");
                return true;
            }
        }

        // no cache (this PC never played before the service ended, or OpenVerse already overwrote it) and the server
        // could not be reached or has none
        Console.WriteLine();
        Console.WriteLine("  이 PC에는 셰도우버스 카드 데이터 캐시가 없고, OpenVerse 서버에서도 받지 못했습니다.");
        Console.WriteLine("  (인터넷 연결과 cafe_key.txt를 확인하고 인스톨러를 다시 실행해도 됩니다)");
        Console.WriteLine($"  카페에서 받은 {CardMaster} 파일을 선택해 주세요.");
        if (o.Yes) return false;

        while (true)
        {
            var picked = PickFile();
            if (picked is null)
            {
                Console.WriteLine("  선택하지 않았습니다.");
                return false;
            }
            try
            {
                Place(picked, target);
                Console.WriteLine($"  넣었습니다: {picked}");
                return true;
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine($"  {e.Message}");
                if (!AskYesNo("  다른 파일을 고를까요? [Y/n] ", true)) return false;
            }
        }
    }

    static void Place(string source, string target)
    {
        if (!IsCardMaster(source))
            throw new InvalidOperationException($"카드 마스터 파일이 아닙니다: {source}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    // the same test setup uses: a gzip'd CSV with more than a hundred rows
    static bool IsCardMaster(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            var lines = 0;
            while (sr.ReadLine() is not null)
                if (++lines > 100) return true;
            return false;
        }
        catch { return false; }
    }

    static string? PickFile()
    {
        if (OperatingSystem.IsWindows())
        {
            const string script =
                "Add-Type -AssemblyName System.Windows.Forms;" +
                "$d = New-Object System.Windows.Forms.OpenFileDialog;" +
                "$d.Title = 'card_master_full.csv.gz 선택';" +
                "$d.Filter = 'card master (*.csv.gz)|*.csv.gz;*.gz|All files (*.*)|*.*';" +
                "if ($d.ShowDialog() -eq 'OK') { [Console]::OutputEncoding = [Text.Encoding]::UTF8; $d.FileName }";
            var picked = PowerShell(script);
            if (picked is not null) return picked.Length > 0 ? picked : null;
        }
        // no dialog (or it failed): a path typed or dragged into the console
        Console.Write("  파일 경로를 입력하거나 파일을 이 창에 끌어다 놓으세요 (취소: Enter): ");
        var line = Console.ReadLine()?.Trim().Trim('"');
        return string.IsNullOrEmpty(line) ? null : line;
    }

    // ---------- shortcut ----------

    static string? CreateShortcut(string dir, string? shortcutDir)
    {
        var desktop = shortcutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop)) return null;
        var lnk = Path.Combine(desktop, "OpenVerse.lnk");
        var target = Path.Combine(dir, "openverse-launcher.exe");
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script =
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({Q(lnk)});" +
            $"$s.TargetPath = {Q(target)}; $s.WorkingDirectory = {Q(dir)}; $s.IconLocation = {Q(target + ",0")};" +
            "$s.Save(); 'ok'";
        return PowerShell(script) == "ok" && File.Exists(lnk) ? lnk : null;
    }

    // ---------- helpers ----------

    // runs a Windows PowerShell script and returns its trimmed output, or null if it could not run
    static string? PowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-Command", script })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    static bool AskYesNo(string prompt, bool defaultYes)
    {
        Console.Write(prompt);
        var a = Console.ReadLine()?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(a) ? defaultYes : a is "y" or "yes" or "ㅛ";
    }

    static void Pause()
    {
        Console.WriteLine();
        Console.Write("Enter를 누르면 닫힙니다.");
        Console.ReadLine();
    }

    static Options Parse(string[] args)
    {
        string? Val(string name)
        {
            var i = Array.IndexOf(args, name);
            if (i < 0) return null;
            if (i + 1 >= args.Length) throw new ArgumentException($"{name} 다음에 값이 필요합니다");
            return args[i + 1];
        }
        var dir = Val("--dir") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVerse");
        // a card master dragged onto the installer arrives as the only argument
        var dropped = args.Length == 1 && args[0].EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? args[0] : null;
        return new Options(
            Dir: Path.GetFullPath(dir),
            Repo: Val("--repo") ?? DefaultRepo,
            Zip: Val("--zip"),
            Tag: Val("--tag") ?? "local",
            CardMasterPath: Val("--card-master") ?? dropped,
            Force: args.Contains("--force"),
            Yes: args.Contains("--yes"),
            NoShortcut: args.Contains("--no-shortcut"),
            ShortcutDir: Val("--shortcut-dir"));
    }
}
