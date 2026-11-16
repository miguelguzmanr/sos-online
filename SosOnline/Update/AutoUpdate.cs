using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using SosOnline.UI;
using UnityEngine;

namespace SosOnline.Update;

/// <summary>
/// Automatic updates from the project's GitHub releases.
///
/// At game start (in the background) the latest release is read from GitHub. If it is a newer build, its zip is
/// downloaded and the DLL inside is installed only if its signature (<c>SosOnline.dll.sig</c>, ECDSA P-256 over SHA-256)
/// matches <see cref="PublicKey"/>, the key whose private half only the maintainer has, and the DLL itself reports
/// a newer version. The running DLL is renamed to <c>SosOnline.dll.old</c> (Windows allows renaming a loaded file)
/// and the new one takes its place: the next game start runs it. Nothing is ever loaded from the download in this
/// session.
///
/// Off with <c>AutoUpdate = false</c> in <c>BepInEx\config\sosonline.coop.cfg</c>; never for a locally modified
/// build or a test run.
/// </summary>
internal static class AutoUpdate
{
    private const string Repository = "miguelguzmanr/sosonline";
    private const string LatestRelease = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>SubjectPublicKeyInfo of the release signing key (tools\new-signing-key.ps1).</summary>
    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEynG2hD1g0qujrTCpsxX31J+B11LrW2eN3XX1m9NTd0YQzHRq4mSW19bO6yUV1IeNC9E1Vh6SmbX+egzSzB/t1g==";

    private const long MaxDownloadBytes = 20 * 1024 * 1024;
    private const string DllName = "SosOnline.dll";

    /// <summary>Messages for the main thread (toasts aren't thread-safe).</summary>
    private static readonly ConcurrentQueue<string> Notices = new();

    public static Version Current => typeof(AutoUpdate).Assembly.GetName().Version;

    /// <summary>"Update 0.1.40 is ready…" once one was installed this session, otherwise null.</summary>
    public static string Installed { get; private set; }

    /// <summary>The version installed for the next start this session, or null. Read on the main thread only.</summary>
    public static Version Downloaded => _downloaded;
    private static volatile Version _downloaded;

    /// <summary>Test harness (--sosonline-fakeupdate=V): act as if V had been downloaded.</summary>
    internal static void FakeDownloaded(Version v) => _downloaded = v;

    /// <summary>"Restart to update (0.1.39 -> 0.1.45)", for the Online menus (the game's menu font has no "→").</summary>
    public static string RestartLabel => _downloaded == null ? null : $"Restart to update ({Current.ToString(3)} -> {_downloaded.ToString(3)})";

    /// <summary>
    /// Quits the game and starts it again through Steam, so the downloaded build runs. A hidden PowerShell waits for
    /// this process to exit (Steam won't start a game it still sees running), then asks Steam to launch the game.
    /// Leaves the co-op session first.
    /// </summary>
    public static void Restart()
    {
        if (_downloaded == null) return;
        Plugin.Log.LogInfo($"[update] restarting the game for {_downloaded.ToString(3)}");
        try { Net.Session.Instance.Leave(); } catch { }
        try
        {
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            string script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; Start-Process 'steam://rungameid/{Steam.SteamApi.AppId}'";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell, $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            // The game still quits: the update runs from the next start either way.
            Plugin.Log.LogWarning($"[update] could not schedule the relaunch: {e.Message}");
        }
        Application.Quit();
    }

    public static void Start()
    {
        string dir = PluginDir();
        if (dir == null) return;
        // Left by the previous update; not loaded any more.
        TryDelete(Path.Combine(dir, DllName + ".old"));
        TryDelete(Path.Combine(dir, DllName + ".new"));

        if (!Settings.AutoUpdate) { Plugin.Log.LogInfo("[update] automatic updates are off"); return; }
        if (SessionLogs.Build.Contains("-modified")) { Plugin.Log.LogInfo("[update] locally modified build: no automatic update"); return; }
        if (TestHarness.HasArgs) return;
        Task.Run(CheckAsync);
    }

    /// <summary>Main thread, every frame.</summary>
    public static void Update()
    {
        while (Notices.TryDequeue(out var message)) Toast.Show(message);
    }

    private static async Task CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SosOnline-updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync(LatestRelease);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { Plugin.Log.LogInfo("[update] no release published yet"); return; }
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = release.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) { Plugin.Log.LogWarning($"[update] unexpected release tag '{tag}'"); return; }
            var current = Current;
            if (Normalize(latest) <= Normalize(current)) { Plugin.Log.LogInfo($"[update] up to date ({current.ToString(3)}; latest release {tag})"); return; }

            string url = null;
            long size = 0;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                url = asset.GetProperty("browser_download_url").GetString();
                size = asset.GetProperty("size").GetInt64();
                break;
            }
            if (url == null || size > MaxDownloadBytes) { Plugin.Log.LogWarning($"[update] release {tag} has no usable zip"); return; }

            Plugin.Log.LogInfo($"[update] {current.ToString(3)} → {tag}: downloading");
            byte[] zipBytes = await http.GetByteArrayAsync(url);
            if (zipBytes.Length > MaxDownloadBytes) return;

            byte[] dll = null, sig = null;
            using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
                foreach (var entry in zip.Entries)
                {
                    // Zips made by Windows PowerShell 5 use backslashes.
                    string path = entry.FullName.Replace('\\', '/');
                    if (path.EndsWith("/" + DllName, StringComparison.OrdinalIgnoreCase)) dll = Read(entry);
                    else if (path.EndsWith("/" + DllName + ".sig", StringComparison.OrdinalIgnoreCase)) sig = Read(entry);
                }
            if (dll == null || sig == null) { Plugin.Log.LogWarning($"[update] release {tag}: DLL or signature missing; not installed"); return; }

            using (var key = ECDsa.Create())
            {
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
                if (!key.VerifyData(dll, sig, HashAlgorithmName.SHA256))
                {
                    Plugin.Log.LogWarning($"[update] release {tag}: signature check failed; not installed");
                    return;
                }
            }

            Install(dll, current, tag);
        }
        catch (Exception e)
        {
            // Offline, GitHub unreachable or rate-limited: try again at the next start.
            Plugin.Log.LogWarning($"[update] check failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Install(byte[] dll, Version current, string tag)
    {
        string dir = PluginDir();
        if (dir == null) return;
        string target = Path.Combine(dir, DllName), staged = target + ".new", old = target + ".old";

        File.WriteAllBytes(staged, dll);
        var name = AssemblyName.GetAssemblyName(staged);
        if (name.Name != "SosOnline" || name.Version == null || Normalize(name.Version) <= Normalize(current))
        {
            // Signed, but not newer (an old release served again): never go back.
            TryDelete(staged);
            Plugin.Log.LogWarning($"[update] release {tag} contains {name.Name} {name.Version}; not newer, not installed");
            return;
        }

        File.Move(target, old, true);
        try { File.Move(staged, target); }
        catch
        {
            File.Move(old, target, true); // put the running build back
            throw;
        }
        Installed = $"Update {name.Version.ToString(3)} downloaded. Restart from the Online menu.";
        _downloaded = name.Version;
        Plugin.Log.LogInfo($"[update] installed {name.Version.ToString(3)}; it runs from the next start");
        Notices.Enqueue(Installed);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static byte[] Read(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxDownloadBytes) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string PluginDir()
    {
        try
        {
            string location = typeof(AutoUpdate).Assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
