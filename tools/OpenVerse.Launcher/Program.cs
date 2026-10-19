using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using OpenVerse.Common;

namespace OpenVerse.Launcher;

enum Args { help, client }

static class Program
{
    const string Marker = "openverse";
    const string SteamAppId = "453480";
    const string CertPassword = "openverse";
    static readonly string[] RedirectHosts = ["utoongaize.shadowverse.jp", "shadowverse.akamaized.net"];

    static int Main(string[] args)
    {
        CmdHelper.RegisterArg(Args.help, new CommandExplanation(
            "print this usage text", "help", "-h")
        { TakeValue = false });
        CmdHelper.RegisterArg(Args.client, new CommandExplanation(
            "the Shadowverse client's data folder (card_master cache, decks); default: %UserProfile%\\AppData\\LocalLow\\Cygames\\Shadowverse", "client"));

        if (CmdHelper.HasFlag(args, Args.help))
        {
            Console.WriteLine(CmdHelper.GenerateMan());
            return 0;
        }

        // Root is what a person edits and keeps, Server is what the build produced. running from release/ puts the
        // first beside the repo rather than inside a directory the next build deletes
        var baseDir = Layout.Root;
        // be_url.txt names the OpenVerseBE server rooms are matched on. without it rooms stay on this machine
        var beUrl = ReadSetting(baseDir, "be_url.txt");
        var cafeKey = ReadSetting(baseDir, "cafe_key.txt");

        if (!IsAdmin())
        {
            Console.WriteLine("elevating (hosts + cert need admin)...");
            return RelaunchAsAdmin(args) ? 0 : 1;
        }

        try
        {
            // One file per run, kept forever: a desync is usually noticed after the fact, and two rotating slots meant
            // the run that showed it was gone by the time anyone looked. Build-release clears the directory when the
            // build actually changed, so old logs never pile up against a server they no longer describe
            var logDir = Layout.InRoot("openverse_log");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"openverse_{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var logStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            Console.WriteLine($"log: {logPath}");
            var tee = new TeeWriter(Console.Out, new StreamWriter(logStream, new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetOut(tee);
            Console.SetError(tee);
        }
        catch { }

        return Run(baseDir, args, beUrl, cafeKey);
    }

    sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override Encoding Encoding => a.Encoding;
        public override void Write(char c) { a.Write(c); b.Write(c); }
        public override void Write(string? s) { a.Write(s); b.Write(s); }
        public override void WriteLine(string? s) { a.WriteLine(s); b.WriteLine(s); }
        public override void Flush() { a.Flush(); b.Flush(); }
    }

    // The game's own data folder (where the card-master cache lives). Decks go here rather than beside the exe so that
    // replacing the OpenVerse folder with a new build never touches them - the same reason the cert lives outside it
    static string GameDataDir(string[] args) =>
        CmdHelper.ReadArg(args, Args.client)
        ?? Environment.GetEnvironmentVariable("OPENVERSE_CLIENT_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "AppData", "LocalLow", "Cygames", "Shadowverse");

    static string ResolveDeckDb(string baseDir, string[] args)
    {
        var explicitPath = Environment.GetEnvironmentVariable("OPENVERSE_DECK_DB");
        if (explicitPath is not null) return explicitPath;

        var gameDir = GameDataDir(args);
        var target = Path.Combine(gameDir, "openverse.db");
        var legacy = Path.Combine(baseDir, "openverse.db");
        try
        {
            if (!Directory.Exists(gameDir)) Directory.CreateDirectory(gameDir);
            if (!File.Exists(target) && File.Exists(legacy))
            {
                File.Copy(legacy, target);
                Console.WriteLine($"moved your decks into the game folder: {target}");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"deck db: using the OpenVerse folder ({e.Message})");
            return legacy;
        }
        return target;
    }

    // Every player runs their own API on this machine (the card master, assets and decks are their own client's).
    // Centrally only room matching leaves the machine, to OpenVerseBE, and battles run on its battle server; standalone
    // the battle server runs here too
    static int Run(string baseDir, string[] args, string? beUrl, string? cafeKey)
    {
        // machine-wide (not per-user) so elevation resolves the same file regardless of account, and it survives a rebuild
        var certPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenVerse", "openverse.pfx");

        var deckDb = ResolveDeckDb(baseDir, args);

        var cardMaster = Layout.InServer("data", "card_master_full.csv.gz");
        if (!File.Exists(cardMaster))
        {
            Console.Error.WriteLine($"card_master not found: {cardMaster}");
            Console.Error.WriteLine("Run openverse-setup first.");
            return 1;
        }

        if (beUrl is not null) Console.WriteLine($"matching on {beUrl}{(cafeKey is null ? "" : " (cafe key set)")}");
        else Console.WriteLine("no be_url.txt: rooms and battles stay on this machine");

        Process? server = null, battle = null;
        var hostsEdited = false;
        X509Certificate2? cert = null;
        try
        {
            cert = CertGen.EnsureSelfSigned(certPath, CertPassword);
            InstallCert(cert);
            EditHosts();
            hostsEdited = true;
            if (beUrl is null) battle = StartBattle(baseDir, deckDb);
            server = StartServer(baseDir, beUrl, cafeKey, certPath, deckDb);
            Console.WriteLine(beUrl is null ? "server up on 80/443, battle on 3001." : "server up on 80/443.");
            Process.Start(new ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
            Console.WriteLine("launched Shadowverse. close the game to stop.");
            WaitForGameExit();
        }
        finally
        {
            if (hostsEdited) RestoreHosts();
            if (cert != null) RemoveCert(cert);
            if (server is { HasExited: false }) server.Kill(entireProcessTree: true);
            if (battle is { HasExited: false }) battle.Kill(entireProcessTree: true);
            cert?.Dispose();
            Console.WriteLine("stopped. hosts restored.");
        }
        return 0;
    }

    // elevation goes through ShellExecute, which drops the environment, so a setting given as an env var never reaches
    // the elevated run. One line in a file next to the exe survives and works from a double-click
    static string? ReadSetting(string baseDir, string name)
    {
        var f = Path.Combine(baseDir, name);
        return File.Exists(f)
            ? File.ReadLines(f).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'))
            : null;
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static bool RelaunchAsAdmin(string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args))
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try { Process.Start(psi); return true; }
        catch { Console.Error.WriteLine("elevation cancelled."); return false; }
    }

    static void InstallCert(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        if (!store.Certificates.Contains(cert))
        {
            store.Add(cert);
            Console.WriteLine("cert trusted.");
        }
    }

    static void RemoveCert(X509Certificate2 cert)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Contains(cert)) store.Remove(cert);
        }
        catch { /* leave it if removal fails */ }
    }

    static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    static string BackupPath => HostsPath + ".openverse.bak";

    static void EditHosts()
    {
        var text = File.ReadAllText(HostsPath);
        if (!File.Exists(BackupPath)) File.Copy(HostsPath, BackupPath);
        File.WriteAllText(HostsPath, AddBlock(text, RedirectHosts), new UTF8Encoding(false));
        Console.WriteLine("hosts redirected to 127.0.0.1.");
    }

    static void RestoreHosts()
    {
        try
        {
            if (File.Exists(BackupPath))
            {
                File.Copy(BackupPath, HostsPath, overwrite: true);
                File.Delete(BackupPath);
            }
            else
            {
                File.WriteAllText(HostsPath, RemoveBlock(File.ReadAllText(HostsPath)), new UTF8Encoding(false));
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"hosts restore failed: {e.Message}"); }
    }

    static string AddBlock(string content, IEnumerable<string> hosts)
    {
        // both redirected names still have AAAA records, so an IPv4-only entry is no redirect on a machine with working
        // IPv6: the A lookup is answered here, the AAAA lookup falls through to real DNS and reaches the dead official
        // server. Pin v6 alongside
        var sb = new StringBuilder(RemoveBlock(content).TrimEnd('\r', '\n'));
        sb.Append('\n').Append($"# >>> {Marker} >>>\n");
        foreach (var h in hosts)
        {
            sb.Append("127.0.0.1 ").Append(h).Append('\n');
            sb.Append("::1 ").Append(h).Append('\n');
        }
        sb.Append($"# <<< {Marker} <<<\n");
        return sb.ToString();
    }

    static string RemoveBlock(string content)
    {
        var start = $"# >>> {Marker} >>>";
        var end = $"# <<< {Marker} <<<";
        var kept = new List<string>();
        var skip = false;
        foreach (var l in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (l.Trim() == start) { skip = true; continue; }
            if (l.Trim() == end) { skip = false; continue; }
            if (!skip) kept.Add(l);
        }
        return string.Join('\n', kept);
    }

    static Process StartServer(string baseDir, string? beUrl, string? cafeKey, string certPath, string deckDb)
    {
        var overridePath = Environment.GetEnvironmentVariable("OPENVERSE_SERVER");
        var exe = Layout.InServer("OpenVerse.Api.exe");
        ProcessStartInfo psi;
        if (overridePath is { } o)  // dev: a .dll runs via dotnet, an .exe directly
            psi = o.EndsWith(".dll") ? new ProcessStartInfo("dotnet", $"\"{o}\"") : new ProcessStartInfo(o);
        else if (File.Exists(exe))
            psi = new ProcessStartInfo(exe);
        else
            throw new FileNotFoundException(
                $"OpenVerse.Api.exe not found next to the launcher ({baseDir}). Run from the full release folder.");
        psi.Environment["OPENVERSE_LISTEN"] = "1";
        psi.Environment["OPENVERSE_CERT"] = certPath;
        psi.Environment["OPENVERSE_DECK_DB"] = deckDb;
        // central: OpenVerseBE hands out the battle server address. standalone: the default 127.0.0.1:3001 is this machine
        if (beUrl is not null) psi.Environment["OPENVERSE_BE_URL"] = beUrl;
        if (cafeKey is not null) psi.Environment["OPENVERSE_CAFE_KEY"] = cafeKey;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Layout.Server;
        return StartWithTee(psi);
    }

    static Process StartBattle(string baseDir, string deckDb)
    {
        var exe = Layout.InServer("OpenVerse.Battle.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"OpenVerse.Battle.exe not found next to the launcher ({baseDir}). Run from the full release folder.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Layout.Server };
        psi.Environment["ASPNETCORE_URLS"] = "http://0.0.0.0:3001";
        psi.Environment["OPENVERSE_DECK_DB"] = deckDb;
        // ships with the build rather than being edited per machine, so it sits with the servers, not with join_host.txt
        if (ReadSetting(Layout.Server, "engine.txt") is { } role)
        {
            psi.Environment["OPENVERSE_ENGINE_ROLE"] = role;
            Console.WriteLine($"engine role: {role} (from engine.txt)");
        }
        return StartWithTee(psi);
    }

    static Process StartWithTee(ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("start failed");
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    static void WaitForGameExit()
    {
        for (var i = 0; i < 30; i++)
        {
            var p = Process.GetProcessesByName("Shadowverse").FirstOrDefault();
            if (p != null) { p.WaitForExit(); return; }
            Thread.Sleep(1000);
        }
        Console.WriteLine("(game not detected. press Enter to stop)");
        Console.ReadLine();
    }
}
