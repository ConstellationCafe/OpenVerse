using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace OpenVerse.Installer;

// Programs and Features ("앱 및 기능") entry, and what removing it does. The entry is per user (HKCU), like the install
// itself, so registering needs no admin. Removing does need it only for what the launcher touches outside the user's
// folders (a hosts block and a trusted root certificate left behind by a launcher that did not exit cleanly, and the
// certificate file in ProgramData); that part runs in an elevated copy of this exe and is skipped if declined.
static class Uninstall
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OpenVerse";
    const string HostsMarker = "openverse";
    const string CertSubject = "CN=OpenVerse";

    // the uninstaller lives outside the install folder so it can remove that folder whole
    public static string UninstallerDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenVerse-Uninstall");

    static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    static string CertDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenVerse");

    public static void Register(string installDir, string version, string? shortcutDir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Directory.CreateDirectory(UninstallerDir);
            var self = Environment.ProcessPath!;
            var copy = Path.Combine(UninstallerDir, "OpenVerse-Installer.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, copy, overwrite: true);

            long kb = 0;
            try { kb = Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024; }
            catch { }

            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("DisplayName", "OpenVerse");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "ConstellationCafe");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("DisplayIcon", $"\"{copy}\",0");
            var extra = shortcutDir is null ? "" : $" --shortcut-dir \"{shortcutDir}\"";
            key.SetValue("UninstallString", $"\"{copy}\" --uninstall --dir \"{installDir}\"{extra}");
            key.SetValue("QuietUninstallString", $"\"{copy}\" --uninstall --yes --dir \"{installDir}\"{extra}");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(kb, int.MaxValue), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            Console.WriteLine("  '앱 및 기능'에 OpenVerse를 등록했습니다 (제거는 그곳에서).");
        }
        catch (Exception e) { Console.WriteLine($"  '앱 및 기능' 등록 실패: {e.Message}"); }
    }

    public static int Run(string installDir, bool yes, string? shortcutDir)
    {
        Console.WriteLine("OpenVerse 제거");
        Console.WriteLine($"설치 위치: {installDir}");
        Console.WriteLine();

        var running = new[] { "openverse-launcher", "OpenVerse.Api", "OpenVerse.Battle" }
            .Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
        if (running.Count > 0)
        {
            Console.WriteLine($"OpenVerse가 실행 중입니다 ({string.Join(", ", running)}). 게임과 런처를 닫고 다시 실행해 주세요.");
            return 1;
        }
        if (!yes && !Ask("OpenVerse를 제거할까요? [y/N] ", false)) { Console.WriteLine("취소했습니다."); return 0; }

        // 1. what the launcher left outside the user's folders (needs admin)
        if (NeedsSystemCleanup())
        {
            Console.WriteLine("hosts 파일과 인증서에 남은 OpenVerse 설정을 정리합니다 (관리자 권한 필요)...");
            if (IsAdmin()) SystemCleanup();
            else if (!RunElevated("--uninstall-system"))
                Console.WriteLine("  관리자 권한이 없어 건너뛰었습니다. hosts의 'openverse' 구간과 'OpenVerse' 루트 인증서를 직접 지워 주세요.");
        }
        else Console.WriteLine("hosts와 인증서는 이미 깨끗합니다.");

        // 2. the install folder and the desktop shortcut
        if (Directory.Exists(installDir))
        {
            try { Directory.Delete(installDir, recursive: true); Console.WriteLine($"설치 폴더를 지웠습니다: {installDir}"); }
            catch (Exception e) { Console.WriteLine($"설치 폴더를 지우지 못했습니다 ({e.Message}). 직접 지워 주세요: {installDir}"); }
        }
        var lnk = Path.Combine(shortcutDir ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OpenVerse.lnk");
        if (File.Exists(lnk)) { try { File.Delete(lnk); Console.WriteLine("바탕화면 바로가기를 지웠습니다."); } catch { } }

        // 3. OpenVerse's files inside the game's own data folder: the bundle index is only a cache, the decks are the
        //    player's, so those go only when asked
        var game = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Cygames", "Shadowverse");
        TryDelete(Path.Combine(game, "openverse-bundles.csv"));
        var decks = Path.Combine(game, "openverse.db");
        if (File.Exists(decks))
        {
            if (!yes && Ask("OpenVerse에서 만든 덱 기록(openverse.db)도 지울까요? 나중에 다시 설치하면 덱을 그대로 쓸 수 있습니다. [y/N] ", false))
            {
                TryDelete(decks);
                Console.WriteLine("덱 기록을 지웠습니다.");
            }
            else Console.WriteLine($"덱 기록은 남겨 두었습니다: {decks}");
        }

        // 4. the Programs and Features entry, and this uninstaller (after it exits)
        if (OperatingSystem.IsWindows())
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); } catch { }
            ScheduleSelfDelete();
        }
        Console.WriteLine();
        Console.WriteLine("제거가 끝났습니다.");
        return 0;
    }

    // the elevated half: hosts block, root certificate, certificate file
    public static int SystemOnly()
    {
        SystemCleanup();
        return 0;
    }

    static bool NeedsSystemCleanup()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { if (File.ReadAllText(HostsPath).Contains($"# >>> {HostsMarker} >>>")) return true; } catch { }
        if (File.Exists(HostsPath + ".openverse.bak")) return true;
        if (Directory.Exists(CertDir)) return true;
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Any(c => c.Subject == CertSubject);
        }
        catch { return false; }
    }

    static void SystemCleanup()
    {
        try
        {
            var text = File.ReadAllText(HostsPath);
            var cleaned = RemoveBlock(text);
            if (cleaned != text.Replace("\r\n", "\n"))
            {
                File.WriteAllText(HostsPath, cleaned, new UTF8Encoding(false));
                Console.WriteLine("  hosts에서 OpenVerse 구간을 지웠습니다.");
            }
            TryDelete(HostsPath + ".openverse.bak");
        }
        catch (Exception e) { Console.WriteLine($"  hosts 정리 실패: {e.Message}"); }

        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Where(c => c.Subject == CertSubject).ToList())
            {
                store.Remove(c);
                Console.WriteLine("  신뢰할 수 있는 루트에서 OpenVerse 인증서를 지웠습니다.");
            }
        }
        catch (Exception e) { Console.WriteLine($"  인증서 정리 실패: {e.Message}"); }

        if (Directory.Exists(CertDir))
        {
            try { Directory.Delete(CertDir, recursive: true); Console.WriteLine($"  {CertDir}를 지웠습니다."); }
            catch (Exception e) { Console.WriteLine($"  {CertDir} 삭제 실패: {e.Message}"); }
        }
    }

    // the same block the launcher writes (# >>> openverse >>> ... # <<< openverse <<<)
    static string RemoveBlock(string content)
    {
        var start = $"# >>> {HostsMarker} >>>";
        var end = $"# <<< {HostsMarker} <<<";
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

    static bool IsAdmin()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static bool RunElevated(string arg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arg) { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit();
            return p is { ExitCode: 0 };
        }
        catch { return false; }   // the UAC prompt was declined
    }

    static void ScheduleSelfDelete()
    {
        var self = Environment.ProcessPath!;
        if (!Path.GetFullPath(self).StartsWith(Path.GetFullPath(UninstallerDir), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"{UninstallerDir}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { }
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    static bool Ask(string prompt, bool defaultYes)
    {
        Console.Write(prompt);
        var a = Console.ReadLine()?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(a) ? defaultYes : a is "y" or "yes" or "ㅛ";
    }
}
