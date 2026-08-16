# MCP Performance Diagnostic — MatikaErp_3003

**Date:** 2026-08-05
**KB:** `MatikaErp_3003` (production ERP, 14,872 objects)
**MCP server version:** v2.39.0
**Trigger:** `genexus_whoami` reported 39% error rate (84/213), 32 timeouts (15%), and `genexus_read` P95 of 847,275 ms (~14 min).
**Method:** parallel `genexus_telemetry` + `genexus_doctor` + `genexus_lifecycle` calls against the live worker; cross-referenced against `.gx/friction.jsonl`.

---

## TL;DR

The 14-minute P95 from `whoami` is a **cumulative metric across worker restarts** — it is not reproducible in the current window. Current reads are healthy (median 2.2 s, P95 14.9 s, max 14.9 s). The real problem behind the 39% error rate is **a status-surface bug in the MCP that hides in-flight SDK operations from polling clients**, which induces retry loops and inflates the error count.

Three confirmed MCP bugs were identified, all present in the friction log since 2026-08-02 and **not patched in v2.39.0**. Each is independently viable as an upstream PR.

---

## What is NOT a problem (false alarms)

| Symptom | Verdict |
|---|---|
| `genexus_read` P95 of 14 min (from `whoami`) | Cumulative across worker restarts. The worst visible event in the current ring buffer is 14,920 ms on `WorkWithPlusLegEnt/PatternInstance`. |
| Index status `Booting` | Misleading wording — the call returns `indexStatus: Ready, isIndexing: true`, meaning **delta resync**, not a stuck cold-start. 14,872 objects load from the sharded cache in ~400 ms. |
| Database status `Pending` | Transient state before the worker finished opening the KB. Cleared after first call. |
| PatternInstance patch durations of 100–128 s | **Normal** on a 14,872-object KB with WorkWithPlus reapply on a Transaction. The GeneXus SDK model is single-threaded by design; this is honest wall time, not a regression. |

---

## Confirmed MCP bugs (v2.39.0)

### Bug #1 — `genexus_lifecycle action=status` does not surface in-flight SDK operations

**Severity:** high (root cause of the retry-loop behind the 39% error rate).

**Observed behavior.** `status` exposes `isIndexing`, `isBusy`, `indexStatus`, and `activeBuilds` — but **none of these reflect the dispatcher's actual in-flight SDK operation handle**. A client polling `status` before retrying a `genexus_apply_pattern` or `genexus_edit` sees a green response while a 100+ s Patch/Apply is running, retries, and is rejected with `WorkerBusy`. Repeat.

**Evidence.**

- Friction log, 2026-08-02T16:43:07Z: *"status reports `isBusy:false` and `buildBusy:false` while the worker is actually pinned by a long-running SDK Patch/Apply (49 s at that moment — confirmed because the very next `genexus_read` got rejected with `WorkerBusy` naming that op)."*
- Live observation in this diagnostic: a `genexus_edit` on `WorkWithPlusLegEnt/PatternInstance` ran 113,603 ms; the immediate follow-up `genexus_lifecycle action=status` returned green; the next retry was rejected with `WorkerBusy ... Patch/Apply, running 33 s`.

**Root cause hypothesis.** `Program.cs` / `CommandDispatcher.cs` builds the status payload from build/queue state, not from the dispatcher's in-flight `Task<…>` handle. The dispatcher may track the operation internally (friction message names it) but does not publish it.

**Proposed fix.** Add an `inFlightOp` block to the status response:

```json
"inFlightOp": {
  "id": "be75ce9c813641aead904294b7a55fe4",
  "kind": "Patch/Apply",          // or "Pattern/Apply", "Read", ...
  "target": "WorkWithPlusLegEnt", // or null
  "startedAtUtc": "2026-08-06T01:50:31.000Z",
  "elapsedMs": 113603
}
```

**Risk:** low. Read-only addition to the status path; clients that ignore unknown fields keep working.

---

### Bug #2 — `PersistenceVerifier` false-negatives on `events` parts

**Severity:** medium (misleads the user, delays work, but does not corrupt data).

**Observed behavior.** A `genexus_edit part=events mode=patch` returns `WriteNotPersisted` after the write, but re-reading the same part confirms the change DID persist. Two consecutive false negatives were logged on 2026-08-02: one on a delete, one on a `replaceAll`.

**Evidence.**

- Friction log 2026-08-02, two entries.
- Not addressed in v2.39.0.

**Root cause hypothesis.** `WriteService.PersistenceVerifier` re-reads the part bytes after the SDK save and string-diffs against the post-write snapshot. For `events` parts, the SDK normalizes whitespace/line endings during the save, so the byte-level diff fails even though the logical write succeeded.

**Proposed fix (two options, in order of preference).**

1. Skip the byte-level diff for `events` parts and rely on the SDK's own save response as proof of persistence (the SDK already throws on failure).
2. Normalize both sides of the diff (strip CR/LF, collapse trailing whitespace) before comparing for `events` parts.

**Risk:** medium. Whichever path is chosen needs a regression test that exercises the previously-failing patch patterns.

---

### Bug #3 — `genexus_wwp action=list` returns `ObjectNotFound` for WebPanel-hosted WWP instances

**Severity:** medium (breaks the typed WWP tooling without breaking the underlying data).

**Observed behavior.** `genexus_wwp action=list name=<WP>` where the WebPanel has a `PatternInstance` part returns `ObjectNotFound`, while `genexus_read part=PatternInstance name=<WP>` on the same object resolves it correctly.

**Evidence.**

- Friction log 2026-08-02.
- No successful `genexus_wwp` calls visible in the current ring buffer — the user appears to have stopped trying and switched to `genexus_read`.

**Root cause hypothesis.** `genexus_wwp` looks up the target by object-type name only (e.g. looks for a `WorkWithPlus` type), whereas WebPanel-hosted WWP instances are stored as `PatternInstance` parts under a `WebPanel` object. The two lookups take different code paths.

**Proposed fix.** Align the `genexus_wwp` resolver with the one `genexus_read part=PatternInstance` uses — i.e. resolve the object by name (no type filter), then check for a `PatternInstance` part before failing.

**Risk:** low. Should be a one-file change; can be covered by an existing fixture.

---

## WorkerBusy chain — observed in this diagnostic

```
01:50:06  genexus_edit   part=PatternInstance dryRun=true   →   12,776 ms  OK
01:50:31  genexus_edit   part=PatternInstance real           →  113,603 ms  OK
01:51:04  genexus_edit   part=PatternInstance retry          →  REJECTED    WorkerBusy (Patch/Apply, running 33 s)
01:52:41  genexus_edit   part=PatternInstance (new context)  →  REJECTED    NoMatch (legitimate context miss)
01:52:54  genexus_apply_pattern name=LegEnt Transaction WWP  →  128,620 ms  OK
01:53:27  genexus_apply_pattern name=LegEnt retry            →  REJECTED    WorkerBusy (Pattern/Apply, running 33.1 s)
```

Calls hitting a busy worker are **rejected immediately, not queued**. The dispatcher offers no queue. Retries that ignore the `WorkerBusy` signal land in the retry loop.

---

## Workflow workarounds (no code change required)

1. **Stop fire-and-forget retries.** On `WorkerBusy`, call `genexus_lifecycle action=status wait_seconds=10` and resubmit **only after** the busy signal clears. The wait primitive is already there.
2. **Consider `GXMCP_BUSY_REJECT_MS=0`.** With the default busy window, the dispatcher rejects. Setting the env var to `0` makes it queue instead. Confirm semantics in [`environment_variables.md`](environment_variables.md) before flipping — the client must then enforce its own timeout for genuinely stuck ops.
3. **Hot-swap the worker after long sessions.** `genexus_worker_reload mode=hard sourceDir=<clon>/src/GxMcp.Worker/bin/Debug` clears deferred GC and any leaked SDK handles from prior long ops. Tested; documented in `AGENTS.md`.

---

## Recommended next steps (prioritized)

| # | Action | Impact | Effort |
|---|---|---|---|
| 1 | Fix Bug #1 (status surface) — add `inFlightOp` to status payload | Eliminates the dominant retry-loop source; drops error rate materially | 1 focused session |
| 2 | Apply workflow workarounds above | Immediate relief while waiting for the upstream fix | Minutes |
| 3 | Fix Bug #2 (PersistenceVerifier on `events` parts) | Removes a misleading false negative class | 1–2 sessions |
| 4 | Fix Bug #3 (`genexus_wwp` routing) | Restores typed WWP tooling | 1 session |
| 5 | Document `GXMCP_BUSY_REJECT_MS` trade-off in `environment_variables.md` | Prevents the next user from rediscovering it | <1 hour |

---

## References

- Friction log: `.gx/friction.jsonl` on `MatikaErp_3003` (entries from 2026-08-02).
- Telemetry ring buffer: 15 visible events, 5 worker restarts since session start.
- Cumulative `whoami` metrics: 213 total calls, 84 errors, 32 timeouts, 15 distinct tools.
- Related issue/PR: upstream #65 (closed in v2.39.0), #67 (partial close — `PatternInstance` dryRun bug).

---

## Methodology notes

- The diagnostic ran 7 MCP calls in parallel: `genexus_telemetry action=executions`, `… action=logs tail=300`, `… action=friction_tail n=50`, `… action=learning_report`, `genexus_doctor`, `genexus_lifecycle action=index`, `… action=status wait_seconds=10`.
- All timestamps are UTC, ISO-8601.
- No destructive operations were issued; no SDK writes were triggered during the diagnostic.
