# genexus_introspect — reconocimiento previo de la KB

## Contexto

Un agente que ataca una KB de ~14.900 objetos sin reconocimiento previo satura su contexto antes de saber qué información importa. El pedido: **visión periférica antes que la lupa** — `introspect → map → select → inspect → deep dive` — con tres niveles de profundidad y un **KB Brief** persistido.

Durante el diseño se midió el índice real de `MatikaErp_3003` y apareció algo que reencuadra todo: **la KB no se puede mapear hoy porque el índice no tiene la jerarquía.** El trabajo deja de ser "agregar una vista" y pasa a ser "dejar de fabricar datos, y recién después mapear".

Decisiones tomadas: local en el fork · los tres niveles + Brief · rebase + `--force-with-lease` · pruebas sobre `MatikaErp_3003`.

---

## Paso 0 — Sincronizar (BLOQUEANTE)

El repo local está **9 commits atrás** (v2.41.1/2/3 + dos fixes de David Agostini). Tocan justo los archivos de este diseño: `Program.ToolPayload.cs` (+92/−54), `SearchService.cs` (+71/−48), `OperationsRouter.cs`, `ListService.cs`, `CommandDispatcher.cs`.

```bash
git fetch upstream --prune
git rebase upstream/main          # conflicto ESPERADO solo en CHANGELOG.md
git push --force-with-lease origin feat/sdk-coverage-map
```

`--force-with-lease`, nunca `--force` pelado: `feat/sdk-coverage-map` ya está publicada (`origin` = `13ffa62` = HEAD, nada en riesgo). Sin colisión en docs: upstream tocó `sdk_coverage_gap_matrix.md` / `sdk_uncovered_endpoints_2026-07-20.md`; la rama toca `sdk_binary_identity.md` + `scripts/sdk_reflection/*.ps1`.

Después del rebase, re-verificar cuatro puntos (no re-explorar): `enrichmentPending` en `SearchService` · `IsMutatingTool` + `GetDefaultCompactFields` en `Program.ToolPayload.cs` · router cases de `kb_readme`/`orient` · `ComputeAggregates`.

---

## El hallazgo que define el diseño

Medición directa del índice persistido (`%LOCALAPPDATA%\GxMcp\Cache\index_7DF6A56C111236AB.json_shards`, 16 shards, 14.932 objetos):

| Campo | Poblado | Origen |
|---|---|---|
| `Guid` / `Name` / `Type` | 100% | lite pass |
| `LastUpdate` | 99,9% | lite pass |
| `Description` | 97,7% | lite pass |
| `LastModifiedBy` | 6,8% | lite pass (ralo en origen) |
| `Embedding` (= enriquecido) | **0,21%** (31 obj) | enriquecimiento |
| `CalledBy` | **0,47%** (70 obj) | enriquecimiento |
| `Calls` | **0,00%** (0 obj) | enriquecimiento |
| `Module` / `ParentPath` | **0,007%** (1 obj) | enriquecimiento |
| `ParentFolderPath` | 100% — **1 solo valor: `"Root Module"`** | sintetizado |

El lite pass (`KbService.cs:634-645`) escribe siete campos; la jerarquía **no** está entre ellos. La escribe `IndexCacheService.UpdateEntry` (`:1541-1565`), que es la ruta SDK. `NormalizeLegacyHierarchy` (`:663-679`) rellena los nulos y `ComposeParentFolderPath("")` produce `"Root Module"` para todos.

**Tres consecuencias verificadas:**

1. **El árbol de carpetas del índice es fabricado.** 14.932 objetos dicen `"Root Module"`, pero existen **304 Folders y 90 Modules** (uno llamado `Payment`). `genexus_list_objects pathPrefix=` sirve esa fabricación hoy.
2. **`genexus_kb_readme` no es reutilizable tal cual.** Su tabla de módulos rinde una fila (`| Root Module | 1 |`), sus dependencias cross-módulo salen vacías (`Calls`=0) y su ranking de entidades ordena 223 Transactions por un `CalledBy` que es cero en casi todas.
3. **`genexus_doc action=health`**: `deadCodeCandidates` / `orphanedObjects` son ~99,5% falsos positivos en esta KB.

**Lo que sí es sólido y gratis:** censo por tipo completo (42 tipos), `Description` al 97,7%, recencia exacta (174 obj en 7d, 491 en 30d, 770 en 90d), los *nombres* de los 90 Modules y 304 Folders (son objetos), adopción de patrones como puro conteo de tipos (`WorkWithPlus` 461, plantillas 115, `WorkWithDevices` 6, `SDPanel` 34, `API` 6) y `MissingKBObject` = 105.

---

## Contrato de datos (se diseña primero)

### Bloque `coverage` — obligatorio, en el tope de `result`

No en `_meta`: ahí es una nota al pie. Acá **la cobertura ES el resultado**.

```jsonc
"coverage": {
  "objectsInScope": 14932, "indexStatus": "Ready",
  "enrichedInScope": 31, "enrichedPct": 0.2,
  "structureResolvedInScope": 1, "structureResolvedPct": 0.0,
  "fieldTrust": {
    "name":"complete", "type":"complete",
    "description":"observed:97.7", "lastModifiedBy":"observed:6.8",
    "module":"unavailable", "folderPath":"unavailable",
    "calls":"partial:0.0", "calledBy":"partial:0.5"
  },
  "doNotConclude": [
    "0 callers NO significa sin uso: 0,2% del scope está enriquecido.",
    "Esta KB NO es plana: todos reportan 'Root Module' porque el lite pass no resuelve la ubicación. Existen 90 Modules y 304 Folders."
  ]
}
```

**El vocabulario de `fieldTrust` es la clave de todo:**

| Valor | Significado | Qué significa la ausencia |
|---|---|---|
| `complete` | Lo escribe el pase que produjo el índice, para todo objeto | La ausencia es un hecho de la KB |
| `observed:<pct>` | Un pase barato lo intentó en todos; `<pct>` tenía valor | La ausencia es un hecho de la KB |
| `partial:<pct>` | Solo lo escribe el enriquecimiento | **Ausencia ≡ "no leído todavía". Nunca razonar sobre ella** |
| `unavailable` | Nadie lo tiene, o el valor presente es sintetizado | No se emite ninguna sección que dependa de él |

`observed` vs `partial` es la distinción central: `LastModifiedBy` al 6,8% y `CalledBy` al 0,5% parecen estadísticamente parecidos y son **epistémicamente opuestos** — 6,8% es la verdad sobre la KB; 0,5% es la verdad sobre nuestro índice.

### Supresión, no ceros

Toda sección cuyos insumos sean `unavailable` o `partial` bajo el piso (60%) **se omite del payload** y se nombra en `suppressed[]` con su desbloqueo:

```jsonc
"suppressed": [
  { "section":"modules", "reason":"moduleMembershipUnresolved",
    "detail":"1 de 14932 entradas tienen Module resuelto. Los 90 Modules van en containerInventory (solo nombres).",
    "unlock":{ "tool":"genexus_introspect", "args":{"depth":"map","scope":"Payment","scopeKind":"module","resolve":true} } }
]
```

Un `modules: {}` vacío invita a concluir "no hay módulos". Una supresión nombrada no se puede malinterpretar. **Regla: nunca emitir un campo cuyo valor vacío sea mentira.**

### Política por `depth`

| depth | SDK | Si la cobertura no alcanza |
|---|---|---|
| `overview` | **cero, siempre** | Nunca falla. Responde lo `complete`/`observed`, suprime el resto. <150 ms |
| `map` | cero salvo `resolve:true` | **Se niega a dibujar el árbol.** `status:"partial"`, `ScopeStructureUnresolved` + subconjunto honesto + desbloqueo |
| `deep` | cero salvo `resolve:true` | Igual, con resolución acotada bajo `resolve:true` |

**Degradar-con-warning se rechaza para `map`/`deep`.** Un árbol advertido pero dibujado igual se lee como árbol, y la próxima llamada del agente va al lugar equivocado. `overview` sí degrada porque nunca hace una afirmación relacional.

### `resolve:true` — dos pases acotados

- **Pase A (estructura):** `ResolveHierarchy` (`IndexCacheService.cs:822`) camina la cadena `obj.Parent` por COM; **no** hace `Objects.Get(guid)` ni carga partes → mucho más barato que enriquecer. Es lo que hace posible `map`.
- **Pase B (aristas):** `EnrichmentQueue.PromoteAsync` por objeto (apertura SDK completa). Solo `deep`, solo top-N.

Ambos con tope de objetos **y** de tiempo, reportando completitud parcial como resultado de primera clase (`stoppedBy: objectCap|timeBudget|complete`, `remaining`, `resumeCursor`). Escribe vía `AddOrUpdateBatch` → persiste en los shards, la próxima sesión lo hereda.

### Resolución de scope

Escalera determinista que **siempre reporta qué regla ganó**: `type` → `module` → `folder` → `path` (**se saltea si `structureResolvedPct == 0`**, si no devuelve la KB entera) → `prefix` → `domain`. Varias reglas matchean → `ScopeAmbiguous`, no se elige. Ninguna → `ScopeNoMatch` + cohortes de prefijo reales.

Ejemplo medido: `scope="Payments"` → **`ScopeNoMatch`** (el módulo se llama `Payment`, singular) + `didYouMean:["Payment"]`. Esa es la respuesta honesta, y es más útil que un árbol vacío con cara de certeza.

---

## Lo que NO se entrega (declarado, no omitido en silencio)

| Concepto | Veredicto v1 | Por qué |
|---|---|---|
| conteos, censo por tipo, recencia | **Va** | `TypeIndex` O(1); `LastUpdate` 99,9% |
| **módulos** | **Solo nombres**; membresía suprimida | 1/14.932 resuelto |
| **generator / runtime** | `generator: null` etiquetado; se envía versión GX + dialecto DB | No hay accesor en el worker para `TargetModel`. Requiere spike de SDK |
| **map: árbol / relaciones** | **Se niega sin `resolve:true`** | `ParentFolderPath` tiene 1 valor distinto; `Calls`=0 |
| **deep: "patrones dominantes"** | Redefinido a **conteos de adopción** por tipo | No existe detección de idiomas arquitectónicos. Más que contar es invención |
| **deep: "convenciones conocidas"** | **No va.** Objeto explícito `notDetected` + `wouldRequire` | No existe detector alguno. (`ToolHelpCatalog.cs:104-123` documenta un `analyze mode=naming` **inexistente** — docs obsoletas) |
| **"arquitectura"** | **No va** | No es derivable de ningún campo presente |
| **hotspots** | Solo `MissingKBObject` (105) | Complejidad/huérfanos son `partial:0.2%` |

Nomenclatura es el único triunfo barato de v2: `Name`+`Type` al 100% → un pase de clustering por regex cuesta ~50 ms sin SDK. Debe salir como **regularidades observadas con soporte** ("prefijo `Proc*` en 1.204/1.788 Procedures, 67%"), nunca como "la convención es X".

---

## Forma de la tool

Tool nueva `genexus_introspect` (ambos diseños coinciden). No re-declarar `genexus_orient` — fue retirada por decisión de release (v2.7.0, "use `genexus_whoami`"); su *servicio* sirve, su *identidad de tool* no. No convertir `kb_readme` a JSON: es el generador de artefacto humano y debe seguir siéndolo.

**Presupuesto:** medido **19.954 tokens / tope 20.000 → 46 de headroom**. El bump es inevitable en cualquier variante. Subir a **20.400** con entrada en CHANGELOG registrando el valor medido, siguiendo la disciplina del bump log.

Schema mínimo (5 propiedades, sin enums verbosos); todo lo explicativo va al recurso `genexus://kb/tool-help/genexus_introspect` vía `ToolHelpCatalog` — **cero costo de schema**. Ahí vive también la política: *"nunca `deep` ni un `genexus_query` amplio sobre una KB desconocida antes de `introspect depth=overview`; leer `coverage.fieldTrust` antes de creer cualquier afirmación relacional; tratar la ausencia en `partial:*` como desconocido, nunca como cero."*

**Caché:** registrar en `isLiveTool` (`Program.RequestLoop.cs:1263-1271`), **NO** en `IsMutatingTool`. `IsMutatingTool` dispara un `_semanticCache.Clear()` **entero** — cada `overview` volaría todas las lecturas cacheadas de la sesión. El Brief escribe en `.gx/`, no cambia objetos de la KB. Nunca cacheada + no mutante, fijado por un caso en `SemanticCacheInvalidationTests`.

---

## KB Brief

`<kbPath>/.gx/brief/kb-brief.json` + `.md` renderizado. No dentro de `memory.jsonl`: es un documento reemplazable, no un journal, y meterlo ahí lo pondría en la ruta de auto-surfacing de `AttachRelevantMemory`.

Contenido = payload de `overview` + `generatedFrom {objectCount, highWaterMarkUtc, lastIndexedAt, workerDllSha256, schemaVersion}`.

**El Brief lleva su propio bloque `coverage`** — innegociable: es lo que impide que un brief generado sobre un índice al 0,2% se lea seis sessiones después como hecho consumado.

Generación **nunca automática**: `depth=overview write=true`. Obsolescencia calculada en cada lectura (drift de conteo >max(25, 0,5%) · HWM avanzado · >14 días · cambió `WorkerDllSha256`). Un brief obsoleto **se devuelve con sus razones**, no se suprime. Puntero de tres campos en `whoami` (~120 B).

---

## Pasos (incrementales, cada uno enviable)

**1. ✅ HECHO — Primitivas de cobertura** (commit `e014dca`). `CoverageSnapshot` + `IndexCacheService.GetCoverageSnapshot(scope)`: un paso O(n) en memoria, sin SDK, con el vocabulario `complete` / `observed:<pct>` / `partial:<pct>` / `unavailable`. La ubicación se cuenta desde `ParentPath` resuelto, nunca desde el `ParentFolderPath` sintetizado. 7 tests; suite del worker 1923/1927.

**2. ✅ HECHO — Medición** (commit `4e18439`). Cuatro corridas sobre `MatikaPayment_2702` (3.321 objetos), aislando el caché del SO con acumulador de ticks propio:

| Corrida | flag | `readMs` | `hierarchyMs` | condición |
|---|---|---|---|---|
| 1 | OFF | 15.976 | 0 | frío |
| 2 | ON | 11.044 | 5.749 | caliente |
| 3 | OFF | 4.939 | 0 | caliente |
| 4 | ON | 8.871 | 4.737 | caliente |

**~1,58 ms/objeto**, contra ~31 ms/objeto del enriquecimiento (20× más barato) y sin abrir un solo objeto. Verificado que además **funciona**: 32 Modules y 109 parent paths distintos poblados con **cero** objetos enriquecidos. Extrapolado a 14.932 objetos: ~24 s por reindex completo — *extrapolación lineal, a confirmar sobre la KB grande, porque un árbol más profundo hace más saltos por objeto*.

**3. ✅ HECHO — La ubicación se resuelve en el lite pass** (commit `4e18439`). `Indexing.LitePassResolvesHierarchy` por defecto **ON**, con kill-switch. **Esto elimina el viejo Paso 5 entero** (ver abajo) y arregla el árbol para *todas* las tools, no solo para introspect.

**4. `IntrospectService.Overview()` + wiring** — bump de budget (medido **19.994 / 20.000 → quedan 6 tokens**, el bump es obligatorio) + schema + router case + `["introspect"]` en `CommandDispatcher` + `isLiveTool` + golden fixture (entra entre `genexus_inspect` e `genexus_io`). Cero SDK. **Ya solo esto reemplaza a `genexus_orient` y entrega el grueso del ahorro de contexto.**

**5. Resolución de scope + `map`** — escalera completa (`type` → `module` → `folder` → `path` → `prefix` → `domain`), `ScopeAmbiguous` / `ScopeNoMatch` / `didYouMean`. Cero SDK. **Nota: con el paso 3 hecho, `scopeKind=path` ya no hay que saltearlo** — la ubicación es real. Y `map` puede dibujar el árbol de entrada en vez de negarse, siempre que la cobertura del scope lo respalde.

**6. KB Brief** — `write=true`, obsolescencia cuádruple, puntero en `whoami`, política en tool-help.

**7. `deep` + Pase B acotado** — secciones con `basedOn` propio. El pase de aristas (enriquecimiento, ~31 ms/objeto) sigue necesitando topes; es el único que los necesita.

**8. Retirar las fabricaciones** — poner el bloque de cobertura detrás de las secciones de `kb_readme`, gatear `deadCodeCandidates`/`orphanedObjects` de `doc action=health` por `enrichedPct`, retirar el router case de `genexus_orient`, arreglar los modos obsoletos de `ToolHelpCatalog.cs:104-123`. Confirmar aparte: `HealthService.cs:17` apunta a `AppDomain.BaseDirectory\cache\search_index.json`, que **no** es el shard store real.

> **~~Paso 5 original (`resolve:true` acotado)~~ — ELIMINADO.** Existía para resolver la ubicación bajo demanda con caps, budgets de tiempo, cursores de reanudación y la disyuntiva job síncrono/asíncrono. La medición del paso 2 mostró que resolverla de entrada en el lite pass cuesta ~24 s **una vez** por reindex. Toda esa maquinaria esquivaba un costo que conviene pagar.

---

## Entorno de pruebas — `genexus18_local` aislado

Gateway paralelo compilado del repo, sin tocar el que sirve la sesión. Puertos verificados: `5000` ocupado (gateway npx), `5016` (Gx16/Activas), `5040` (svchost) → **`5001` libre**, el que ya usa `AGENTS.md`.

```jsonc
// %LOCALAPPDATA%\GenexusMCP\config.local.json
{ "Server": {"HttpPort": 5001, "McpStdio": false},
  "Environment": {"KBPath": "D:\\Proyectos\\Matika\\ModelosGX\\MatikaErp_3003"},
  "GeneXus": {"InstallationPath":"C:\\Program Files (x86)\\GeneXus\\GeneXus18",
              "WorkerExecutable":"D:\\...\\src\\GxMcp.Worker\\bin\\Debug\\GxMcp.Worker.exe"} }
```

```bash
claude mcp add genexus18_local -s user \
  -e "GX_CONFIG_PATH=C:\Users\wigam\AppData\Local\GenexusMCP\config.local.json" \
  -- "D:\...\src\GxMcp.Gateway\bin\Debug\net8.0-windows\GxMcp.Gateway.exe"
```

El lease se indexa por `port|kb|program|shadow` → puerto distinto = instancia distinta, sin colisión. Iterar con `genexus_worker_reload` sobre HTTP. Matarlo al terminar (el hook `SessionStart` lo barre si queda huérfano).

---

## Verificación

**Unitarias (worker).** Tres fixtures de `SearchIndex` en memoria: *lite* (espeja la realidad medida: `Module`/`ParentPath` nulos, `ParentFolderPath="Root Module"` en todos), *enriquecido*, *mixto 60/40*.

1. **Anti-fabricación (la que importa):** en el fixture lite, `fieldTrust.module == "unavailable"` y `result["modules"] == null`; en el enriquecido, presente y `complete`.
2. `suppressed[]` contiene `modules`/`folderTree`/`callGraph` en lite, vacío en enriquecido.
3. Un solo valor distinto en `ParentFolderPath` ⇒ `structureResolvedPct==0` ⇒ árbol suprimido **y** `scopeKind=path` salteado.
4. `doNotConclude[]` no vacío siempre que se consuma un campo `partial`/`unavailable`.
5. Scope: `"Payments"`→`ScopeNoMatch`+`didYouMean:Payment`; `"Payment"`→`resolvedBy:"module"`; coincidencia doble→`ScopeAmbiguous` **sin** devolver resultados.
6. Topes: fixture de 800 objetos ⇒ ≤200 nodos, ≤40 filas de tipo, payload bajo el techo de cada depth.
7. `resolve` acotado: resolver falso que siempre corta ⇒ `stoppedBy:"objectCap"` + `remaining` + `resumeCursor`.
8. Obsolescencia del Brief: los cuatro disparadores por separado; 26 objetos de drift es obsoleto, 24 no.

**Gateway.** Bump con valor medido en el comentario · golden fixture regenerado y ordenado · `SemanticCacheInvalidationTests` (nunca cacheada **y** no mutante) · test de techo que asegure que la respuesta jamás entra en la ruta de truncado de 60.000 chars (si el gateway tiene que truncar un introspect, los topes del servicio fallaron).

**En vivo sobre `MatikaErp_3003` (HTTP `127.0.0.1:5001/mcp`).** Handshake `initialize`, reusar `MCP-Session-Id`, parsear `result.content[0].text` (JSON dentro de JSON).

- **Frío** (shards renombrados aparte, abrir KB, llamar ya): debe devolver `status:"partial"` y **no** presentar un censo que se lea como completo — `censusInProgress:true`. *Este test caza el bug de reportar un índice a medio construir como si fuera la KB.*
- **Tibio** (14.932 objetos, 31 enriquecidos): `byType` suma exacto al total con 42 tipos · `enrichedPct≈0.2` · `structureResolvedPct≈0.0` · `modules`/`folderTree`/`callGraph`/`hotspots` **ausentes** y los cuatro nombrados en `suppressed[]` · `containerInventory.modules.count==90`, `.folders.count==304` · `patternAdoption.WorkWithPlus.instances==461` · `integrity.missingKbObjects==105` · payload <8 KB · latencia <400 ms.
- **`resolve:true`** sobre `scope="Payment" scopeKind=module`: `stoppedBy` en uno de los tres valores legales, `structureResolvedPct` sube, y una **segunda** llamada con `resolve:false` ya devuelve el árbol (prueba que la resolución persistió en los shards).

Frío y tibio prueban cosas distintas: frío, que nunca presenta un índice en vuelo como censo terminado; tibio, que nunca presenta un índice perezoso como grafo completo. Probar solo uno deja pasar un bug.

---

## Decisiones abiertas

1. ~~**¿síncrono con tope menor o job asíncrono?**~~ → **Sin objeto.** No hay operación bajo demanda que agendar: la ubicación se resuelve en el reindex.
2. ~~**¿El arreglo real es meter la resolución en el lite pass?**~~ → **Sí, medido.** ~1,58 ms/objeto, ~24 s para 14.932 objetos, cero objetos abiertos. Hecho en el commit `4e18439`.
3. **Piso de supresión**: propuesto 60% para campos `partial`. Sigue abierto — pero ahora aplica sobre todo a las **aristas** (`Calls`/`CalledBy`), porque la ubicación dejó de ser un campo `partial`.
4. **NUEVO — ¿reindexar `MatikaErp_3003`?** El índice existente sigue teniendo la ubicación fabricada; el arreglo aplica al reconstruirlo (`genexus_lifecycle action=index force=true`). Confirma de paso la extrapolación de los ~24 s sobre una KB 4,5× más grande. Costo: el índice en obras y las escrituras bloqueadas mientras dura.
