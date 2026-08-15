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
            Assert.Equal("partial:0", snap.TrustOf("module"));
            Assert.Equal(0, snap.EnrichedInScope);
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
            Assert.Equal("partial:100", snap.TrustOf("calls"));
            Assert.Equal("partial:100", snap.TrustOf("folderPath"));
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
