using System;
using System.IO;
using Xunit;
using GxMcp.Gateway;

namespace GxMcp.Gateway.Tests
{
    // Self-updater (corporate fixed-path Squirrel-style update). The file-swap and
    // download paths mutate the real install dir / network, so they aren't unit
    // tested here; we cover the pure decision pieces: staged-payload validation
    // (what makes a downloaded tree safe to apply) and version comparison (which
    // gates both staging and apply).
    public class SelfUpdaterTests
    {
        [Fact]
        public void StagedPayloadValid_RequiresBothBinaries()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-staged-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "worker"));

                Assert.False(SelfUpdater.StagedPayloadValid(root), "empty tree is not valid");

                File.WriteAllText(Path.Combine(root, "GxMcp.Gateway.exe"), "x");
                Assert.False(SelfUpdater.StagedPayloadValid(root), "gateway alone is not valid (worker missing)");

                File.WriteAllText(Path.Combine(root, "worker", "GxMcp.Worker.exe"), "x");
                Assert.True(SelfUpdater.StagedPayloadValid(root), "both binaries present => valid");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// The binaries were renamed GxMcp.* -> GxMcp18.* so the process is identifiable next to
        /// the GeneXus 16 server. This pins the compatibility that makes the rename safe to ship.
        ///
        /// Read the failure direction before "simplifying" this away: the install doing the
        /// updating is the OLD one. If a package built under the new name is rejected — or an
        /// install sitting on the old name is treated as unmanaged — the user downloads an update
        /// that can never apply and is stranded on a manual reinstall. Both namings must stay
        /// valid until every supported install has moved over.
        /// </summary>
        [Theory]
        [InlineData("GxMcp18.Gateway.exe", "GxMcp18.Worker.exe")]   // post-rename package
        [InlineData("GxMcp.Gateway.exe", "GxMcp.Worker.exe")]       // package built before the rename
        [InlineData("GxMcp18.Gateway.exe", "GxMcp.Worker.exe")]     // half-migrated tree
        [InlineData("GxMcp.Gateway.exe", "GxMcp18.Worker.exe")]
        public void StagedPayloadValid_AcceptsBothNamings(string gatewayName, string workerName)
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-staged-names-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "worker"));
                File.WriteAllText(Path.Combine(root, gatewayName), "x");
                File.WriteAllText(Path.Combine(root, "worker", workerName), "x");

                Assert.True(SelfUpdater.StagedPayloadValid(root),
                    $"payload with {gatewayName} + worker/{workerName} must be accepted");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void StagedPayloadValid_StillRejectsAWorkerNamedLikeTheGateway()
        {
            // Guards against "accept anything vaguely GxMcp-ish". The worker must be under
            // worker/, whichever of the two names it carries.
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-staged-bad-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "worker"));
                File.WriteAllText(Path.Combine(root, "GxMcp18.Gateway.exe"), "x");
                File.WriteAllText(Path.Combine(root, "GxMcp18.Worker.exe"), "x");   // wrong location

                Assert.False(SelfUpdater.StagedPayloadValid(root));
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        [Fact]
        public void StagedPayloadValid_FalseForMissingDir()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-staged-missing-" + Guid.NewGuid().ToString("N"));
            Assert.False(SelfUpdater.StagedPayloadValid(root));
        }

        [Theory]
        [InlineData("2.8.4", "2.8.3", 1)]
        [InlineData("v2.9.0", "2.8.9", 1)]
        [InlineData("2.8.3", "2.8.3", 0)]
        [InlineData("2.8.2", "2.8.3", -1)]
        public void CompareSemver_GatesStageAndApply(string a, string b, int expectedSign)
        {
            int r = UpdateNotifier.CompareSemver(a, b);
            Assert.Equal(expectedSign, Math.Sign(r));
        }

        // End-to-end swap against a temp "install" dir: stage a newer build and
        // confirm ApplyStagedUpdate replaces the live files (gateway + worker +
        // sibling files), bumps version.txt, backs the old ones up, and clears
        // .staged. This exercises the real move/backup ordering — the only part not
        // covered is rename-self of the *running* exe (here it's an inert stub).
        [Fact]
        public void ApplyStagedUpdate_SwapsFiles_BumpsVersion_ClearsStaging()
        {
            string install = Path.Combine(Path.GetTempPath(), "gxmcp-apply-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(install, "worker"));
                File.WriteAllText(Path.Combine(install, "GxMcp.Gateway.exe"), "OLD-GW");
                File.WriteAllText(Path.Combine(install, "worker", "GxMcp.Worker.exe"), "OLD-WK");
                File.WriteAllText(Path.Combine(install, "tool_definitions.json"), "OLD-TOOLS");
                File.WriteAllText(Path.Combine(install, "version.txt"), "v2.8.3");

                string staged = Path.Combine(install, ".staged");
                Directory.CreateDirectory(Path.Combine(staged, "worker"));
                File.WriteAllText(Path.Combine(staged, "GxMcp.Gateway.exe"), "NEW-GW");
                File.WriteAllText(Path.Combine(staged, "worker", "GxMcp.Worker.exe"), "NEW-WK");
                File.WriteAllText(Path.Combine(staged, "tool_definitions.json"), "NEW-TOOLS");
                File.WriteAllText(Path.Combine(staged, "staged.json"), "{\"version\":\"2.8.4\"}");

                bool applied = SelfUpdater.ApplyStagedUpdate(install);

                Assert.True(applied, "apply should succeed");
                Assert.Equal("NEW-GW", File.ReadAllText(Path.Combine(install, "GxMcp.Gateway.exe")));
                Assert.Equal("NEW-WK", File.ReadAllText(Path.Combine(install, "worker", "GxMcp.Worker.exe")));
                Assert.Equal("NEW-TOOLS", File.ReadAllText(Path.Combine(install, "tool_definitions.json")));
                Assert.Equal("v2.8.4", File.ReadAllText(Path.Combine(install, "version.txt")));
                Assert.False(Directory.Exists(staged), ".staged should be cleared after apply");
                // The previous binaries are kept as .old-* backups.
                Assert.Contains(Directory.GetFiles(install), f => f.Contains(".old-"));
            }
            finally
            {
                try { Directory.Delete(install, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ApplyStagedUpdate_NoOp_WhenStagedNotNewer()
        {
            string install = Path.Combine(Path.GetTempPath(), "gxmcp-apply-noop-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(install, "worker"));
                File.WriteAllText(Path.Combine(install, "GxMcp.Gateway.exe"), "LIVE-GW");
                File.WriteAllText(Path.Combine(install, "worker", "GxMcp.Worker.exe"), "LIVE-WK");
                File.WriteAllText(Path.Combine(install, "version.txt"), "v2.8.4");

                string staged = Path.Combine(install, ".staged");
                Directory.CreateDirectory(Path.Combine(staged, "worker"));
                File.WriteAllText(Path.Combine(staged, "GxMcp.Gateway.exe"), "STALE-GW");
                File.WriteAllText(Path.Combine(staged, "worker", "GxMcp.Worker.exe"), "STALE-WK");
                File.WriteAllText(Path.Combine(staged, "staged.json"), "{\"version\":\"2.8.3\"}");

                bool applied = SelfUpdater.ApplyStagedUpdate(install);

                Assert.False(applied, "older staged version must not be applied");
                Assert.Equal("LIVE-GW", File.ReadAllText(Path.Combine(install, "GxMcp.Gateway.exe")));
                Assert.False(Directory.Exists(staged), "stale staged dir should be discarded");
            }
            finally
            {
                try { Directory.Delete(install, recursive: true); } catch { }
            }
        }
    }
}
