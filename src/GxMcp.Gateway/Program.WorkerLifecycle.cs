using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static void StartWorker(Configuration config)
        {
            _kbResolver = new KbResolver(config);
            _workerPool = new WorkerPool(config);
            _workerPool.OnRpcResponse += HandleWorkerResponse;
            _workerPool.OnWorkerExited += (kb, stopReason) => {
                string alias = kb.NormalizedAlias;
                int aborted = 0;
                foreach (var kvp in _pendingRequests.ToArray())
                {
                    if (!string.Equals(kvp.Value.WorkerAlias, alias, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string id = kvp.Key;
                    if (_pendingRequests.TryRemove(id, out var pending))
                    {
                        _operationTracker.MarkFailedByRequest(id, $"Worker for KB '{kb.Alias}' crashed/exited.");
                        var errorJson = JsonConvert.SerializeObject(new
                        {
                            jsonrpc = "2.0",
                            id = id,
                            error = new { code = -32603, message = $"Worker for KB '{kb.Alias}' crashed/exited." }
                        });
                        pending.CompletionSource.TrySetResult(errorJson);
                        aborted++;
                    }
                }
                Log($"Worker for KB '{kb.Alias}' exited. Aborted {aborted} pending request(s) bound to it.");

                // v2.6.8: eager respawn. Without this, the next tool call paid the
                // ~10–15s cold-start latency inline — long enough for short-timeout
                // MCP clients (VS Code Codex) to close the transport entirely.
                // Fire-and-forget: failures are logged but don't propagate; the
                // lazy path in WorkerPool.AcquireAsync still works as a fallback.
                // Skip eager respawn for intentional/planned exits.
                if (stopReason == WorkerStopReason.IdleTimeout ||
                    stopReason == WorkerStopReason.GatewayShutdown ||
                    stopReason == WorkerStopReason.BusyReject ||
                    stopReason == WorkerStopReason.ExplicitClose ||
                    stopReason == WorkerStopReason.PlannedReload)
                {
                    Log($"[Respawn] Skipped eager respawn for KB '{kb.Alias}' — stop reason: {stopReason}.");
                    return;
                }
                if (IsEagerRespawnSuppressed())
                {
                    Log($"[Respawn] Skipped eager respawn for KB '{kb.Alias}' — planned exit in progress.");
                    return;
                }
                Task.Run(async () =>
                {
                    // issue #26 P1: retry the respawn a few times with backoff instead of
                    // giving up after a single throw. A transient spawn failure used to
                    // leave the pool with no worker AND no process coming up, while whoami
                    // kept reporting "respawning" forever (nothing was). On final failure we
                    // record it so health can report the truth.
                    const int maxAttempts = 3;
                    Exception? lastEx = null;
                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            var ctSrc = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            // Drop only the dead LIVE entry so AcquireAsync's fast path can't
                            // return the just-exited WorkerProcess — but keep the durable
                            // _known record (issue #26 P3) so the KB stays resolvable.
                            try { _workerPool!.DropLiveEntry(kb.NormalizedAlias); } catch { }
                            await _workerPool!.AcquireAsync(kb, ctSrc.Token).ConfigureAwait(false);
                            _respawnFailures.TryRemove(kb.NormalizedAlias, out _);
                            Log($"[Respawn] Replacement worker spawned for KB '{kb.Alias}' (attempt {attempt}).");
                            // issue #25 #2: the index bootstrap fires once per gateway process,
                            // so a crash-respawned worker (same gateway) otherwise never gets a
                            // reindex trigger and its index stays Cold until an explicit
                            // lifecycle call — forcing the agent to re-walk. Re-arm and re-fire
                            // the one-shot: BulkIndex(force:false) reuses the persisted on-disk
                            // snapshot (delta-on-open) instead of a cold 38k re-walk.
                            Interlocked.Exchange(ref _indexBootstrapStarted, 0);
                            TriggerIndexBootstrapOnce();
                            return;
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            Log($"[Respawn] Attempt {attempt}/{maxAttempts} to respawn worker for KB '{kb.Alias}' failed: {ex.Message}");
                            if (attempt < maxAttempts)
                            {
                                try { await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false); } catch { }
                            }
                        }
                    }
                    _respawnFailures[kb.NormalizedAlias] = (DateTime.UtcNow, lastEx?.Message ?? "unknown");
                    Log($"[Respawn] Fast respawn for KB '{kb.Alias}' failed after {maxAttempts} attempts; entering slow background retry (every 60s).");

                    // Slow self-heal instead of a dead-end. The old behavior gave up here and
                    // left the KB stuck in respawn_failed until a manual genexus_worker_reload.
                    // Keep retrying on a long interval so a transient cause (host under load,
                    // an IDE holding the KB, a brief file lock) recovers on its own. Bounded at
                    // ~30 min so a genuinely unspawnable worker can't loop forever, and it bails
                    // early if a worker came up by any path or the gateway is shutting down.
                    for (int slow = 1; slow <= 30; slow++)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(60), _gatewayLifetime.Token).ConfigureAwait(false); }
                        catch { return; } // gateway shutting down
                        if (IsEagerRespawnSuppressed()) return;
                        if (_workerPool!.TryGet(kb.NormalizedAlias) != null)
                        {
                            _respawnFailures.TryRemove(kb.NormalizedAlias, out _);
                            return;
                        }
                        try
                        {
                            var slowCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                            try { _workerPool!.DropLiveEntry(kb.NormalizedAlias); } catch { }
                            await _workerPool!.AcquireAsync(kb, slowCts.Token).ConfigureAwait(false);
                            _respawnFailures.TryRemove(kb.NormalizedAlias, out _);
                            Log($"[Respawn] Slow-retry respawn succeeded for KB '{kb.Alias}' (retry {slow}).");
                            Interlocked.Exchange(ref _indexBootstrapStarted, 0);
                            TriggerIndexBootstrapOnce();
                            return;
                        }
                        catch (Exception ex)
                        {
                            _respawnFailures[kb.NormalizedAlias] = (DateTime.UtcNow, ex.Message);
                            Log($"[Respawn] Slow-retry {slow}/30 for KB '{kb.Alias}' failed: {ex.Message}");
                        }
                    }
                    Log($"[Respawn] Slow retry exhausted for KB '{kb.Alias}' (~30 min). whoami reports respawn_failed. Recovery: genexus_worker_reload mode=soft force=true.");
                });
            };
        }

        // mode=hard worker-binary swap. Called from DrainAndReplaceAsync's post-drain hook —
        // old worker has exited (exe unlocked) and eager respawn is suppressed, so the copy is
        // race-free (this is what the old best-effort path lost against the respawn). Copies
        // only the GxMcp.Worker.* assembly files; the dependency DLLs already sit in targetDir.
        private static void CopyWorkerBinaries(string sourceDir, string? targetDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetDir) || !System.IO.Directory.Exists(sourceDir))
                {
                    Log($"[Gateway] worker_reload copy skipped — sourceDir '{sourceDir}' missing or targetDir unresolved.");
                    return;
                }
                // Both namings: sourceDir is whatever bin\Debug the developer points at, which may
                // still hold a pre-rename build. Missing entries are skipped by the loop below, so
                // listing both is free and keeps hot-reload working across the rename.
                string[] files =
                {
                    "GxMcp18.Worker.exe", "GxMcp18.Worker.dll", "GxMcp18.Worker.pdb", "GxMcp18.Worker.exe.config",
                    "GxMcp.Worker.exe", "GxMcp.Worker.dll", "GxMcp.Worker.pdb", "GxMcp.Worker.exe.config"
                };
                int copied = 0;
                foreach (var f in files)
                {
                    string src = System.IO.Path.Combine(sourceDir, f);
                    if (!System.IO.File.Exists(src)) continue;
                    string dst = System.IO.Path.Combine(targetDir!, f);
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        try { System.IO.File.Copy(src, dst, overwrite: true); copied++; break; }
                        catch (System.IO.IOException) when (attempt < 9) { System.Threading.Thread.Sleep(150); }
                    }
                }
                Log($"[Gateway] worker_reload swapped {copied} worker binary file(s): {sourceDir} -> {targetDir}");
            }
            catch (Exception ex) { Log($"[Gateway] worker_reload CopyWorkerBinaries failed: {ex.Message}"); }
        }

        private static void RestartWorker(Configuration config)
        {
            if (_workerPool != null)
            {
                using (SuppressEagerRespawn())
                {
                    try { _workerPool.StopAll(); } catch { }
                }
            }
            // Clear cache on KB change
            _semanticCache.Clear();
            StartWorker(config);
            BroadcastToolsListChanged("worker_restarted");
            BroadcastResourcesListChanged("worker_restarted");
        }

        private static void HandleWorkerResponse(string json)
        {
            try {
                var val = JObject.Parse(json);
                string? id = val["id"]?.ToString();

                if (string.IsNullOrEmpty(id))
                {
                    // JSON-RPC Notification from Worker
                    string? method = val["method"]?.ToString();
                    if (method == "notifications/resources/updated")
                    {
                        var p = val["params"];
                        string name = p?["name"]?.ToString() ?? "unknown";
                        Log($"[Gateway] Notification from Worker: Resource {name} updated externally.");
                        BroadcastResourceUpdated($"genexus://objects/{name}", "external_kb_change");
                    }
                    else if (method == "notifications/progress" || method == "notifications/message")
                    {
                        // A4: correlate the frame to its tracked operation (the worker
                        // command's progressToken is the operationId) and bump UpdatedAtUtc
                        // so a status poll shows live progress instead of a frozen timestamp.
                        if (method == "notifications/progress")
                        {
                            var pp = val["params"];
                            string opId = pp?["progressToken"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(opId))
                                _operationTracker.TouchProgress(opId, pp?["stage"]?.ToString(), pp?["message"]?.ToString());

                            // issue #44: never relay a progress frame whose operation is no longer
                            // active (terminal, or an unknown/literal token). A retired token —
                            // classically an async build still emitting "Build phase: OpeningKB"
                            // after its RPC returned Accepted — makes the client (Cursor) mark the
                            // transport errored ("progress notification for an unknown token") and
                            // drop the whole MCP connection. Progress for a live op still flows; the
                            // async progress channel is the self-scoped lifecycle status poll.
                            if (!_operationTracker.IsProgressTokenActive(opId))
                            {
                                Log($"[Gateway] Dropped stale/unknown progress token '{opId}' (op not active) — not relayed to client.");
                                return;
                            }
                        }
                        if (ShouldForwardNotificationToStdio(method, val["params"]))
                        {
                            EmitStdioNotification(json);
                        }
                        if (val["params"] != null)
                        {
                            foreach (var session in _httpSessions.ActiveSessions)
                            {
                                QueueSessionMessage(session, json);
                            }
                        }
                    }
                    return;
                }

                _operationTracker.CompleteFromWorker(id, val);
                if (_pendingRequests.TryRemove(id, out var pending))
                {
                    pending.CompletionSource.TrySetResult(json);
                    if (!string.IsNullOrWhiteSpace(pending.OperationId))
                    {
                        BroadcastNotification("notifications/message", new
                        {
                            level = "info",
                            logger = "operation",
                            data = $"Operation {pending.OperationId} finished.",
                            operationId = pending.OperationId,
                            correlationId = pending.CorrelationId,
                            status = val["error"] != null ? "Failed" : "Completed",
                            timestamp = DateTime.UtcNow
                        });
                    }
                }
            } catch (Exception ex) { Log($"HandleWorkerResponse Error: {ex.Message}"); }
        }

        // Records end-to-end tool latency (from just before the worker send to the response)
        // into ToolLatencyStats and emits one [TOOL-LATENCY] log line. Cold-start is already
        // awaited before CreatedAtUtc is stamped, so this measures real tool cost, not boot.
        private static void RecordToolLatency(string toolName, DateTime createdAtUtc)
        {
            try
            {
                double ms = (DateTime.UtcNow - createdAtUtc).TotalMilliseconds;
                ToolLatencyStats.Record(toolName, ms);
                Log($"[TOOL-LATENCY] tool={toolName} ms={(long)ms}");
            }
            catch { /* instrumentation must never break the call */ }
        }

        private static JObject BuildWorkerRpcRequest(JObject workerCommand, string requestId, string? operationId = null)
        {
            var rpc = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = workerCommand["module"]?.ToString() ?? string.Empty,
                ["action"] = workerCommand["action"]?.DeepClone(),
                ["target"] = workerCommand["target"]?.DeepClone(),
                ["payload"] = workerCommand["payload"]?.DeepClone(),
                // Hoist dryRun alongside action/target/payload: several worker handlers
                // (Refactor, index, build, run, github) read it from the top level of the
                // request, but it only ever arrived nested under params — so dryRun was
                // silently dropped and those previews executed for real. Carry it up too.
                ["dryRun"] = workerCommand["dryRun"]?.DeepClone(),
                ["params"] = workerCommand.DeepClone()
            };

            if (!string.IsNullOrWhiteSpace(operationId))
            {
                rpc["_meta"] = new JObject
                {
                    ["progressToken"] = operationId
                };
            }

            return rpc;
        }

        // Safety ceiling for waiting on worker SDK-ready before billing the op timeout.
        // Generous (cold-start is ~50s); only caps a wedged/never-ready worker.
        private const int WorkerSdkReadyCeilingMs = 180000;

        // issue #25 #2: read-only / idempotent tools that are safe to re-send once
        // after a worker crash. Writes/edits/builds are deliberately excluded — a
        // blind resend of a mutation could double-apply. The gateway already eagerly
        // respawns the worker; this retry hides the transient "crashed/exited" error
        // from the client for reads so the agent doesn't have to reconnect + re-issue.
        private static readonly HashSet<string> RetrySafeReadTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genexus_read", "genexus_list_objects", "genexus_inspect", "genexus_query",
            "genexus_search_source", "genexus_analyze", "genexus_structure", "genexus_navigation",
            "genexus_whoami", "genexus_doctor"
        };

        private static bool IsWorkerCrashEnvelope(JObject workerResponse)
        {
            var err = workerResponse?["error"];
            string msg = err is JObject eo ? eo["message"]?.ToString() : err?.ToString();
            return !string.IsNullOrEmpty(msg) &&
                   msg.IndexOf("crashed/exited", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldRetryWorkerCrash(JObject workerResponse, string toolName, int attempt)
        {
            return attempt == 1
                && !string.IsNullOrEmpty(toolName)
                && RetrySafeReadTools.Contains(toolName)
                && IsWorkerCrashEnvelope(workerResponse);
        }

        private static async Task<JObject?> SendWorkerCommandAsync(
            JObject workerCommand,
            int timeoutMs,
            string timeoutLogMessage,
            Func<JObject, JObject> onSuccess,
            Func<string?, string, JObject> onTimeout,
            string toolName = "unknown",
            JObject? toolArgs = null,
            bool trackOperation = false,
            JToken? progressToken = null,
            Func<JObject, Task>? heartbeat = null)
        {
            string requestId = Guid.NewGuid().ToString();
            string correlationId = Guid.NewGuid().ToString("N");
            string? operationId = null;

            if (trackOperation)
            {
                operationId = _operationTracker.StartOperation(requestId, toolName, toolArgs, correlationId);
                BroadcastNotification("notifications/message", new
                {
                    level = "info",
                    logger = "operation",
                    data = $"Operation {operationId} started for tool {toolName}.",
                    operationId,
                    correlationId,
                    status = "Running",
                    timestamp = DateTime.UtcNow
                });
            }

            workerCommand["correlationId"] = correlationId;

            // issue #25 #2: idempotent single retry for read-only tools. When a worker
            // crashes mid-call the completion resolves with a "crashed/exited" envelope;
            // the gateway already eagerly respawns, so for retry-safe reads we re-send
            // once to the replacement instead of surfacing the transient error (which
            // forced the user to manually /mcp reconnect and re-issue).
            int workerAttempt = 0;
            while (true)
            {
                workerAttempt++;
                string attemptRequestId = workerAttempt == 1 ? requestId : Guid.NewGuid().ToString();
                var workerRequest = BuildWorkerRpcRequest(workerCommand, attemptRequestId, operationId);
                var worker = await GetActiveWorkerAsync();

                // Don't bill worker cold-start against the per-tool timeout. If the worker is
                // still initializing (SDK init ~50s on a large KB), wait for its sdk_ready signal
                // FIRST — emitting progress heartbeats so the client stays alive — and only then
                // start the operation's timeout clock below. Capped so a wedged worker can't block
                // forever; on cap we proceed and let the normal op timeout apply.
                if (!worker.IsSdkReady)
                {
                    bool ready = await McpRouter.AwaitWithHeartbeat(
                        worker.SdkReadyTask, WorkerSdkReadyCeilingMs, progressToken, heartbeat, $"{toolName} (worker starting)");
                    if (!ready)
                        Log($"[Gateway] worker not SDK-ready after {WorkerSdkReadyCeilingMs}ms for tool {toolName}; proceeding — op timeout applies.");
                }

                var pending = new PendingWorkerRequest
                {
                    CompletionSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously),
                    ToolName = toolName,
                    CorrelationId = correlationId,
                    OperationId = operationId,
                    CreatedAtUtc = DateTime.UtcNow,
                    WorkerAlias = worker.Kb?.NormalizedAlias
                };
                _pendingRequests[attemptRequestId] = pending;
                // A worker-crash retry mints a fresh attemptRequestId; the worker's completion
                // comes back keyed by it, so link it to the operation or CompleteFromWorker misses
                // and the op record stays "Running" forever. Idempotent on the first attempt.
                if (operationId != null)
                {
                    _operationTracker.LinkRequest(attemptRequestId, operationId);
                }

                await worker.SendCommandAsync(workerRequest.ToString(Formatting.None));

                if (timeoutMs <= 0)
                {
                    var workerResponse = JObject.Parse(await pending.CompletionSource.Task.ConfigureAwait(false));
                    if (ShouldRetryWorkerCrash(workerResponse, toolName, workerAttempt))
                    {
                        Log($"[Retry] {toolName} hit worker crash on attempt {workerAttempt}; re-sending to replacement worker.");
                        await Task.Delay(750).ConfigureAwait(false);
                        continue;
                    }
                    if (workerResponse["result"] is JObject workerResultObjNoTimeout && workerResultObjNoTimeout["correlationId"] == null)
                    {
                        workerResultObjNoTimeout["correlationId"] = correlationId;
                    }
                    if (workerResponse["error"] is JObject workerErrorObjNoTimeout && workerErrorObjNoTimeout["correlationId"] == null)
                    {
                        workerErrorObjNoTimeout["correlationId"] = correlationId;
                    }
                    RecordToolLatency(toolName, pending.CreatedAtUtc);
                    return onSuccess(workerResponse);
                }

                // MCP-spec keepalive for long synchronous tool calls: while waiting on the
                // worker, emit `notifications/progress` every HeartbeatIntervalSeconds when the
                // client supplied a progressToken, so it doesn't fire its own request timeout
                // (the -32001 "Request timed out" users hit on long apply_pattern / delete).
                // The call stays synchronous and returns the real result inline — not a job.
                //
                // A4: without a client progressToken we can't keep the connection alive, so
                // blocking the stdio response for the full (multi-minute) timeout makes the
                // client treat the request as dead (~120s) and shove it to the background —
                // exactly the "batch specify fell to background" symptom. For a TRACKED op we
                // already have an interim "still running, poll op:<id>" envelope (onTimeout),
                // so cap the synchronous wait at the safe window; the op keeps running in the
                // worker and the client re-polls. Non-tracked calls and calls WITH a token are
                // unchanged (full timeout + heartbeats).
                bool noClientProgressToken = progressToken == null
                    || progressToken.Type == JTokenType.Null;
                int effectiveTimeoutMs = timeoutMs;
                if (noClientProgressToken && !string.IsNullOrWhiteSpace(operationId))
                    effectiveTimeoutMs = Math.Min(timeoutMs, McpRouter.SafeLongPollSecondsWithoutProgress * 1000);
                bool workerCompleted = await McpRouter.AwaitWithHeartbeat(
                    pending.CompletionSource.Task, effectiveTimeoutMs, progressToken, heartbeat, toolName);
                if (workerCompleted)
                {
                    var workerResponse = JObject.Parse(await pending.CompletionSource.Task);
                    if (ShouldRetryWorkerCrash(workerResponse, toolName, workerAttempt))
                    {
                        Log($"[Retry] {toolName} hit worker crash on attempt {workerAttempt}; re-sending to replacement worker.");
                        await Task.Delay(750).ConfigureAwait(false);
                        continue;
                    }
                    if (workerResponse["result"] is JObject workerResultObj && workerResultObj["correlationId"] == null)
                    {
                        workerResultObj["correlationId"] = correlationId;
                    }
                    if (workerResponse["error"] is JObject workerErrorObj && workerErrorObj["correlationId"] == null)
                    {
                        workerErrorObj["correlationId"] = correlationId;
                    }
                    RecordToolLatency(toolName, pending.CreatedAtUtc);
                    return onSuccess(workerResponse);
                }
                break; // timeout — fall through to the timeout handling below
            }

            if (!string.IsNullOrWhiteSpace(operationId))
            {
                _operationTracker.MarkTimeout(operationId);
                BroadcastNotification("notifications/message", new
                {
                    level = "warning",
                    logger = "operation",
                    data = $"Operation {operationId} is still running after timeout budget.",
                    operationId,
                    correlationId,
                    status = "Running",
                    timestamp = DateTime.UtcNow
                });
            }
            else
            {
                _pendingRequests.TryRemove(requestId, out _);
            }

            Log($"{timeoutLogMessage} (operationId={operationId ?? "n/a"}, correlationId={correlationId})");
            return onTimeout(operationId, correlationId);
        }

        // Issue #27 item 1: self-healing status/result reconciliation.
        //
        // The async-build background poller (in the genexus_lifecycle build intercept) is
        // fire-and-forget: it's the ONLY thing that flips a JobEntry from "running" to a
        // terminal state. If that task wedges — stale worker pipe after a soft-reload,
        // STA serialization behind a long SDK call, or a worker recycle that drops the
        // in-memory _tasks map — the job stays "running" forever and every action=status /
        // action=result poll returns "running" / "Pending" indefinitely (the exact symptom
        // reported: plain status shows isBusy=false/Ready, yet the job never resolves).
        //
        // This makes the READ path self-healing: before returning the passive JobEntry
        // envelope, actively re-query the worker's build-task status for the job's stored
        // WorkerTaskId and reconcile the JobEntry to its real terminal state. Cheap (one
        // short worker round-trip) and only runs while the job is still "running".
        private static async Task ReconcileJobWithWorkerAsync(JobEntry job, string toolName, JObject? toolArgs)
        {
            try
            {
                if (job == null) return;
                if (!string.Equals(job.Status, "running", StringComparison.OrdinalIgnoreCase)) return;
                if (string.IsNullOrEmpty(job.WorkerTaskId)) return;

                var statusCmd = new JObject
                {
                    ["module"] = "Build",
                    ["action"] = "Status",
                    ["target"] = job.WorkerTaskId
                };

                JObject? statusEnv = await SendWorkerCommandAsync(
                    statusCmd,
                    8000,
                    $"Timeout reconciling job status (job={job.Id}, workerTask={job.WorkerTaskId})",
                    env => env,
                    (_, correlationId) => new JObject { ["error"] = "reconcile timeout", ["correlationId"] = correlationId },
                    toolName: toolName, toolArgs: toolArgs, trackOperation: false);

                JObject? ws = (statusEnv?["result"] as JObject) ?? statusEnv;

                var verdict = McpRouter.ClassifyWorkerBuildStatus(ws);
                if (verdict == null) return; // still running / transient — leave running, next poll retries

                var (success, summary, result) = verdict.Value;
                if (result["workerTaskId"] == null) result["workerTaskId"] = job.WorkerTaskId;
                JobRegistry.Complete(job.Id, success, summary, result);
                Log($"[AsyncBuild] Reconcile resolved job={job.Id} success={success} (background poller had not yet completed it).");
            }
            catch (Exception ex)
            {
                // Reconciliation is best-effort — never let it break the status/result read.
                Log($"[AsyncBuild] Reconcile failed for job={job?.Id}: {ex.Message}");
            }
        }

        internal static int GetToolTimeoutMs(string? toolName, JObject? args)
        {
            if (toolName == "genexus_lifecycle" || toolName == "genexus_analyze" || toolName == "genexus_test")
            {
                return 600000;
            }

            // genexus_gxserver update/commit talk to the GeneXus Server and apply a full
            // changelist — on a large KB that runs for minutes (the 60s default cut it off
            // while the worker was still legitimately applying). Reads (status/pending/…) are
            // fast but share the tool name, so the generous ceiling is harmless for them.
            if (toolName == "genexus_gxserver")
            {
                return 600000;
            }

            // genexus_db action=reorg_impact / reorg_preview / drift_check with deep=true
            // runs ISpecifierService.ImpactDatabase — a full specification pass that is
            // build-heavy and can take minutes on a large KB (the 60s default cut it off
            // while the worker was still legitimately working). Fast (deep=false) reorg/
            // drift reads share the tool name, so the generous ceiling is harmless for them.
            if ((string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(toolName, "genexus_db_drift", StringComparison.OrdinalIgnoreCase))
                && args?["deep"]?.ToObject<bool?>() == true)
            {
                return 600000;
            }

            string? part = args?["part"]?.ToString();
            if (string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(part, "Layout", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Events", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "PatternVirtual", StringComparison.OrdinalIgnoreCase))
                {
                    return 180000;
                }
            }

            if (string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(part, "Source", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Events", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Rules", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(part, "Variables", StringComparison.OrdinalIgnoreCase))
                {
                    return 300000;
                }
            }

            // apply_pattern (esp. reapply) runs the WWP projection step, which on a
            // large host or an IDE-tab-held object takes minutes. The worker bounds it
            // with GENEXUS_MCP_REAPPLY_TIMEOUT_MS (default 5 min); align the gateway
            // ceiling so the client doesn't get a premature -32001 mid-reapply while the
            // worker is still legitimately working.
            if (string.Equals(toolName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase))
            {
                int reapplyMs = 300000;
                var envVal = Environment.GetEnvironmentVariable("GENEXUS_MCP_REAPPLY_TIMEOUT_MS");
                if (!string.IsNullOrWhiteSpace(envVal) && int.TryParse(envVal, out var parsed) && parsed > 0)
                    reapplyMs = parsed;
                // Add a 30s gateway-side cushion over the worker's own hard-timeout
                // window so that when the projection DOES return near the deadline, the
                // client receives the worker's rich envelope (slowReapply / recoveryRequired
                // / recoveryHint) rather than a bare transport -32001. If the STA call never
                // returns, the gateway times out here and recoveryHint tells the agent to
                // genexus_worker_reload mode=hard — the worker can't self-abort an STA SDK call.
                return reapplyMs + 30000;
            }

            return 60000;
        }

        // genexus_gxserver update/commit talk to the server and can run many minutes on a stale
        // KB (a first update applied ~850 objects, exceeding even the 600s sync ceiling). With
        // async=true they go through the same background-job path as edits: return an
        // operationId immediately, poll via genexus_lifecycle status/result. Reads
        // (status/pending/conflicts/history) and lock are fast and always stay synchronous.
        internal static bool IsAsyncGxServerAction(string? toolName, JObject? args)
        {
            if (!string.Equals(toolName, "genexus_gxserver", StringComparison.OrdinalIgnoreCase)) return false;
            string? action = args?["action"]?.ToString()?.ToLowerInvariant();
            return action == "update" || action == "commit";
        }

        internal static bool IsAsyncMutationTool(string? toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            return string.Equals(toolName, "genexus_edit", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(toolName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildAsyncAcceptedPayload(JobEntry job, string acceptedSummary)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            return new JObject
            {
                ["job_id"] = job.Id,
                ["operationId"] = job.Id,
                ["status"] = "running",
                ["estimated_seconds"] = job.EstimatedSeconds,
                ["pollTarget"] = "op:" + job.Id,
                ["hint"] = acceptedSummary + " poll genexus_lifecycle(action='status'|'result', target='op:" + job.Id + "') or watch _meta.background_jobs."
            };
        }

        // Issue #79: only edit/variable jobs carry the watchdog bound in their accepted
        // envelope — they are the tools whose SDK save can silently block. gxserver
        // update/commit is deliberately excluded: a server apply can legitimately run
        // arbitrarily long (an 850-object changelist exceeded even the 10 min sync
        // ceiling), so it gets no stall bound and no 'will be marked stalled' promise.
        internal static JObject BuildAsyncMutationAcceptedPayload(JobEntry job, string acceptedSummary)
        {
            var payload = BuildAsyncAcceptedPayload(job, acceptedSummary);
            int boundSeconds = AsyncEditWatchdogMs(job.EstimatedSeconds) / 1000;
            payload["stallBoundSeconds"] = boundSeconds;
            payload["hint"] = payload["hint"]!.ToString()
                + " If it stays 'running' past " + boundSeconds
                + "s the SDK call is blocked (an IDE modal dialog can hold the model) or retrying a failing validation — the job will be marked 'stalled' with recovery steps AND the wedged worker process will be recycled (force-killed and respawned) so the KB stays usable; you can also cancel earlier with genexus_lifecycle action=cancel.";
            return payload;
        }

        internal static JObject BuildAsyncEditAcceptedPayload(JobEntry job)
            => BuildAsyncMutationAcceptedPayload(job, "Edit accepted;");

        internal static JObject BuildAsyncVariableAcceptedPayload(JobEntry job)
            => BuildAsyncMutationAcceptedPayload(job, "Variable update accepted;");

        internal static JObject BuildAsyncLifecycleAcceptedPayload(JobEntry job, string? action)
        {
            string acceptedSummary = string.Equals(action, "validate", StringComparison.OrdinalIgnoreCase)
                ? "Validate accepted;"
                : string.Equals(action, "rebuild", StringComparison.OrdinalIgnoreCase)
                    ? "Rebuild accepted;"
                    : "Build accepted;";
            return BuildAsyncAcceptedPayload(job, acceptedSummary);
        }

        internal static string BuildAsyncMutationCompletionSummary(string? toolName, bool success)
        {
            if (string.Equals(toolName, "genexus_gxserver", StringComparison.OrdinalIgnoreCase))
                return success ? "GXserver operation succeeded" : "GXserver operation failed";
            bool isVariableTool = string.Equals(toolName, "genexus_variable", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(toolName, "genexus_add_variable", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(toolName, "genexus_delete_variable", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(toolName, "genexus_modify_variable", StringComparison.OrdinalIgnoreCase);
            if (isVariableTool)
            {
                return success ? "Variable update succeeded" : "Variable update failed";
            }

            return success ? "Edit succeeded" : "Edit failed";
        }

        // Issue #79: the async edit/variable/gxserver path waits on the SDK with NO
        // timeout (timeoutMs=0), so a blocked SDK call — an IDE modal dialog holding the
        // model, or the SDK retrying a failing validation internally — left the job
        // 'running' forever with no actionable signal. This watchdog converts that dead
        // end into a terminal "stalled" state after a generous multiple of the caller's
        // estimate, so a legitimate slow write still finishes while a genuinely stuck
        // one surfaces with recovery steps.
        //
        // Bound = max(10 min, min(est × 8, 60 min)); override with
        // GXMCP_ASYNC_JOB_WATCHDOG_S (seconds, 0 disables the watchdog).
        internal static int AsyncEditWatchdogMs(int estimatedSeconds)
        {
            var envVal = Environment.GetEnvironmentVariable("GXMCP_ASYNC_JOB_WATCHDOG_S");
            if (!string.IsNullOrWhiteSpace(envVal) && int.TryParse(envVal, out var parsed))
            {
                return parsed <= 0 ? int.MaxValue : parsed * 1000; // 0/negative disables
            }
            long boundSeconds = Math.Max(600L, Math.Min(estimatedSeconds * 8L, 3600L));
            return (int)(boundSeconds * 1000);
        }

        // Plan 069: `workerRecycled` reports whether the wedged worker process was
        // force-recycled (RecycleStalledWorker) the moment the stall was detected, so
        // the envelope can tell the agent the KB is coming back instead of leaving it
        // to rediscover the dead worker on the next call.
        internal static JObject BuildStalledAsyncMutationEnvelope(string jobId, string toolName, int estimatedSeconds, int boundSeconds, bool workerRecycled = false)
        {
            string boundText = boundSeconds > 0
                ? "did not return within the " + boundSeconds + "s time bound (caller estimated " + estimatedSeconds + "s)"
                : "did not return within the configured time bound (caller estimated " + estimatedSeconds + "s; watchdog disabled)";
            var envelope = new JObject
            {
                ["status"] = "stalled",
                ["code"] = "AsyncJobStalled",
                ["tool"] = toolName,
                ["jobId"] = jobId,
                ["estimated_seconds"] = estimatedSeconds,
                ["boundSeconds"] = boundSeconds,
                ["message"] = "The SDK operation " + boundText
                    + ". The write is likely blocked by a modal dialog in the GeneXus IDE holding the model (e.g. \"object modified externally — reload?\"), or the SDK is retrying a failing validation internally. This job is now terminal; it will not keep 'running'.",
                ["hint"] = "1) Run the same edit WITHOUT async=true to get the immediate SDK error (the sync path surfaces TransactionFailed/srcXXXX in seconds). 2) Or cancel with genexus_lifecycle action=cancel target=op:" + jobId + " and check the IDE for a waiting dialog. 3) genexus_read the object before retrying — the write may have partially persisted."
            };
            if (workerRecycled)
            {
                envelope["recycledWorker"] = true;
                envelope["workerRecovery"] = "The wedged worker process was force-recycled the moment the stall was detected and a replacement worker is respawning for this KB (WorkerStopReason.Wedged → eager respawn). Subsequent tool calls should proceed normally; re-run the edit only after the replacement is up (check genexus_whoami or genexus_kb action=list), and genexus_read the object first — the write may have partially persisted.";
            }
            return envelope;
        }

        internal static void NormalizeEditAndBuildPayload(JObject? payload)
        {
            if (payload == null) return;
            if (payload["build"] is not JObject buildBlock) return;

            string? taskId = buildBlock["taskId"]?.ToString() ?? buildBlock["TaskId"]?.ToString();
            if (string.IsNullOrWhiteSpace(taskId)) return;

            if (buildBlock["pollTarget"] == null)
            {
                // edit_and_build currently orchestrates its caller rebuild entirely on
                // the worker side, so the follow-up handle is the worker build taskId,
                // not a gateway background-job operationId.
                buildBlock["pollTarget"] = taskId;
            }

            if (buildBlock["hint"] == null)
            {
                buildBlock["hint"] = "Poll genexus_lifecycle(action='status'|'result', target='" + taskId + "') for the caller rebuild.";
            }
        }

        internal static bool IsSuccessfulBackgroundToolCompletion(JObject? workerEnvelope)
        {
            if (workerEnvelope == null) return false;
            if (workerEnvelope["error"] != null) return false;

            string? outerStatus = workerEnvelope["status"]?.ToString();
            if (string.Equals(outerStatus, "Error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outerStatus, "Running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outerStatus, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            JObject? resultObj = workerEnvelope["result"] as JObject;
            if (resultObj == null && workerEnvelope["result"]?.Type == JTokenType.String)
            {
                string? raw = workerEnvelope["result"]?.ToString();
                if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try { resultObj = JObject.Parse(raw); }
                    catch { }
                }
            }

            if (resultObj == null) return true;
            if (resultObj["error"] != null) return false;
            if (resultObj["isError"]?.ToObject<bool?>() == true) return false;

            string? innerStatus = resultObj["status"]?.ToString();
            if (string.Equals(innerStatus, "Error", StringComparison.OrdinalIgnoreCase)
                || string.Equals(innerStatus, "Running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(innerStatus, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(innerStatus, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
    }
}
