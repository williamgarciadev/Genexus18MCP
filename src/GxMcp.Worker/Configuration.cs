using System;
using System.Configuration;

namespace GxMcp.Worker
{
    /// <summary>
    /// Centralised typed accessors over App.config &lt;appSettings&gt;. Keep this small —
    /// only host flags that gate worker behaviour at runtime (perf knobs, feature flags).
    /// </summary>
    public static class Configuration
    {
        // Shared accessor: an appSetting bool that defaults to true (on) and is also true on
        // any read/parse failure. All the indexing feature flags share these semantics.
        private static bool BoolSetting(string key, bool defaultValue = true)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings[key];
                if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
                return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return defaultValue;
            }
        }

        // SP6.T6 — gate the new lite-pass + lazy-enrichment indexing pipeline.
        // Defaults to true (fast path on). Set Indexing.UseLitePass=false in App.config
        // to fall back to the legacy monolithic BulkIndex path for one release.
        public static bool UseLitePass => BoolSetting("Indexing.UseLitePass");

        // Fase 1 — gate the persistible delta-on-open path. Defaults to true. Set
        // Indexing.UseDeltaOnOpen=false in App.config to fall back to the previous
        // "load cache + trust forever" warm-start behaviour (no automatic delta).
        public static bool UseDeltaOnOpen => BoolSetting("Indexing.UseDeltaOnOpen");

        // Post-upgrade write-starvation fix — gate the delta-across-worker-DLL path.
        // Defaults to true. Every MCP version upgrade changes the worker DLL hash →
        // DllMatch=False → a full 38k re-walk that sets _isIndexing and BLOCKS ALL WRITES
        // (EnsureNotIndexing) for its whole duration. When this is on and only the DLL
        // differs (schema still matches → index layout unchanged), we run the bounded
        // DELTA refresh instead and let it re-baseline the sidecar to the new DLL hash.
        // Tradeoff: enrichment-LOGIC changes in the new release won't retro-apply to
        // UNCHANGED objects until a forced reindex (genexus_lifecycle action=index
        // force=true). In LazyEnrichment mode (default) those objects are re-enriched
        // on demand anyway, so the staleness window is invisible in practice. Set
        // Indexing.DeltaAcrossWorkerDll=false to restore the full-rebuild-on-DLL-change
        // behaviour (always re-enriches everything, but starves writes on large KBs).
        public static bool DeltaAcrossWorkerDll => BoolSetting("Indexing.DeltaAcrossWorkerDll");

        // Fase 3 — lazy/on-demand enrichment. Defaults to true. When true, the index build
        // stops after the lite catalogue (LiteReady→Ready) and does NOT eagerly drain all
        // objects through enrichment (measured ~20min on a 38.6k KB, ~91% STA-bound SDK reads,
        // and mostly wasted since most objects are never queried). Edges/snippets/embeddings
        // are filled in on demand via EnrichmentQueue.PromoteAsync when a tool needs a target.
        // Set Indexing.LazyEnrichment=false to restore the eager full-KB enrichment drain.
        public static bool LazyEnrichment => BoolSetting("Indexing.LazyEnrichment");

        // Resolve each object's placement (Module / ParentPath / Path) during the lite walk
        // instead of leaving it to enrichment. Defaults to TRUE; set
        // Indexing.LitePassResolvesHierarchy=false to fall back to the previous behaviour.
        //
        // Why it must be on. Placement used to be written only by IndexCacheService.UpdateEntry,
        // i.e. by enrichment — and under the default LazyEnrichment most objects never reach it.
        // Measured on a 14,932-object KB: Module was populated for exactly 1 object, while every
        // single entry reported the folder "Root Module", because ComposeParentFolderPath
        // synthesizes that string from an empty ParentPath. The folder tree every tool read was
        // therefore fabricated — that KB has 90 Module and 304 Folder objects.
        //
        // Why it is affordable. The lite walk already holds a materialised KBObject (it iterates
        // kb.DesignModel.Objects directly, it does not Objects.Get per object) and already reads
        // seven handle properties because handle reads load no parts. ResolveHierarchy walks
        // .Parent reading .Name / .TypeDescriptor.Name — the same kind of read.
        //
        // Measured on a 3,321-object KB, warm, isolated by its own tick accumulator:
        // ~1.58 ms/object (4.7-5.7 s per run), against ~31 ms/object for full enrichment — about
        // 20x cheaper, and it opens no objects. It roughly doubles the warm lite walk, which is
        // the cost of a reindex paying once for placement instead of every tool resolving it
        // piecemeal forever. The [LITE-WALK] log reports hierarchyMs separately so the cost stays
        // observable on any KB; on a deeper object tree expect more .Parent hops per object.
        public static bool LitePassResolvesHierarchy => BoolSetting("Indexing.LitePassResolvesHierarchy");
    }
}
