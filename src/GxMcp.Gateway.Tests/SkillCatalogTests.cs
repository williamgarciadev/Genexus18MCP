using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2.8.0 (S2) — verified-source GeneXus development skills exposed via
    // MCP resources/. These tests pin: (a) all curated keys are advertised,
    // (b) each body is non-trivial and cites its source, (c) the
    // hallucination-killer facts (CallProtocol does NOT accept "Modal";
    // CallProtocol does NOT apply to WebPanel) are spelled out so a future
    // refactor can't quietly drop the LLM-correction wording.
    public class SkillCatalogTests
    {
        [Fact]
        public void EveryCuratedSkill_HasTitleDescriptionBody()
        {
            Assert.NotEmpty(SkillCatalog.All);
            foreach (var e in SkillCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Key), "skill key missing");
                Assert.False(string.IsNullOrWhiteSpace(e.Title), $"{e.Key}: title missing");
                Assert.False(string.IsNullOrWhiteSpace(e.Description), $"{e.Key}: description missing");
                Assert.True(e.Body.Length >= 400, $"{e.Key}: body suspiciously short");
                // Each body must cite its source(s).
                Assert.Contains("docs.genexus.com", e.Body, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void CuratedKeysArePresent()
        {
            // The first four are the LLM-anti-hallucination minimum for v2.8.0;
            // clean-architecture is the team's mandatory coding standard.
            var keys = SkillCatalog.All.Select(e => e.Key).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("navigation", keys);
            Assert.Contains("gam-integrated-security", keys);
            Assert.Contains("sd-panel-mobile", keys);
            Assert.Contains("webpanel-events", keys);
            Assert.Contains("clean-architecture", keys);
        }

        /// <summary>
        /// The honesty is the value. The whole point of this standard is that it does NOT
        /// pretend GeneXus has classes/inheritance/interfaces — it translates what it can and
        /// says outright which SOLID letter does not apply (Liskov). A future edit that dilutes
        /// that admission into generic SOLID prose would turn the document into cargo cult;
        /// this test makes that edit go red.
        /// </summary>
        [Fact]
        public void CleanArchitectureSkill_DeclaresGeneXusIsNotOOP()
        {
            var ca = SkillCatalog.FindByKey("clean-architecture");
            Assert.NotNull(ca);
            Assert.Contains("no es un lenguaje orientado a objetos", ca.Body);
            Assert.Contains("Liskov", ca.Body);
            Assert.Contains("NO aplica", ca.Body);
        }

        /// <summary>
        /// The concrete numbers the linter enforces and the doc teaches must be the SAME story:
        /// the 80-line procedure limit (GX014), the canonical parm() signature (what GX012's
        /// message prescribes), single commit ownership (GX001's concept), and the blocking
        /// Definition of Done. If the doc and the linter drift apart, developers get told two
        /// different standards and trust dies — this pins the shared facts.
        /// </summary>
        [Fact]
        public void CleanArchitectureSkill_PinsTheTeamLimits()
        {
            var ca = SkillCatalog.FindByKey("clean-architecture");
            Assert.NotNull(ca);
            // Size limits (§2-S) and the rule that enforces the procedure one.
            Assert.Contains("80", ca.Body);
            Assert.Contains("150", ca.Body);
            Assert.Contains("GX014", ca.Body);
            // Canonical signature, exactly as the linter messages spell it.
            Assert.Contains("out:&Resultado, out:&Mensajes", ca.Body);
            // Single owner of the Commit.
            Assert.Contains("Commit on Exit = No", ca.Body);
            Assert.Contains("GX001", ca.Body);
            // The smells the linter enforces are named with their codes.
            Assert.Contains("GX012", ca.Body);
            Assert.Contains("GX015", ca.Body);
            // The Definition of Done is a brake, not a suggestion.
            Assert.Contains("bloqueante", ca.Body);
            Assert.Contains("GXnnn-justified", ca.Body);
        }

        /// <summary>
        /// The body is embedded from docs/clean-architecture-genexus.md at build time (docs/
        /// does not travel in publish.zip; the assembly does). This test proves the embedding
        /// worked AND that the assembly copy matches the repo doc — the single-source-of-truth
        /// guarantee. If someone edits the .md and the test runs against a stale build, the
        /// mismatch surfaces here instead of shipping silently.
        /// </summary>
        [Fact]
        public void CleanArchitectureSkill_BodyLoadsFromEmbeddedResource_AndMatchesRepoDoc()
        {
            var ca = SkillCatalog.FindByKey("clean-architecture");
            Assert.NotNull(ca);
            Assert.False(ca.Body.StartsWith("ERROR:"), "embedded resource failed to load: " + ca.Body);

            // Locate the repo doc the way ContractGoldenHarness locates fixtures: walk up.
            string dir = System.AppContext.BaseDirectory;
            string docPath = null;
            for (int i = 0; i < 10; i++)
            {
                var candidate = System.IO.Path.Combine(dir, "docs", "clean-architecture-genexus.md");
                if (System.IO.File.Exists(candidate)) { docPath = candidate; break; }
                var parent = System.IO.Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            Assert.False(docPath == null, "could not locate docs/clean-architecture-genexus.md from " + System.AppContext.BaseDirectory);

            string repoDoc = System.IO.File.ReadAllText(docPath);
            // Normalize line endings and BOM: the embedding preserves bytes, git may not.
            static string Norm(string s) => s.Replace("\r\n", "\n").TrimStart('﻿').Trim();
            Assert.Equal(Norm(repoDoc), Norm(ca.Body));
        }

        [Fact]
        public void NavigationSkill_KillsTheCallProtocolModalHallucination()
        {
            // The motivating example: an LLM suggested `CallProtocol = Modal`,
            // which doesn't exist. The navigation skill must spell out both
            // facts: (1) CallProtocol does NOT apply to WebPanel/SDPanel,
            // (2) "Modal" is not a value.
            var nav = SkillCatalog.FindByKey("navigation");
            Assert.NotNull(nav);
            Assert.Contains("CallProtocol", nav.Body);
            Assert.Contains("Modal", nav.Body);
            Assert.Contains("does **NOT**", nav.Body);
            // Must list the real CallProtocol values verbatim.
            Assert.Contains("Internal", nav.Body);
            Assert.Contains("Command Line", nav.Body);
            Assert.Contains("HTTP", nav.Body);
            Assert.Contains("SOAP", nav.Body);
            Assert.Contains("Enterprise Java Bean", nav.Body);
        }

        [Fact]
        public void GamSkill_NamesTheRealProperty()
        {
            // Real property is "Integrated Security Level" (NOT "Enable Integrated Security").
            var gam = SkillCatalog.FindByKey("gam-integrated-security");
            Assert.NotNull(gam);
            Assert.Contains("Integrated Security Level", gam.Body);
            // Real enum values verbatim.
            Assert.Contains("Authorization", gam.Body);
            Assert.Contains("Authentication", gam.Body);
            Assert.Contains("None", gam.Body);
        }

        [Fact]
        public void SdPanelSkill_DocumentsMainProperty()
        {
            var sd = SkillCatalog.FindByKey("sd-panel-mobile");
            Assert.NotNull(sd);
            // The IDE-facing name is "Main program" — important fact to pin.
            Assert.Contains("Main program", sd.Body);
            // Object types that can be Main.
            Assert.Contains("Menu", sd.Body);
            Assert.Contains("Panel", sd.Body);
            Assert.Contains("Work With", sd.Body);
        }

        [Fact]
        public void WebPanelEventsSkill_PinsRefreshLoadOrder()
        {
            var wp = SkillCatalog.FindByKey("webpanel-events");
            Assert.NotNull(wp);
            Assert.Contains("Refresh", wp.Body);
            Assert.Contains("Load", wp.Body);
            // The canonical sequence Start → Refresh → Load must be present
            // as a literal "Refresh event ... followed by ... Load" statement
            // so an LLM reading the body sees the ordering explicitly.
            Assert.Contains("followed by the Load", wp.Body);
            Assert.Contains("Start event", wp.Body);
        }

        [Fact]
        public void FindByKey_UnknownKey_ReturnsNull()
        {
            Assert.Null(SkillCatalog.FindByKey("nonexistent-skill"));
            Assert.Null(SkillCatalog.FindByKey(""));
            Assert.Null(SkillCatalog.FindByKey(null));
        }
    }
}
