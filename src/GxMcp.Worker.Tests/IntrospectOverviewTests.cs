using System;
using System.Collections.Generic;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Overview's contract is mostly about what it REFUSES to say. These tests are written
    /// against the false conclusions a caller would draw, not against the happy path: an
    /// assertion that a section is ABSENT is the one that catches a fabricated answer, and a
    /// fabricated answer is worse than no answer because the agent acts on it.
    /// </summary>
    public class IntrospectOverviewTests
    {
        // ── fixtures ────────────────────────────────────────────────────────────

        /// <summary>What the lite walk produces with placement unresolved: the state that made
        /// 14,932 objects report the single synthesized folder "Root Module".</summary>
        private static List<SearchIndex.IndexEntry> LiteFixture(int n)
        {
            var list = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < n; i++)
            {
                list.Add(new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString("N"),
                    Name = (i % 2 == 0 ? "Proc" : "Trn") + i,
                    Type = (i % 2 == 0) ? "Procedure" : "Transaction",
                    Description = "desc " + i,
                    LastUpdate = DateTime.UtcNow.AddDays(-i),
                    Module = null,
                    ParentPath = null,
                    ParentFolderPath = "Root Module"
                });
            }
            return list;
        }

        /// <summary>Module and Folder are objects, so they show up in the census whether or not
        /// anything's membership was resolved. That gap is the whole point of containerInventory.</summary>
        private static void AddContainers(List<SearchIndex.IndexEntry> list, int modules, int folders)
        {
            for (int i = 0; i < modules; i++)
                list.Add(new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString("N"), Name = "Mod" + i, Type = "Module",
                    LastUpdate = DateTime.UtcNow, ParentFolderPath = "Root Module"
                });
            for (int i = 0; i < folders; i++)
                list.Add(new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString("N"), Name = "Fld" + i, Type = "Folder",
                    LastUpdate = DateTime.UtcNow, ParentFolderPath = "Root Module"
                });
        }

        private static IntrospectService ServiceOver(List<SearchIndex.IndexEntry> entries, bool ready = true)
        {
            var cache = new IndexCacheService();
            cache.LoadFromEntries(entries, markReady: ready);
            return new IntrospectService(cache);
        }

        private static JObject Overview(List<SearchIndex.IndexEntry> entries, bool ready = true)
        {
            return JObject.Parse(ServiceOver(entries, ready).Overview());
        }

        private static bool IsSuppressed(JObject envelope, string section)
        {
            var arr = envelope["result"]?["suppressed"] as JArray;
            return arr != null && arr.Any(s => (string)s["section"] == section);
        }

        private static JObject SuppressedEntry(JObject envelope, string section)
        {
            var arr = envelope["result"]?["suppressed"] as JArray;
            return arr?.FirstOrDefault(s => (string)s["section"] == section) as JObject;
        }

        // ── the anti-fabrication core ───────────────────────────────────────────

        /// <summary>
        /// THE test. On an index where placement was never resolved, module membership must be
        /// absent — not an empty object, not zeroes. An empty "modules": {} reads as "this KB has
        /// no modules", which is false: the same fixture carries 90 Module objects.
        /// </summary>
        [Fact]
        public void UnresolvedPlacement_OmitsModules_AndNamesTheSuppressionWithAnUnlock()
        {
            var entries = LiteFixture(200);
            AddContainers(entries, modules: 90, folders: 304);

            var env = Overview(entries);
            var result = (JObject)env["result"];

            Assert.Equal("ok", (string)env["status"]);
            // Absent, not empty. This is the assertion that catches a fabricated tree.
            Assert.Null(result["modules"]);
            Assert.Equal("unavailable", (string)result["coverage"]["fieldTrust"]["module"]);

            var s = SuppressedEntry(env, "modules");
            Assert.NotNull(s);
            Assert.Equal("moduleMembershipUnresolved", (string)s["reason"]);
            // A suppression without a way out just moves the dead end. It must carry the fix.
            Assert.Equal("genexus_lifecycle", (string)s["unlock"]["tool"]);
            Assert.True((bool)s["unlock"]["args"]["force"]);
        }

        /// <summary>
        /// The names survive the suppression. Module/Folder are objects, so the census knows them
        /// even when it does not know who lives inside — and that inventory is most of the value:
        /// the caller learns the KB is not flat without being handed a false tree.
        /// </summary>
        [Fact]
        public void ContainerNames_AreStillListed_WhenMembershipIsSuppressed()
        {
            var entries = LiteFixture(50);
            AddContainers(entries, modules: 90, folders: 304);

            var result = (JObject)Overview(entries)["result"];

            Assert.Null(result["modules"]);                                   // membership: withheld
            Assert.Equal(90, (int)result["containerInventory"]["modules"]["count"]);   // names: known
            Assert.Equal(304, (int)result["containerInventory"]["folders"]["count"]);
            Assert.True(((JArray)result["containerInventory"]["modules"]["names"]).Count > 0);
        }

        /// <summary>
        /// The state the lite walk now produces: placement resolved for everything, nothing
        /// enriched. The tree becomes emittable while the call graph stays untrustworthy — the two
        /// used to travel together, and conflating them is what produced fabricated dead code.
        /// </summary>
        [Fact]
        public void ResolvedPlacement_EmitsModules_WhileEdgesStaySuppressed()
        {
            var entries = LiteFixture(120);
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].Module = (i % 3 == 0) ? "Payment" : "Billing";
                entries[i].ParentPath = entries[i].Module;
                entries[i].ParentFolderPath = "Root Module/" + entries[i].Module;
                // Embedding stays null: NOT enriched.
            }

            var env = Overview(entries);
            var result = (JObject)env["result"];

            Assert.NotNull(result["modules"]);
            Assert.Equal(2, (int)result["modules"]["distinct"]);
            Assert.Equal("complete", (string)result["modules"]["basedOn"]);
            Assert.False(IsSuppressed(env, "modules"));

            // ...and the edges are still off-limits.
            Assert.True(IsSuppressed(env, "callGraph"));
        }

        /// <summary>
        /// Overview must never surface Calls/CalledBy even when they happen to be populated.
        /// Reading them at this depth is what makes the answer depend on enrichment progress, and
        /// an answer that silently changes meaning between calls is not a reconnaissance tool.
        /// </summary>
        [Fact]
        public void CallGraph_IsNeverEmitted_EvenWhenEdgesArePopulated()
        {
            var entries = LiteFixture(60);
            foreach (var e in entries)
            {
                e.Module = "Payment";
                e.ParentPath = "Payment";
                e.ParentFolderPath = "Root Module/Payment";
                e.Calls = new List<string> { "Other" };
                e.CalledBy = new List<string> { "Caller" };
                e.Embedding = new float[128];
            }

            var env = Overview(entries);
            var result = (JObject)env["result"];

            Assert.Null(result["callGraph"]);
            Assert.Null(result["hotspots"]);
            Assert.Null(result["deadCodeCandidates"]);
            Assert.True(IsSuppressed(env, "callGraph"));
        }

        // ── the cold index ──────────────────────────────────────────────────────

        /// <summary>
        /// A census taken mid-build describes our progress, not the KB. Presenting it as a census
        /// is the worst failure this tool can have, so counts are withheld entirely rather than
        /// shipped with a caveat — a caveat next to a number loses to the number.
        /// </summary>
        [Fact]
        public void ColdIndex_WithholdsTheCensus_InsteadOfReportingAPartialOneAsWhole()
        {
            var env = Overview(LiteFixture(30), ready: false);
            var result = (JObject)env["result"];

            Assert.Equal("partial", (string)env["status"]);
            Assert.Equal("CensusInProgress", (string)env["code"]);
            Assert.True((bool)result["censusInProgress"]);
            Assert.Null(result["census"]);
            Assert.True(IsSuppressed(env, "census"));
            // Coverage still ships: knowing how little is known is the point of the call.
            Assert.NotNull(result["coverage"]);
        }

        // ── honesty of the coverage block ───────────────────────────────────────

        [Fact]
        public void DoNotConclude_CallsOutTheUnreadEdges_OnAnUnenrichedIndex()
        {
            var result = (JObject)Overview(LiteFixture(100))["result"];
            var warnings = (JArray)result["coverage"]["doNotConclude"];

            Assert.NotEmpty(warnings);
            Assert.Contains(warnings, w => ((string)w).IndexOf("0 callers", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Contains(warnings, w => ((string)w).IndexOf("flat", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// The vocabulary travels with the payload. A caller that reads fieldTrust without knowing
        /// what "partial" means will read it as a percentage of quality rather than a warning.
        /// </summary>
        [Fact]
        public void Coverage_ShipsTheVocabulary_SoFieldTrustCannotBeMisread()
        {
            var result = (JObject)Overview(LiteFixture(20))["result"];
            var vocab = (JObject)result["coverage"]["vocabulary"];

            Assert.NotNull(vocab["partial:<pct>"]);
            Assert.Contains("NOT READ YET", (string)vocab["partial:<pct>"]);
        }

        // ── caps and arithmetic ─────────────────────────────────────────────────

        /// <summary>
        /// A silent top-N makes byType look like the whole census while its sum quietly disagrees
        /// with total. Either show everything or name what you hid — never let the reader
        /// reconcile the difference by guessing.
        /// </summary>
        [Fact]
        public void Census_AlwaysReconciles_WithTheHiddenTailNamed()
        {
            var entries = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < 600; i++)
                entries.Add(new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString("N"),
                    Name = "Obj" + i,
                    Type = "Type" + (i % 55),          // 55 distinct types > MaxTypeRows (40)
                    LastUpdate = DateTime.UtcNow
                });

            var result = (JObject)Overview(entries)["result"];
            var census = (JObject)result["census"];
            var byType = (JObject)census["byType"];

            Assert.Equal(600, (int)census["total"]);
            Assert.Equal(55, (int)census["distinctTypes"]);
            Assert.True(byType.Count <= 40);

            int shown = byType.Properties().Sum(p => (int)p.Value);
            int hidden = (int)census["byTypeTruncated"]["hiddenObjects"];
            Assert.Equal(600, shown + hidden);
        }

        /// <summary>
        /// If the gateway ever has to truncate an introspect response, the service's caps failed —
        /// and a truncated JSON payload is not a smaller answer, it is an unparseable one.
        /// </summary>
        [Fact]
        public void Payload_StaysFarBelowTheGatewayTruncationCeiling()
        {
            var entries = LiteFixture(800);
            AddContainers(entries, modules: 90, folders: 304);

            string json = ServiceOver(entries).Overview();

            Assert.True(json.Length < 60000, "payload is " + json.Length + " chars; the gateway truncates at 60000.");
            Assert.True(json.Length < 16000, "overview should stay compact; got " + json.Length + " chars.");
        }

        // ── stated ignorance ────────────────────────────────────────────────────

        /// <summary>
        /// Silence about the generator reads as "nothing to report". It has to say "we cannot know
        /// this, and here is what it would take" — otherwise the caller assumes the KB has none.
        /// </summary>
        [Fact]
        public void NotDetected_StatesWhatIsUnknowable_RatherThanOmittingIt()
        {
            var result = (JObject)Overview(LiteFixture(20))["result"];
            var nd = (JArray)result["notDetected"];

            Assert.NotEmpty(nd);
            Assert.Contains(nd, x => ((string)x["what"]).IndexOf("generator", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.All(nd, x => Assert.False(string.IsNullOrWhiteSpace((string)x["wouldRequire"])));
        }

        /// <summary>
        /// Naming cohorts must read as an observation with its support, never as a declared rule.
        /// "67% of Procedures start with Proc" is a measurement; "the convention is Proc" is a
        /// claim the worker has no detector to back.
        /// </summary>
        [Fact]
        public void NamingCohorts_ReportSupport_AndDisclaimBeingAConvention()
        {
            var result = (JObject)Overview(LiteFixture(200))["result"];
            var block = (JObject)result["namingCohorts"];

            Assert.NotNull(block);
            Assert.Contains("NOT a declared convention", (string)block["note"]);
            foreach (var c in (JArray)block["cohorts"])
            {
                Assert.True((double)c["supportPct"] > 0);
                Assert.True((int)c["objects"] <= (int)c["ofType"]);
            }
        }
    }
}
