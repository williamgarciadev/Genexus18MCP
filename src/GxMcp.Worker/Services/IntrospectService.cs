using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Reconnaissance before the magnifying glass. An agent that attacks a ~15,000-object KB
    /// with no peripheral vision burns its context before it knows which information matters.
    /// Overview() is the unconditional first call: it opens no object, touches no SDK, and
    /// answers only from fields the pass that built the index attempted for every object.
    ///
    /// The governing rule is not "report everything we have". It is: NEVER EMIT A FIELD WHOSE
    /// EMPTY VALUE WOULD BE A LIE. An empty "modules": {} invites the reader to conclude the KB
    /// has no modules; a named entry in suppressed[] carrying its own unlock cannot be misread.
    /// That is why sections disappear instead of degrading, and why the coverage block sits at
    /// the top of the result rather than in _meta — here the coverage IS the result.
    ///
    /// Overview deliberately never reads Calls / CalledBy / Tables / Complexity. Those are
    /// enrichment-only, sit near zero on a real lazily-enriched index (measured: Calls populated
    /// for 0 of 14,932 objects), and any ranking built on them ranks noise. Refusing to touch
    /// them is what makes this response trustworthy on a cold KB, and therefore safe to call
    /// first, every time.
    /// </summary>
    public class IntrospectService
    {
        private readonly IndexCacheService _indexCacheService;

        /// <summary>Sections built on a partial field are suppressed below this coverage.</summary>
        private const double SuppressionFloorPct = 60d;

        // Caps. The response must never reach the gateway's 60,000-char truncation path: if the
        // gateway has to truncate an introspect, these caps failed. Overview is a budget, not a dump.
        private const int MaxTypeRows = 40;
        private const int MaxContainerNames = 60;
        private const int MaxNamingCohorts = 12;

        // A prefix cohort is only worth reporting when it actually describes the type.
        private const int MinTypeSizeForCohorts = 20;
        private const double MinCohortSupportPct = 10d;
        private const int PrefixLength = 3;

        /// <summary>
        /// Object types whose mere presence indicates an adopted pattern. This is a COUNT of
        /// types, not architecture detection — see the notDetected block. Anything beyond
        /// counting would be invention: the worker has no detector for architectural idioms.
        /// </summary>
        private static readonly string[] PatternTypes =
        {
            "WorkWithPlus", "WorkWithPlusTemplate", "WorkWithDevices", "SDPanel", "API", "Pattern"
        };

        public IntrospectService(IndexCacheService indexCacheService)
        {
            _indexCacheService = indexCacheService;
        }

        public string Overview()
        {
            var state = _indexCacheService.GetState();
            string indexStatus = state?.Status ?? "Cold";

            SearchIndex index = null;
            try { index = _indexCacheService.GetIndex(); } catch { }

            var entries = (index?.Objects != null)
                ? index.Objects.Values.Where(e => e != null).ToList()
                : new List<SearchIndex.IndexEntry>();

            // A census taken mid-build is not a census. Reporting a half-populated index as if it
            // described the KB is the single worst failure mode of this tool, so it is called out
            // as a first-class field rather than left for the caller to infer from indexStatus.
            bool censusInProgress = entries.Count == 0
                || string.Equals(indexStatus, "Cold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(indexStatus, "Reindexing", StringComparison.OrdinalIgnoreCase);

            var snap = _indexCacheService.GetCoverageSnapshot(entries);
            var suppressed = new JArray();

            var result = new JObject
            {
                ["coverage"] = BuildCoverage(snap, indexStatus, censusInProgress)
            };

            if (censusInProgress)
            {
                result["censusInProgress"] = true;
                result["suppressed"] = Suppress(suppressed, "census", "indexNotReady",
                    "The index is still being built (indexStatus=" + indexStatus + ", " + entries.Count +
                    " entries so far). Any count taken now describes our progress, not the KB.",
                    "genexus_lifecycle", new JObject { ["action"] = "status", ["wait"] = 30 });

                return Models.McpResponse.Partial(
                    target: "_kb",
                    code: "CensusInProgress",
                    result: result,
                    warnings: new JArray("Counts are withheld until the index reaches LiteReady. Re-run after genexus_lifecycle action=status reports Ready."));
            }

            var byType = CountByType(entries);

            result["census"] = BuildCensus(entries.Count, byType);
            result["activity"] = BuildActivity(entries, snap);

            AddContainerInventory(result, entries, byType, suppressed);
            AddPlacement(result, entries, snap, suppressed);
            AddNamingCohorts(result, entries, byType);
            AddPatternAdoption(result, byType);
            AddIntegrity(result, byType);

            result["notDetected"] = BuildNotDetected();

            // Edges are never consulted by overview, so they are declared suppressed rather than
            // silently missing — the reader must not mistake their absence for "no dependencies".
            Suppress(suppressed, "callGraph", "edgesAreEnrichmentOnly",
                "Calls/CalledBy are written only by enrichment (" + snap.TrustOf("calledBy") +
                "). overview never reads them: on a lazily-enriched index a 0-caller object is almost always unread, not unused.",
                "genexus_introspect", new JObject { ["depth"] = "deep" });

            result["suppressed"] = suppressed;
            return Models.McpResponse.Ok(target: "_kb", result: result);
        }

        // ── coverage ────────────────────────────────────────────────────────────

        private static JObject BuildCoverage(CoverageSnapshot snap, string indexStatus, bool censusInProgress)
        {
            var fieldTrust = new JObject();
            foreach (var f in new[] { "name", "type", "description", "lastUpdate", "lastModifiedBy",
                                      "module", "folderPath", "calls", "calledBy" })
            {
                fieldTrust[f] = snap.TrustOf(f);
            }

            var coverage = new JObject
            {
                ["objectsInScope"] = snap.ObjectsInScope,
                ["indexStatus"] = indexStatus,
                ["censusInProgress"] = censusInProgress,
                ["enrichedInScope"] = snap.EnrichedInScope,
                ["enrichedPct"] = snap.EnrichedPct,
                ["structureResolvedInScope"] = snap.StructureResolvedInScope,
                ["structureResolvedPct"] = snap.StructureResolvedPct,
                ["distinctFolderPaths"] = snap.DistinctFolderPaths,
                ["fieldTrust"] = fieldTrust,
                ["vocabulary"] = new JObject
                {
                    ["complete"] = "written for every object; an absence is a fact about the KB",
                    ["observed:<pct>"] = "a cheap pass attempted every object; an absence is a fact about the KB",
                    ["partial:<pct>"] = "enrichment-only; an absence means NOT READ YET. Never reason over it",
                    ["unavailable"] = "nothing carries it, or the present value is synthesized; no section may lean on it"
                },
                ["doNotConclude"] = BuildDoNotConclude(snap)
            };

            return coverage;
        }

        /// <summary>
        /// The explicit false conclusions this exact index invites. Generated from the measured
        /// snapshot, not boilerplate: a warning that fires unconditionally gets ignored, and one
        /// that never fires on a healthy index keeps its force.
        /// </summary>
        private static JArray BuildDoNotConclude(CoverageSnapshot snap)
        {
            var a = new JArray();

            if (snap.EnrichedPct < 100d)
            {
                a.Add("0 callers does NOT mean unused: " + Fmt(snap.EnrichedPct) +
                      "% of the scope is enriched, so most objects were never opened to find their callers.");
            }

            if (snap.StructureResolvedInScope == 0 && snap.ObjectsInScope > 0)
            {
                a.Add("Do NOT read this KB as flat. Placement is unresolved for every entry, so folder/module " +
                      "filters operate on a synthesized single bucket. Rebuild with genexus_lifecycle action=index force=true.");
            }
            else if (snap.StructureResolvedPct < 100d && snap.StructureResolvedInScope > 0)
            {
                a.Add("Placement is resolved for " + Fmt(snap.StructureResolvedPct) +
                      "% of the scope; objects missing from a folder listing may simply be unresolved.");
            }

            if (snap.PctOf("description") < 100d)
            {
                a.Add("An object with no description is not undocumented by policy — description is " +
                      snap.TrustOf("description") + " across the scope.");
            }

            return a;
        }

        // ── census ──────────────────────────────────────────────────────────────

        private static Dictionary<string, int> CountByType(List<SearchIndex.IndexEntry> entries)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                string t = string.IsNullOrWhiteSpace(e.Type) ? "(untyped)" : e.Type;
                int n;
                d[t] = d.TryGetValue(t, out n) ? n + 1 : 1;
            }
            return d;
        }

        private static JObject BuildCensus(int total, Dictionary<string, int> byType)
        {
            var ordered = byType.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();

            var rows = new JObject();
            int shown = 0, tailCount = 0, tailTypes = 0;
            foreach (var kv in ordered)
            {
                if (shown < MaxTypeRows) { rows[kv.Key] = kv.Value; shown++; }
                else { tailCount += kv.Value; tailTypes++; }
            }

            var census = new JObject
            {
                ["total"] = total,
                ["distinctTypes"] = byType.Count,
                ["byType"] = rows
            };

            // A silent top-N would make byType look like the whole census and its sum disagree
            // with total for no visible reason. Name the tail instead.
            if (tailTypes > 0)
            {
                census["byTypeTruncated"] = new JObject
                {
                    ["hiddenTypes"] = tailTypes,
                    ["hiddenObjects"] = tailCount,
                    ["note"] = "Showing the " + MaxTypeRows + " largest types. byType + byTypeTruncated.hiddenObjects = total."
                };
            }

            return census;
        }

        // ── activity ────────────────────────────────────────────────────────────

        private static JObject BuildActivity(List<SearchIndex.IndexEntry> entries, CoverageSnapshot snap)
        {
            var now = DateTime.UtcNow;
            int d7 = 0, d30 = 0, d90 = 0, dated = 0;
            DateTime newest = DateTime.MinValue, oldest = DateTime.MaxValue;

            foreach (var e in entries)
            {
                if (e.LastUpdate == default(DateTime)) continue;
                dated++;
                var age = now - e.LastUpdate;
                if (age.TotalDays <= 7) d7++;
                if (age.TotalDays <= 30) d30++;
                if (age.TotalDays <= 90) d90++;
                if (e.LastUpdate > newest) newest = e.LastUpdate;
                if (e.LastUpdate < oldest) oldest = e.LastUpdate;
            }

            var j = new JObject
            {
                ["basedOn"] = snap.TrustOf("lastUpdate"),
                ["objectsWithDate"] = dated,
                ["changedLast7d"] = d7,
                ["changedLast30d"] = d30,
                ["changedLast90d"] = d90
            };
            if (newest != DateTime.MinValue) j["mostRecentUtc"] = newest.ToUniversalTime().ToString("o");
            if (oldest != DateTime.MaxValue) j["oldestUtc"] = oldest.ToUniversalTime().ToString("o");
            return j;
        }

        // ── containers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Module and Folder are OBJECTS, so their names come from the census and are as
        /// trustworthy as it is — even when membership (which object lives in which) is not.
        /// Emitting the inventory while suppressing membership is the whole point: the caller
        /// learns the KB has 90 modules named X, Y, Z without being told a false tree.
        /// </summary>
        private static void AddContainerInventory(JObject result, List<SearchIndex.IndexEntry> entries,
                                                  Dictionary<string, int> byType, JArray suppressed)
        {
            var inv = new JObject();
            bool any = false;

            foreach (var kind in new[] { "Module", "Folder" })
            {
                int n;
                if (!byType.TryGetValue(kind, out n) || n == 0) continue;
                any = true;

                var names = entries
                    .Where(e => string.Equals(e.Type, kind, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Name)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var block = new JObject
                {
                    ["count"] = n,
                    ["names"] = new JArray(names.Take(MaxContainerNames).Cast<object>().ToArray())
                };
                if (names.Count > MaxContainerNames)
                    block["namesTruncated"] = names.Count - MaxContainerNames;

                inv[kind.ToLowerInvariant() + "s"] = block;
            }

            if (any) result["containerInventory"] = inv;
        }

        /// <summary>
        /// Membership — which objects sit in which container — rides entirely on resolved
        /// placement. Emitted only when coverage backs it; otherwise named in suppressed[] with
        /// the exact call that fixes it.
        /// </summary>
        private static void AddPlacement(JObject result, List<SearchIndex.IndexEntry> entries,
                                         CoverageSnapshot snap, JArray suppressed)
        {
            if (snap.ShouldSuppress("module", SuppressionFloorPct))
            {
                Suppress(suppressed, "modules", "moduleMembershipUnresolved",
                    snap.StructureResolvedInScope + " of " + snap.ObjectsInScope +
                    " entries have placement resolved (module trust=" + snap.TrustOf("module") +
                    "). Container NAMES are still listed under containerInventory.",
                    "genexus_lifecycle", new JObject { ["action"] = "index", ["force"] = true });
                return;
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int unplaced = 0;
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.Module)) { unplaced++; continue; }
                int n;
                counts[e.Module] = counts.TryGetValue(e.Module, out n) ? n + 1 : 1;
            }

            var rows = new JObject();
            foreach (var kv in counts.OrderByDescending(k => k.Value).Take(MaxContainerNames))
                rows[kv.Key] = kv.Value;

            result["modules"] = new JObject
            {
                ["basedOn"] = snap.TrustOf("module"),
                ["distinct"] = counts.Count,
                // With placement resolved, "outside any module" is a FACT about the KB, not a gap
                // in our index — which is exactly what the observed/partial distinction buys.
                ["outsideAnyModule"] = unplaced,
                ["byModule"] = rows
            };
        }

        // ── naming ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Observed regularities WITH their support, never "the convention is X". Name and Type
        /// are complete, so this costs one in-memory pass and states a fact: "1,204 of 1,788
        /// Procedures start with Proc". Whether that is the team's rule is not ours to declare.
        /// </summary>
        private static void AddNamingCohorts(JObject result, List<SearchIndex.IndexEntry> entries,
                                             Dictionary<string, int> byType)
        {
            var cohorts = new JArray();

            foreach (var type in byType.Where(kv => kv.Value >= MinTypeSizeForCohorts)
                                       .OrderByDescending(kv => kv.Value)
                                       .Select(kv => kv.Key))
            {
                var names = entries
                    .Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(e.Name) && e.Name.Length >= PrefixLength)
                    .Select(e => e.Name)
                    .ToList();
                if (names.Count < MinTypeSizeForCohorts) continue;

                var groups = names
                    .GroupBy(n => n.Substring(0, PrefixLength), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { Prefix = g.Key, Count = g.Count() })
                    .Where(g => g.Count * 100d / names.Count >= MinCohortSupportPct)
                    .OrderByDescending(g => g.Count)
                    .ToList();

                foreach (var g in groups)
                {
                    if (cohorts.Count >= MaxNamingCohorts) break;
                    cohorts.Add(new JObject
                    {
                        ["prefix"] = g.Prefix,
                        ["type"] = type,
                        ["objects"] = g.Count,
                        ["ofType"] = names.Count,
                        ["supportPct"] = Math.Round(g.Count * 100d / names.Count, 1)
                    });
                }
                if (cohorts.Count >= MaxNamingCohorts) break;
            }

            if (cohorts.Count > 0)
            {
                result["namingCohorts"] = new JObject
                {
                    ["basedOn"] = "complete",
                    ["note"] = "Observed prefix regularities with their support. NOT a declared convention — " +
                               "the worker has no naming-convention detector.",
                    ["cohorts"] = cohorts
                };
            }
        }

        // ── patterns / integrity ────────────────────────────────────────────────

        private static void AddPatternAdoption(JObject result, Dictionary<string, int> byType)
        {
            var j = new JObject();
            foreach (var t in PatternTypes)
            {
                int n;
                if (byType.TryGetValue(t, out n) && n > 0) j[t] = n;
            }
            if (j.Count == 0) return;

            result["patternAdoption"] = new JObject
            {
                ["basedOn"] = "complete",
                ["note"] = "Counts of pattern-bearing object types. NOT architectural analysis.",
                ["instancesByType"] = j
            };
        }

        private static void AddIntegrity(JObject result, Dictionary<string, int> byType)
        {
            int missing;
            byType.TryGetValue("MissingKBObject", out missing);

            // Emitted even at zero: this count comes from the census, which is complete, so 0 is a
            // fact rather than a blind spot. That is precisely the case the suppression rule allows.
            result["integrity"] = new JObject
            {
                ["basedOn"] = "complete",
                ["missingKbObjects"] = missing
            };
        }

        /// <summary>
        /// What this tool does NOT know, stated explicitly with what it would take. Silence here
        /// would read as "nothing to report"; a caller that needs the generator must learn that
        /// the worker has no accessor for it rather than assume the KB has none.
        /// </summary>
        private static JArray BuildNotDetected()
        {
            return new JArray
            {
                new JObject
                {
                    ["what"] = "generator / runtime target",
                    ["why"] = "The worker exposes no TargetModel accessor.",
                    ["wouldRequire"] = "An SDK spike over the KB's target model."
                },
                new JObject
                {
                    ["what"] = "naming / error-handling / REST conventions",
                    ["why"] = "No convention detector exists. namingCohorts reports observed prefixes with support, which is not the same claim.",
                    ["wouldRequire"] = "A classifier over source parts, i.e. enrichment."
                },
                new JObject
                {
                    ["what"] = "architecture / dominant patterns",
                    ["why"] = "Not derivable from any field the lite pass writes. patternAdoption counts types; anything beyond counting would be invention.",
                    ["wouldRequire"] = "Source-level analysis per object."
                }
            };
        }

        // ── helpers ─────────────────────────────────────────────────────────────

        private static JArray Suppress(JArray suppressed, string section, string reason, string detail,
                                       string unlockTool, JObject unlockArgs)
        {
            suppressed.Add(new JObject
            {
                ["section"] = section,
                ["reason"] = reason,
                ["detail"] = detail,
                ["unlock"] = new JObject { ["tool"] = unlockTool, ["args"] = unlockArgs }
            });
            return suppressed;
        }

        private static string Fmt(double pct)
        {
            return pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
