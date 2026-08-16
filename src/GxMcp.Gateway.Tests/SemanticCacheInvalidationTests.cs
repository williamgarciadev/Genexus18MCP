using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Regression for the read-after-delete staleness bug: the gateway's semantic
    /// cache (Program._semanticCache) replays successful read envelopes until a
    /// mutating tool clears it. Program.IsMutatingTool is that gate — a miss leaves
    /// stale reads alive (a cached part=Structure read kept returning a deleted
    /// object because genexus_delete_object was not recognised as mutating).
    ///
    /// These tests pin the mutating/read-only classification for every tool that
    /// can change KB object state, so a future write tool that forgets to
    /// invalidate the cache fails here instead of silently serving stale reads.
    /// </summary>
    public class SemanticCacheInvalidationTests
    {
        // ── Name-only mutating tools (no action argument) ────────────────────────
        [Theory]
        [InlineData("genexus_edit")]
        [InlineData("genexus_edit_form")]
        [InlineData("genexus_edit_and_build")]
        [InlineData("genexus_bulk_edit")]
        [InlineData("genexus_create")]              // umbrella: object/popup/scaffold/…
        [InlineData("genexus_create_object")]
        [InlineData("genexus_create_popup")]
        [InlineData("genexus_save_as")]
        [InlineData("genexus_sd_panel_create")]
        [InlineData("genexus_sd_panel_edit")]
        [InlineData("genexus_delete_object")]        // ← reported regression
        [InlineData("genexus_variable")]             // umbrella: action=add/delete/modify
        [InlineData("genexus_add_variable")]
        [InlineData("genexus_modify_variable")]
        [InlineData("genexus_delete_variable")]
        [InlineData("genexus_refactor")]
        [InlineData("genexus_rename_across_kb")]
        [InlineData("genexus_apply_pattern")]
        [InlineData("genexus_kb_import")]
        [InlineData("genexus_import_object")]
        public void NameOnlyMutatingTools_AreDetected(string toolName)
        {
            Assert.True(Program.IsMutatingTool(toolName, new JObject()),
                $"expected {toolName} to invalidate the semantic cache");
        }

        // ── Action-gated mutating calls ─────────────────────────────────────────
        [Theory]
        [InlineData("genexus_properties", "set")]
        [InlineData("genexus_properties", "move")]   // reparent (Folder/Module placement)
        [InlineData("genexus_asset", "write")]
        [InlineData("genexus_history", "save")]
        [InlineData("genexus_history", "restore")]
        [InlineData("genexus_transfer", "import")]
        [InlineData("genexus_structure", "update_visual")]
        [InlineData("genexus_structure", "create_index")]
        [InlineData("genexus_structure", "drop_index")]
        [InlineData("genexus_structure", "set_attribute")]
        [InlineData("genexus_structure", "set_level")]
        [InlineData("genexus_structure", "set_domain")]
        [InlineData("genexus_structure", "update_group")]
        [InlineData("genexus_structure", "move_attribute")]
        [InlineData("genexus_layout", "set_property")]
        [InlineData("genexus_layout", "set_properties")]
        [InlineData("genexus_layout", "rename_printblock")]
        [InlineData("genexus_layout", "add_printblock")]
        [InlineData("genexus_gxserver", "commit")]
        [InlineData("genexus_gxserver", "update")]
        [InlineData("genexus_gxserver", "lock")]
        [InlineData("genexus_gxserver", "resolve")]
        [InlineData("genexus_gxserver", "pipeline_run")]
        [InlineData("genexus_gxserver", "pipeline_abort")]
        [InlineData("genexus_db", "translations_import")]
        [InlineData("genexus_db", "sample_data")]
        [InlineData("genexus_lifecycle", "build")]
        [InlineData("genexus_lifecycle", "rebuild")]
        [InlineData("genexus_lifecycle", "specify")]
        [InlineData("genexus_lifecycle", "validate")]
        [InlineData("genexus_lifecycle", "validate-kb")]
        [InlineData("genexus_lifecycle", "sync")]
        [InlineData("genexus_lifecycle", "index")]
        [InlineData("genexus_lifecycle", "reorg")]
        [InlineData("genexus_lifecycle", "snapshots-restore")]
        public void MutatingActions_AreDetected(string toolName, string action)
        {
            var args = new JObject { ["action"] = action };
            Assert.True(Program.IsMutatingTool(toolName, args),
                $"expected {toolName} action={action} to invalidate the semantic cache");
        }

        // ── Read-only tools / actions must NOT invalidate (cache stays warm) ────
        [Theory]
        [InlineData("genexus_read")]
        [InlineData("genexus_query")]
        [InlineData("genexus_list_objects")]
        [InlineData("genexus_inspect")]
        [InlineData("genexus_search_source")]
        [InlineData("genexus_analyze")]
        [InlineData("genexus_what_if")]
        [InlineData("genexus_kb")]
        [InlineData("genexus_lifecycle")]
        [InlineData("genexus_gxserver")]
        [InlineData("genexus_transfer")]
        [InlineData("genexus_db")]
        [InlineData("genexus_history")]
        [InlineData("genexus_properties")]
        [InlineData("genexus_structure")]
        [InlineData("genexus_layout")]
        [InlineData("genexus_deploy")]
        [InlineData("genexus_multi_agent_lock")]
        [InlineData("genexus_sandbox")]
        [InlineData("genexus_introspect")]
        public void ToolsWithNoMutatingSignal_AreNotFlagged(string toolName)
        {
            // No action argument at all: these tools must not invalidate the cache.
            Assert.False(Program.IsMutatingTool(toolName, new JObject()),
                $"expected {toolName} (no action) to be treated as read-only");
        }

        [Theory]
        [InlineData("genexus_properties", "get")]
        [InlineData("genexus_asset", "read")]
        [InlineData("genexus_history", "list")]
        [InlineData("genexus_transfer", "export")]
        [InlineData("genexus_transfer", "inspect")]
        [InlineData("genexus_structure", "get_visual")]
        [InlineData("genexus_structure", "get_indexes")]
        [InlineData("genexus_structure", "get_logic")]
        [InlineData("genexus_layout", "get_tree")]
        [InlineData("genexus_layout", "find_controls")]
        [InlineData("genexus_layout", "list_controls")]
        [InlineData("genexus_layout", "design_system")]
        [InlineData("genexus_gxserver", "status")]
        [InlineData("genexus_gxserver", "pending")]
        [InlineData("genexus_gxserver", "conflicts")]
        [InlineData("genexus_gxserver", "history")]
        [InlineData("genexus_gxserver", "pipeline_list")]
        [InlineData("genexus_db", "sql_ddl")]
        [InlineData("genexus_db", "reorg_impact")]
        [InlineData("genexus_db", "drift_check")]
        [InlineData("genexus_db", "optimize_analyze")]
        [InlineData("genexus_lifecycle", "status")]
        [InlineData("genexus_lifecycle", "result")]
        [InlineData("genexus_lifecycle", "reorg_preview")]
        [InlineData("genexus_deploy", "list_targets")]
        public void ReadOnlyActions_AreNotFlagged(string toolName, string action)
        {
            var args = new JObject { ["action"] = action };
            Assert.False(Program.IsMutatingTool(toolName, args),
                $"expected {toolName} action={action} to be treated as read-only");
        }

        // ── The exact reported scenario ─────────────────────────────────────────
        [Fact]
        public void DeleteObject_InvalidatesSemanticCache()
        {
            Assert.True(Program.IsMutatingTool("genexus_delete_object", new JObject
            {
                ["name"] = "McpSdtFixValidate",
                ["type"] = "SDT",
                ["confirm"] = true
            }));
        }

        [Fact]
        public void DeleteObject_WithoutArgs_StillInvalidates()
        {
            // The gateway must not depend on the caller passing arguments to decide
            // whether a delete invalidates cached reads.
            Assert.True(Program.IsMutatingTool("genexus_delete_object", null));
        }

        // ── The other half: never-cached, and why that is NOT the same as mutating ──

        /// <summary>
        /// genexus_introspect must sit in exactly one of the two lists, and getting it wrong is
        /// costly in both directions.
        ///
        /// Cached (neither list): after a reindex it would still report the pre-reindex coverage,
        /// and every suppression decision downstream keys off those numbers — the agent would be
        /// told a resolved tree is still unavailable.
        ///
        /// Mutating (the wrong list): IsMutatingTool triggers a full _semanticCache.Clear(), so
        /// a reconnaissance call meant to be made FIRST and OFTEN would flush every cached read in
        /// the session. It changes no KB object; the Brief lands in .gx/.
        /// </summary>
        [Fact]
        public void Introspect_IsNeverCached_ButDoesNotFlushTheCache()
        {
            Assert.True(Program.IsLiveTool("genexus_introspect", new JObject()),
                "genexus_introspect must never serve a cached envelope: it reports current index coverage.");
            Assert.False(Program.IsMutatingTool("genexus_introspect", new JObject()),
                "genexus_introspect must NOT be flagged mutating — that clears the whole semantic cache on every call.");
        }

        [Theory]
        [InlineData("genexus_logs")]
        [InlineData("genexus_gxserver")]
        [InlineData("genexus_introspect")]
        public void LiveTools_AreNeverCached(string toolName)
        {
            Assert.True(Program.IsLiveTool(toolName, new JObject()), $"expected {toolName} to be never-cached");
        }

        [Theory]
        [InlineData("status")]
        [InlineData("result")]
        [InlineData("cancel")]
        public void Lifecycle_ProgressReads_AreNeverCached(string action)
        {
            Assert.True(Program.IsLiveTool("genexus_lifecycle", new JObject { ["action"] = action }));
        }

        [Theory]
        [InlineData("genexus_lifecycle", "index")]   // long-running, but its ENVELOPE is cacheable
        [InlineData("genexus_read", null)]
        [InlineData("genexus_inspect", null)]
        [InlineData("genexus_list_objects", null)]
        public void OrdinaryReads_StayCacheable(string toolName, string action)
        {
            var args = new JObject();
            if (action != null) args["action"] = action;
            Assert.False(Program.IsLiveTool(toolName, args),
                $"{toolName} should remain cacheable; marking it live throws away the cache's whole benefit.");
        }
    }
}
