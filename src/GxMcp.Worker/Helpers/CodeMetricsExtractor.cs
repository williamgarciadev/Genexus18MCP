using System;
using System.Text.RegularExpressions;
using GxMcp.Worker.Models;

namespace GxMcp.Worker.Helpers
{
    // Extracts compact source metrics from GeneXus Procedure/DataProvider source so KB-wide
    // analytics can run over the index without re-reading each object via the SDK. Regex-based
    // and comment-stripped; intentionally cheap (runs once per object at enrichment).
    public static class CodeMetricsExtractor
    {
        private static readonly Regex RxForEach = new Regex(@"\bfor\s+each\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxEndFor  = new Regex(@"\bendfor\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxWhere   = new Regex(@"\bwhere\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxNew      = new Regex(@"\bnew\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxCommit   = new Regex(@"\bcommit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // GeneXus line comments start with // ; block comments /* */.
        private static readonly Regex RxLineComment  = new Regex(@"//.*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex RxBlockComment = new Regex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

        public static SearchIndex.CodeMetrics Extract(string source)
        {
            var m = new SearchIndex.CodeMetrics();
            if (string.IsNullOrEmpty(source)) return m;

            m.Lines = source.Split('\n').Length;

            // Strip comments so a commented-out 'for each' doesn't inflate counts.
            string code = RxBlockComment.Replace(source, " ");
            code = RxLineComment.Replace(code, "");

            m.ForEach = RxForEach.Matches(code).Count;
            m.Where   = RxWhere.Matches(code).Count;
            m.New     = RxNew.Matches(code).Count;
            m.Commit  = RxCommit.Matches(code).Count;

            // Nested for-each: walk the for-each/endfor tokens in order and count any
            // 'for each' opened while already inside one (a classic optimization smell —
            // often collapsible to a single navigation or a data selector).
            m.NestedForEach = CountNestedForEach(code);
            return m;
        }

        /// <summary>
        /// Lines that carry actual code: non-blank after stripping line and block comments.
        /// This is the number the team's size limits are written against ("a main Procedure
        /// stays under 80 useful lines" — see docs/clean-architecture-genexus.md §2-S and
        /// linter rule GX014), so the definition lives here, next to the comment-stripping
        /// regexes it depends on, rather than being re-derived by each caller.
        /// </summary>
        public static int CountCodeLines(string source)
        {
            if (string.IsNullOrEmpty(source)) return 0;

            // Same stripping order as Extract: block comments first (they may span lines and
            // hide // sequences), then line comments. Replacing a block comment with " "
            // leaves its lines blank, so they fall out of the count below.
            string code = RxBlockComment.Replace(source, " ");
            code = RxLineComment.Replace(code, "");

            int count = 0;
            foreach (var line in code.Split('\n'))
            {
                if (line.Trim().Length > 0) count++;
            }
            return count;
        }

        private static int CountNestedForEach(string code)
        {
            int depth = 0, nested = 0;
            // Scan for-each and endfor tokens in source order.
            var tokens = Regex.Matches(code, @"\bfor\s+each\b|\bendfor\b", RegexOptions.IgnoreCase);
            foreach (Match t in tokens)
            {
                bool isFor = t.Value.IndexOf("for", StringComparison.OrdinalIgnoreCase) == 0
                             && t.Value.IndexOf("each", StringComparison.OrdinalIgnoreCase) > 0;
                if (isFor)
                {
                    if (depth >= 1) nested++;
                    depth++;
                }
                else if (depth > 0)
                {
                    depth--;
                }
            }
            return nested;
        }
    }
}
