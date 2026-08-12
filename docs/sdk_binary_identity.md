# GeneXus 18 SDK — binary identity and coverage map

What `GeneXus.exe` actually is, which of its assemblies the MCP already exploits, and
which ones are still unexplored territory for new tools.

Reproduce everything here with two read-only scripts (neither writes to the GeneXus
install directory):

```powershell
powershell.exe -File scripts\sdk_reflection\identify_gx_binary.ps1 -Inventory
powershell.exe -File scripts\sdk_reflection\map_sdk_coverage.ps1 -Top 0
```

> Run them under **Windows PowerShell 5.1** (`powershell.exe`), not `pwsh`.
> `ReflectionOnlyLoadFrom` — needed for referenced-assembly and service-interface counts —
> throws `PlatformNotSupportedException` on .NET Core. The PE-header analysis works on
> both editions; only the reflection sections degrade, and they say so explicitly.

Measured on GeneXus `18.0.13.55666`, 2026-08-12.

---

## 1. Identity of `GeneXus.exe`

| Fact | Value | How it is proven |
|---|---|---|
| Managed? | **Yes — C# / .NET** | PE data directory 14 (CLI header) present at RVA `0x2008`, size 72; metadata root signature `BSJB` |
| Assembly | `GeneXus, Version=11.0.0.0, Culture=neutral, PublicKeyToken=560b7a861f66753b` | `AssemblyName.GetAssemblyName()` |
| Runtime | `v4.0.30319` | metadata root version string |
| Target framework | `.NETFramework,Version=v4.7.1` | `GeneXus.exe.config` → `supportedRuntime … sku` |
| COR20 flags | `0x0002000B` | CLI header `Flags` field |
| Platform | **AnyCPU, 32-bit preferred** | `32BITREQUIRED` **and** `32BITPREFERRED` both set; `ProcessorArchitecture = MSIL` |
| Strong-named | Yes | `STRONGNAMESIGNED` bit |
| Product version | `18.0.13.186738+8371a17…` | Win32 `VersionInfo` |
| UI stack | WinForms + Infragistics `11.1` | referenced assemblies; root namespace `Genexus.Win` |
| Direct references | 33 (16 GeneXus, 7 third-party, 10 BCL) | `GetReferencedAssemblies()` |

### Do not misread the platform bits

`32BITREQUIRED` on its own would mean an x86-only image. Set **together with**
`32BITPREFERRED`, as it is here, it is the .NET 4.5+ encoding of the *"Prefer 32-bit"*
build option: an MSIL image that merely **runs in a 32-bit process**. Reading only the
`32BITREQUIRED` bit and concluding "x86-only" is an easy and common mistake — the
identity script decodes both bits precisely to avoid it.

**The practical consequence is unchanged:** the host process is 32-bit, and the install
ships 30 native DLLs (`Gxasodbc.dll`, `gxcadll.dll`, `ProtKey.dll`, `libcef.dll`, …) that
are x86. `GxMcp.Worker.csproj:5-12` correctly declares `net48` + `PlatformTarget x86`.
Do not "modernise" that to AnyCPU or x64.

### Install directory

| | |
|---|---|
| Top-level DLLs | **374** |
| Managed | **344** |
| Native | **30** |
| `Artech.*` | **109** |

Managed-vs-native is decided by the presence of the CLI header, i.e. the same test the
CLR loader makes — no assembly is ever loaded to classify it.

---

## 2. Coverage map — what the MCP actually uses

Of every managed GeneXus-family assembly on disk (install root **and** `Packages\`):

| Bucket | Count | Meaning |
|---|---:|---|
| **Referenced** | **18** | bound by `GxMcp.Worker.csproj`; reachable today |
| **ProbeOnly** | **51** | loaded transitively at runtime, never referenced explicitly |
| **Untouched** | **146** | never referenced, never loaded — **never inspected** |
| Total | **215** | |

### Why the existing backlog under-reports the surface

`SdkSurfaceProbe.Run()` enumerates **`AppDomain.CurrentDomain.GetAssemblies()`**
(`src/GxMcp.Worker/Services/SdkSurfaceProbe.cs:78`). It can only describe assemblies the
worker has **already loaded**. An assembly that is never referenced, and never pulled in
transitively, is *structurally invisible* to it.

That is not a bug in the probe — it is the consequence of its design. But it does mean
the backlog derived from it (`docs/sdk_uncovered_endpoints_2026-07-20.md`,
`docs/sdk_coverage_gap_matrix.md`) describes the frontier of **what we already load**, not
the frontier of **what exists**. `scripts/sdk_reflection/map_sdk_coverage.ps1` closes the
gap by starting from the filesystem instead of the AppDomain.

---

## 3. Candidates for new tools

Untouched assemblies exposing `I*Service` interfaces — the shape the worker already knows
how to consume through `SdkServiceResolver` / `SdkServiceLocator`, so the cheapest path
from "assembly exists" to "tool exists":

| Assembly | Service interfaces | Public types |
|---|---|---:|
| `GeneXus.Server.Contracts` | `IKBModelObjectsService`, `ISyndicationService`, `ITeamWorkService` | 58 |
| `Artech.Editors.Common.Report` | `IIdCreationService`, `IKBObjectContextService` | 36 |
| `Artech.Genexus.Debugx.SocketServer` | `IIMageService` | 19 |
| `GeneXus.TeamDevClient.Architecture.UI` | `IContinuousIntegrationService` | 3 |

Large untouched assemblies with **no** service interface — they would need a different
entry-point shape (concrete classes, static helpers) and are therefore more expensive:

| Assembly | Public types | Note |
|---|---:|---|
| `Genexus.Web.UI.Common` | 761 | |
| `Artech.K2B.Common` | 657 | K2B pattern family |
| `Artech.Gxpm.Interop` | 365 | BPM |
| `Artech.Generator.SmartDevices` | 317 | |
| `Artech.Specifier.Helper` | 98 | adjacent to the specification chain (§4) |
| `Artech.ReverseEngineering.Core` | 95 | |

**Vet each candidate before planning work.** Two known traps:

- **`GeneXus.TeamDevClient.Architecture.UI`** exposes `IContinuousIntegrationService`, but
  the `.BL` sibling — already referenced (`GxMcp.Worker.csproj:107`) — is what
  `genexus_gxserver action=pipeline_*` uses. The UI-side services generally do not resolve
  in a headless worker; see the "wall" table in `docs/sdk_endpoints_roadmap.md`. Likely a
  false positive.
- A service interface existing does **not** mean it resolves. Many are not registered in
  the headless service registry and need `SdkServiceLocator.ConstructOrResolve<T>()` with a
  concrete implementation constructed by hand (`Helpers/SdkServiceLocator.cs:40`).

---

## 4. The build / syntax-checking chain (already wired)

Per-object build and syntax validation **already exist**. This is the most frequently
re-discovered part of the codebase, so it is written down here.

### Syntax is validated in TWO places, and the first one is the write

This is the part that surprises people. Validation does not start at `specify` — the
GeneXus SDK parses the Source **during `Save`**, so a syntax error never reaches the
specifier at all:

| Layer | When | Codes | Cost |
|---|---|---|---|
| **1. Write-time** — SDK `Save` parses the part | on every write | `src####` | immediate |
| **2. Specify** — Spec+Gen pass | on `action=specify` / build | `spc####`, `gen####` | seconds to minutes |

Measured live against a 14,988-object KB (2026-08-12), creating throwaway Procedures via
`genexus_create action=object_atomic … validate=true rollbackOnFailure=true`. Three
deliberately broken sources, **none of which reached the specify pass**:

| Broken source | Outcome |
|---|---|
| `If` with no `EndIf` | `SDK Save Exception` — `src0057: Esperando o comando 'EndIf' para fechar o bloco 'If' (Source, Linha: 2, Char: 1)` |
| `Call(ZZObjetoInexistenteXYZ)` — valid syntax, missing callee | `SDK Save Exception` — `src0287: O programa '…' não existe ou está inacessível (Source, Linha: 2, Char: 6)` |
| `for each` with no determinable base table | `The SDK save completed, but the persisted part does not match the requested content` |

What this proves:

- **The write is the first validator, and it does far more than parse.** Case 2 is
  syntactically perfect; the SDK still rejected it at `Save` because it **resolves object
  references** during the save. Do not reach for `action=specify` to catch a missing
  `EndIf` or a bad `Call` — the write already refused both, in milliseconds, with exact
  line and character.
- `validate=true` never executed in any of the three runs, because step `source` never
  succeeded. `rollbackOnFailure=true` behaved exactly as documented: all three objects
  were verified absent afterwards (`ObjectNotFound`), leaving the KB clean.
- `src####` codes are classified as **specification failures** by the build-diagnostics
  classifier added in v2.40.2 — consistent with what these errors actually are.
- Case 3 is a different mechanism again: not a validation failure but the **persistence
  verifier** (`TextPersistenceVerifier`, v2.40.2) refusing a save whose persisted content
  diverged from what was requested, because GeneXus normalised it. Note that
  `object_atomic` exposes no `verifyMode` knob — the `normalized|semantic|exact` escape
  hatch v2.40.2 added applies to `genexus_edit mode=patch`, not to this path.

> **Not verified in live testing:** an actual `spc####` / `gen####` diagnostic. Three
> attempts were all intercepted earlier in the pipeline. Reaching the specify pass
> requires an error the write-time validator genuinely cannot see — table-navigation and
> specification-time failures — which needs KB-specific attributes to construct. The
> `spc*`/`gen*` behaviour below is therefore documented **from code reading**, not from
> live observation. Treat it accordingly.

The happy path was verified in the same session: `genexus_lifecycle action=specify
target=<Procedure>` on a healthy object returned `Succeeded`, `0 errors / 0 warnings`,
with `buildPlan.expanded` holding exactly one object and `includeCallees: "none"` —
confirming `Specify` does not expand the call graph. Wall clock was ~100 s, but the phase
transitions (`InProcess-Specifying` at 6.8 s, `Specifying` at 33.8 s) show most of that
was **cold worker start-up**, not specification.

> The GeneXus model is single-threaded: the worker runs one SDK command at a time and
> rejects (rather than queues) concurrent calls with `WorkerBusy`. Expect that whenever a
> build, specify or a large read is in flight.

### From an MCP session

| Need | Call |
|---|---|
| "Is the syntax of this object valid?" | `genexus_lifecycle action=specify target=<obj>` — Spec+Gen only, no Compile, no deploy; returns `spc*`/`gen*` diagnostics |
| "Did my edit break the build?" | `genexus_lifecycle action=build mode=compile_check target=<obj>` — spec+gen+compile of the target **plus its transitive callers**, skipping the KB-wide `DeveloperMenu` regen |

`mode=compile_check` requires a target — scoping it is the entire point. For a
from-scratch full-KB compile use `action=build` with no target.

### Implementation

| Layer | Location | What it does |
|---|---|---|
| Spec-check | `Services/BuildService.cs:872` (`Specify`) | `SpecifyOneOnly` pass through the normal build-task pipeline |
| Compile-check | `Services/BuildService.cs:893` (`CompileCheck`) | caller closure capped at `CompileCheckDefaultCallerCap = 40`; echoes `CompileCheckTruncated` when bounded |
| Dispatch | `Services/CommandDispatcher.cs:1702` | `action == "Specify"` → `BuildService.Specify` |
| Edit→validate orchestration | `EditAndBuildOrchestrator.cs:126`, `SaveSpecifyOrchestrator.cs:188`, `AtomicAuthoringService.cs:115` | write and specify in one step |
| DB reorg impact | `ReorgImpactService.cs:217-218,736-737` | `ISpecifierService.ImpactDatabase` via `SdkServiceLocator.ConstructOrResolve<ISpecifierService>(() => new Artech.Packages.Specifier.Services.SpecifierService())` — build-heavy, opt-in with `deep=true` |
| Write diagnostics | `Services/WritePolicy.cs`, `Helpers/SdkDiagnosticsHelper.cs` | turns `src0208` / `src0216` / `src0233` into actionable hints |
| Language / parser | `Artech.Architecture.Language`, `Artech.Common.Language` (`csproj:51-58`) | GeneXus Basic |

A caller-closure cap exists because a base Business Component is called everywhere:
expanding it drags in fan-in orchestrators like the KB-wide `DeveloperMenu` and can cost
20–30 minutes, defeating the purpose of a fast check.

---

## 5. Service resolution — the two idioms

New tools must pick the right one, and the choice is not obvious:

| Interface implements `IGxService`? | Use | Location |
|---|---|---|
| Yes | `SdkServiceResolver.Resolve<T>()` — `Services.TryGetService<T>()` with bounded retries | `Helpers/SdkServiceResolver.cs:23` |
| No | `SdkServiceLocator.ConstructOrResolve<T>(factory)` — construct the concrete impl, cast to the interface | `Helpers/SdkServiceLocator.cs:40` |

Both return `null` rather than throwing. Services already resolved through the second
idiom: `ISpecifierService`, `IStatisticsService`, `IModelInformationService`,
`IDeploymentTargetService`, `ISecurityScannerService`, `ILibraryService`.

### Bootstrap order

`Program.cs:599-660` (`InitializeSdk`) must run before any of this works:

```
Artech.Architecture.Common      → ContextService.Initialize
Artech.Architecture.BL.Framework→ CommonServices.Initialize
Artech.Architecture.UI.Framework→ UIServices.Initialize
Artech.Genexus.Common           → KBModelObjectsInitializer.Initialize
Connector                       → Artech.Core.Connector.Initialize / Start
```

Assemblies are resolved at runtime by the global `AppDomain.AssemblyResolve` hook
(`Program.cs:220-227`) against `GX_PROGRAM_DIR`. Note the split: **`GX_PATH` is
build-time only** (`csproj:20-22`); **`GX_PROGRAM_DIR` is what the worker reads at
runtime** (`Program.cs:174-188`). `DoctorService` accepts either.

---

## 6. Build alignment

`map_sdk_coverage.ps1` cross-checks every `<Reference>` `HintPath` in the Worker csproj
against the filesystem. Current state on this machine:

- **`Microsoft.Build.Utilities.Core` is absent from the install.** Its reference is
  `Condition="Exists(…)"` (`csproj:35`), so the build does not fail — the reference is
  simply never applied, silently.
- All other referenced assemblies are present at `11.0.0.0`.

Watch this after every GeneXus upgrade: the csproj suppresses `MSB3277`
(`csproj:13`), so assembly-version conflicts **do not surface at build time**.

Two conditional references disable features silently when missing:

| Reference | csproj | Effect if absent |
|---|---|---|
| `GeneXus.SecurityScanner.Common` | `:102` | `HAS_SECURITY_SCANNER` undefined → `genexus_security action=scan_native` unavailable |
| `Microsoft.Build.Utilities.Core` | `:35` | reference dropped |

---

## 7. Known gaps in the tooling itself

- **`SdkProbeService` is dead code.** `src/GxMcp.Worker/Services/SdkProbeService.cs`
  implements a cached in-memory `ListTypes()` (`:66`) / `ListMethods(typeName)` (`:99`)
  with curated aliases (`:28-44`), but nothing instantiates it and no tool schema exposes
  it. It is the lightweight alternative to `genexus_sdk_probe`, which writes ~17 MB to
  disk. Wiring it up needs schema + router + dispatcher + golden fixture.
- **Doc/code mismatch:** `docs/sdk-probe/README.md` documents `GX_MCP_SDK_PROBE_DIR`, but
  `SdkSurfaceProbe.cs:244-268` reads `GX_MCP_REPO_ROOT`.

## Related

- `docs/sdk_gx18_discovery.md` — the source of the `InitializeSdk` bootstrap sequence
- `docs/sdk_uncovered_endpoints_2026-07-20.md` — ranked endpoint backlog (AppDomain-scoped; see §2)
- `docs/sdk_coverage_gap_matrix.md` — IDE-parity matrix
- `docs/sdk-probe/INDEX.md` — full type index of the assemblies the probe loaded
