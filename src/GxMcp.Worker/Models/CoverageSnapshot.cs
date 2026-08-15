using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Models
{
    /// <summary>
    /// Per-field population census over a set of index entries, plus the vocabulary that
    /// says what an ABSENT value means for each field. This is the honesty contract: a
    /// caller that reads a field without reading its trust level can silently mistake
    /// "we have not looked yet" for "there is nothing there".
    ///
    /// Why this exists. The lite index pass (KbService, the walk over DesignModel.Objects)
    /// writes seven fields for every object. Everything else — Module, ParentPath, Calls,
    /// CalledBy, Tables, Complexity, Embedding — is written only by IndexCacheService.UpdateEntry,
    /// which opens the object through the SDK. With Configuration.LazyEnrichment=true (the
    /// default) that happens on demand, so on a real KB those fields can sit near zero:
    /// measured on MatikaErp_3003 (14,932 objects) Calls was populated for 0 objects,
    /// CalledBy for 70, and Module for 1. A caller that ranks by CalledBy on that index is
    /// ranking noise, and a caller that reports "no callers" is reporting its own blindness.
    /// </summary>
    public class CoverageSnapshot
    {
        /// <summary>Fields the lite pass attempts for every object. Absence here is a fact about the KB.</summary>
        private static readonly HashSet<string> LitePassFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "guid", "name", "type", "description", "lastUpdate", "createdAt", "lastModifiedBy"
        };

        /// <summary>
        /// Fields only enrichment writes. Absence here is indistinguishable from "not read yet",
        /// so it must never be reasoned over.
        /// </summary>
        private static readonly HashSet<string> EnrichmentOnlyFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "module", "parentPath", "calls", "calledBy", "tables", "rules",
            "sourceSnippet", "complexity", "embedding"
        };

        public int ObjectsInScope { get; set; }
        public int EnrichedInScope { get; set; }

        /// <summary>
        /// Entries whose placement was actually resolved — i.e. a non-empty ParentPath.
        /// NOT the same as a non-empty ParentFolderPath: IndexCacheService.ComposeParentFolderPath
        /// turns an empty ParentPath into the literal string "Root Module", so ParentFolderPath is
        /// 100% populated on an index where nothing was ever resolved.
        /// </summary>
        public int StructureResolvedInScope { get; set; }

        /// <summary>
        /// Distinct ParentFolderPath values in scope. When this is 1 on a KB that contains Folder
        /// or Module objects, the folder tree in the index is synthesized, not observed — the
        /// single value is "Root Module" for everything.
        /// </summary>
        public int DistinctFolderPaths { get; set; }

        /// <summary>Field name (camelCase, as emitted) -> how many entries in scope carry a value.</summary>
        public Dictionary<string, int> PopulatedByField { get; set; }

        public CoverageSnapshot()
        {
            PopulatedByField = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public double EnrichedPct
        {
            get { return Pct(EnrichedInScope); }
        }

        public double StructureResolvedPct
        {
            get { return Pct(StructureResolvedInScope); }
        }

        /// <summary>Percentage of the scope carrying a value for <paramref name="field"/>, rounded to 1dp.</summary>
        public double PctOf(string field)
        {
            int n;
            if (PopulatedByField == null || !PopulatedByField.TryGetValue(field ?? string.Empty, out n)) return 0d;
            return Pct(n);
        }

        /// <summary>
        /// The trust level for a field, in the four-value vocabulary:
        ///   complete        - written for every object by the pass that produced this index
        ///   observed:&lt;pct&gt;  - a cheap pass attempted every object; absence is a real fact
        ///   partial:&lt;pct&gt;   - enrichment-only; absence means "not read yet". Never reason on it
        ///   unavailable     - nothing in scope carries it, or the value present is synthesized
        /// </summary>
        public string TrustOf(string field)
        {
            if (ObjectsInScope <= 0) return "unavailable";

            // Placement is a special case: ParentFolderPath/Path are always populated because
            // they are composed from ParentPath, so their raw population count says nothing.
            // What matters is whether placement was ever resolved.
            if (string.Equals(field, "folderPath", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field, "parentFolderPath", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field, "path", StringComparison.OrdinalIgnoreCase))
            {
                if (StructureResolvedInScope == 0) return "unavailable";
                return "partial:" + Fmt(StructureResolvedPct);
            }

            double pct = PctOf(field);

            if (EnrichmentOnlyFields.Contains(field ?? string.Empty))
            {
                // Deliberately still "partial" at 0.0 rather than "unavailable": the distinction
                // the caller needs is WHY it is empty, and for an enrichment field the answer is
                // always "we have not looked", never "the KB has none".
                return "partial:" + Fmt(pct);
            }

            if (LitePassFields.Contains(field ?? string.Empty))
            {
                return pct >= 100d ? "complete" : "observed:" + Fmt(pct);
            }

            return pct <= 0d ? "unavailable" : "observed:" + Fmt(pct);
        }

        /// <summary>True when a section built on <paramref name="field"/> must be suppressed rather than emitted.</summary>
        public bool ShouldSuppress(string field, double floorPct)
        {
            string trust = TrustOf(field);
            if (trust == "unavailable") return true;
            if (trust == "complete") return false;
            return PctOfTrust(trust) < floorPct;
        }

        private static double PctOfTrust(string trust)
        {
            int i = (trust ?? string.Empty).IndexOf(':');
            if (i < 0) return 100d;
            double v;
            return double.TryParse(trust.Substring(i + 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0d;
        }

        private double Pct(int n)
        {
            if (ObjectsInScope <= 0) return 0d;
            return Math.Round(n * 100d / ObjectsInScope, 1);
        }

        private static string Fmt(double pct)
        {
            return pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
