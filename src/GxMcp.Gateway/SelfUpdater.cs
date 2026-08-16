using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    // Squirrel-style self-update for the corporate fixed-path install (the one the
    // npx `@latest` launcher can't auto-update). Two halves:
    //
    //   1. MaybeStageAsync — background download of the new publish.zip into a
    //      `.staged/` folder next to the install, verified by SHA-256.
    //   2. ApplyStagedUpdateOnStartup — at the next gateway launch, swap the staged
    //      files over the live ones. Windows won't let a running exe overwrite
    //      itself, so we rename the running gateway exe out of the way (allowed
    //      while running) and move the staged one in; the current session keeps
    //      running the old in-memory code and the NEXT launch is the new binary.
    //
    // Only activates for a MANAGED install (a version.txt sits next to the exe,
    // written by scripts/install.ps1). An npx-cache launch has no version.txt and
    // is skipped — npx already fetches @latest on each start. Disable entirely with
    // GENEXUS_MCP_NO_SELF_UPDATE=1. Everything is best-effort and fail-safe: any
    // error logs and leaves the install untouched (or rolls back) — it must never
    // crash the gateway or leave a half-applied state.
    internal static class SelfUpdater
    {
        private const string Repo = "lennix1337/Genexus18MCP";
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

        private static bool Disabled =>
            Environment.GetEnvironmentVariable("GENEXUS_MCP_NO_SELF_UPDATE") == "1";

        private static string InstallDir
        {
            get
            {
                try
                {
                    string? exe = Environment.ProcessPath
                        ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    string? dir = exe != null ? Path.GetDirectoryName(exe) : null;
                    return dir ?? AppContext.BaseDirectory;
                }
                catch { return AppContext.BaseDirectory; }
            }
        }

        // Renamed to GxMcp18.* so the running process is identifiable next to the GeneXus 16
        // server. Both names are honoured on purpose, and the direction matters: an install
        // sitting on the OLD name must still be able to update itself to a package built under
        // the NEW one. If this only knew the new name, every existing install would download the
        // update, fail to find its own binary, and strand the user on a manual reinstall.
        // Preference is new-then-legacy so a fresh install never picks up a stale leftover.
        private const string GatewayExeNameCurrent = "GxMcp18.Gateway.exe";
        private const string GatewayExeNameLegacy = "GxMcp.Gateway.exe";
        private const string WorkerExeNameCurrent = "GxMcp18.Worker.exe";
        private const string WorkerExeNameLegacy = "GxMcp.Worker.exe";

        private static string GatewayExeName => GatewayExeNameCurrent;

        /// <summary>First of <paramref name="candidates"/> that exists under <paramref name="dir"/>, else null.</summary>
        private static string? FirstExisting(string dir, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                try { if (File.Exists(Path.Combine(dir, c))) return c; } catch { }
            }
            return null;
        }
        private static string VersionFile => Path.Combine(InstallDir, "version.txt");
        private static string StagedDir => Path.Combine(InstallDir, ".staged");
        private static string StagedTmpDir => Path.Combine(InstallDir, ".staged.tmp");
        private static string StagedMarker => Path.Combine(StagedDir, "staged.json");
        private static string MutexName => @"Global\GenexusMCP-SelfUpdate-" + InstallDir.ToLowerInvariant().GetHashCode();

        // A managed install is one materialized by scripts/install.ps1 (version.txt
        // present next to the gateway exe). npx-cache launches have neither.
        public static bool IsManagedInstall()
        {
            // Either binary name counts. Checking only the current name would report every
            // install made before the GxMcp18.* rename as "unmanaged" and silently switch its
            // self-update off — the users who most need the update are exactly the ones on the
            // old name.
            try
            {
                return File.Exists(VersionFile)
                    && FirstExisting(InstallDir, GatewayExeNameCurrent, GatewayExeNameLegacy) != null;
            }
            catch { return false; }
        }

        private static string? CurrentInstalledVersion()
        {
            try { return File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim().TrimStart('v', 'V') : null; }
            catch { return null; }
        }

        private static string? StagedVersion()
        {
            try
            {
                if (!File.Exists(StagedMarker)) return null;
                var j = JObject.Parse(File.ReadAllText(StagedMarker));
                string? v = j["version"]?.ToString();
                return string.IsNullOrEmpty(v) ? null : v!.TrimStart('v', 'V');
            }
            catch { return null; }
        }

        /// <summary>
        /// For genexus_whoami.update — reports whether an update is staged and waiting
        /// for the next restart. Returns null when nothing is staged.
        /// </summary>
        public static JObject? GetStagedStatusSync()
        {
            try
            {
                if (!IsManagedInstall()) return null;
                string? staged = StagedVersion();
                if (string.IsNullOrEmpty(staged)) return null;
                return new JObject
                {
                    ["stagedVersion"] = staged,
                    ["appliesOn"] = "next restart",
                    ["managedInstall"] = true
                };
            }
            catch { return null; }
        }

        // ── Apply ────────────────────────────────────────────────────────────────

        public static void ApplyStagedUpdateOnStartup()
        {
            if (Disabled) return;
            try
            {
                if (!IsManagedInstall()) return;

                // Quick pre-check before taking the mutex (cheap early-out).
                string? staged = StagedVersion();
                if (string.IsNullOrEmpty(staged)) { SweepOld(InstallDir); return; }

                bool created;
                using var mutex = new Mutex(false, MutexName, out created);
                bool held = false;
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return; // another process is applying — let it
                try
                {
                    SweepOld(InstallDir); // clear *.old-* leftovers from a prior apply
                    if (ApplyStagedUpdate(InstallDir))
                    {
                        Program.Log($"[SelfUpdate] Applied staged update -> v{StagedVersionWasApplied}. Active session continues on the previous version; the new binary loads on next launch.");
                    }
                }
                finally { try { mutex.ReleaseMutex(); } catch { } }
            }
            catch (Exception ex)
            {
                Program.Log($"[SelfUpdate] apply skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Set by ApplyStagedUpdate for the startup log line; not load-bearing.
        private static string? StagedVersionWasApplied;

        // Testable core: gate on version + payload validity, then swap the files in
        // <installDir>/.staged over <installDir>. installDir is a parameter so this
        // can be exercised against a temp dir without touching the real install or
        // needing a running gateway. Returns true if a swap was applied.
        internal static bool ApplyStagedUpdate(string installDir)
        {
            string stagedRoot = Path.Combine(installDir, ".staged");
            string versionFile = Path.Combine(installDir, "version.txt");
            string marker = Path.Combine(stagedRoot, "staged.json");
            if (!File.Exists(marker)) return false;

            string? staged = ReadMarkerVersion(marker);
            if (string.IsNullOrEmpty(staged)) { TryDeleteDir(stagedRoot); return false; }

            string? current = ReadVersionFile(versionFile);
            if (current != null && UpdateNotifier.CompareSemver(staged!, current) <= 0)
            {
                TryDeleteDir(stagedRoot); // stale staged (already at/again this version)
                return false;
            }
            if (!StagedPayloadValid(stagedRoot)) { TryDeleteDir(stagedRoot); return false; }

            if (!ApplyStagedFiles(stagedRoot, installDir, staged!)) return false;
            try { File.WriteAllText(versionFile, "v" + staged!.TrimStart('v', 'V')); } catch { }
            TryDeleteDir(stagedRoot);
            StagedVersionWasApplied = staged;
            return true;
        }

        // Returns true if the swap completed. Conservative: pre-flights every target
        // EXCEPT the gateway exe (renamed last; rename-self reliably handles the
        // running binary). If any other file is locked (e.g. a worker from another
        // open client), aborts WITHOUT touching anything and retries next launch.
        private static bool ApplyStagedFiles(string stagedRoot, string installDir, string stagedVersion)
        {
            var plan = Directory.GetFiles(stagedRoot, "*", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), "staged.json", StringComparison.OrdinalIgnoreCase))
                .Select(src => new { Src = src, Dst = Path.Combine(installDir, GetRelative(stagedRoot, src)) })
                .ToList();

            // The gateway binary cannot be overwritten while it is running, so it gets the
            // rename-then-replace treatment below and every other file is copied normally. That
            // means this must name the binary THIS PROCESS is actually running from — which, on an
            // install predating the GxMcp18.* rename, is still the legacy name. Hardcoding the new
            // name here would treat the running exe as an ordinary file, and the copy would fail
            // against the lock we were trying to avoid.
            string runningExe = Path.Combine(installDir,
                FirstExisting(installDir, GatewayExeNameCurrent, GatewayExeNameLegacy) ?? GatewayExeName);
            try
            {
                string? self = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(self) && PathEquals(Path.GetDirectoryName(self) ?? "", installDir))
                    runningExe = self!;
            }
            catch { /* fall back to the name-based guess above */ }

            string gatewayTarget = runningExe;

            // Pre-flight: confirm every existing non-gateway target is replaceable.
            foreach (var p in plan)
            {
                if (PathEquals(p.Dst, gatewayTarget)) continue;
                if (File.Exists(p.Dst) && !IsRenamable(p.Dst))
                {
                    Program.Log($"[SelfUpdate] target locked, aborting apply (will retry): {p.Dst}");
                    return false;
                }
            }

            // Apply non-gateway files first, then the gateway exe last.
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            foreach (var p in plan)
            {
                if (PathEquals(p.Dst, gatewayTarget)) continue;
                ReplaceFile(p.Src, p.Dst, stamp);
            }
            var gw = plan.FirstOrDefault(p => PathEquals(p.Dst, gatewayTarget));
            if (gw != null) ReplaceFile(gw.Src, gw.Dst, stamp);
            return true;
        }

        private static string? ReadMarkerVersion(string marker)
        {
            try
            {
                var j = JObject.Parse(File.ReadAllText(marker));
                string? v = j["version"]?.ToString();
                return string.IsNullOrEmpty(v) ? null : v!.TrimStart('v', 'V');
            }
            catch { return null; }
        }

        private static string? ReadVersionFile(string versionFile)
        {
            try { return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim().TrimStart('v', 'V') : null; }
            catch { return null; }
        }

        // Move src over dst, renaming an existing dst out of the way first (works even
        // when dst is the running exe — Windows allows renaming an in-use file).
        private static void ReplaceFile(string src, string dst, string stamp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (File.Exists(dst))
            {
                string backup = dst + ".old-" + stamp;
                try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                File.Move(dst, backup);
            }
            File.Move(src, dst);
        }

        private static bool IsRenamable(string path)
        {
            string probe = path + ".swap-test";
            try
            {
                if (File.Exists(probe)) { try { File.Delete(probe); } catch { } }
                File.Move(path, probe);
                File.Move(probe, path);
                return true;
            }
            catch
            {
                // Best effort to restore if the first move succeeded but the second didn't.
                try { if (File.Exists(probe) && !File.Exists(path)) File.Move(probe, path); } catch { }
                return false;
            }
        }

        private static void SweepOld(string installDir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(installDir, "*.old-*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { /* still locked — try again later */ }
                }
            }
            catch { }
        }

        // ── Stage ──────────────────────────────────────────────────────────────

        public static async Task MaybeStageAsync(string latestVersion)
        {
            if (Disabled || string.IsNullOrEmpty(latestVersion)) return;
            try
            {
                if (!IsManagedInstall()) return;
                string? current = CurrentInstalledVersion();
                if (current != null && UpdateNotifier.CompareSemver(latestVersion, current) <= 0) return;

                string? alreadyStaged = StagedVersion();
                if (alreadyStaged != null && UpdateNotifier.CompareSemver(alreadyStaged, latestVersion) >= 0) return; // already staged this (or newer)

                string v = latestVersion.TrimStart('v', 'V');
                string zipUrl = $"https://github.com/{Repo}/releases/download/v{v}/publish.zip";
                string shaUrl = zipUrl + ".sha256";

                using var http = new HttpClient { Timeout = DownloadTimeout };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("genexus-mcp-gateway");

                byte[] zipBytes;
                try { zipBytes = await http.GetByteArrayAsync(zipUrl); }
                catch (Exception ex) { Program.Log($"[SelfUpdate] download failed: {ex.Message}"); return; }

                // Verify SHA-256 if the release publishes a checksum; else fall back to
                // structural validation only (with a logged warning).
                string? expected = await TryGetChecksumAsync(http, shaUrl);
                if (expected != null)
                {
                    string actual = Sha256Hex(zipBytes);
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        Program.Log($"[SelfUpdate] checksum MISMATCH for v{v} (expected {expected}, got {actual}) — refusing to stage.");
                        return;
                    }
                }
                else
                {
                    Program.Log($"[SelfUpdate] no publish.zip.sha256 for v{v} — staging after structural validation only.");
                }

                // Extract to .staged.tmp, validate, then atomically swap to .staged.
                TryDeleteDir(StagedTmpDir);
                Directory.CreateDirectory(StagedTmpDir);
                string tmpZip = Path.Combine(StagedTmpDir, "publish.zip");
                await File.WriteAllBytesAsync(tmpZip, zipBytes);
                ZipFile.ExtractToDirectory(tmpZip, StagedTmpDir, overwriteFiles: true);
                try { File.Delete(tmpZip); } catch { }

                if (!StagedPayloadValid(StagedTmpDir))
                {
                    Program.Log($"[SelfUpdate] staged payload for v{v} failed validation — discarding.");
                    TryDeleteDir(StagedTmpDir);
                    return;
                }

                File.WriteAllText(Path.Combine(StagedTmpDir, "staged.json"),
                    new JObject { ["version"] = v, ["stagedAt"] = DateTime.UtcNow.ToString("o") }.ToString());

                TryDeleteDir(StagedDir);
                Directory.Move(StagedTmpDir, StagedDir);
                Program.Log($"[SelfUpdate] staged v{v}; it will be applied on the next gateway launch.");
            }
            catch (Exception ex)
            {
                Program.Log($"[SelfUpdate] staging skipped: {ex.GetType().Name}: {ex.Message}");
                TryDeleteDir(StagedTmpDir);
            }
        }

        private static async Task<string?> TryGetChecksumAsync(HttpClient http, string shaUrl)
        {
            try
            {
                var resp = await http.GetAsync(shaUrl);
                if (!resp.IsSuccessStatusCode) return null;
                string body = (await resp.Content.ReadAsStringAsync()).Trim();
                // Accept either "<hex>" or "<hex>  filename" (sha256sum format).
                string token = body.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : null;
            }
            catch { return null; }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        // The staged tree must contain the two binaries the install asserts on. Either naming is
        // accepted: a package built before the GxMcp18.* rename is still a valid payload, and
        // refusing it would turn a cosmetic rename into a failed update.
        internal static bool StagedPayloadValid(string root)
        {
            try
            {
                bool gateway = FirstExisting(root, GatewayExeNameCurrent, GatewayExeNameLegacy) != null;
                bool worker = FirstExisting(Path.Combine(root, "worker"), WorkerExeNameCurrent, WorkerExeNameLegacy) != null;
                return gateway && worker;
            }
            catch { return false; }
        }

        private static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }

        private static string GetRelative(string root, string full)
        {
            string rel = full.Substring(root.Length);
            return rel.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool PathEquals(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
