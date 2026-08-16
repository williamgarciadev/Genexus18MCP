using System.Collections.Generic;

namespace GxMcp.Gateway
{
    /// <summary>
    /// v2.8.0 — curated, source-verified GeneXus development reference
    /// material exposed as MCP `resources/` content. Each skill below was
    /// fact-checked against docs.genexus.com (linked at the bottom of each
    /// entry). When the wiki didn't confirm a fact, it was omitted rather
    /// than guessed — the goal is to be a TRUSTABLE reference for LLM
    /// clients that lack reliable GeneXus training data.
    ///
    /// Exposed via:
    ///   - `resources/list` advertises every entry below.
    ///   - `resources/read` with uri="genexus://kb/skills/<key>" returns body.
    /// </summary>
    internal static class SkillCatalog
    {
        public sealed class Entry
        {
            public string Key { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public string Body { get; set; }
        }

        public static readonly IReadOnlyList<Entry> All = new List<Entry>
        {
            new Entry
            {
                Key = "navigation",
                Title = "GeneXus navigation methods (Call, CallOptions, ReplaceMainPanel)",
                Description = "How to navigate between Web Panels / SD Panels. Includes the Call method, CallOptions.Target enum, and the **CallProtocol** property pitfall.",
                Body = @"# GeneXus navigation methods — verified reference

LLMs frequently hallucinate properties here. Read this before claiming an enum value or property exists.

## `Call` method — universal invocation

```
ObjectName.Call([parm1, ..., parN])
```

- Applies to: **Procedure**, **Transaction**, **Web Panel**, **Panel** (SD).
- Shorthand: `ObjectName(parm1, ..., parN)` is exactly equivalent to `ObjectName.Call(...)`.
- Parameters are optional; the called object declares them via the **Parm rule**.
- **Caller context placement:**
  - Procedures: in the **Source** section.
  - Transactions: in the **Rules** or **Events** section.
  - Web Panels: in event handlers.
  - **Smart Devices: the call MUST be in the Native Mobile Events section.**

## `CallOptions.Target` — modal / popup / replace navigation (SD only)

For Smart Device apps that use **Slide** or **Split** navigation styles, control where the called panel appears:

```
<ObjectName>.CallOptions.Target = <TargetName>
<ObjectName>()
```

Valid values for `<TargetName>` (verbatim strings):

| Value     | Behaviour |
|-----------|-----------|
| `""Left""`    | Panel appears in the left menu area (Slide nav) or left pane (Split nav) |
| `""Content""` | Panel appears in the central screen region (Slide) or right pane (Split) |
| `""Blank""`   | Exits Slide/Split navigation; useful for full-screen panels (login, settings) |

`Target[1]` and `Target[2]` are generic index aliases for the same positions.

> CallOptions only have effect when the application's navigation style is Slide or Split. Default style ignores them and uses standard push.

## Common LLM pitfall — **`CallProtocol` does NOT do what you think**

`CallProtocol` is a **design-time property**, NOT a runtime navigation option. It only applies to:

- **Procedure**
- **Data Provider**
- **Transaction**

It does **NOT** apply to **Web Panel** or **Panel** (SD). Setting `CallProtocol = ""Modal""` does not exist — Modal is **not** a valid value.

Valid `CallProtocol` values (verbatim):

| Value                 | Meaning |
|-----------------------|---------|
| `Internal`            | Default. Usual call type for objects inside the KB. |
| `Command Line`        | Object can only be invoked from command line. |
| `HTTP`                | Object is callable via HTTP, can return data through `HttpResponse` data type. |
| `SOAP`                | Particular HTTP case, callable via SOAP protocol. |
| `Enterprise Java Bean` | Java generator only — object exposed as a stateless session bean / MDB. |

If you want modal/popup behaviour in a panel call, use **`CallOptions.Target`** (SD) or the equivalent web mechanism — NOT `CallProtocol`.

## `Application.ReplaceMainPanel` (Smart Devices)

Used to replace the currently displayed main panel of a running SD application — typically after login or to switch top-level navigation context. The exact signature is install-specific; consult the live `genexus_api` tool against your KB or the Smart Devices API resource to confirm parameters.

> Source: docs.genexus.com — Call method (16224), Call protocol property (7947), CallOptions Target for Smart Devices (32404)."
            },

            new Entry
            {
                Key = "gam-integrated-security",
                Title = "GAM (GeneXus Access Manager) — Integrated Security",
                Description = "How to enable GAM, the **Integrated Security Level** property, where it applies, and how Authorization vs Authentication differ.",
                Body = @"# GAM (GeneXus Access Manager) — verified reference

## Enabling GAM

The canonical property is **`Integrated Security Level`** (NOT `Enable Integrated Security`, NOT `IntegratedSecurity`).

### Where it's set

- **Version level** (Knowledge Base active version): sets the default applied to every object.
- **Object level**: overrides the version default for a specific object.

### Accepted values (verbatim)

| Value              | Behaviour |
|--------------------|-----------|
| `Authorization`    | Security is enforced. Object security checks run automatically at startup. Both **Authentication AND Authorization** are checked. |
| `Authentication`   | Security is enforced. Object security checks run automatically at startup. **Only Authentication** is checked. **This is the default at Version level.** |
| `None`             | Security is not enforced. |

### Where it applies

Object types that honour Integrated Security Level:

- Procedure
- Data Provider
- Panel (SD)
- Work With
- Menu
- Web Panel
- Web Component
- Transaction
- Query
- Dashboard

### Permissions

When `Authorization` is set, permissions are automatically generated in the **GAM Database**. The runtime then checks Authentication AND Authorization via the GAM module.

**Important caveat (Data Providers):** permissions for Data Providers are only checked when the DP is invoked from a **Query Viewer** OR exposed as a **Web Service**. Internal DP calls do NOT trigger GAM checks.

## Smart Device login flow (high-level)

A standard GAM-secured Smart Device application follows this shape:

1. The **SD Panel marked as Main** is the entry point.
2. If GAM is enabled and the user isn't authenticated, GAM redirects to a login panel (often `SDLogin` or a custom panel).
3. After successful login, the application typically replaces the visible main panel using **`Application.ReplaceMainPanel`** (Smart Devices API) — this avoids leaving the login panel in the back stack.

> See the navigation skill (`genexus://kb/skills/navigation`) for `Application.ReplaceMainPanel` notes. The exact signature varies across GeneXus 18 builds — confirm via the live `genexus_api` tool when in doubt.

> Source: docs.genexus.com — Integrated Security Level property (15214), GeneXus Access Manager (19888)."
            },

            new Entry
            {
                Key = "sd-panel-mobile",
                Title = "Smart Device (SD) panel basics — Main property + navigation",
                Description = "What makes a Smart Device panel the application entry point, what the **Main program property** does, what objects can be Main.",
                Body = @"# Smart Device panel — verified reference

## ""Main"" — the entry point of a Native Mobile application

The canonical property name is **`Main program`** (boolean). LLMs often hallucinate `IsMain` — the actual property is just **`Main`** in the property grid (with `IsMain` sometimes appearing in the metadata XML, but the IDE-facing name is `Main program`).

### Which objects can be Main

| Object type | Can be Main? |
|-------------|--------------|
| Menu        | Yes |
| Panel       | Yes |
| Work With   | Yes |

### What setting Main=True changes

The object becomes the entry point of the Native Mobile application, and additional ""Main object properties"" become editable. These are properties that apply to the **whole application**, organised in groups:

**Core Application:**
- Application title
- Default Layout Orientation
- Analytics Provider
- Enable Logging

**Android-specific / Apple-specific:**
- Version codes and version names
- Package / Bundle identifiers
- Application icon and launch images
- API keys (Maps, Google Services)
- Application signing credentials

**Feature configuration:**
- Push Notifications
- Deep Link Base URL
- Google Cast Receiver Application ID
- Offline Database assignment
- Test Mode and Obfuscation

**Security & permissions:**
- WebView JavaScript / file execution settings
- iOS Purpose Strings (camera, location, contacts, …)
- App Transport Security settings

**Third-party integration:**
- Facebook credentials
- Twitter credentials
- WeChat Pay Application ID

### Multiple Main objects in the same KB

A KB can have **multiple Main objects** — each represents a distinct mobile application (typical pattern: an admin app and an end-user app from the same KB). The `Application Class` property groups them; the build pipeline emits one APK / IPA per Main.

## Where SD-specific code lives

- **Events**: Native Mobile Events section. This is where `ObjectName.Call(...)` invocations are written for Smart Devices (NOT in Procedure-style Source).
- **Rules**: Parm rule declarations.
- **Layout**: graphic designer (or XML if edited via the MCP).

> Source: docs.genexus.com — Native Mobile Main object properties (17817), Call method (16224)."
            },

            new Entry
            {
                Key = "webpanel-events",
                Title = "Web Panel events — Start / Refresh / Load order",
                Description = "Verified event order in a Web Panel: Start, Refresh, Load. What's accessible in each.",
                Body = @"# Web Panel events — verified reference

## Initial load (GET request)

The system events fire in this order:

1. **Start event** — runs first. Page-level initialisation.
2. **Refresh event** — runs once. **Database queries execute here.**
3. **Load event** — runs **once per record** retrieved (grid population).

## Refresh event

> The Refresh event is a system event that is followed by the Load event.

### Triggers

- The user presses the Refresh button in a Web Panel.
- The `Refresh` command is executed programmatically (Web AND Smart Devices).
- For every GET or POST request in Web environments.
- **NOT** triggered by client events in Smart Devices.

### Attribute access inside Refresh

- **Web**: no grid attributes available; no plain-part attributes available (they haven't loaded yet).
- **Smart Devices**: fixed-part attributes ARE accessible.

### Typical usage

Initialise variables that accumulate data across the multiple Load iterations — e.g. resetting a `&total` variable to zero before the records are pulled.

## Load event

- Fires **once per row** for grid-backed parts.
- Reads attributes from the row being processed.
- Don't call slow operations here (they multiply by N).

## Start event

- Fires before Refresh.
- Use for one-time setup that doesn't need a database query (defaults for input variables, etc.).

## Control-bound events — ordering + WWP userAction stub (issue #36.4)

### Edit the layout/PatternInstance BEFORE the Events part
Control-bound events and properties only validate once the control exists in the
projected form. Writing them first fails spec:
- `Event &Var.ControlValueChanged` / `Event &Var.Click` → `src0233 '…' is not a valid event`.
- `&Var.Display = 1` → `src0216 'Display' invalid property`.

**Order:** first add the control (`genexus_edit part=PatternInstance` on a WWP host,
or `part=WebForm`/layout otherwise), THEN add the control-bound `Event`/property to
`part=Events`. Reversing the order fails validation.

### A WWP `userAction` auto-generates its own empty event stub
Adding `userAction name=""Foo""` makes WWP emit an **empty `'DoFoo'` event stub**
automatically. So appending your own `Event 'DoFoo'` collides:
- `src0208 event already defined`.

**Do not add `Event 'DoFoo'` yourself.** Instead FILL the generated stub: `part=Events`
`operation=Insert_After` anchored on the `Event 'DoFoo'` header line, inserting your
body before the generated end markers. Read the Events part first to see the stub.

## Scope

Applicable to:
- Web Panels
- Work With patterns
- Panel objects (Smart Devices) — same Refresh→Load order

> Source: docs.genexus.com — Refresh event (8195)."
            },
            new Entry
            {
                Key = "clean-architecture",
                Title = "Clean Code & SOLID adaptado a GeneXus 18 (estándar del equipo)",
                Description = "MANDATORY team coding standard: SOLID translated honestly to non-OOP GeneXus, ACID/LUW rules, size limits per object, canonical parm() signature, and the code-smell catalog the linter enforces (GX012/GX014/GX015). Read before writing or reviewing any GeneXus code.",
                // Unlike the four entries above, this body is NOT inline: it is the team's
                // normative document, maintained as docs/clean-architecture-genexus.md and
                // embedded into this assembly at build time (see the <EmbeddedResource> item
                // in GxMcp.Gateway.csproj). Single source of truth: editing the standard is
                // editing that .md — docs/ does not travel in publish.zip, the binary does.
                Body = LoadEmbeddedBody("GxMcp.Gateway.Skills.clean-architecture.md")
            },
        };

        // Loads a skill body embedded at build time. Materialized ONCE at type initialization
        // (this is a static field initializer), so consumers see a plain string exactly like
        // the inline entries. Failure yields an explicit error body rather than null/empty:
        // SkillCatalogTests asserts bodies are >= 400 chars and cite docs.genexus.com, so a
        // missing resource fails tests loudly instead of shipping an empty skill.
        private static string LoadEmbeddedBody(string logicalName)
        {
            try
            {
                var asm = typeof(SkillCatalog).Assembly;
                using var stream = asm.GetManifestResourceStream(logicalName);
                if (stream == null)
                    return "ERROR: embedded skill resource '" + logicalName + "' not found in assembly. " +
                           "The <EmbeddedResource> item for docs/clean-architecture-genexus.md is missing from GxMcp.Gateway.csproj.";
                using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (System.Exception ex)
            {
                return "ERROR: failed to load embedded skill resource '" + logicalName + "': " + ex.Message;
            }
        }

        public static Entry FindByKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            foreach (var e in All)
            {
                if (string.Equals(e.Key, key, System.StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }
    }
}
