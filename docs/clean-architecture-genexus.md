# Clean Code & SOLID adaptado a GeneXus 18 — estándar del equipo

> Este documento es **normativo**: define cómo se escribe código GeneXus en este equipo.
> Se distribuye con el servidor MCP como recurso `genexus://kb/skills/clean-architecture`,
> de modo que lo reciben tanto desarrolladores como agentes de IA conectados a la KB.

## 0. Cómo leer este documento

- **MUST** (obligatorio): incumplirlo bloquea la tarea o exige justificación escrita.
- **SHOULD** (recomendado): apartarse requiere una razón, no un permiso.
- **✅ verificado**: confirmado contra la documentación oficial de GeneXus
  (wiki en docs.genexus.com) o medido contra una KB real.
- **⚠️ a verificar**: no se pudo confirmar; NO usarlo como hecho sin probarlo antes.

**La premisa honesta que gobierna todo:** GeneXus **no es un lenguaje orientado a objetos**.
No hay clases, ni herencia, ni interfaces, ni polimorfismo de subtipos. Los
principios de Uncle Bob (SOLID, Clean Code) nacieron en POO; este estándar **traduce lo
que se puede traducir y declara sin rodeos lo que no aplica**. Un estándar que finge que
GeneXus tiene interfaces produce cargo cult, no calidad.

Los objetos con los que se razona aquí: **Transaction, Procedure, Web Panel, Data
Provider, Data Selector, SDT, Domain, Business Component** (docs.genexus.com, categoría
Objects).

---

## 1. KISS · DRY · YAGNI — con ejemplos de KB, no de libro

### KISS — lo más simple que funcione

**Regla práctica de extracción (MUST):** se extrae lógica a un Procedure cuando
(a) la llaman **dos o más** objetos, o (b) supera el límite de líneas de su rol (§2-S).
Se deja inline cuando tiene **un** solo uso y menos de ~10 líneas. Extraer un procedure
para tres líneas usadas una vez no es Clean Code: es indirección gratuita.

### DRY — los tres duplicados típicos de una KB

1. **La misma navegación copiada en N procedures.** El mismo `For Each` con el mismo
   `Where` en seis objetos = seis lugares que actualizar cuando cambie el filtro.
   → Un **Data Selector** (el mecanismo nativo para reutilizar condiciones de
   navegación, docs.genexus.com: Data Selector object) o un procedure de consulta único.
2. **La misma validación en las Rules de la Transaction Y en los eventos del WebPanel.**
   Divergen en la primera modificación y el sistema queda validando dos cosas distintas.
   → La regla vive en la **Transaction** (o en el Business Component, que ejecuta esas
   mismas rules); la UI **delega**, no re-valida.
3. **Constantes mágicas repetidas** (`&Estado = 3`, `if &Tipo = 'FA'`).
   → **Dominios enumerados** (docs.genexus.com: Enumerated Domains): el valor tiene
   nombre, el compilador vigila los usos, y cambiar el código de negocio es un solo edit.

### YAGNI — no se construye "por si acaso"

- Parámetros que ningún llamador envía todavía.
- Campos de SDT que nadie llena.
- Subrutinas "genéricas" sin segundo llamador.

Si aparece la segunda necesidad, se refactoriza en ese momento — con el uso real como
especificación, que siempre es mejor que la imaginada.

---

## 2. SOLID traducido honestamente

| Letra | ¿Aplica en GeneXus? | Traducción |
|---|---|---|
| **S** | **Sí — es la columna vertebral** | Un objeto = una responsabilidad, con límites de tamaño medibles |
| **O** | Parcialmente | Extensión por parametrización, no por herencia |
| **L** | **Limitada — declarado sin rodeos** | Contratos `parm()` intercambiables; Subtypes para atributos |
| **I** | Sí, como diseño de contratos | Contra el SDT gordo y el `parm()` de 8 argumentos |
| **D** | Sí, como dirección de dependencias | UI → caso de uso → infraestructura, nunca al revés |

### S — Single Responsibility (MUST)

Si describir el objeto exige la palabra "y", son dos objetos. La vara es medible —
**límites de líneas útiles** (línea útil = no vacía después de quitar comentarios;
definición ejecutable en el linter, regla **GX014**):

| Rol del objeto | Límite (líneas útiles) |
|---|---|
| Web Panel (eventos totales) | 150 |
| Procedure principal / orquestador | **80** ← lo vigila el linter (GX014) |
| Procedure de validación | 60 |
| Procedure de guardado | 50 |
| Procedure de interoperabilidad (API externa) | 30 |
| Subrutina dentro de un WebPanel | 10 |

**Firma canónica de todo Procedure de negocio (MUST):**

```genexus
parm( in:&EntidadSDT, out:&Resultado, out:&Mensajes );
```

Entra el dato, sale un resultado y salen los mensajes. El comando `parm` define el
contrato explícito de entradas y salidas (docs.genexus.com: Parm rule). Un procedure
sin `parm` es un objeto sin contrato.

### O — Open/Closed, sin herencia

"Abierto a extensión, cerrado a modificación" se logra en GeneXus cuando **el caso nuevo
no toca el código existente**:

- **Dominios enumerados + `Do Case`** en un solo punto de despacho — el caso nuevo agrega
  una rama, no reescribe el flujo.
- **Tablas de configuración**: el comportamiento parametrizable (montos límite, endpoints,
  flags de producto) vive en datos, no en el Source. Agregar un producto = un INSERT.
- **Data Providers** como puntos de variación de datos: el consumidor no cambia cuando
  cambia la fuente.

El anti-patrón: un orquestador que crece un `if` por cada caso nuevo. Si cada requerimiento
modifica el mismo procedure, ese procedure está **cerrado a extensión** — exactamente lo
contrario de la O.

### L — Liskov, declarada con honestidad

**La L clásica NO aplica**: no hay subtipado de clases ni sustitución polimórfica. Lo que
sí existe y se exige:

- **Procedures intercambiables ⇔ contrato `parm()` idéntico** en orden, tipos **y
  semántica** de in/out. Un "reemplazo" que exige más precondiciones que el original
  (falla donde el original funcionaba) viola el espíritu de la L aunque compile.
- **Subtypes** de GeneXus (docs.genexus.com: Subtype Group) para atributos que comparten
  dominio pero tienen roles distintos (ClienteId origen / ClienteId destino en una
  transferencia) — es sustitución a nivel de atributo, lo más cercano que el paradigma ofrece.

### I — Interface Segregation, como diseño de contratos

Nadie debe depender de datos que no usa:

- **El SDT gordo**: el llamador recibe 40 campos y usa 3 → cada cambio del SDT lo arrastra.
  → SDTs **por caso de uso**, no un SDT universal por entidad.
- **El `parm()` de 8 argumentos** donde cada llamador llena 2 y manda 6 vacíos
  → dividir el procedure por consumidor.

### D — Dependency Inversion, como dirección de dependencias (MUST)

```
Web Panel (UI)  →  Procedure de caso de uso  →  Procedure de infraestructura
                                             →  Configuración (tabla clave-valor)
```

- La **UI no navega tablas** — delega en procedures/Data Providers (lo vigila el linter,
  regla **GX012**). Un WebPanel es una pantalla, no una consulta.
- El **caso de uso no conoce detalles**: endpoints, credenciales, rutas y hosts viven en
  configuración externalizada, nunca hardcodeados.
- Los procedures de **interop son adaptadores finos** (≤30 líneas): traducen, no deciden.

---

## 3. ACID e integridad transaccional

En banca esto no es teoría: **Atomicidad** es que no quede un débito sin su crédito;
**Consistencia** es que el asiento cuadre; **Aislamiento** es que dos procesos
concurrentes no se pisen; **Durabilidad** es que lo confirmado sobreviva a una caída.

La unidad de trabajo en GeneXus es la **LUW (Logical Unit of Work)**: un conjunto de
operaciones que se ejecutan **todas o ninguna** (docs.genexus.com: Logical Unit of Work).

### 3.1 Tabla ACID → GeneXus 18 (verificada)

| Elemento | Comportamiento | Estado |
|---|---|---|
| Comando `Commit` | Cierra la LUW y **libera los bloqueos**. Válido en Procedure y Web Panel (docs.genexus.com: Commit command) | ✅ |
| Comando `Rollback` | Deshace la LUW. Válido en Procedure y Web Panel, **NO en Transactions**. Ignorado sin integridad transaccional | ✅ |
| Commit implícito | El alcance por defecto de la LUW es **el programa completo**: GeneXus genera un `Commit` al final del Source del Procedure | ✅ |
| Propiedad `Commit on Exit` | En `Yes`, cada objeto que actualiza la base confirma al salir | ✅ |
| Transactions | Confirman automáticamente al terminar cada instancia — por eso `Rollback` no aplica ahí | ✅ |
| Business Components | `.Save()` / `.Insert()` / `.Update()` **NO confirman por sí solos** — evaluar el resultado y decidir | ✅ |
| Propiedad `Isolation Level` | Se define en el **Data Store**. Valores: Read Committed (default), Repeatable Read, Serializable, Read Uncommitted. **No existe valor SNAPSHOT** — el versionado de filas se configura en el motor de BD, no en la KB | ✅ |
| Lecturas | Los `For Each` de solo lectura no bloquean; un `For Each` que actualiza bloquea hasta el `Commit`/`Rollback` | ✅ |
| Propiedad `Execute in new LUW` | La redacción de la documentación es ambigua sobre su efecto exacto — **probarlo antes de apoyar un diseño en ella** | ⚠️ |

### 3.2 El batch largo: un solo dueño del Commit (MUST)

El commit implícito al final del Procedure convierte un proceso de 200.000 registros en
**una sola LUW gigante**: el log de transacciones crece sin freno, los bloqueos se
acumulan durante horas, y si el proceso muere en el registro 199.000 se pierde todo.
El error espejo es igual de grave: `Commit on Exit = Yes` en cada procedure auxiliar
rompe la atomicidad del conjunto — cuando falla el paso 4 ya no hay forma de deshacer
el paso 2, y la plata quedó debitada sin acreditar.

**La forma correcta:**

- `Commit on Exit = No` en **todos** los procedures que participan de la unidad de trabajo.
- **Un solo dueño de la decisión**: el orquestador es el único que ejecuta `Commit` o
  `Rollback`. Lo vigila el linter: `Commit` dentro de un `For Each` es **GX001 (Critical)**.

```genexus
// PedidoOrquestar — Main = No, Commit on Exit = No
parm( in:&PedidoId, out:&Ok, out:&Mensajes );

PedidoValidar(&PedidoId, &Ok, &Mensajes)
if not &Ok
   Rollback
   return
endif

PedidoStockReservar(&PedidoId, &Ok, &Mensajes)
if not &Ok
   Rollback
   return
endif

PedidoContabGenerarAsiento(&PedidoId, &Ok, &Mensajes)
if not &Ok
   Rollback
   return
endif

Commit   // el ÚNICO Commit de toda la unidad de trabajo
```

En procesos masivos, confirmar **por lotes** (p. ej. cada 1.000 registros) con contador
explícito — nunca por registro (bloqueos y log por las nubes) ni una sola vez al final
(todo o nada de cuatro horas).

---

## 4. Reglas de composición

### 4.1 Subrutina o Procedure

| Característica | Subrutina (`Sub`/`Do`) | Procedure |
|---|---|---|
| Parámetros | No acepta | `parm()` explícito (in/out/inout) |
| Alcance | Local al objeto | Objeto independiente, reutilizable |
| Comunicación | Variables del objeto (implícita) | Contrato explícito |
| Costo | Método de la misma clase generada, sin overhead | Nueva instancia |

**Subrutina** para lógica específica del objeto actual (inicializar pantalla, validación
interna del formulario). **Procedure** para lógica que usan dos o más objetos — la
comunicación por variables compartidas de las subrutinas es acoplamiento invisible, y a
partir del segundo consumidor se paga caro.

**Prohibido `return` dentro de una subrutina (MUST):** `return` no sale de la subrutina —
**sale del objeto entero**, saltándose todo lo que faltaba ejecutar. Usar variables de
control para el flujo.

### 4.2 Demeter · Tell, Don't Ask · CQS

- **Ley de Demeter**: `&SdtPedido.Cliente.Direccion.Ciudad.Nombre` regado por el Source
  acopla el objeto a toda la cadena — cualquier cambio estructural del SDT rompe N
  objetos. La navegación profunda se encapsula en un procedure que devuelve lo que se
  necesita.
- **Tell, Don't Ask**: no interrogar al dato para decidir afuera lo que el dueño de la
  lógica puede decidir adentro. En vez de leer 5 campos del SDT para calcular afuera,
  pedirle el resultado a un procedure de la entidad.
- **Command-Query Separation**: un procedure **consulta** (devuelve datos, no comitea,
  no escribe) o **comanda** (cambia estado y devuelve `&Resultado`/`&Mensajes`) — nunca
  las dos cosas. Un `PObtenerSaldo` que además graba auditoría tiene un nombre que miente.

---

## 5. Catálogo de code smells — la misma historia que el linter

El linter del MCP (`genexus_analyze mode=linter`) hace cumplir este estándar. La política
de severidades es **operativa, no decorativa**:

| Severidad | Efecto |
|---|---|
| **Critical / Error** | **Bloquea.** No se da por terminada una tarea con uno presente |
| **Warning** | Exige **justificación escrita** junto al hallazgo: `// GXnnn-justified: <razón>` |
| **Info** | Registro; no frena |

| Smell | Por qué es un problema | Regla |
|---|---|---|
| `Commit` dentro de un loop | Rompe el cursor y la atomicidad del lote | **GX001** Critical |
| `For Each` sin filtro | Full table scan | **GX002** Critical |
| Full scan confirmado por navegación | Confirmado contra el plan real | **GX013** Error |
| `For Each` en eventos de UI | La pantalla acoplada al modelo de datos; se copia a la siguiente pantalla | **GX012** Warning |
| Procedure de más de 80 líneas útiles | Hace más de un trabajo (viola la S) | **GX014** Warning |
| Bloque de código comentado muerto (>10 líneas) | El control de versiones lo recuerda; el Source no es un cementerio | **GX015** Warning |
| `For Each` anidado (posible N+1) | A menudo colapsable a una navegación o Data Selector | **GX010** Warning |
| Variable sin uso | Ruido que esconde lo importante | **GX008** Warning (auto-fix) |
| SDT gordo / `parm()` kilométrico | Dependencia de datos que no se usan (viola la I) | revisión manual |
| Primitive obsession (códigos mágicos) | Para eso existen los **Domains** | revisión manual |
| Lógica de negocio en eventos de UI | No es testeable ni reutilizable | revisión manual |

Notas del linter:
- GX012 se **suprime automáticamente** en objetos con patrón WorkWithPlus aplicado — el
  patrón prescribe acceso directo en su Event Start generado, y marcar al generador es ruido.
- GX015 **excluye las regiones generadas** por WorkWithPlus (marcadores
  `/* Generated by DVelop Work With Plus Pattern [Start] - Do not change */` … `[End]`):
  ese código se regenera con el pattern y no es del desarrollador para borrarlo.

---

## 6. TDD con realismo GeneXus

Las tres leyes de Uncle Bob (no escribir código sin un test que falle; no escribir más
test que el necesario para fallar; no escribir más código que el necesario para pasar)
se aplican donde el tooling lo permite:

- GeneXus 18 tiene el **Unit Test object** (docs.genexus.com: Unit Testing): clic derecho
  → Create Unit Test sobre **Procedures, Data Providers y Business Components**.
- **GXtest** agrega ejecución en CI y Database Mocking.
- **Dónde paga TDD**: procedures de cálculo y validación (entradas/salidas puras — el
  caso ideal), reglas de negocio de BCs.
- **Dónde no llega**: eventos de UI y layouts — ahí la protección es la separación de
  responsabilidades (§2-D): cuanto menos lógica tenga la pantalla, menos importa que la
  pantalla no sea testeable.

Mínimo exigible (SHOULD): todo procedure de negocio nuevo con firma canónica lleva su
Unit Test de camino feliz + un caso de error.

---

## 7. Nomenclatura — tabla canónica

Fuente única para KBs propias. (Para KBs **Bantotal** rige la nomenclatura del estándar
CAP-99000 — `BT-NOM-*` en la skill `bantotal-estandares` — que tiene precedencia en ese
contexto.)

| Tipo | Convención | Ejemplo |
|---|---|---|
| Transaction | Sustantivo de la entidad | `Cliente`, `Producto` |
| Procedure de negocio | Entidad + Verbo | `ClienteRegistrar`, `ProductoActualizar` |
| Procedure de validación | Entidad + `Validar` | `ClienteValidar`, `EmailValidar` |
| Web Panel | Entidad + Función | `ClienteIngreso`, `ProductoLista` |
| SDT | Entidad + sufijo `SDT` | `ClienteSDT`, `PedidoSDT` |
| Variable | `&` + CamelCase descriptivo | `&ClienteId`, `&NombreCliente` |
| Subrutina | Verbo + objeto, descriptivo | `Sub 'ValidarDatosCliente'` |

- **CamelCase** en todos los nombres · **español** · **descriptivos** · **sin
  abreviaciones confusas** (`&ClienteNom` ❌, `&temp` ❌, `&x` ❌).

---

## 8. Definition of Done — checklist bloqueante

Una tarea GeneXus **no está terminada** hasta que:

1. ☐ `genexus_analyze mode=linter` corrido sobre **cada objeto tocado**: cero
   Critical/Error, cero Warning sin su `// GXnnn-justified:`.
2. ☐ Límites de líneas útiles respetados (tabla §2-S).
3. ☐ Todo procedure nuevo con la firma canónica `parm(in:…, out:&Resultado, out:&Mensajes)`.
4. ☐ Un solo dueño del `Commit` por unidad de trabajo; participantes con `Commit on Exit = No`.
5. ☐ Cero código muerto comentado — lo borrado vive en el control de versiones.
6. ☐ Sin hardcodes de endpoints, credenciales, rutas ni códigos mágicos.
7. ☐ Procedure de negocio nuevo → Unit Test de camino feliz + un caso de error.

Este checklist aplica igual a desarrolladores humanos y a agentes de IA que operan la KB
a través de este MCP.

---

## 9. Fuentes

- Documentación oficial GeneXus — https://docs.genexus.com/en/genexus — artículos:
  Commit command · Rollback command · Logical Unit of Work · Commit on Exit property ·
  Isolation Level property · Parm rule · Data Selector object · Enumerated Domains ·
  Subtype Group · Unit Testing · Business Component.
- Comportamientos marcados ✅ verificados contra el RAG local de esa documentación
  (MCP `genexus-docs`) el 2026-08-15; los marcados ⚠️ quedaron explícitamente sin confirmar.
- Límites de tamaño y firma canónica: pautas internas del equipo (repositorio
  `pautas-genexus`, patrón profesional de WebPanels y guía de subrutinas).
- Marcadores de código generado de WorkWithPlus: verificados en vivo contra una KB real
  (18 pares `[Start]`/`[End]` en un solo WebPanel).
