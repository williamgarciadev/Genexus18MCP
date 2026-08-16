using System;
using System.Linq;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Gateway
{
    partial class Program
    {
        private static JToken TruncateResponseIfNeeded(JToken? result, string toolName)
        {
            if (result == null) return JValue.CreateNull();
            
            string? readPart = (result as JObject)?["part"]?.ToString();
            bool isXmlMetadataRead = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                                     (string.Equals(readPart, "Layout", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "WebForm", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternInstance", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(readPart, "PatternVirtual", StringComparison.OrdinalIgnoreCase));

            // PERFORMANCE (G-T1): fast structural pre-check to avoid full tree serialization
            // on obviously small responses (whoami, status, inspect, short reads, mutations).
            if (result is JObject quickObj)
            {
                bool mayExceedBudget = false;
                foreach (var prop in quickObj.Properties())
                {
                    if (prop.Value is JValue jv && jv.Value is string s && s.Length > (isXmlMetadataRead ? 100000 : 15000))
                    {
                        mayExceedBudget = true;
                        break;
                    }
                    if (prop.Value is JArray jarr && jarr.Count > 10)
                    {
                        mayExceedBudget = true;
                        break;
                    }
                }
                if (!mayExceedBudget)
                {
                    return result;
                }
            }

            string raw = result.ToString(Formatting.None);
            // issue #25 #6: the worker already paginates genexus_read to ~200 lines /
            // 16 KB and reports it via `isTruncatedByWorker` + offset/limit/
            // suggestedNextOffset. When the worker already bounded the page, the
            // gateway must NOT char-slice `source` again — that re-cut dropped the
            // middle of an already-bounded page and orphaned the pagination fields.
            bool workerPaginatedRead =
                string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase) &&
                ((result as JObject)?["isTruncatedByWorker"]?.ToObject<bool>() ?? false);
            int softBudget = isXmlMetadataRead
                ? 220000
                : string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                ? 24000
                : string.Equals(toolName, "genexus_asset", StringComparison.OrdinalIgnoreCase)
                    ? 400000
                    : 60000;
            if (raw.Length < softBudget) return result;

            Log($"[Budget] Truncating response for {toolName} ({raw.Length} chars)");

            if (result is JObject obj)
            {
                if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var metadataField in new[] { "variables", "calls", "dataSchema", "patternMetadata" })
                    {
                        if (obj[metadataField] != null)
                        {
                            obj.Remove(metadataField);
                            obj["isTruncated"] = true;
                            obj["message"] = "Gateway trimmed derived metadata from genexus_read to keep the response within the MCP context budget.";
                        }
                    }
                }

                if (obj["results"] is JArray searchResults && searchResults.Count > 10)
                {
                    int originalCount = searchResults.Count;
                    string currentRaw = obj.ToString(Formatting.None);
                    if (currentRaw.Length > 80000)
                    {
                        // Drastic pruning: keep only first 5
                        while (searchResults.Count > 5) searchResults.RemoveAt(searchResults.Count - 1);
                        obj["isTruncated"] = true;
                        obj["returnedCount"] = 5;
                        obj["originalCount"] = originalCount;
                        return obj;
                    }
                }

                bool isRead = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase);

                // issue #26 P7: for genexus_read source/content, the gateway used to
                // head+tail slice and DROP THE MIDDLE — leaving a silent hole and an
                // offset that no longer described the returned bytes. Replace that with a
                // single, predictable, LINE-ALIGNED PREFIX cut that shares the worker's
                // line-based pagination model: keep whole lines from the front, tell the
                // caller exactly which limit hit and the safe line offset to continue from.
                // No middle is ever dropped.
                if (isRead && !isXmlMetadataRead)
                {
                    // Issue #27 item 7: when the caller explicitly asked for the whole part
                    // (limit=0 → worker sets explicitFullRead), honour it with a much larger
                    // budget so "read in full" is truthful. Still a line-aligned prefix cut
                    // (never a middle drop) with a safe continuation offset if the part is
                    // genuinely enormous, so the contract stays predictable either way.
                    bool explicitFullRead = (obj["explicitFullRead"]?.ToObject<bool?>() ?? false);
                    int readFieldBudget = explicitFullRead ? 200000 : 20000;
                    foreach (var field in new[] { "source", "content", "code" })
                    {
                        // Worker already paginated this page — its offset/suggestedNextOffset
                        // are authoritative; don't second-guess with a gateway cut.
                        if (workerPaginatedRead && string.Equals(field, "source", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (obj[field]?.Type != JTokenType.String) continue;
                        string val = obj[field]!.ToString();
                        if (val.Length <= readFieldBudget) continue;

                        // Cut on a line boundary at/under the budget so we never split a line.
                        int cut = val.LastIndexOf('\n', Math.Min(readFieldBudget, val.Length) - 1);
                        if (cut <= 0) cut = Math.Min(readFieldBudget, val.Length); // no newline: hard prefix
                        string kept = val.Substring(0, cut);
                        int keptLines = kept.Length == 0 ? 0 : kept.Split('\n').Length;
                        int baseOffset = obj["offset"]?.ToObject<int?>() ?? 0;
                        int safeNextOffset = baseOffset + keptLines;

                        obj[field] = kept;
                        obj["isTruncated"] = true;
                        obj["truncatedByGateway"] = true;
                        obj["truncatedBy"] = "gateway";
                        obj["gatewaySafeNextOffset"] = safeNextOffset;
                        obj["gatewayTruncationHint"] =
                            $"Gateway trimmed '{field}' to the context budget by keeping whole lines from the front (NO middle dropped). " +
                            $"Continue cleanly with genexus_read offset={safeNextOffset} (line-based) to read the next page.";
                    }
                }

                // Non-read tools (and read metadata fields): head+tail trim is fine here —
                // these are derived blobs, not paginable source, so a middle elision just
                // fits the budget without a pagination contract to break.
                var fieldsToTruncate = (isRead && !isXmlMetadataRead)
                    ? new[] { "fileContent", "details" }
                    : new[] { "source", "content", "code", "fileContent", "details" };
                foreach (var field in fieldsToTruncate)
                {
                    var fieldValue = obj[field];
                    if (fieldValue != null && fieldValue.Type == JTokenType.String)
                    {
                        string val = fieldValue.ToString();
                        int fieldBudget = isXmlMetadataRead ? 180000 : 20000;
                        int headBudget = isXmlMetadataRead ? 140000 : 15000;
                        int tailBudget = isXmlMetadataRead ? 40000 : 5000;
                        if (val.Length > fieldBudget)
                        {
                            obj[field] = val.Substring(0, headBudget) +
                                           "\n\n[... TRUNCATED BY GATEWAY TOKEN BUDGET ...] \n\n" +
                                           val.Substring(val.Length - tailBudget);
                            obj["isTruncated"] = true;
                            obj["truncatedByGateway"] = true;
                            obj["gatewayTruncationHint"] = "Gateway trimmed this field to fit the context budget (a middle slice was dropped). This field is not paginable; re-request the specific object/part if you need the full bytes.";
                        }
                    }
                }

                string truncatedRaw = obj.ToString(Formatting.None);
                if (truncatedRaw.Length > 80000)
                {
                    // issue #25 #6: for genexus_read, preserve the head+tail-trimmed
                    // object instead of wiping it to a bare error (the old fallback
                    // discarded the tail it had just carefully kept). Only non-read
                    // shapes fall back to the structural error.
                    if (string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase))
                    {
                        obj["isTruncated"] = true;
                        obj["truncatedByGateway"] = true;
                        obj["message"] = "Response exceeded the gateway budget even after trimming; re-request with a smaller `limit` or use offset/limit pagination for exact bytes.";
                        return obj;
                    }
                    // Fallback to ensuring valid JSON structure when heavily nested Strings overfill
                    return JToken.FromObject(new {
                        jsonrpc = "2.0",
                        error = "Response exceeded 80k token budget and could not be safely parsed. Try lower limits or pagination.",
                        isTruncated = true
                    });
                }
                return obj;
            }
            else if (result is JArray arr)
            {
                // Truncate arrays if they exceed limits
                while (arr.Count > 5 && arr.ToString(Formatting.None).Length > 80000)
                {
                    arr.RemoveAt(arr.Count - 1);
                }
                if (arr.ToString(Formatting.None).Length > 80000)
                {
                    return JToken.FromObject(new { 
                        error = "Array response exceeded 80k token budget. Try lower limits or pagination.", 
                        isTruncated = true 
                    });
                }
                return arr;
            }

            return new JValue(raw.Substring(0, 75000) + "... [TRUNCATED]");
        }

        // The OTHER half of the cache contract, and the one that is easy to get wrong.
        //
        // IsMutatingTool answers "must the cache be cleared?"; this answers "may this response be
        // cached at all?". They are not opposites, and a tool that needs the second must NOT be
        // put in the first: IsMutatingTool triggers a full _semanticCache.Clear(), so registering
        // a frequently-called read there would flush every cached read in the session on each call.
        //
        // A live tool reports state that moves without any KB object changing — job progress, log
        // tails, server state, index coverage — so replaying an envelope for it hands the caller a
        // past reading of the present. Extracted from the request loop so it can be asserted; it
        // was previously a local boolean, which is why nothing guarded it.
        internal static bool IsLiveTool(string toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            if (string.Equals(toolName, "genexus_logs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_gxserver", StringComparison.OrdinalIgnoreCase) ||
                // Reports current index coverage/census. After a reindex a replayed envelope would
                // still claim 0.2% enriched, and every suppression decision downstream keys off it.
                string.Equals(toolName, "genexus_introspect", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                string? a = args?["action"]?.ToString()?.ToLowerInvariant();
                return a == "status" || a == "result" || a == "cancel";
            }

            return false;
        }

        // Semantic-cache invalidation gate: returns true when a tool call may
        // change KB object state, so DispatchCore can clear _semanticCache before
        // (and only before) a mutation. A MISS here means the next identical read
        // replays a stale envelope — the read-after-delete staleness bug (a
        // genexus_delete_object was not recognised as mutating, so a cached
        // part=Structure read survived the delete). The verb-substring heuristic
        // covers names like edit/create/refactor; umbrella tools and
        // action-dependent tools need the explicit cases below.
        internal static bool IsMutatingTool(string toolName, JObject? args)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;

            if (string.Equals(toolName, "genexus_import_object", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_delete_object", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_rename_across_kb", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_apply_pattern", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_kb_import", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, "genexus_save_as", StringComparison.OrdinalIgnoreCase)) // clones parts into a new object
            {
                return true;
            }

            if (toolName.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("edit", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("refactor", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("variable", StringComparison.OrdinalIgnoreCase) || // genexus_variable (add/delete/modify)
                toolName.Contains("add_variable", StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains("modify_variable", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(toolName, "genexus_properties", StringComparison.OrdinalIgnoreCase))
            {
                // set = scalar property write; move = reparent (Folder/Module placement).
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "set", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "move", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_asset", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(args?["action"]?.ToString(), "write", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_history", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "save", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "restore", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_transfer", StringComparison.OrdinalIgnoreCase))
            {
                // import is destructive (applies an XPZ into the KB); export/inspect read-only.
                return string.Equals(args?["action"]?.ToString(), "import", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase))
            {
                // translations_import writes translated strings into object parts and
                // sample_data writes test rows into the DB — both mutate state that the
                // (cacheable) db analysis reads report on. All other actions
                // (drift/optimize/sql/types/reorg_*) are read-only analysis.
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "translations_import", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "sample_data", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_gxserver", StringComparison.OrdinalIgnoreCase))
            {
                // commit/update/lock/resolve apply server state to the KB; pipeline_run/abort
                // drive CI runs. The reads (status/pending/conflicts/...) are never cached
                // anyway, but these WRITES change object parts and must drop cached reads.
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "commit", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "update", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "lock", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "resolve", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "pipeline_run", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "pipeline_abort", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_structure", StringComparison.OrdinalIgnoreCase))
            {
                // Every non-get action mutates the structure/indexes.
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "update_visual", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "create_index", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "drop_index", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "set_attribute", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "set_level", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "set_domain", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "update_group", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "move_attribute", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_layout", StringComparison.OrdinalIgnoreCase))
            {
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "set_property", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "set_properties", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "rename_printblock", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "add_printblock", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_lifecycle", StringComparison.OrdinalIgnoreCase))
            {
                // status/result/cancel/reorg_preview are reads (and status/result/cancel are
                // already excluded from the semantic cache as live tools). Everything else
                // writes build/spec artefacts or the index.
                string? action = args?["action"]?.ToString();
                return string.Equals(action, "build", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "rebuild", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "specify", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "validate", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "validate-kb", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "sync", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "index", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "reorg", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(action, "snapshots-restore", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        // Items 54/55/56: resolve a "KB ref" argument that may be either an alias
        // declared in config.Environment.KBs[] or a literal filesystem path.
        // Returns the resolved absolute path, or null if neither match.
        private static string? ResolveKbPath(string aliasOrPath)
        {
            if (string.IsNullOrWhiteSpace(aliasOrPath)) return null;
            var declared = _activeConfig?.Environment?.KBs?.FirstOrDefault(
                k => string.Equals(k.Alias, aliasOrPath, StringComparison.OrdinalIgnoreCase));
            if (declared != null) return declared.Path;
            if (System.IO.Directory.Exists(aliasOrPath)) return aliasOrPath;
            return null;
        }

        /// <summary>
        /// Add <c>_meta.autoInjected: ["type"]</c> to the content text payload of a
        /// tool result envelope so the LLM sees that gateway inferred the type.
        /// Does not overwrite any existing <c>_meta</c> structure — merges only.
        /// </summary>
        private static void InjectAutoTypeAnnotation(JObject toolInnerResult, string injectedType)
        {
            try
            {
                var contentArr = toolInnerResult["content"] as JArray;
                if (contentArr == null || contentArr.Count == 0) return;
                var firstContent = contentArr[0] as JObject;
                if (firstContent == null) return;

                string? rawText = firstContent["text"]?.ToString();
                if (rawText == null) return;

                JObject payload;
                try { payload = JObject.Parse(rawText); }
                catch { return; }  // non-JSON text blob — skip

                // Merge into existing _meta or create new
                if (payload["_meta"] is not JObject meta)
                {
                    meta = new JObject();
                    payload["_meta"] = meta;
                }
                meta["autoInjected"] = new JArray("type");
                meta["autoInjectedType"] = injectedType;

                firstContent["text"] = payload.ToString(Formatting.None);
            }
            catch
            {
                // Best-effort — never fail a tool call over annotation
            }
        }

        internal static JObject BuildToolTextResponse(JToken? idToken, JToken payload, bool isError, string? toolName = null, JObject? toolArgs = null)
        {
            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = idToken?.DeepClone(),
                ["result"] = BuildToolResultContent(payload, isError, toolName, toolArgs)
            };
        }

        internal static JObject BuildToolResultContent(JToken payload, bool isError, string? toolName = null, JObject? toolArgs = null)
        {
            JToken axiPayload = NormalizeToolPayloadForAxi(payload, toolName ?? "unknown", toolArgs, isError);
            var result = new JObject
            {
                ["resultType"] = "complete",
                ["isError"] = isError,
                ["content"] = new JArray { new JObject
                {
                    ["type"] = "text",
                    ["text"] = axiPayload.ToString(Formatting.None)
                } }
            };
            // MCP's structuredContent lets modern clients consume the JSON result
            // without reparsing the text content. Keep the text representation for
            // legacy clients and omit structuredContent on tool errors.
            if (!isError && (axiPayload.Type == JTokenType.Object || axiPayload.Type == JTokenType.Array))
            {
                result["structuredContent"] = axiPayload;
            }
            return result;
        }

        private static JToken NormalizeToolPayloadForAxi(JToken? payload, string toolName, JObject? toolArgs, bool isError)
        {
            HashSet<string>? requestedFields = ParseRequestedFields(toolArgs);
            // Friction 2026-05-22 #64: projection=minimal|standard|verbose lets the
            // agent opt into a smaller or larger field set without having to enumerate
            // fields[]. Resolves to a HashSet that overrides the axiCompact default —
            // explicit fields[] still wins (highest specificity).
            string? projection = toolArgs?["projection"]?.ToString();
            bool verboseRequested = !string.IsNullOrWhiteSpace(projection)
                && string.Equals(projection.Trim(), "verbose", StringComparison.OrdinalIgnoreCase);
            if (requestedFields == null && !string.IsNullOrWhiteSpace(projection))
            {
                requestedFields = ResolveProjection(toolName, projection);
            }
            // projection=verbose explicitly opts OUT of the compact filter — earlier
            // versions silently fell into GetDefaultCompactFields here because
            // ResolveProjection returns null for both 'verbose' and unknown levels.
            if (requestedFields == null && !verboseRequested && ShouldUseCompactDefaults(toolArgs))
            {
                requestedFields = GetDefaultCompactFields(toolName);
            }

            bool shouldProject = requestedFields != null && requestedFields.Count > 0 && ShouldProjectFieldsForTool(toolName);

            string[] collectionKeys = {
                "results", "objects", "items", "tools", "checks", "entries", "nodes", "controls",
                // Additional primary-collection keys used by non-search tools:
                "endpoints", "history", "snapshots", "versions", "modules",
                "pending", "ignored", "conflicts", "targets", "pipelines"
            };

            JObject obj;
            string? matchedKey = null;

            if (payload is JArray arrayPayload)
            {
                matchedKey = "results";
                obj = new JObject
                {
                    ["results"] = shouldProject ? ProjectArrayItems(arrayPayload, requestedFields!) : arrayPayload.DeepClone()
                };
            }
            else if (payload is JObject objPayload)
            {
                matchedKey = collectionKeys.FirstOrDefault(k => objPayload[k] is JArray);
                if (shouldProject && matchedKey != null)
                {
                    obj = new JObject();
                    foreach (var prop in objPayload.Properties())
                    {
                        if (string.Equals(prop.Name, matchedKey, StringComparison.Ordinal))
                        {
                            obj[prop.Name] = ProjectArrayItems((JArray)prop.Value, requestedFields!);
                        }
                        else
                        {
                            obj[prop.Name] = prop.Value.DeepClone();
                        }
                    }
                }
                else
                {
                    obj = (JObject)objPayload.DeepClone();
                }
            }
            else
            {
                return payload ?? JValue.CreateNull();
            }

            // Per-response meta is intentionally lean: `schemaVersion` is emitted
            // once in the `initialize` handshake (`_meta.schemaVersion`) and the
            // client already knows which tool it called, so neither field is
            // repeated per response (~60B/response saved). Only emit `meta` when
            // a real signal (truncated/fields/totalByType/…) gets attached below.
            var meta = obj["meta"] as JObject ?? new JObject();

            if (obj["isTruncated"]?.Value<bool>() == true)
            {
                meta["truncated"] = true;
                var help = obj["help"] as JArray ?? new JArray();
                string truncateHint = string.Equals(toolName, "genexus_read", StringComparison.OrdinalIgnoreCase)
                    ? "Response truncated by gateway budget. Use limit/offset to page source content."
                    : "Response truncated by gateway budget. Narrow filters or lower limit for deterministic follow-up.";

                if (!help.Any(item => string.Equals(item?.ToString(), truncateHint, StringComparison.OrdinalIgnoreCase)))
                {
                    help.Add(truncateHint);
                }

                obj["help"] = help;
            }

            if (!isError &&
                string.Equals(obj["status"]?.ToString(), "ok", StringComparison.Ordinal) &&
                obj["noChange"] == null &&
                (string.Equals(obj["code"]?.ToString(), "NoChange", StringComparison.Ordinal)
                 || string.Equals(obj["result"]?["noChangeReason"]?.ToString(), "literal_identical", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(obj["details"]?.ToString(), "No change", StringComparison.OrdinalIgnoreCase)))
            {
                obj["noChange"] = true;
            }

            // Where the collection lives: top-level first (search tools), then inside the
            // canonical `result` object (McpResponse.Ok producers). Deterministic lookup —
            // never auto-detect "the sole array property" (would wrongly pick up per-row
            // sub-arrays like `endpoints[i].parms`).
            JObject collectionHost = obj;
            if (matchedKey == null && obj["result"] is JObject resultObj)
            {
                matchedKey = collectionKeys.FirstOrDefault(k => resultObj[k] is JArray);
                if (matchedKey != null)
                {
                    collectionHost = resultObj;
                }
            }

            if (matchedKey != null)
            {
                var arr = (JArray)collectionHost[matchedKey]!;

                if (collectionHost == obj && shouldProject)
                {
                    meta["fields"] = new JArray(requestedFields!.OrderBy(field => field, StringComparer.OrdinalIgnoreCase));
                }

                if (meta["totalByType"] == null)
                {
                    var totalsByType = BuildTotalsByType(arr);
                    if (totalsByType.Properties().Any())
                    {
                        meta["totalByType"] = totalsByType;
                    }
                }

                int returned = arr.Count;
                if (obj["returned"] == null) obj["returned"] = returned;
                if (obj["empty"] == null) obj["empty"] = returned == 0;
                if ((obj["empty"]?.Value<bool>() ?? false))
                {
                    EnsureEmptyStateHelp(obj, toolName);
                }

                int? total = TryReadInt(obj["total"]) ??
                             TryReadInt(obj["count"]) ??
                             TryReadInt(obj["totalCount"]);
                if (total.HasValue && obj["total"] == null)
                {
                    obj["total"] = total.Value;
                }

                int? limit = TryReadInt(toolArgs?["limit"]);
                int offset = TryReadInt(toolArgs?["offset"]) ?? 0;
                int? effectiveTotal = TryReadInt(obj["total"]);

                if (limit.HasValue && effectiveTotal.HasValue)
                {
                    bool hasMore = (offset + returned) < effectiveTotal.Value;
                    if (obj["hasMore"] == null) obj["hasMore"] = hasMore;
                    if (hasMore && obj["nextOffset"] == null)
                    {
                        obj["nextOffset"] = offset + returned;
                    }
                }
            }

            // Only emit a `meta` block when at least one signal was attached;
            // an empty `{}` is pure overhead for the 90% of responses that have
            // no truncation/projection/totals to surface.
            if (meta.Properties().Any())
            {
                obj["meta"] = meta;
            }
            else
            {
                obj.Remove("meta");
            }

            // SQL-dialect nudge for DB tools. The LLM already sees the dialect in
            // whoami.database.default.dialect, but planting it on the response of the
            // tool that actually returns SQL is the second-nudge that lets the agent
            // align dialect at point-of-use without re-reading whoami.
            try
            {
                if (IsSqlGeneratingTool(toolName, toolArgs) && obj["dialect"] == null)
                {
                    var info = GetCachedDatabaseInfo();
                    var defaultStore = info?["default"] as JObject;
                    string? dialect = defaultStore?["dialect"]?.ToString();
                    string? type = defaultStore?["type"]?.ToString();
                    if (!string.IsNullOrEmpty(dialect) && !string.Equals(dialect, "unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        obj["dialect"] = dialect;
                        if (!string.IsNullOrEmpty(type)) obj["dialectType"] = type;
                    }
                }
            }
            catch { /* best-effort UX sugar */ }

            // next_legal_actions injection — last step.
            // SOTA LLM-UX: state-changing tool responses carry an additive
            // array of the most-likely useful next tool calls so the LLM
            // doesn't have to guess across turns. Read-only tools and
            // payloads without a natural follow-up return null and the
            // field is simply omitted. Spec-clean: extra top-level field;
            // clients that don't know about it ignore it.
            try
            {
                if (obj["next_legal_actions"] == null)
                {
                    JArray? actions = NextLegalActionsBuilder.BuildFor(toolName, toolArgs, obj, isError);
                    if (actions != null && actions.Count > 0)
                    {
                        obj["next_legal_actions"] = actions;
                    }
                }
            }
            catch
            {
                // Builder is best-effort UX sugar; never let it break the
                // response envelope.
            }

            return obj;
        }

        private static void EnsureEmptyStateHelp(JObject obj, string toolName)
        {
            var help = obj["help"] as JArray ?? new JArray();
            string hint = string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase)
                ? "No matches found for the current query. Try broader terms or remove filters."
                : string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase)
                    ? "No objects found for the current scope. Verify parentPath/parent filters."
                    : "No results returned for this request.";

            if (!help.Any(item => string.Equals(item?.ToString(), hint, StringComparison.OrdinalIgnoreCase)))
            {
                help.Add(hint);
            }

            obj["help"] = help;
        }

        private static bool ShouldProjectFieldsForTool(string toolName)
        {
            return string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase);
        }

        private static JObject BuildTotalsByType(JArray arr)
        {
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in arr)
            {
                if (row is JObject rowObj)
                {
                    if (rowObj.TryGetValue("type", StringComparison.OrdinalIgnoreCase, out var typeTok) &&
                        typeTok is JValue jv && jv.Value is string s && !string.IsNullOrWhiteSpace(s))
                    {
                        totals[s] = totals.TryGetValue(s, out int count) ? count + 1 : 1;
                    }
                }
            }

            var outObj = new JObject();
            foreach (var kv in totals.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                outObj[kv.Key] = kv.Value;
            }

            return outObj;
        }

        private static JArray ProjectArrayItems(JArray arr, HashSet<string> fields)
        {
            var projected = new JArray();
            foreach (var row in arr)
            {
                if (row is not JObject rowObj)
                {
                    projected.Add(row.DeepClone());
                    continue;
                }

                var outRow = new JObject();
                foreach (var prop in rowObj.Properties())
                {
                    if (fields.Contains(prop.Name))
                    {
                        outRow[prop.Name] = prop.Value.DeepClone();
                    }
                }

                projected.Add(outRow);
            }

            return projected;
        }

        // Returns true when compact-by-default projection should be applied for tools that
        // declare a default compact field set in GetDefaultCompactFields. Default behavior
        // (no axiCompact key) is TRUE — the LLM must pass `axiCompact: false` to opt out.
        private static bool ShouldUseCompactDefaults(JObject? toolArgs)
        {
            if (toolArgs == null) return true;
            var token = toolArgs["axiCompact"];
            if (token == null) return true;
            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }
            return !bool.TryParse(token.ToString(), out bool parsed) || parsed;
        }

        /// <summary>
        /// Friction 2026-05-22 #64: resolve projection=minimal|standard|verbose to
        /// the field set the gateway should apply. Returns null for unknown levels
        /// or when the tool doesn't support projection (caller falls back to
        /// axiCompact defaults).
        /// </summary>
        ///   - minimal: name + kind/type + lastUpdate (3 fields, smallest legal shape)
        ///   - standard: GetDefaultCompactFields(toolName) — same as today's default
        ///   - verbose: returns null so no projection filter is applied → full payload
        internal static HashSet<string>? ResolveProjection(string toolName, string projection)
        {
            if (string.IsNullOrWhiteSpace(projection)) return null;
            string p = projection.Trim().ToLowerInvariant();
            if (p == "verbose")
            {
                // No filter at all — caller sees every field the worker emitted.
                return null;
            }
            if (p == "minimal")
            {
                // The smallest legal projection. Matches the schema description
                // exactly: {name, type, lastUpdate}. (Prior versions also whitelisted
                // 'kind' defensively but no worker emits it today — keeping the
                // field-set tight so 'minimal' is honest about its contract.)
                return new HashSet<string>(
                    new[] { "name", "type", "lastUpdate" },
                    StringComparer.OrdinalIgnoreCase);
            }
            if (p == "standard")
            {
                // Fall through to today's default. GetDefaultCompactFields is the
                // single source of truth — keeping projection=standard in lockstep.
                return GetDefaultCompactFields(toolName);
            }
            // Unknown projection level — treat like default (caller will fall back).
            return null;
        }

        private static string BuildIndexingMessage(string? status, double? progress, int? etaMs)
        {
            string s = status ?? "Cold";
            string phase = string.Equals(s, "Reindexing", StringComparison.OrdinalIgnoreCase) ? "Rebuilding index"
                : string.Equals(s, "UltraLiteReady", StringComparison.OrdinalIgnoreCase) ? "Walking KB (ultra-lite pass)"
                : string.Equals(s, "Cold", StringComparison.OrdinalIgnoreCase) ? "Building index from cold start"
                : "Building index";

            var parts = new System.Collections.Generic.List<string> { phase };
            if (progress.HasValue && progress.Value > 0 && progress.Value < 1)
            {
                parts.Add($"{(int)Math.Round(progress.Value * 100)}% complete");
            }
            if (etaMs.HasValue && etaMs.Value > 0)
            {
                int seconds = (int)Math.Ceiling(etaMs.Value / 1000.0);
                parts.Add(seconds <= 1 ? "~1s remaining" : $"~{seconds}s remaining");
            }
            return string.Join(", ", parts) + ".";
        }

        private static bool IsSqlGeneratingTool(string toolName, JObject? toolArgs)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            if (string.Equals(toolName, "genexus_db", StringComparison.OrdinalIgnoreCase))
            {
                string? action = toolArgs?["action"]?.ToString();
                return string.Equals(action, "sql_ddl", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "sql_navigation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_analyze", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_suggest", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(action, "optimize_report", StringComparison.OrdinalIgnoreCase);
            }
            // Legacy aliases — keep emitting the nudge for callers using the old names
            // until they drop out of LegacyToolAliases.
            return string.Equals(toolName, "genexus_sql", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolName, "genexus_db_optimize", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string>? GetDefaultCompactFields(string toolName)
        {
            if (string.Equals(toolName, "genexus_query", StringComparison.OrdinalIgnoreCase))
            {
                // v2.6.8: lastUpdate is part of the compact projection — same
                // rationale as list_objects (small, answers "what changed").
                return new HashSet<string>(new[] { "name", "type", "path", "lastUpdate" }, StringComparer.OrdinalIgnoreCase);
            }

            if (string.Equals(toolName, "genexus_list_objects", StringComparison.OrdinalIgnoreCase))
            {
                // v2.6.8: keep lastUpdate in the compact projection — it's the
                // signal that powers "what changed?" workflows and is cheap (~30b).
                // createdAt/lastModifiedBy stay verbose-only at the worker.
                return new HashSet<string>(new[] { "name", "type", "path", "parentPath", "lastUpdate" }, StringComparer.OrdinalIgnoreCase);
            }

            return null;
        }

        private static HashSet<string>? ParseRequestedFields(JObject? toolArgs)
        {
            if (toolArgs == null) return null;
            var token = toolArgs["fields"];
            if (token == null) return null;

            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Values<string>())
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        fields.Add(item.Trim());
                    }
                }
            }
            else
            {
                string raw = token.ToString();
                foreach (var piece in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string value = piece.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        fields.Add(value);
                    }
                }
            }

            return fields.Count == 0 ? null : fields;
        }

        private static int? TryReadInt(JToken? token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            if (token.Type == JTokenType.Float) return (int)Math.Floor(token.Value<double>());
            if (token.Type == JTokenType.String &&
                int.TryParse(token.Value<string>(), out int parsed))
            {
                return parsed;
            }

            return null;
        }

    }
}
