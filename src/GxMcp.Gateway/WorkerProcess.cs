using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    /// <summary>Reason a WorkerProcess was intentionally stopped.</summary>
    public enum WorkerStopReason
    {
        None,
        IdleTimeout,
        GatewayShutdown,
        BusyReject,      // exit code 17 — sibling already owns this KB
        ExplicitClose,   // genexus_kb action=close
        PlannedReload,   // genexus_worker_reload (non-force); gateway is orchestrating drain+respawn
        Wedged,          // BUG-03: an in-flight command exceeded WedgedCommandTimeoutMinutes with no response
        HeapRecycle      // idle worker exceeded WorkerHeapRecycleMB; recycled proactively so a long
                         // session can't drift into an OOM/fragmented state. Eager-respawns.
    }

    public class WorkerProcess
    {
        // PERFORMANCE (G-M6): jitter source for exponential-backoff in spawn retry.
        private static readonly Random _jitter = new Random();
        private static readonly object _jitterLock = new object();

        public KbHandle Kb { get; }
        private Process? _process;
        private readonly Configuration _config;
        private readonly Channel<string> _commandChannel = Channel.CreateUnbounded<string>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _processLock = new object();
        private readonly TimeSpan _workerIdleTimeout;
        private Task? _writerTask;
        private Task? _healthCheckTask;
        private DateTime _lastResponse = DateTime.UtcNow;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private NamedPipeServerStream? _pipeServer;
        private StreamReader? _pipeReader;
        private StreamWriter? _pipeWriter;
        private TaskCompletionSource<bool> _pipeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Completes when the worker signals SDK-ready (KB open + SDK init done). The gateway
        // waits on this before starting a tool's timeout clock so worker cold-start (~50s)
        // isn't billed against the operation budget.
        private TaskCompletionSource<bool> _sdkReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SdkReadyTask => _sdkReady.Task;
        public bool IsSdkReady => _sdkReady.Task.IsCompleted;
        private string _lastOperationInfo = "None";
        private bool _isStarting;
        private WorkerStopReason _stopReason = WorkerStopReason.None;
        // Guards OnWorkerExited so it fires exactly once per worker, whether the exit is
        // observed via the async Process.Exited event OR forced synchronously by StopProcess.
        // StopProcess disposes the Process right after Kill, which suppresses the Exited
        // event — so idle-shutdown teardown must signal the exit itself, or the pool never
        // drops the entry and the next command hits a dead worker (WorkerCrashed).
        private int _exitNotified;
        private int _inFlightCommands;
        private int _queuedCommands;
        // BUG-03: start timestamp of each in-flight command, keyed by JSON-RPC id.
        // Populated when a command that counts as activity is written to the pipe,
        // removed on normal completion (CompleteInFlight) or send failure. The health
        // check uses the OLDEST surviving entry to detect a worker wedged mid-command
        // (crashed processes are already handled elsewhere; this is for a worker that's
        // alive but never responds).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _inFlightStartTimes = new();
        private readonly TimeSpan _wedgedCommandTimeout;
        // A worker with an old in-flight command is only "wedged" if it has ALSO gone silent
        // (no stdout/stderr) this long. Below it, an old-but-chatty command is treated as
        // progressing (long update/build), not reaped.
        private const int WedgedSilenceSeconds = 120;
        // Proactive idle heap-recycle ceiling (bytes; 0 = disabled) and a grace so we only
        // recycle a worker that has been genuinely idle, not one momentarily between commands.
        private readonly long _heapRecycleBytes;
        private static readonly TimeSpan HeapRecycleIdleGrace = TimeSpan.FromSeconds(30);
        private long _spawnMs = -1;
        private long _sdkInitMs = -1;
        private System.Diagnostics.Stopwatch? _spawnWatch;
        private System.Diagnostics.Stopwatch? _sdkInitWatch;
        // Crash-ledger inputs, captured while the process is still alive so they survive
        // the Kill/Dispose that zeroes WorkingSet64 and hides the exit code. The health
        // check refreshes _lastWorkingSetBytes on each tick; the Exited handler and
        // StopProcess capture the exit code before the Process is disposed.
        private int _lastExitCode = int.MinValue;
        private long _lastWorkingSetBytes = -1;
        private int _lastPid;

        public long? SpawnMs { get { var v = System.Threading.Interlocked.Read(ref _spawnMs); return v < 0 ? (long?)null : v; } }
        public long? SdkInitMs { get { var v = System.Threading.Interlocked.Read(ref _sdkInitMs); return v < 0 ? (long?)null : v; } }

        public event Action<string>? OnRpcResponse;
        public event Action<WorkerStopReason>? OnWorkerExited;

        public int? Pid
        {
            get
            {
                try { return _process?.HasExited == false ? _process.Id : (int?)null; }
                catch { return null; }
            }
        }

        // Friction 2026-05-22: surface the exe path the worker was actually
        // spawned from so whoami can show it. Worker can come from publish/worker/
        // (config.json default), dev bin/Debug (fallback), or the gateway-relative
        // worker/ dir — telling them apart used to require ps + filesystem inspection.
        public string? SpawnedExePath { get; private set; }
        public DateTime? SpawnedExeBuiltAtUtc { get; private set; }
        // Item 52: uptime tracking — set when the worker process is successfully started.
        public DateTime? SpawnedAtUtc { get; private set; }

        public long? WorkingSetBytes
        {
            get
            {
                try { return _process?.HasExited == false ? _process.WorkingSet64 : (long?)null; }
                catch { return null; }
            }
        }

        public WorkerProcess(Configuration config, KbHandle kb)
        {
            _config = config;
            Kb = kb;
            // A configured value <= 0 means "never idle-reap" — honor it (ShouldStopForIdle
            // short-circuits on TimeSpan.Zero). The previous Math.Max(1, …) floor forced 0 up
            // to 1 minute, making the documented disable path dead code AND turning the most
            // aggressive setting into the worst 90s-tax generator.
            int idleMin = _config.Server?.WorkerIdleTimeoutMinutes ?? 60;
            _workerIdleTimeout = idleMin <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(idleMin);
            _heapRecycleBytes = (long)Math.Max(0, _config.Server?.WorkerHeapRecycleMB ?? 1500) * 1024 * 1024;
            _wedgedCommandTimeout = TimeSpan.FromMinutes(Math.Max(1, _config.Server?.WedgedCommandTimeoutMinutes ?? 15));
            _writerTask = Task.Run(ProcessQueueAsync);
        }

        private async Task RunHealthCheckAsync(CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        SnapshotVitals();
                        if (ShouldStopForIdle())
                        {
                            Program.Log($"[Gateway] worker_idle_shutdown pid={_process.Id} idleTimeoutMinutes={_workerIdleTimeout.TotalMinutes}");
                            StopProcess(WorkerStopReason.IdleTimeout);
                            continue;
                        }

                        if (ShouldRecycleForHeap(out long wsBytes))
                        {
                            Program.Log($"[Gateway] worker_heap_recycle pid={_process.Id} workingSetMB={wsBytes / (1024 * 1024)} thresholdMB={_heapRecycleBytes / (1024 * 1024)} — recycling idle bloated worker (eager respawn).");
                            StopProcess(WorkerStopReason.HeapRecycle);
                            continue;
                        }

                        if (Volatile.Read(ref _inFlightCommands) <= 0)
                        {
                            await Task.Delay(15000, ct);
                            continue;
                        }

                        // BUG-03: a worker that's alive but never responds to an in-flight
                        // command used to sit forever — ShouldStopForIdle refuses to reap
                        // while _inFlightCommands > 0, and the gateway op timeout only marks
                        // the operation as timed out without touching the worker itself.
                        // HasWedgedCommand only trips once a command has been unanswered past
                        // _wedgedCommandTimeout (default 15 min — far above the 45s warning
                        // below and generous enough that legitimate multi-minute builds never
                        // trigger it). Workers with no in-flight command are unaffected — this
                        // branch is only reached when _inFlightCommands > 0.
                        if (HasWedgedCommand(out var oldestAge))
                        {
                            // Progress-aware guard: a worker still EMITTING OUTPUT (applying a
                            // huge GXserver update, a long build) is slow, not wedged — only a
                            // truly hung STA call goes silent. `_lastResponse` advances on every
                            // worker stdout/stderr line, so reap only when the old in-flight
                            // command is paired with silence past WedgedSilenceSeconds. Without
                            // this, a 15-min-but-actively-progressing update was falsely killed
                            // (worker_wedged_shutdown at 15.1min while still applying an object
                            // every second).
                            double silentSec = (DateTime.UtcNow - _lastResponse).TotalSeconds;
                            if (silentSec >= WedgedSilenceSeconds)
                            {
                                Program.Log($"[Gateway] worker_wedged_shutdown pid={_process.Id} oldestInFlightAgeMinutes={oldestAge.TotalMinutes:F1} silentSec={silentSec:F0} ceilingMinutes={_wedgedCommandTimeout.TotalMinutes}");
                                StopProcess(WorkerStopReason.Wedged);
                                continue;
                            }
                            Program.Log($"[Gateway] worker in-flight command old ({oldestAge.TotalMinutes:F1}min) but still emitting output ({silentSec:F0}s ago) — progressing, not wedged.");
                        }

                        if ((DateTime.UtcNow - _lastResponse).TotalSeconds > 45)
                        {
                            Program.Log($"[Gateway] Warning: Worker unresponsive for 45s. Last activity: {_lastOperationInfo}. It may be processing a heavy load or a long KB operation.");
                        }
                        else
                        {
                            Program.Log("[Health] Sending Ping to Worker...");
                            try
                            {
                                var ping = new { jsonrpc = "2.0", id = "heartbeat", method = "ping" };
                                await SendCommandAsync(JsonConvert.SerializeObject(ping));
                            }
                            catch (Exception exPing)
                            {
                                Program.Log($"[Health] Error sending ping: {exPing.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[Health] Error during health check loop: {ex.Message}");
                }

                await Task.Delay(15000, ct);
            }
        }

        private async Task ProcessQueueAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (await _commandChannel.Reader.WaitToReadAsync(_cts.Token))
                    {
                        while (_commandChannel.Reader.TryRead(out var jsonRpc))
                        {
                            Interlocked.Decrement(ref _queuedCommands);
                            if (string.IsNullOrEmpty(jsonRpc))
                            {
                                continue;
                            }

                            // Fix 3: removed lazy Start() — on unexpected exit the pool drops this
                            // entry and a fresh WorkerProcess is created by AcquireAsync. Calling
                            // Start() from inside the queue loop caused double-spawning (tracked +
                            // orphaned worker) and bypassed the respawn-suppression path in Program.cs.
                            // If the process is not running, fail this command with a typed error
                            // so the gateway returns a clean JSON-RPC error instead of silently dropping it.
                            if (!IsProcessRunning(_process))
                            {
                                string failId = "unknown";
                                try
                                {
                                    var failJson = JObject.Parse(jsonRpc);
                                    failId = failJson["id"]?.ToString() ?? "unknown";
                                }
                                catch { }
                                Program.Log($"[Gateway] Worker not running; failing command {failId} with WorkerCrashed error.");
                                var errResponse = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["id"] = failId == "unknown" ? (JToken)JValue.CreateNull() : new JValue(failId),
                                    ["error"] = new JObject { ["code"] = -32000, ["message"] = $"Worker for KB '{Kb.Alias}' crashed/exited. Reconnect or try again." }
                                };
                                OnRpcResponse?.Invoke(errResponse.ToString(Formatting.None));
                                continue;
                            }

                            string id = "unknown";
                            var countsAsActivity = false;
                            try
                            {
                                // PERFORMANCE (G-M1): JObject.Parse is the direct constructor — avoids
                                // the JsonConvert.DeserializeObject<T> reflection-style dispatch on every
                                // command. Semantically identical for our case.
                                var json = JObject.Parse(jsonRpc);
                                if (json["id"] != null)
                                {
                                    id = json["id"]?.ToString() ?? "unknown";
                                }

                                var method = json["method"]?.ToString() ?? "unknown";
                                _lastOperationInfo = $"{method} (ID: {id})";
                                countsAsActivity = !string.Equals(id, "heartbeat", StringComparison.OrdinalIgnoreCase) &&
                                                   !string.Equals(method, "ping", StringComparison.OrdinalIgnoreCase);
                            }
                            catch
                            {
                            }

                            try
                            {
                                if (countsAsActivity)
                                {
                                    MarkActivity();
                                    Interlocked.Increment(ref _inFlightCommands);
                                    _inFlightStartTimes[id] = DateTime.UtcNow;
                                }

                                await WaitForPipeReadyAsync(id, _cts.Token);

                                // Fix 7: snapshot writer under lock, write outside. Holding
                                // _processLock during the blocking WriteLine/Flush was causing
                                // deadlock risk: StopProcess() also takes _processLock and the
                                // write could block on a full pipe buffer.
                                StreamWriter? writer;
                                lock (_processLock)
                                {
                                    writer = _pipeWriter;
                                }

                                if (writer != null)
                                {
                                    // WriteLineAsync + FlushAsync with cancellation support.
                                    await writer.WriteLineAsync(jsonRpc).ConfigureAwait(false);
                                    await writer.FlushAsync().ConfigureAwait(false);
                                    Program.Log($"[Gateway] Command written to pipe: {id}");
                                }
                                else
                                {
                                    if (countsAsActivity)
                                    {
                                        CompleteInFlight(id);
                                    }

                                    Program.Log($"[Gateway] ERROR: Cannot send command {id}, pipe not available after wait.");
                                }
                            }
                            catch (Exception ex)
                            {
                                if (countsAsActivity)
                                {
                                    CompleteInFlight(id);
                                }

                                Program.Log($"[Gateway] IPC Send Error ({id}): {ex.Message}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Program.Log($"[Gateway] Critical Error in ProcessQueueAsync: {ex.Message}");
                    try
                    {
                        await Task.Delay(1000, _cts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }

        // Capture the process vitals that Kill/Dispose destroys (WorkingSet64 → 0, Id →
        // throws) so the crash ledger can report memory-at-death and pid even for an exit
        // observed only after teardown. Best-effort; called while the process is alive.
        private void SnapshotVitals()
        {
            try
            {
                var p = _process;
                if (p != null && !p.HasExited)
                {
                    _lastWorkingSetBytes = p.WorkingSet64;
                    _lastPid = p.Id;
                }
            }
            catch { /* process may have exited between the check and the read */ }
        }

        private static bool IsProcessRunning(Process? process)
        {
            if (process == null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private async Task WaitForPipeReadyAsync(string id, CancellationToken cancellationToken)
        {
            Task pipeReadyTask;
            lock (_processLock)
            {
                if (_pipeWriter != null)
                {
                    return;
                }

                pipeReadyTask = _pipeReady.Task;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var cancellationTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
            var completed = await Task.WhenAny(pipeReadyTask, cancellationTask);
            if (completed != pipeReadyTask)
            {
                throw new TimeoutException($"Worker pipe was not ready in time for command {id}.");
            }
        }

        public static void KillOrphanGateways(string? kbPath = null)
        {
            try
            {
                int currentPid = Environment.ProcessId;
                string[] targets = { "GxMcp.Gateway", "GxMcp.Worker" };

                foreach (var name in targets)
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        if (proc.Id == currentPid)
                        {
                            continue;
                        }

                        try
                        {
                            Program.Log($"[Gateway] Killing orphan {name} (PID {proc.Id})...");
                            proc.Kill(true);
                            proc.WaitForExit(3000);
                            Thread.Sleep(200);
                        }
                        catch
                        {
                        }
                    }
                }

                foreach (var proc in Process.GetProcessesByName("dotnet"))
                {
                    if (proc.Id == currentPid)
                    {
                        continue;
                    }

                    try
                    {
                        string cmdLine = GetCommandLine(proc);
                        if (string.IsNullOrEmpty(cmdLine))
                        {
                            continue;
                        }

                        bool isOurs =
                            cmdLine.Contains("GxMcp.Gateway.dll", StringComparison.OrdinalIgnoreCase) ||
                            cmdLine.Contains("GxMcp.Worker.dll", StringComparison.OrdinalIgnoreCase);

                        if (isOurs)
                        {
                            Program.Log($"[Gateway] Killing orphan dotnet-mcp (PID {proc.Id}, Cmd: {cmdLine})...");
                            proc.Kill(true);
                            proc.WaitForExit(3000);
                            Thread.Sleep(200);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        // Hard "exactly one worker per KB" backstop. Run at the top of every Start():
        // kill any OTHER GxMcp.Worker process bound to this KB (matched on the --kb path
        // in its command line) before we spawn ours. This reaps orphans left by crashes,
        // reload races, or a gateway that died without cleaning up — so a KB can never
        // accumulate duplicate workers regardless of how the previous one ended. Our own
        // live process (when self != exited) is preserved. Best-effort per process.
        private void KillOrphanWorkers()
        {
            try
            {
                string norm = (Kb?.Path ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
                if (norm.Length == 0) return;

                int? ourPid = null;
                try { ourPid = _process?.HasExited == false ? _process.Id : (int?)null; } catch { }

                foreach (var proc in Process.GetProcessesByName("GxMcp.Worker"))
                {
                    try
                    {
                        if (ourPid.HasValue && proc.Id == ourPid.Value) continue;
                        string cmd = GetCommandLine(proc);
                        if (string.IsNullOrEmpty(cmd)) continue;
                        if (!CommandLineTargetsKb(cmd, norm)) continue;

                        Program.Log($"[Gateway] KillOrphanWorkers: reaping duplicate worker pid={proc.Id} for KB '{Kb?.Alias}'.");
                        proc.Kill(true);
                        proc.WaitForExit(3000);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[Gateway] KillOrphanWorkers: probe/kill pid={proc.Id} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[Gateway] KillOrphanWorkers: enumeration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// FR#19: locate an existing GxMcp.Worker process bound to the given KB by
        /// scanning the Win32_Process command-line for "--kb &lt;kbPath&gt;". Used when
        /// our own spawn lost the single-instance race (exit code 17) so we can
        /// surface the live worker's PID to operators rather than thrashing on the
        /// lock. Returns null when no match is found.
        /// </summary>
        public static int? FindExistingWorkerPidForKb(string kbPath)
        {
            if (string.IsNullOrWhiteSpace(kbPath)) return null;
            string norm = kbPath.Trim().TrimEnd('\\', '/').ToLowerInvariant();
            try
            {
                foreach (var proc in Process.GetProcessesByName("GxMcp.Worker"))
                {
                    try
                    {
                        string cmd = GetCommandLine(proc);
                        if (string.IsNullOrEmpty(cmd)) continue;
                        if (CommandLineTargetsKb(cmd, norm)) return proc.Id;
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[Gateway] FindExistingWorkerPidForKb: probe pid={proc.Id} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[Gateway] FindExistingWorkerPidForKb: enumeration failed: {ex.Message}");
            }
            return null;
        }

        // BUG-fix: a bare Contains(norm) match falsely matches when one KB path is a
        // prefix of another (C:\KBs\Foo vs C:\KBs\FooBar), so starting Foo's worker
        // would reap FooBar's live worker. Workers are spawned with --kb "<path>"
        // (see Start()), so extract that argument's value and compare it, normalized
        // the same way (trim, strip trailing separators, lowercase), by whole path.
        internal static bool CommandLineTargetsKb(string commandLine, string normalizedKbPath)
        {
            if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(normalizedKbPath)) return false;
            string cmd = commandLine.ToLowerInvariant();

            int idx = cmd.IndexOf("--kb", StringComparison.Ordinal);
            while (idx >= 0)
            {
                int p = idx + 4;
                while (p < cmd.Length && (cmd[p] == ' ' || cmd[p] == '\t' || cmd[p] == '=')) p++;
                string value;
                if (p < cmd.Length && cmd[p] == '"')
                {
                    int end = cmd.IndexOf('"', p + 1);
                    value = end > p ? cmd.Substring(p + 1, end - p - 1) : cmd.Substring(p + 1);
                }
                else
                {
                    int end = p;
                    while (end < cmd.Length && cmd[end] != ' ' && cmd[end] != '\t') end++;
                    value = cmd.Substring(p, end - p);
                }
                value = value.Trim().TrimEnd('\\', '/');
                if (value == normalizedKbPath) return true;
                idx = cmd.IndexOf("--kb", idx + 4, StringComparison.Ordinal);
            }
            return false;
        }

        private static string GetCommandLine(Process process)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id);
                using var objects = searcher.Get();
                foreach (var obj in objects)
                {
                    return obj["CommandLine"]?.ToString() ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public void Start()
        {
            lock (_processLock)
            {
                if (_isStarting || IsProcessRunning(_process))
                {
                    return;
                }

                _isStarting = true;
            }

            try
            {
                KillOrphanWorkers();
                _stopReason = WorkerStopReason.None;
                MarkActivity();
                // Publish the readiness sources under the lock: StopProcess /
                // WaitForPipeReadyAsync read these same fields under _processLock, and
                // a concurrent stop (idle-timeout / health-check thread) must never
                // observe a half-updated field set.
                lock (_processLock)
                {
                    _pipeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _sdkReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string workerPath = _config.GeneXus?.WorkerExecutable ?? string.Empty;
                if (!Path.IsPathRooted(workerPath))
                {
                    workerPath = Path.Combine(baseDir, workerPath);
                }

                if (!File.Exists(workerPath))
                {
                    // New name first, legacy second: a dev tree or publish/ built before the
                    // GxMcp18.* rename must still resolve, otherwise the gateway cannot spawn a
                    // worker at all. Note the directory stays GxMcp.Worker — only the binary
                    // was renamed, not the project.
                    string[] devPaths = new[]
                    {
                        Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp18.Worker.exe")),
                        Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp18.Worker.exe")),
                        Path.Combine(baseDir, @"worker\GxMcp18.Worker.exe"),
                        Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp.Worker.exe")),
                        Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\src\GxMcp.Worker\bin\Debug\GxMcp.Worker.exe")),
                        Path.Combine(baseDir, @"worker\GxMcp.Worker.exe")
                    };

                    foreach (var path in devPaths)
                    {
                        if (File.Exists(path))
                        {
                            workerPath = path;
                            break;
                        }
                    }
                }

                if (!File.Exists(workerPath))
                {
                    throw new FileNotFoundException($"Worker NOT FOUND at {workerPath}");
                }

                SpawnedExePath = workerPath;
                try { SpawnedExeBuiltAtUtc = File.GetLastWriteTimeUtc(workerPath); } catch { SpawnedExeBuiltAtUtc = null; }

                var startInfo = new ProcessStartInfo
                {
                    FileName = workerPath,
                    WorkingDirectory = Path.GetDirectoryName(workerPath) ?? string.Empty,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = System.Text.Encoding.UTF8,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                string kbPath = Kb.Path;
                startInfo.Arguments = $"--kb \"{kbPath}\"";
                startInfo.EnvironmentVariables["GX_PROGRAM_DIR"] = _config.GeneXus?.InstallationPath ?? string.Empty;
                startInfo.EnvironmentVariables["GX_KB_PATH"] = kbPath;
                // v2.8.5: hand the worker the authoritative server version so
                // genexus_doctor reports the same number as whoami (the worker
                // assembly version can lag the package version between releases).
                startInfo.EnvironmentVariables["GXMCP_SERVER_VERSION"] = McpRouter.ServerVersion;
                startInfo.EnvironmentVariables["GX_SHADOW_PATH"] = _config.Environment?.GX_SHADOW_PATH ?? Path.Combine(kbPath, ".gx_mirror");
                startInfo.EnvironmentVariables["PATH"] = (_config.GeneXus?.InstallationPath ?? string.Empty) + ";" + Environment.GetEnvironmentVariable("PATH");

                // Forward any GXMCP_* env vars from the gateway process to the worker.
                // Lets benchmarks / opt-outs (GXMCP_BUILD_COMPILE_ONLY, GXMCP_INPROCESS_BUILD_FASTPATH,
                // GXMCP_BUILD_PROFILE, GXMCP_REAP_ORPHAN_MSBUILD, ...) be set on the gateway and
                // automatically reach the worker without editing config files.
                try
                {
                    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                    {
                        string? key = entry.Key?.ToString();
                        if (!string.IsNullOrEmpty(key)
                            && key.StartsWith("GXMCP_", StringComparison.OrdinalIgnoreCase)
                            && !startInfo.EnvironmentVariables.ContainsKey(key))
                        {
                            startInfo.EnvironmentVariables[key] = entry.Value?.ToString() ?? string.Empty;
                        }
                    }
                }
                catch { /* env forwarding is best-effort */ }

                // Swap the process handle under the lock so a concurrent StopProcess
                // can't dispose/null _process mid-assignment.
                lock (_processLock)
                {
                if (_process != null)
                {
                    try
                    {
                        _process.Dispose();
                    }
                    catch
                    {
                    }
                }

                _process = new Process { StartInfo = startInfo };
                _process.EnableRaisingEvents = true;
                _process.Exited += (s, e) =>
                {
                    var exitedProcess = s as Process;
                    int exitCode = -1;
                    try
                    {
                        exitCode = exitedProcess?.ExitCode ?? -1;
                    }
                    catch
                    {
                    }
                    _lastExitCode = exitCode;

                    // FR#19: exit code 17 means a sibling worker already serves this KB
                    // (single-instance reject). Don't respawn — the live worker is authoritative.
                    bool busyReject = exitCode == 17;

                    // Capture the stop reason set before Kill; upgrade to BusyReject if the
                    // exit code says so (can override an earlier "none").
                    WorkerStopReason reason = _stopReason;
                    if (busyReject) reason = WorkerStopReason.BusyReject;

                    Program.Log($"[Gateway] Worker process EXITED with code {exitCode}. reason={reason}");

                    // SINGLE RESPAWN AUTHORITY. The WorkerPool owns the worker lifecycle:
                    // firing OnWorkerExited lets the pool drop this KB's entry and spawn
                    // exactly ONE replacement (Program's eager-respawn handler, or the lazy
                    // AcquireAsync path on the next call). The WorkerProcess deliberately does
                    // NOT also restart itself in place. Having both paths active spawned two
                    // live processes per exit — one tracked by the pool, one orphaned-but-alive
                    // — which compounded into a runaway worker-process explosion (a real memory
                    // leak: hundreds of GxMcp.Worker for a single KB). KillOrphanWorkers() at
                    // the top of Start() is the hard "exactly one worker per KB" backstop.
                    FireWorkerExitedOnce(reason);
                };
                }

                _spawnWatch = System.Diagnostics.Stopwatch.StartNew();
                _sdkInitWatch = System.Diagnostics.Stopwatch.StartNew();

                for (int attempt = 1; attempt <= 10; attempt++)
                {
                    try
                    {
                        _process.Start();
                        _lastPid = _process.Id;
                        SpawnedAtUtc = DateTime.UtcNow;
                        _spawnWatch.Stop();
                        System.Threading.Interlocked.Exchange(ref _spawnMs, _spawnWatch.ElapsedMilliseconds);
                        Program.Log($"[Gateway] worker_spawned pid={_process.Id} spawnMs={_spawnWatch.ElapsedMilliseconds} attempt={attempt} idleTimeoutMinutes={_workerIdleTimeout.TotalMinutes}");
                        break;
                    }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                    {
                        if (attempt == 10)
                        {
                            Program.Log($"[Gateway] Access denied (5) when starting worker. Attempt {attempt}/10. Giving up.");
                            throw;
                        }

                        // PERFORMANCE (G-M6): exponential backoff (100, 200, 400, 800, 1000…ms)
                        // with up-to-50% jitter. Total worst-case wait drops from 9s of flat
                        // 1-second sleeps to ~4.6s, and the FIRST retry fires 10× sooner.
                        int baseMs = Math.Min(1000, 100 << Math.Min(attempt - 1, 4));
                        int jitterMs;
                        lock (_jitterLock) { jitterMs = _jitter.Next(0, baseMs / 2 + 1); }
                        int sleepMs = baseMs + jitterMs;
                        Program.Log($"[Gateway] Access denied (5) when starting worker. Attempt {attempt}/10. File might be locked. Retrying in {sleepMs}ms...");
                        // Fix 7: replaced Thread.Sleep (blocks worker thread) with async delay.
                        Task.Delay(sleepMs).GetAwaiter().GetResult();
                    }
                }

                _process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _lastResponse = DateTime.UtcNow;
                        if (_sdkInitWatch != null && _sdkInitWatch.IsRunning &&
                            e.Data.Contains("Full SDK Initialization SUCCESS"))
                        {
                            _sdkInitWatch.Stop();
                            System.Threading.Interlocked.Exchange(ref _sdkInitMs, _sdkInitWatch.ElapsedMilliseconds);
                            Program.Log($"[Gateway] worker_sdk_init pid={_process?.Id} sdkInitMs={_sdkInitWatch.ElapsedMilliseconds}");
                        }
                        if (e.Data.TrimStart().StartsWith("{") && e.Data.Contains("\"jsonrpc\""))
                        {
                            HandleWorkerRpcResponse(e.Data);
                            OnRpcResponse?.Invoke(e.Data);
                        }
                        else
                        {
                            Program.Log($"[Worker] {e.Data}");
                        }
                    }
                };

                _process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _lastResponse = DateTime.UtcNow;
                        Program.Log($"[Worker-Err] {e.Data}");
                    }
                };

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                lock (_processLock)
                {
                    _pipeWriter = _process.StandardInput;
                    _pipeWriter.AutoFlush = true;
                }
                Program.Log("[Gateway] Worker stdio command channel initialized.");
                _pipeReady.TrySetResult(true);

                if (_healthCheckTask == null || _healthCheckTask.IsCompleted)
                {
                    _healthCheckTask = Task.Run(() => RunHealthCheckAsync(_cts.Token));
                }
            }
            finally
            {
                lock (_processLock)
                {
                    _isStarting = false;
                }
            }
        }

        public async Task SendCommandAsync(string jsonRpc)
        {
            Interlocked.Increment(ref _queuedCommands);
            await _commandChannel.Writer.WriteAsync(jsonRpc);
        }

        public void Stop() => StopWithReason(WorkerStopReason.GatewayShutdown);

        /// <summary>
        /// Waits asynchronously for the underlying OS process to exit.
        /// Completes immediately if the process is already exited or was never started.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires.
        /// </summary>
        public Task WaitForExitAsync(CancellationToken ct = default)
        {
            Process? p;
            lock (_processLock) { p = _process; }
            if (p == null) return Task.CompletedTask;
            try { if (p.HasExited) return Task.CompletedTask; } catch { return Task.CompletedTask; }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.Exited += (_, __) => tcs.TrySetResult(true);
            // Double-check after wiring the event — process may have exited between the
            // HasExited check above and Exited subscription.
            try { if (p.HasExited) tcs.TrySetResult(true); } catch { tcs.TrySetResult(true); }

            if (ct == CancellationToken.None) return tcs.Task;
            var reg = ct.Register(() => tcs.TrySetCanceled());
            return tcs.Task.ContinueWith(_ => { try { reg.Dispose(); } catch { } return _; },
                TaskContinuationOptions.ExecuteSynchronously).Unwrap();
        }

        public void StopWithReason(WorkerStopReason reason)
        {
            StopProcess(reason);
        }

        // Invokes OnWorkerExited at most once per WorkerProcess lifetime. Both the async
        // Process.Exited event and StopProcess route through here; whichever runs first
        // wins and the other becomes a no-op.
        private void FireWorkerExitedOnce(WorkerStopReason reason)
        {
            if (Interlocked.Exchange(ref _exitNotified, 1) != 0) return;
            try
            {
                int? exitCode = _lastExitCode == int.MinValue ? (int?)null : _lastExitCode;
                double? uptimeSec = SpawnedAtUtc.HasValue
                    ? (DateTime.UtcNow - SpawnedAtUtc.Value).TotalSeconds : (double?)null;
                long? lastWs = _lastWorkingSetBytes >= 0 ? _lastWorkingSetBytes : (long?)null;
                CrashLedger.Record(
                    kbAlias: Kb?.Alias ?? "",
                    reason: reason,
                    exitCode: exitCode,
                    pid: _lastPid > 0 ? _lastPid : (int?)null,
                    uptimeSec: uptimeSec,
                    lastWorkingSetBytes: lastWs,
                    lastOperation: _lastOperationInfo,
                    spawnMs: SpawnMs,
                    sdkInitMs: SdkInitMs,
                    sdkReady: IsSdkReady);
            }
            catch (Exception ex) { Program.Log($"[Gateway] CrashLedger.Record threw: {ex.Message}"); }
            try { OnWorkerExited?.Invoke(reason); }
            catch (Exception ex) { Program.Log($"[Gateway] OnWorkerExited handler threw: {ex.Message}"); }
        }

        private void StopProcess(WorkerStopReason reason)
        {
            // Every stop path (idle/heap/wedged reap from the health loop, and
            // StopWithReason for gateway shutdown / pool teardown) funnels here.
            // Cancel _cts so the writer loop (ProcessQueueAsync) and health loop
            // (RunHealthCheckAsync) actually exit instead of leaking for the life of
            // the gateway. Idempotent — safe when StopWithReason already cancelled.
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }

            lock (_processLock)
            {
                _stopReason = reason;
                if (_pipeWriter != null)
                {
                    try { _pipeWriter.Dispose(); } catch { }
                    _pipeWriter = null;
                }

                if (_pipeReader != null)
                {
                    try { _pipeReader.Dispose(); } catch { }
                    _pipeReader = null;
                }

                if (_pipeServer != null)
                {
                    try { _pipeServer.Dispose(); } catch { }
                    _pipeServer = null;
                }

                _pipeReady.TrySetCanceled();
                Interlocked.Exchange(ref _queuedCommands, 0);
                Interlocked.Exchange(ref _inFlightCommands, 0);
                _inFlightStartTimes.Clear();

                if (_process != null)
                {
                    try
                    {
                        // Capture vitals before Kill zeroes WorkingSet64 / invalidates the
                        // handle, so the ledger can attribute memory-at-death for reaped
                        // (idle / wedged) workers too.
                        SnapshotVitals();
                        if (!_process.HasExited)
                        {
                            _process.Kill(true);
                        }
                        else
                        {
                            try { _lastExitCode = _process.ExitCode; } catch { }
                        }

                        _process.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[Gateway] Error during process cleanup: {ex.Message}");
                    }

                    _process = null;
                }
            }

            // Signal the exit deterministically. Disposing the Process above suppresses its
            // async Exited event, so without this the pool would never drop the entry on an
            // idle/planned teardown and the next AcquireAsync would hand back this dead
            // worker. Fired outside _processLock; idempotent with the Exited handler.
            FireWorkerExitedOnce(reason);
        }

        private void MarkActivity()
        {
            _lastActivityUtc = DateTime.UtcNow;
        }

        // True when the worker should be proactively recycled for heap: recycling is enabled,
        // NOTHING is in flight or queued (never interrupt real work), it has been idle past the
        // grace window, and its working set is over the ceiling. The gateway eager-respawns a
        // fresh warm worker so the next burst of work starts clean rather than on a bloated,
        // fragmentation-prone heap heading for the x86 ceiling. `wsBytes` returns the last
        // snapshotted working set for the log line.
        internal bool ShouldRecycleForHeap(out long wsBytes)
        {
            wsBytes = _lastWorkingSetBytes;
            if (_heapRecycleBytes <= 0) return false;
            if (_isStarting) return false;
            if (Volatile.Read(ref _queuedCommands) > 0 || Volatile.Read(ref _inFlightCommands) > 0) return false;
            if (DateTime.UtcNow - _lastActivityUtc < HeapRecycleIdleGrace) return false;
            return wsBytes > 0 && wsBytes > _heapRecycleBytes;
        }

        private bool ShouldStopForIdle()
        {
            if (_workerIdleTimeout <= TimeSpan.Zero)
            {
                return false;
            }

            if (Volatile.Read(ref _queuedCommands) > 0 || Volatile.Read(ref _inFlightCommands) > 0)
            {
                return false;
            }

            if (_isStarting)
            {
                return false;
            }

            return DateTime.UtcNow - _lastActivityUtc >= _workerIdleTimeout;
        }

        private void HandleWorkerRpcResponse(string json)
        {
            try
            {
                var payload = JObject.Parse(json);
                var id = payload["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    // Notification path — handle soft-reload persist request inline so
                    // the JobRegistry survives the worker's clean exit (FR#20).
                    var method = payload["method"]?.ToString();
                    if (string.Equals(method, "notifications/worker/sdk_ready", StringComparison.Ordinal))
                    {
                        if (_sdkReady.TrySetResult(true))
                            Program.Log($"[Gateway] worker SDK ready (KB '{Kb?.Alias}').");
                    }
                    else if (string.Equals(method, "notifications/worker/build_active", StringComparison.Ordinal))
                    {
                        // issue #42 (P3a) — a background build is running. It is not an
                        // in-flight RPC, so bump the activity clock explicitly to keep
                        // ShouldStopForIdle / ShouldRecycleForHeap from reaping the
                        // worker mid-build.
                        MarkActivity();
                    }
                    else if (string.Equals(method, "notifications/worker/persist_jobs_request", StringComparison.Ordinal))
                    {
                        TryPersistJobsForSoftReload(payload["params"] as JObject);
                    }
                    else if (string.Equals(method, "notifications/worker/jobs_restored", StringComparison.Ordinal))
                    {
                        TryReloadJobsAfterSoftReload(payload["params"] as JObject);
                    }
                    return;
                }

                if (!string.Equals(id, "heartbeat", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback readiness signal: a real response means the worker is processing
                    // commands, so it's SDK-ready even if the sdk_ready notification was missed
                    // (e.g. an older worker binary that doesn't emit it).
                    _sdkReady.TrySetResult(true);
                    MarkActivity();
                    CompleteInFlight(id);
                }
            }
            catch (Exception ex)
            {
                // Never swallow silently — a parse failure here always meant a bug upstream
                // (worker emitted malformed JSON-RPC) but historically nobody saw it.
                Program.Log($"[Gateway] HandleWorkerRpcResponse error: {ex.Message}");
            }
        }

        private void TryReloadJobsAfterSoftReload(JObject? p)
        {
            try
            {
                string? path = p?["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    Program.Log("[Gateway] soft_reload jobs_restored missing path; skipping.");
                    return;
                }
                int count = Program.JobRegistry.LoadFrom(path, deleteAfterRead: true);
                Program.Log($"[Gateway] soft_reload rehydrated {count} jobs from {path}");
            }
            catch (Exception ex)
            {
                Program.Log($"[Gateway] soft_reload rehydrate failed: {ex.Message}");
            }
        }

        private void TryPersistJobsForSoftReload(JObject? p)
        {
            try
            {
                string? path = p?["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    Program.Log("[Gateway] soft_reload persist_jobs_request missing path; skipping.");
                    return;
                }
                Program.JobRegistry.SaveTo(path);
                int count = Program.JobRegistry.Count;
                Program.Log($"[Gateway] soft_reload persisted {count} jobs to {path}");
            }
            catch (Exception ex)
            {
                Program.Log($"[Gateway] soft_reload persist failed: {ex.Message}");
            }
        }

        private void CompleteInFlight(string? id = null)
        {
            if (!string.IsNullOrEmpty(id))
            {
                _inFlightStartTimes.TryRemove(id, out _);
            }

            while (true)
            {
                var current = Volatile.Read(ref _inFlightCommands);
                if (current <= 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _inFlightCommands, current - 1, current) == current)
                {
                    return;
                }
            }
        }

        // BUG-03: true when the oldest in-flight command has been unanswered for
        // longer than _wedgedCommandTimeout. This is the hard ceiling used by the
        // health check to force-stop a worker that's alive but will never respond —
        // distinct from ShouldStopForIdle (which only fires when NOTHING is in flight)
        // and from the 45s "unresponsive" log warning (informational only, no action).
        internal bool HasWedgedCommand(out TimeSpan oldestAge)
        {
            oldestAge = TimeSpan.Zero;
            DateTime oldestStart = DateTime.MaxValue;
            bool any = false;
            foreach (var kv in _inFlightStartTimes)
            {
                if (kv.Value < oldestStart)
                {
                    oldestStart = kv.Value;
                    any = true;
                }
            }
            if (!any) return false;
            oldestAge = DateTime.UtcNow - oldestStart;
            return oldestAge >= _wedgedCommandTimeout;
        }

        // --- Test seams (BUG-03) -------------------------------------------------
        // RunHealthCheckAsync itself is timer-driven (5s/15s Task.Delay) and gated
        // behind a live pipe/process, so it isn't practical to exercise end-to-end in
        // a fast unit test. These seams let tests drive the extracted decision logic
        // (HasWedgedCommand) and the bookkeeping (CompleteInFlight cleanup) directly,
        // the same way RegisterForTest/SetDrainingForTest do for WorkerPool.
        internal void SeedInFlightForTest(string id, DateTime startUtc)
        {
            _inFlightStartTimes[id] = startUtc;
            Interlocked.Increment(ref _inFlightCommands);
        }

        internal void CompleteInFlightForTest(string id) => CompleteInFlight(id);

        internal int InFlightStartTimesCountForTest => _inFlightStartTimes.Count;

        // Test seam: lets a reap-path test assert the background loops were told to stop.
        internal bool CancellationRequestedForTest => _cts.IsCancellationRequested;

        // Test seam: invoke the private teardown sink directly (no real process needed).
        internal void StopProcessForTest(WorkerStopReason reason) => StopProcess(reason);

        // Idle-reap window resolved from config in the ctor. TimeSpan.Zero == disabled.
        internal TimeSpan IdleTimeoutForTest => _workerIdleTimeout;

        // Drives the heap-recycle decision without a live OS process: seed the last-known
        // working set and last-activity time, then call ShouldRecycleForHeap.
        internal void SetHeapProbeForTest(long workingSetBytes, DateTime lastActivityUtc)
        {
            _lastWorkingSetBytes = workingSetBytes;
            _lastActivityUtc = lastActivityUtc;
        }
    }
}
