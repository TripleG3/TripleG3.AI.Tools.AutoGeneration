# AI Tool Authoring, Discovery, and MCP Roadmap

## Status

This document captures the proposed direction for extending `TripleG3.AI.Tools.AutoGeneration`. It is a design and implementation plan, not a commitment to implement every item immediately.

The package should remain host-neutral. It should provide reusable discovery, analysis, generation, selection, invocation, and schema infrastructure while leaving authentication, authorization, business policy, transport hosting, and application lifetimes to the consuming application.

## Goals

The proposed work should enable applications to:

1. Expose annotated .NET methods as model-facing tools.
2. Provide an MCP adapter without duplicating discovery or invocation behavior.
3. Generate AI-facing wrapper classes from existing classes or interfaces.
4. Scan a repository for methods that may be suitable as tools.
5. Standardize existing tools without rewriting domain services by default.
6. Perform one deterministic catalog scan and create different model-specific tool sets from it.
7. Keep categories as host-side filtering metadata rather than model-facing concepts.
8. Validate generated tools before they are registered or invoked.

## Non-goals

The package should not:

- Decide whether a business operation is authorized.
- Automatically execute arbitrary methods from an untrusted assembly.
- Infer tenant, user, resource, or ownership permissions.
- Replace domain services or application architecture automatically.
- Require every consuming application to use MCP.
- Force models to understand category names, toolboxes, or generation mechanics.
- Silently rewrite or delete existing source files.
- Treat reflection-based discovery as a security boundary.

## Current package capabilities

The existing package already provides:

- `[Tool]` method metadata.
- `[ToolCategory]` class-level toolbox metadata.
- `[ToolParameter]` parameter and method metadata.
- `[ToolSchema]` input schema descriptions.
- `[ToolResult]` result schema metadata.
- `[ToolEnumDescription]` enum metadata.
- Explicit assembly scanning through `ResponseToolDiscovery`.
- `GeneratedToolCatalog` with primary, all, required, and category views.
- Category lookup through `CreateToolsForCategory` and `TryGetToolsForCategory`.
- OpenAI Responses and Realtime projections.
- JSON argument binding and cancellation-token injection.
- DI-aware target resolution.
- Result serialization with optional schema envelopes.
- Assembly registration through `AddToolAssembly` and related extensions.

The main architectural gap is that the package currently focuses on runtime discovery and invocation. The proposed work adds developer tooling and clearer model-specific selection on top of that foundation.

## Terminology

The package currently has two category-related concepts that should be made explicit:

### Capability categories

`ToolAttribute.Categories` assigns one or more host-side capability labels to an individual tool.

Examples:

- `A`
- `B`
- `C`
- `ImageGeneration`
- `Documents`

Capability categories are used by the host to select tools for a model. They are not tools. They should not be exposed to the model unless a host explicitly chooses to provide catalog-management functionality.

### Toolboxes

`ToolCategoryAttribute` groups declared methods behind a generated selector tool. This is a progressive-disclosure mechanism for applications that intentionally want a category selector.

Toolboxes and capability categories should not be presented as interchangeable concepts. The documentation and API should use names that make the distinction obvious. A future compatibility-preserving rename could introduce `ToolboxAttribute` or `ToolGroupAttribute` while retaining `ToolCategoryAttribute` as an obsolete alias.

## Target architecture

```text
Application source projects
          |
          v
One repository/type analysis pass
          |
          +--> Candidate report
          +--> Standardization plan
          +--> Generated wrapper source
          |
          v
One runtime GeneratedToolCatalog scan
          |
          +--> Tool selection for model A
          +--> Tool selection for model B
          +--> Tool selection for model AB
          +--> All ordinary tools
          |
          +--> OpenAI adapter
          +--> Realtime adapter
          +--> MCP adapter
```

The catalog should be discovered once from explicitly selected assemblies. Model-specific requests should create filtered projections from the same catalog rather than scanning or generating separately for every model.

## Part 1: MCP adapter

### Purpose

Provide a host or companion server that exposes generated tools through MCP while reusing the package's catalog, schema, target-resolution, and invocation behavior.

### Normal MCP tool projection

Every ordinary `GeneratedTool` can be projected to an MCP tool:

- `ResponseTool.FunctionName` becomes the MCP tool name.
- `ResponseTool.FunctionDescription` becomes the MCP description.
- `ResponseTool.FunctionParameters` becomes the MCP input schema.
- `GeneratedTool.SerializeResult` produces the result payload.
- `ToolTargetResolver` resolves instance-method targets.
- The host supplies authentication, authorization, policy, logging, and lifetime management.

The MCP adapter should exclude internal toolbox selector tools by default. It should also exclude deprecated tools and tools that are not part of the selected tool set.

### Proposed MCP tools

The initial MCP surface should be small and predictable:

#### `inspect_type_methods`

Lists methods on a selected type without writing files or executing application behavior.

Inputs may include:

- Project or solution path.
- Type name.
- Accessibility filter.
- Whether inherited methods are included.
- Whether static methods are included.

Outputs should include method signatures, source locations, documentation, parameter information, return types, existing tool metadata, and warnings.

#### `scan_repository_for_tool_candidates`

Analyzes source projects and returns methods that could be exposed as AI tools. This is an analysis operation and must not modify files.

#### `preview_generated_tools`

Produces the proposed wrapper source and generation diagnostics without writing files.

#### `generate_tools`

Writes generated wrapper files after the caller supplies an explicit output path and confirms the operation.

#### `validate_generated_tools`

Analyzes the generated source and, where possible, compiles the affected project. It should report errors, warnings, duplicate names, unsupported signatures, and missing registrations.

#### `list_tool_categories`

Returns available capability categories and counts. This is primarily a trusted host/developer operation, not a model-facing business tool.

#### `list_category_tools`

Returns tool definitions for selected capability categories. It should use the same selection semantics described later in this document.

#### `get_tool_definition`

Returns a complete definition for a named tool. Exposing method and target type names should be configurable because those details may reveal internal implementation structure.

### Generic invocation

A generic MCP operation such as `invoke_tool(name, arguments)` could be useful for trusted administration, but it should not be the primary model-facing interface. Normal MCP registration gives clients explicit schemas and lets the server enforce an allowlist more naturally.

If a generic operation is implemented, it must:

- Resolve names only through a selected `ToolSet`.
- Enforce authorization before invocation.
- Apply timeouts and cancellation.
- Limit input and output sizes.
- Log structured metadata without secrets.
- Return safe error messages.
- Require explicit policy for side-effecting tools.
- Never invoke arbitrary reflection-discovered methods by name.

## Part 2: Transport-neutral invocation

The current invocation path is centered on OpenAI Responses types such as `FunctionCallResponseItem` and `FunctionCallOutputResponseItem`. MCP support would be cleaner if binding and invocation were separated from OpenAI-specific request and response objects.

### Proposed abstractions

```csharp
public sealed record ToolCall(
    string Name,
    string CallId,
    string ArgumentsJson);

public sealed record ToolInvocationResult(
    string CallId,
    string ToolName,
    object? Result,
    string SerializedResult,
    ToolInvocationState State);
```

`GeneratedTool` could expose a transport-neutral overload such as:

```csharp
ValueTask<ToolInvocationResult> InvokeAsync(
    ToolCall call,
    object? target = null,
    CancellationToken cancellationToken = default);
```

The OpenAI Responses integration would adapt `FunctionCallResponseItem` to `ToolCall`, and the MCP integration would adapt MCP call requests to the same type.

Argument binding and validation should be shared. Transport-specific code should only translate request and response envelopes.

## Part 3: Repository candidate scanning

### Purpose

Find existing methods that could be made available to AI models without requiring developers to manually inspect an entire repository.

### Preferred implementation

Use Roslyn for source projects whenever possible. Roslyn provides:

- Source file and line locations.
- XML documentation comments.
- Nullable reference type information.
- Accessibility and inheritance information.
- Generic type details.
- Existing attributes.
- Project references.
- Compilation diagnostics.

Reflection should remain a fallback for compiled assemblies or scenarios where source is unavailable.

### Candidate classifications

Each method should be assigned one classification:

- `Recommended`: documented, accessible, serializable, and suitable for review.
- `ReviewRequired`: potentially useful but ambiguous, side-effecting, undocumented, or structurally complex.
- `NotSuitable`: unsupported or unsafe for automatic exposure.
- `AlreadyExposed`: already has `[Tool]` metadata.

### Candidate signals

Positive signals include:

- Public or explicitly selected method.
- Application or service-layer type.
- `Task<T>`, `ValueTask<T>`, synchronous, or supported task-like return type.
- Serializable parameter and result types.
- XML documentation comments.
- Existing dependency-injection registration.
- Existing cancellation-token support.
- Query or read-oriented behavior.
- Stable method and type names.

Warning signals include:

- Missing cancellation token.
- Destructive or externally visible side effects.
- Database writes, deletion, provisioning, or messaging.
- Secrets, credentials, tokens, or sensitive data in parameters/results.
- `ref`, `out`, pointer, function-pointer, or unsupported stream parameters.
- Open generic methods or unresolved generic types.
- Private implementation helpers.
- Infrastructure internals such as database contexts or HTTP clients.
- Ambiguous names or missing descriptions.
- Very large result types.
- Methods that expose domain entities directly where a safe DTO would be preferable.

### Candidate model

A public result model could resemble:

```csharp
public sealed record ToolCandidate(
    string TypeName,
    string MethodName,
    string? SourceFile,
    int? SourceLine,
    ToolCandidateClassification Classification,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ToolParameterCandidate> Parameters,
    string ReturnType,
    bool HasCancellationToken,
    bool AlreadyHasToolAttribute);
```

The exact API should remain source-provider neutral so both Roslyn and reflection can produce the same report format.

## Part 4: Tool generation from existing types

### Purpose

Generate AI-facing wrapper classes from a class, interface, abstract class, or selected methods without modifying the original domain type.

### Wrapper-first strategy

Generated wrappers should be the default because they:

- Keep domain logic unchanged.
- Separate AI descriptions from business code.
- Provide a clear authorization boundary.
- Support third-party and external interfaces.
- Permit different tool façades over the same service.
- Make generated code reviewable and removable.
- Allow consistent naming, descriptions, result types, and metadata.

### Example input type

```csharp
public interface ICustomerService
{
    Task<Customer?> GetCustomerAsync(
        string customerId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Customer>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}
```

### Example generated wrapper

```csharp
using TripleG3.AI.Tools.AutoGeneration;

namespace MyApp.AiTools;

[ToolCategory("customers", "Customer lookup and management tools.")]
public sealed class CustomerTools(ICustomerService service)
{
    [Tool(
        "customers_get",
        "Gets a customer by identifier.",
        "Getting customer")]
    public Task<Customer?> GetAsync(
        [ToolParameter("customerId", "The customer identifier.")]
        string customerId,
        CancellationToken cancellationToken) =>
        service.GetCustomerAsync(customerId, cancellationToken);

    [Tool(
        "customers_search",
        "Searches customers using a text query.",
        "Searching customers")]
    public Task<IReadOnlyList<Customer>> SearchAsync(
        [ToolParameter("query", "The customer search text.")]
        string query,
        CancellationToken cancellationToken) =>
        service.SearchAsync(query, cancellationToken);
}
```

Generated code must not assume that `Customer` is in the generated namespace. The renderer must emit valid type references and required project usings, or use fully qualified names where necessary.

### Generation request

```csharp
public sealed record ToolGenerationRequest(
    string ProjectPath,
    string TypeName,
    IReadOnlyList<string> MethodNames,
    string OutputPath,
    string Namespace,
    string? ToolNamePrefix,
    bool IncludeInheritedMethods,
    bool Overwrite);
```

The request should also support an optional assembly path for reflection-based generation and a provider selection such as `Roslyn` or `Reflection`.

### Generation result

```csharp
public sealed record ToolGenerationResult(
    string OutputPath,
    IReadOnlyList<GeneratedToolDescription> GeneratedTools,
    IReadOnlyList<ToolGenerationDiagnostic> Diagnostics,
    bool WroteFile);
```

### Generation rules

The generator should:

- Generate one wrapper class per selected source type or logical capability group.
- Use constructor injection for instance dependencies.
- Preserve cancellation support when present.
- Optionally add a trailing `CancellationToken` to wrappers and pass `CancellationToken.None` to legacy methods only when explicitly configured.
- Use stable tool names based on configured prefixes and method names.
- Use XML documentation where available.
- Require explicit descriptions when documentation is missing or ambiguous.
- Generate parameter metadata for stable model-facing names.
- Avoid exposing internal implementation types where possible.
- Produce deterministic output so source control diffs remain reviewable.
- Report every skipped method and every fallback decision.

## Part 5: Tool standardization and migration

### Purpose

Provide a safe process for making existing tools consistent without automatically rewriting domain code.

### Standardization workflow

1. Discover existing `[Tool]` methods.
2. Scan selected projects for additional candidates.
3. Analyze names, descriptions, parameters, results, categories, and cancellation behavior.
4. Build a proposed standardization plan.
5. Generate wrappers for selected methods.
6. Preserve old names through aliases when required.
7. Generate a migration report.
8. Validate the generated source.
9. Build and test the affected project.
10. Require explicit approval before deleting or modifying existing tool implementations.

### Standardization rules

A generated or migrated wrapper should have:

- One clear AI-facing responsibility.
- A stable, descriptive tool name.
- A description that says when the model should use it.
- Explicit parameter names and descriptions.
- A trailing `CancellationToken`.
- Constructor-injected dependencies.
- A serializable result DTO where appropriate.
- Explicit capability categories.
- Consistent handling of long-running and side-effecting operations.
- No authorization bypass.
- No direct model-specific logic in the domain service.

### Compatibility strategy

Existing names may already be referenced by prompts, tests, clients, or saved conversations. The migration should therefore prefer aliases:

```csharp
[Tool(
    "customers_get",
    "Gets a customer by identifier.",
    "Getting customer",
    Aliases = ["get_customer"])]
```

The migration report should list:

- Existing tool name.
- New canonical name.
- Compatibility aliases.
- Removed or skipped methods.
- Description changes.
- Parameter schema changes.
- Category changes.
- Potential breaking changes.

Automatic deletion should never be the default.

### Proposed standardization operation

A future developer-facing operation could be named `standardize_repository_tools`. It should be a plan-and-generate operation rather than an opaque rewrite operation. It should support preview, explicit method selection, output-path selection, and an overwrite flag that defaults to false.

## Part 6: Single scan and model-specific tool selection

### Required behavior

The package should scan all configured assemblies once and create one authoritative `GeneratedToolCatalog`.

From that catalog, the host creates filtered tool sets:

| Model profile | Included capability categories |
|---|---|
| `A` | `A` |
| `B` | `B` |
| `C` | `C` |
| `AB` | `A` or `B` |
| `All` | All ordinary tools regardless of category |

The `AB` profile must be a union of categories `A` and `B`. It must not look for a category literally named `AB`.

### Proposed API

```csharp
public sealed record ToolSelection(
    bool IncludeAll = false,
    IReadOnlySet<string>? Categories = null,
    bool IncludeRequiredTools = true,
    bool IncludeToolboxSelectors = false)
{
    public static ToolSelection All { get; } = new(IncludeAll: true);

    public static ToolSelection ForCategories(params string[] categories) =>
        new(Categories: categories.ToHashSet(StringComparer.Ordinal));
}
```

```csharp
public sealed class ToolSet
{
    public IReadOnlyList<GeneratedTool> Tools { get; }

    public FunctionTool[] CreateResponsesTools();
    public RealtimeFunctionTool[] CreateRealtimeTools();

    public bool TryGetTool(string name, out GeneratedTool tool);
}
```

`GeneratedToolCatalog` should expose:

```csharp
public ToolSet SelectTools(ToolSelection selection);
```

### Selection semantics

`ForCategories("A")`:

- Includes ordinary tools whose `Categories` contains `A`.
- Excludes uncategorized tools unless `IncludeRequiredTools` adds them.
- Excludes toolbox selector tools by default.

`ForCategories("A", "B")`:

- Includes the union of `A` and `B`.
- Deduplicates by canonical function name.
- Does not include tools from a category named `AB` unless `AB` was explicitly requested.

`All`:

- Includes every non-deprecated ordinary generated tool.
- Excludes toolbox selectors by default.
- May include required tools naturally because all ordinary tools are already selected.

`IncludeRequiredTools`:

- When true, adds tools marked `[ToolRequired]` to category selections.
- The behavior should be documented clearly because required tools may be uncategorized or belong to another category.

### Invocation security

Invocation must resolve against the selected `ToolSet`, not the complete catalog:

```csharp
if (!toolSet.TryGetTool(callName, out GeneratedTool tool))
{
    throw new InvalidOperationException(
        $"Tool '{callName}' is not available to this model.");
}
```

This prevents a model from calling a globally discovered tool that was not assigned to its profile.

### Named profiles

Applications with recurring model profiles may define configuration such as:

```json
{
  "toolProfiles": {
    "A": ["A"],
    "B": ["B"],
    "C": ["C"],
    "AB": ["A", "B"],
    "All": ["*"]
  }
}
```

Profile resolution should occur when creating a model session or request. The model should receive only the final projected tool definitions, not the profile configuration or category mechanics.

## Part 7: Category semantics and compatibility

The existing `ToolAttribute.Categories` property should remain compatible, but documentation should state:

> Categories are host-side filtering labels. They are not model-facing tools and do not require the model to select or implement categories.

The package should consider adding a more explicit alias such as `Capabilities` in a future version, while retaining `Categories` for compatibility.

The existing class-level toolbox functionality should remain available for applications that intentionally use progressive disclosure. It should not be automatically mixed into model-specific capability selection.

Recommended defaults:

- Capability selection starts from `AllTools`.
- Toolbox selectors are excluded.
- Deprecated tools are excluded.
- Category matching is ordinal and case-sensitive unless a future compatibility decision changes this.
- Blank category values are ignored.
- Duplicate names are rejected during catalog construction.
- Aliases resolve to the same underlying generated tool but should not create duplicate definitions in a selected tool set.

## Part 8: Generated files and repository layout

Generated source should normally be committed to the consuming repository so it can be reviewed, built, and versioned.

A consuming application could use:

```text
.ai/
    tool-generation.json
    tool-candidates.json
    tool-migration-report.md
Generated/
    CustomerTools.g.cs
    OrderTools.g.cs
    ToolRegistration.g.cs
```

The package repository itself should keep its public runtime library separate from optional developer tooling. Possible future projects include:

```text
src/
    TripleG3.AI.Tools.AutoGeneration/
    TripleG3.AI.Tools.AutoGeneration.Generation/
    TripleG3.AI.Tools.AutoGeneration.Mcp/
    TripleG3.AI.Tools.AutoGeneration.Cli/
```

The core package should not acquire unnecessary dependencies on Roslyn or MCP abstractions unless the project intentionally chooses a combined distribution. Separate projects preserve a small runtime dependency footprint.

## Part 9: Safety and write policy

Any MCP or CLI operation that writes generated code must:

- Require an explicit workspace or project root.
- Reject paths outside the allowed root.
- Require an explicit output path.
- Refuse overwrites by default.
- Support preview before writing.
- Report the exact files that would change.
- Produce deterministic output.
- Preserve existing files unless overwrite is explicitly enabled.
- Run source validation after writing.
- Avoid executing newly generated code until the project builds and the host explicitly reloads it.

Assembly loading should be treated as potentially unsafe. Reflection providers should be used only for trusted assemblies, and source analysis should be preferred for repository workflows.

## Part 10: Validation and tests

The implementation should add tests for:

### Catalog selection

- One category selects only matching tools.
- Multiple categories produce a union.
- `AB` does not select category `AB` accidentally.
- `All` selects all ordinary tools.
- Toolbox selectors are excluded by default.
- Required tools are included according to configuration.
- Aliases do not duplicate selected definitions.
- Invocation rejects tools outside the selected set.

### Candidate scanning

- Existing `[Tool]` methods are classified as already exposed.
- Documented service methods are recommended.
- Unsupported signatures produce diagnostics.
- Side-effecting methods receive review warnings.
- Source locations and XML documentation are retained.

### Generation

- Class and interface wrappers compile.
- Static and instance methods are handled correctly.
- DI constructor parameters are generated correctly.
- Tool names are deterministic.
- Parameter metadata is emitted correctly.
- Cancellation tokens are forwarded.
- Generic or unsupported methods are skipped with diagnostics.
- Existing names can be preserved as aliases.
- Generated files are not overwritten unless requested.

### MCP integration

- MCP schemas match generated parameter schemas.
- Unknown tools are rejected.
- Selected tool sets are enforced.
- Cancellation reaches the target method.
- Errors do not expose sensitive implementation details.
- Result serialization matches the core invocation behavior.

## Recommended implementation phases

### Phase 1: Selection API

Implement `ToolSelection`, `ToolSet`, and `GeneratedToolCatalog.SelectTools(...)`. Add tests for `A`, `B`, `C`, `AB`, and `All` behavior. Exclude toolbox selectors by default.

### Phase 2: Transport-neutral invocation

Introduce transport-neutral call and result abstractions. Keep the current OpenAI APIs as adapters and preserve existing behavior.

### Phase 3: Source analysis

Create a Roslyn-based analysis project with `inspect_type_methods` and `scan_repository_for_tool_candidates` functionality. Keep the result models provider-neutral.

### Phase 4: Wrapper generation

Add deterministic wrapper generation for explicitly selected classes, interfaces, and methods. Implement preview and validation before file writing.

### Phase 5: Standardization workflow

Add migration planning, compatibility aliases, reports, and generated registration guidance. Do not delete or rewrite existing tools automatically.

### Phase 6: MCP adapter

Expose selected generated tools through MCP. Add developer-facing discovery and generation operations only with strict workspace and write policies.

### Phase 7: Optional toolbox compatibility improvements

Clarify toolbox terminology, document the distinction from capability categories, and consider compatibility aliases such as `Capabilities` or `ToolboxAttribute`.

## Success criteria

The work is successful when a consuming application can:

1. Configure all relevant tool assemblies once.
2. Build one cached catalog.
3. Define model profiles such as `A`, `B`, `C`, `AB`, and `All`.
4. Give each model only the appropriate projected tool definitions.
5. Invoke only tools assigned to that model profile.
6. Scan a repository and receive useful candidate recommendations.
7. Generate reviewed wrapper source from selected types or methods.
8. Standardize existing tools without changing domain services by default.
9. Expose the same generated tools through OpenAI or MCP adapters.
10. Validate generated code and schemas before production registration.

## Final recommendation

The first implementation should focus on the selection API and its tests. This directly addresses the current model confusion and establishes the central invariant:

> Discover once, select on the host, expose only the selected tools, and never require the model to understand categories.

Generation, repository scanning, standardization, and MCP hosting should build on that invariant rather than introduce separate per-model discovery or per-type tool implementations.
