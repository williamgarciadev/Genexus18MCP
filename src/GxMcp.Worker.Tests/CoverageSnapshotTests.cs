using System;
using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// The anti-fabrication suite. These tests exist because of a measurement on the real
    /// MatikaErp_3003 index (14,932 objects): Calls populated for 0 objects, CalledBy for 70,
    /// Module for 1 — while ParentFolderPath was populated for 14,932 with a single distinct
    /// value, "Root Module". Anything that reads that index naively reports a flat KB with no
    /// call graph, which is false: the KB has 90 Module and 304 Folder objects.
    /// </summary>
    public class CoverageSnapshotTests
    {
        /// <summary>
        /// Mirrors what the lite pass actually writes: seven fields, no hierarchy, no edges.
        /// ParentFolderPath is what ComposeParentFolderPath synthesizes from an empty ParentPath.
        /// </summary>
        private static List<SearchIndex.IndexEntry> LiteFixture(int n)
        {
            var list = new List<SearchIndex.IndexEntry>();
            for (int i = 0; i < n; i++)
            {
                list.Add(new SearchIndex.IndexEntry
                {
                    Guid = Guid.NewGuid().ToString("N"),
                    Name = "Obj" + i,
                    Type = (i % 2 == 0) ? "Procedure" : "Transaction",
                    Description = "desc " + i,
                    LastUpdate = DateTime.UtcNow,
                    Module = null,
                    ParentPath = null,
                    ParentFolderPath = "Root Module"
                });
            }
            return list;
        }

        private static List<SearchIndex.IndexEntry> EnrichedFixture(int n)
        {
            var list = LiteFixture(n);
            for (int i = 0; i < list.Count; i++)
            {
                list[i].Module = (i % 2 == 0) ? "Payment" : "Billing";
                list[i].ParentPath = list[i].Module;
                list[i].ParentFolderPath = "Root Module/" + list[i].Module;
                list[i].Calls = new List<string> { "Other" + i };
                list[i].CalledBy = new List<string> { "Caller" + i };
                list[i].Embedding = new float[128];
            }
            return list;
        }

        [Fact]
        public void LiteIndex_ReportsStructureUnresolved_EvenThoughFolderPathIsFullyPopulated()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(LiteFixture(100));

            var snap = svc.GetCoverageSnapshot();

            Assert.Equal(100, snap.ObjectsInScope);
            // The trap: every entry HAS a ParentFolderPath, yet nothing was ever resolved.
            Assert.Equal(0, snap.StructureResolvedInScope);
            Assert.Equal(0d, snap.StructureResolvedPct);
            Assert.Equal(1, snap.DistinctFolderPaths);
            Assert.Equal("unavailable", snap.TrustOf("folderPath"));
        }

        [Fact]
        public void LiteIndex_EnrichmentFieldsArePartialNotUnavailable()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(LiteFixture(50));

            var snap = svc.GetCoverageSnapshot();

            // "partial:0" not "unavailable": the caller must be told WHY it is empty. For an
            // enrichment field the answer is always "not read yet", never "the KB has none".
            Assert.Equal("partial:0", snap.TrustOf("calls"));
            Assert.Equal("partial:0", snap.TrustOf("calledBy"));
            Assert.Equal(0, snap.EnrichedInScope);
            // Module used to be asserted here as an enrichment field. It no longer is — the lite
            // walk resolves placement (Indexing.LitePassResolvesHierarchy), so its trust level is
            // decided at runtime, not by the field name. Covered by the placement tests below.
        }

        [Fact]
        public void LitePassFields_AreCompleteOrObserved_NeverPartial()
        {
            var svc = new IndexCacheService();
            var entries = LiteFixture(10);
            entries[0].Description = null;          // a real gap in the KB, not a gap in our index
            entries[1].Description = null;
            svc.LoadFromEntries(entries);

            var snap = svc.GetCoverageSnapshot();

            Assert.Equal("complete", snap.TrustOf("name"));
            Assert.Equal("complete", snap.TrustOf("type"));
            // 8/10 -> observed, which means "the two without one genuinely have none".
            Assert.Equal("observed:80", snap.TrustOf("description"));
        }

        [Fact]
        public void EnrichedIndex_ReportsCompleteStructureAndEdges()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(EnrichedFixture(40));

            var snap = svc.GetCoverageSnapshot();

            Assert.Equal(40, snap.EnrichedInScope);
            Assert.Equal(100d, snap.EnrichedPct);
            Assert.Equal(40, snap.StructureResolvedInScope);
            // Edges stay "partial" even at 100%: the label describes what an ABSENCE would mean,
            // and for an enrichment field that is always "not read yet". At full population there
            // is no absence to misread, so it is harmless — but it must not silently become
            // "complete", or a later partially-enriched index would inherit the wrong reading.
            Assert.Equal("partial:100", snap.TrustOf("calls"));
            // Placement fully resolved => "complete", matching this test's name. It read
            // "partial:100" until the trust level stopped being hardcoded per field name.
            Assert.Equal("complete", snap.TrustOf("folderPath"));
            Assert.True(snap.DistinctFolderPaths > 1);
        }

        [Fact]
        public void ShouldSuppress_HonoursFloor_AndAlwaysSuppressesUnavailable()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(LiteFixture(100));
            var lite = svc.GetCoverageSnapshot();

            Assert.True(lite.ShouldSuppress("folderPath", 60d));   // unavailable -> always
            Assert.True(lite.ShouldSuppress("calls", 60d));        // partial:0 -> below floor
            Assert.False(lite.ShouldSuppress("name", 60d));        // complete -> never

            var svc2 = new IndexCacheService();
            svc2.LoadFromEntries(EnrichedFixture(100));
            var rich = svc2.GetCoverageSnapshot();

            Assert.False(rich.ShouldSuppress("calls", 60d));       // partial:100 -> above floor
        }

        [Fact]
        public void ScopedSnapshot_CountsOnlyTheGivenSubset()
        {
            var svc = new IndexCacheService();
            var all = LiteFixture(100);
            for (int i = 0; i < 10; i++) all[i].Embedding = new float[128];   // only 10 enriched
            svc.LoadFromEntries(all);

            var scoped = svc.GetCoverageSnapshot(all.GetRange(0, 10));

            Assert.Equal(10, scoped.ObjectsInScope);
            Assert.Equal(10, scoped.EnrichedInScope);
            Assert.Equal(100d, scoped.EnrichedPct);   // 100% of the SCOPE, not of the KB
        }

        /// <summary>
        /// The state the lite walk now produces, and which used to be impossible: placement is
        /// resolved for every object while nothing is enriched. Before Indexing.LitePassResolvesHierarchy
        /// the two travelled together — both were written only by enrichment — so "no module" and
        /// "no callers" were the same fact. They are not, and coverage must report them apart:
        /// the tree is trustworthy, the call graph is not.
        /// </summary>
        [Fact]
        public void StructureResolvedWithoutEnrichment_IsTrustedTreeButUntrustedEdges()
        {
            var svc = new IndexCacheService();
            var entries = LiteFixture(60);
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].Module = (i % 3 == 0) ? "Payment" : "Flow";
                entries[i].ParentPath = entries[i].Module;
                entries[i].ParentFolderPath = "Root Module/" + entries[i].Module;
                // Embedding stays null: NOT enriched.
            }
            svc.LoadFromEntries(entries);

            var snap = svc.GetCoverageSnapshot();

            Assert.Equal(60, snap.StructureResolvedInScope);
            Assert.Equal(100d, snap.StructureResolvedPct);
            Assert.Equal(0, snap.EnrichedInScope);
            Assert.True(snap.DistinctFolderPaths > 1);

            // The tree can be drawn...
            Assert.False(snap.ShouldSuppress("folderPath", 60d));
            // ...while the call graph still must not be.
            Assert.True(snap.ShouldSuppress("calledBy", 60d));
        }

        [Fact]
        public void LitePassResolvesHierarchy_DefaultsToOn()
        {
            // A regression guard on the decision, not on the plumbing: leaving this off is what
            // makes every tool read a fabricated "Root Module"-only folder tree.
            Assert.True(GxMcp.Worker.Configuration.LitePassResolvesHierarchy);
        }

        /// <summary>
        /// The label, not just the behaviour. The previous test asserts ShouldSuppress does the
        /// right thing; this one asserts coverage TELLS THE TRUTH about why, which is the whole
        /// product of the coverage block — an agent reads fieldTrust and acts on the vocabulary.
        ///
        /// "partial:&lt;pct&gt;" is defined as enrichment-only: the absent share means "not read yet,
        /// never reason over it". Once the lite pass resolves placement for every object, an absent
        /// Module is a FACT (the object sits outside any module), so the honest label is
        /// "observed"/"complete". Labelling a resolved tree "partial" is the same class of error as
        /// the fabricated "Root Module", pointed the other way: it under-trusts data we do have,
        /// and makes Overview() suppress a tree it could legitimately draw.
        /// </summary>
        [Fact]
        public void PlacementResolvedByLitePass_IsObservedNotPartial()
        {
            var svc = new IndexCacheService();
            var entries = LiteFixture(50);
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].Module = "Payment";
                entries[i].ParentPath = "Payment";
                entries[i].ParentFolderPath = "Root Module/Payment";
                // Embedding stays null: NOT enriched. Placement came from the lite walk.
            }
            svc.LoadFromEntries(entries);

            var snap = svc.GetCoverageSnapshot();
            snap.PlacementResolvedByLitePass = true;

            // Resolved for all 50 by a pass that attempted all 50 => a fact about the KB.
            Assert.Equal("complete", snap.TrustOf("module"));
            Assert.Equal("complete", snap.TrustOf("parentPath"));
            Assert.Equal("complete", snap.TrustOf("folderPath"));

            // Edges are still enrichment-only and must stay untrusted.
            Assert.StartsWith("partial:", snap.TrustOf("calledBy"));
        }

        /// <summary>
        /// Same index shape, kill-switch off: placement is enrichment-only again, so an absent
        /// Module really does mean "not read yet" and "partial" is the honest label. The trust
        /// level depends on WHICH PASS produced the index, which is exactly why it cannot be
        /// hardcoded per field name.
        /// </summary>
        [Fact]
        public void PlacementFromEnrichmentOnly_StaysPartial()
        {
            var svc = new IndexCacheService();
            var entries = LiteFixture(50);
            for (int i = 0; i < 20; i++)
            {
                entries[i].Module = "Payment";
                entries[i].ParentPath = "Payment";
                entries[i].ParentFolderPath = "Root Module/Payment";
            }
            svc.LoadFromEntries(entries);

            var snap = svc.GetCoverageSnapshot();
            snap.PlacementResolvedByLitePass = false;

            Assert.Equal("partial:40", snap.TrustOf("module"));
            Assert.Equal("partial:40", snap.TrustOf("folderPath"));
        }

        /// <summary>
        /// The stale-index case, and the reason "observed:0" must never be emitted here. An index
        /// persisted before Indexing.LitePassResolvesHierarchy carries no placement even though the
        /// flag is on now. Reporting "observed:0" would assert the KB is flat — the exact false
        /// claim this whole vocabulary exists to prevent. Nothing resolved => unavailable, and every
        /// section built on placement is suppressed rather than drawn empty.
        /// </summary>
        [Fact]
        public void FlagOnButNothingResolved_IsUnavailableNotObservedZero()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(LiteFixture(50));   // ParentPath null, ParentFolderPath "Root Module"

            var snap = svc.GetCoverageSnapshot();
            snap.PlacementResolvedByLitePass = true;

            Assert.Equal(0, snap.StructureResolvedInScope);
            Assert.Equal("unavailable", snap.TrustOf("module"));
            Assert.Equal("unavailable", snap.TrustOf("parentPath"));
            Assert.Equal("unavailable", snap.TrustOf("folderPath"));
            Assert.True(snap.ShouldSuppress("folderPath", 60d));
        }

        /// <summary>
        /// The floor is for "partial", never for "observed" — same number, opposite meaning.
        ///
        /// Found live: after a reindex, placement resolved for 1,504 of 3,307 objects. That is not
        /// 55% missing data, it is 1,803 objects that genuinely sit at the root. The floor
        /// suppressed a complete and correct answer because the KB was not tidy enough, which is
        /// the mirror of the bug this whole vocabulary exists to prevent — withholding a fact
        /// instead of inventing one, but wrong either way.
        /// </summary>
        [Fact]
        public void ObservedBelowFloor_IsStillEmitted_BecauseThePctDescribesTheKbNotOurBlindness()
        {
            var svc = new IndexCacheService();
            var entries = LiteFixture(100);
            for (int i = 0; i < 45; i++)          // 45% placed, 55% genuinely at the root
            {
                entries[i].Module = "Payment";
                entries[i].ParentPath = "Payment";
                entries[i].ParentFolderPath = "Root Module/Payment";
            }
            svc.LoadFromEntries(entries);

            var snap = svc.GetCoverageSnapshot();
            snap.PlacementResolvedByLitePass = true;

            Assert.Equal("observed:45", snap.TrustOf("module"));
            Assert.False(snap.ShouldSuppress("module", 60d),
                "observed:45 is a fact about the KB — 55% of objects really have no module. Emit it with its basedOn.");

            // The same 45% under enrichment-only semantics IS our blindness, and must be withheld.
            snap.PlacementResolvedByLitePass = false;
            Assert.Equal("partial:45", snap.TrustOf("module"));
            Assert.True(snap.ShouldSuppress("module", 60d));
        }

        [Fact]
        public void EmptyScope_IsUnavailableNotComplete()
        {
            var svc = new IndexCacheService();
            svc.LoadFromEntries(new List<SearchIndex.IndexEntry>());

            var snap = svc.GetCoverageSnapshot();

            Assert.Equal(0, snap.ObjectsInScope);
            // Nothing in scope must never read as "complete" — an empty census proves nothing.
            Assert.Equal("unavailable", snap.TrustOf("name"));
        }
    }
}
