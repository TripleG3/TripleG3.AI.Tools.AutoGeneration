# TripleG3.AI.Tools.AutoGeneration

`TripleG3.AI.Tools.AutoGeneration` is host-neutral reflection infrastructure for exposing annotated .NET methods as OpenAI function tools. It builds one JSON Schema from .NET types, then projects each discovered `GeneratedTool` into either an `OpenAI.Responses.FunctionTool` or an `OpenAI.Realtime.RealtimeFunctionTool`.

The library does not run a Responses API loop, enforce authorization, or know how an application creates tool-target instances. The host application owns those concerns. This separation makes the package suitable for web applications, workers, console applications, and agent hosts.

`GeneratedTool` is reusable metadata, not a shared execution service. Create a
host-owned `ToolBinding` for each independently serialized target/session, or use
the stateless convenience calls. Bind either a reflected target resolver or an
explicit alternative handler; both use the same argument and result contract.

## Contents

- [End-to-end flow](#end-to-end-flow)
- [Requirements and reference](#requirements-and-reference)
- [Define a tool](#define-a-tool)
- [Attribute reference](#attribute-reference)
- [Generated parameter schema](#generated-parameter-schema)
- [Discover tools](#discover-tools)
- [Choose a tool transport](#choose-a-tool-transport)
- [Use toolboxes](#use-toolboxes)
- [Find tools by function category](#find-tools-by-function-category)
- [Select tools for model profiles](#select-tools-for-model-profiles)
- [Resolve targets and invoke calls](#resolve-targets-and-invoke-calls)
- [Bind alternative implementations](#bind-alternative-implementations)
- [Persist tool descriptors](#persist-tool-descriptors)
- [Serialize results](#serialize-results)
- [Migration from 0.0.7](#migration-from-007)
- [Publishing this package](#publishing-this-package)
- [Dependency injection](#dependency-injection)
- [Implementation checklist](#implementation-checklist)
- [License](#license)

## End-to-end flow

The normal host flow is:

1. Define a method with `[Tool]` and exactly one trailing `CancellationToken`.
2. Scan one or more explicitly selected assemblies with `ResponseToolDiscovery.CreateToolCatalog(...)`.
3. Add each `GeneratedTool.ResponseTool` from `catalog.CreatePrimaryTools()` to `CreateResponseOptions.Tools`.
4. When `ResponseResult.OutputItems` contains a `FunctionCallResponseItem`, locate the matching `GeneratedTool` by function name.
5. Resolve the selected definition's session binding and invoke it. The binding
   resolves an instance target or invokes its explicitly supplied handler.
6. Add the resulting `FunctionCallOutputResponseItem` to the next `CreateResponseOptions.InputItems`, correlated through `FunctionCallResponseItem.CallId`.
7. Repeat the Responses API request flow until the model returns a final response.

`GeneratedToolCatalog` is the authoritative lookup table between model-visible function names and .NET methods. Do not infer a method from a model-provided name yourself.

## Choose a tool transport

Tool discovery is transport-neutral. Scan an assembly once and keep the returned `GeneratedToolCatalog` as the authoritative reflected-method and JSON-schema catalog. Select the SDK-specific projection only when configuring a model session or request:

```csharp
GeneratedToolCatalog catalog = ResponseToolDiscovery.CreateToolCatalog([toolsAssembly]);

// Responses API
FunctionTool[] responsesTools = catalog.CreatePrimaryResponsesTools();

// Realtime API
RealtimeFunctionTool[] realtimeTools = catalog.CreatePrimaryRealtimeTools();
```

For an individual generated tool, use:

```csharp
FunctionTool responsesTool = generatedTool.ToResponsesTool();
RealtimeFunctionTool realtimeTool = generatedTool.ToRealtimeTool();
```

Both projections use the same function name, description, aliases, and generated JSON parameter schema. Tool invocation remains host-owned: the Responses host resolves `FunctionCallResponseItem` values, while a Realtime host resolves `RealtimeFunctionCallItem` values and returns a matching realtime function-call-output item before starting a follow-up response.
Projections are fresh SDK objects. Mutating a returned projection does not change
the shared definition, its descriptor, or another consumer's projection.

## Requirements and reference

The project targets `net10.0` and exposes types from `OpenAI.Responses`. Reference the project, or the package produced by your organization's distribution process, from the host application.

OpenAI 2.12.0 marks its Responses API types with the `OPENAI001` experimental diagnostic. This package intentionally opts into that SDK surface. A host that directly uses `OpenAI.Responses` types must also suppress `OPENAI001` when its build configuration requires it.

For a source-based reference, add the library project to the host project:

```xml
<ItemGroup>
  <ProjectReference Include="..\path\to\TripleG3.AI.Tools.AutoGeneration\src\TripleG3.AI.Tools.AutoGeneration\TripleG3.AI.Tools.AutoGeneration.csproj" />
</ItemGroup>
```

Tool implementations normally need these namespaces:

```csharp
using System.Text.Json.Serialization;
using TripleG3.AI.Tools.AutoGeneration;
```

Hosts that use the DI integration also need:

```csharp
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Responses;
using TripleG3.AI.Tools.AutoGeneration;
```

## Define a tool

The example below is a complete shape for an instance tool. Replace the sample search behavior with application behavior and authorization appropriate to the host.

```csharp
using System.Text.Json.Serialization;
using TripleG3.AI.Tools.AutoGeneration;

[ToolCategory("place_search", "Searches for places and returns matching locations.")]
public sealed class PlaceTools
{
	[Tool(
		"find_places",
		"Finds places matching a text query. Use this when the user asks to locate a city, venue, or landmark.",
		"Searching for places",
		IsAlwaysAvailable = true,
		SchemaVersion = 1)]
	[ToolRequired]
	public Task<PlaceSearchResult> FindPlacesAsync(
		[ToolParameter("request", "The search phrase and optional country restriction.")] PlaceSearchRequest request,
		[ToolParameter("maxResults", "Maximum number of matches to return.", isRequired: false)] int maxResults = 10,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		PlaceMatch match = new()
		{
			Name = request.Query,
			CountryCode = request.CountryCode ?? "unknown"
		};

		return Task.FromResult(new PlaceSearchResult
		{
			Matches = [match]
		});
	}
}

[ToolSchema("Criteria used to search for places.")]
public sealed record PlaceSearchRequest
{
	[ToolSchema("Text to search for.")]
	[JsonPropertyName("query")]
	public required string Query { get; init; }

	[ToolSchema("Optional ISO-style country restriction.")]
	[JsonPropertyName("countryCode")]
	public string? CountryCode { get; init; }
}

[ToolResult("A set of places matching the search.")]
public sealed record PlaceSearchResult
{
	[ToolResult("Matching places.")]
	[JsonPropertyName("matches")]
	public required IReadOnlyList<PlaceMatch> Matches { get; init; }
}

[ToolResult("One matching place.")]
public sealed record PlaceMatch
{
	[ToolResult("Display name of the place.")]
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[ToolResult("Country code for the place.")]
	[JsonPropertyName("countryCode")]
	public required string CountryCode { get; init; }
}
```

### Required method contract

Every method that becomes a `GeneratedTool` must meet these rules:

- It has `[Tool]` directly applied to the method.
- It declares **exactly one** `CancellationToken`, and that parameter is last.
- The cancellation token is omitted from the model-visible schema and receives the token supplied to `GeneratedTool.InvokeAsync(...)`.
- A tool can be `public`, `internal`, `protected`, or `private`; discovery scans all declared instance and static methods. Treat the explicitly scanned assembly as a trust boundary.
- Instance methods require a target object at invocation time unless they were registered from a delegate that already captures a target instance.
- Tool names, aliases, and generated category names must be unique within a catalog.

The library accepts synchronous return values, `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>`. The reflected invocation awaits task-like return values before producing `ToolInvocationState.Result`.

## Attribute reference

| Attribute | Apply to | Purpose and runtime effect |
| --- | --- | --- |
| `Tool(name, description, friendlyUIDescription, isManuallyRegistered: false)` | Method | Defines the model-visible function name and description. `friendlyUIDescription` is carried in invocation state for host UI or telemetry; it is not sent as the model's function description. |
| `ToolAttribute.IsAlwaysAvailable` | `[Tool]` property | For a tool in a category, includes that tool in `PrimaryTools` in addition to the category selector. All uncategorized tools are already primary. |
| `ToolAttribute.IsLongRunning` | `[Tool]` property | Preserved on `GeneratedTool` for host behavior or UI. It does not schedule, poll, or timeout work automatically. |
| `ToolAttribute.SchemaVersion` | `[Tool]` property | Positive contract version exposed by every `GeneratedTool` and its portable descriptor. It does not alter generated JSON Schema. |
| `ToolAttribute.Aliases` | `[Tool]` property | Creates an additional `FunctionTool` definition for every non-empty alias. Each alias invokes the same method. |
| `ToolAttribute.Categories` | `[Tool]` property | Adds zero or more independent categories to this function. These do not come from, and are not inherited from, a class-level `ToolCategory` toolbox. |
| `ToolAttribute.Replaces` | `[Tool]` property | Informational replacement metadata reported by a category descriptor. It does not redirect calls. |
| `ToolAttribute.IsDeprecated` | `[Tool]` property | Excludes the tool from both assembly discovery and delegate registration. |
| `ToolAttribute.DeprecationMessage` | `[Tool]` property | Informational deprecation metadata reported by a category descriptor. |
| `ToolAttribute.IsManuallyRegistered` | `[Tool]` constructor argument | Excludes the method from assembly scanning. Register it explicitly through a delegate when it should be available. |
| `ToolRequired` | Method | Marks a tool as required and exposes it through `GeneratedToolCatalog.RequiredTools`. For category members, it also adds the tool to `PrimaryTools`. |
| `ToolCategory(name, description)` | Class, struct, or interface | Groups declared tools behind a generated zero-argument category selector tool. |
| `ToolParameter(name, description, isRequired: true)` | Parameter or method | Supplies the JSON property name, model-facing parameter description, and required status. A parameter-level attribute takes precedence over a matching method-level attribute. |
| `ToolParameter<T>(...)` | Parameter or method | Supplies an explicit schema type. Use it only when `T` is the parameter type, or is assignable to the parameter type; it also guides argument conversion in those cases. |
| `ToolSchema(description)` | Type or public property | Adds descriptive metadata to generated input JSON Schema. Use it on types and public properties that participate in serialization. |
| `ToolResult(description)` | Result type or public property | Adds descriptive metadata to the result schema and causes result serialization to include a schema envelope. |
| `ToolEnumDescription(description)` | Enum field | Describes one enum value in generated input schema. |

### Parameter metadata placement

Use a parameter-level `ToolParameter` whenever the model-visible name differs from the C# parameter name:

```csharp
[Tool("get_weather", "Gets current weather for a location.", "Getting weather")]
public Task<string> GetWeatherAsync(
	[ToolParameter("city", "City and region to look up.")] string location,
	CancellationToken cancellationToken) =>
	Task.FromResult(location);
```

A method-level `ToolParameter` matches a C# parameter by name, case-insensitively. It is useful when the C# and JSON names are already the same:

```csharp
[Tool("get_weather", "Gets current weather for a location.", "Getting weather")]
[ToolParameter("city", "City and region to look up.")]
public Task<string> GetWeatherAsync(string city, CancellationToken cancellationToken) =>
	Task.FromResult(city);
```

`isRequired: false` requires a usable C# parameter default. Discovery rejects
optional metadata without that default. An explicit `isRequired: true` requires
the caller to supply the argument even when the C# signature has a default.
A nullable reference type permits JSON null; it does not make omission optional.
Defaults are applied only to omitted arguments, never to overwrite explicit values.

## Generated parameter schema

Each `FunctionTool` receives a JSON Schema object with `type: "object"`, `properties`, `required`, and `additionalProperties: false`. The function description comes from `ToolAttribute.Description`.

The schema generator handles common .NET shapes as follows:

| .NET shape | Generated schema |
| --- | --- |
| `string`, `char`, `TimeOnly`, `TimeSpan` | `string` |
| `bool` | `boolean` |
| Integral numeric types | `integer` |
| `float`, `double`, `decimal` | `number` |
| `DateTime`, `DateTimeOffset` | `string` with `date-time` format |
| `DateOnly` | `string` with `date` format |
| `Guid` | `string` with `uuid` format |
| `Uri` | `string` with `uri` format |
| `byte[]`, `BinaryData` | Base64-encoded `string` |
| Enum | String enum, including `x-enum-descriptions` when values have `ToolEnumDescription` |
| `IEnumerable<T>` or array | Array with an item schema for `T` |
| `IDictionary<string, T>` / `IReadOnlyDictionary<string, T>` | Object with an `additionalProperties` schema for `T` |
| Serializable object | Object schema built from its public properties |
| `object`, `JsonElement`, `JsonNode` | Unconstrained JSON value, including arrays and primitives |

Every generated schema includes the nonstandard `x-dotnet-type` extension so hosts and model tooling can retain the original .NET type. Nullable values are represented by allowing both the generated type and `null`.

For object properties, the generator follows familiar `System.Text.Json` metadata including `JsonPropertyName`, `JsonIgnore`, `JsonInclude`, `JsonRequired`, `RequiredMember`, `JsonConstructor`, and `JsonExtensionData`. Use `JsonPropertyName` whenever a stable, explicit wire name matters.

`JsonStringEnumMemberNameAttribute` wire names are honored in schemas, input
binding, defaults and results. Numeric enum input is rejected. Nullable enum
schemas explicitly permit null. `BinaryData` uses base64 strings in both
directions; `TimeSpan` uses invariant strings rather than numeric seconds.

Use `ToolSchema` on types and public properties, rather than fields or top-level parameters, when you need descriptions to appear in generated input schema.

## Discover tools

Prefer explicit assembly scanning so tool exposure is deterministic:

```csharp
using System.Reflection;
using OpenAI.Responses;
using TripleG3.AI.Tools.AutoGeneration;

Assembly toolsAssembly = typeof(PlaceTools).Assembly;
GeneratedToolCatalog catalog = ResponseToolDiscovery.CreateToolCatalog([toolsAssembly]);

FunctionTool[] initialResponseTools = catalog.CreatePrimaryTools()
	.Select(tool => tool.ResponseTool)
	.ToArray();

CreateResponseOptions requestOptions = new()
{
	Model = "gpt-5.1"
};

foreach (FunctionTool responseTool in initialResponseTools)
{
	requestOptions.Tools.Add(responseTool);
}
```

Pass `requestOptions` to `ResponsesClient.CreateResponseAsync(...)`. `ResponseToolDiscovery` caches catalogs by the scanned assemblies and their module version IDs.

The catalog exposes these views:

| Member | Use it for |
| --- | --- |
| `PrimaryTools` / `CreatePrimaryTools()` | Initial tool definitions. This includes all uncategorized tools, every category selector, and category tools marked required or always available. |
| `AllTools` / `CreateAllTools()` | Every discovered tool definition, including all category members and aliases. Use this when category-based progressive disclosure is unnecessary. |
| `RequiredTools` | Tools marked with `ToolRequired`, including category members that are not otherwise primary. |
| `Categories` | Class-level toolboxes: each generated selector plus the tools it reveals. |
| `CategoryNames` | Every distinct function category assigned through `ToolAttribute.Categories`. |
| `CreateToolsForCategory(name)` | A new array containing every generated tool in the named function category, or an empty array when no tools match. |
| `TryGetToolsForCategory(name, out tools)` | Looks up every generated tool in the named function category without treating an empty result as an error. |
| `TryGetTool(name, out tool)` | Resolve a model tool-call name, including an alias, to its generated tool. |
| `TryGetCategory(name, out category)` | Detect and inspect a class-level toolbox selector call. |

`CreateToolCatalog()` and `CreateTools()` overloads without assemblies scan the calling assembly. They are convenient for simple applications, but explicit assemblies are preferable for reusable host infrastructure.

### Manual delegate registration

Set `isManuallyRegistered: true` when an annotated method must not be included in assembly scanning. It can still be registered through a delegate:

```csharp
GeneratedTool[] tools = ResponseToolDiscovery.CreateTools(
	(Func<string, CancellationToken, Task<string>>)ManualTools.RunAsync);
```

Delegate registration preserves a captured instance as `GeneratedTool.TargetInstance`. The delegate method still needs `[Tool]`, must satisfy the trailing-cancellation-token rule, and is skipped if `IsDeprecated` is true. The overload that accepts both assemblies and delegates combines both sources.

## Use toolboxes

Apply `ToolCategory` to a type when a host should initially expose a compact selector rather than every tool in that group. It is a toolbox mechanism only: it does not assign a function category to the methods in the type.

```csharp
[ToolCategory("documents", "Finds, reads, and updates documents.")]
public sealed class DocumentTools
{
	[Tool("find_documents", "Finds documents by text.", "Finding documents")]
	public Task<string> FindAsync(string query, CancellationToken cancellationToken) =>
		Task.FromResult(query);

	[Tool("read_document", "Reads one document by identifier.", "Reading document")]
	public Task<string> ReadAsync(string documentId, CancellationToken cancellationToken) =>
		Task.FromResult(documentId);
}
```

Discovery generates a zero-argument `documents` selector. Invoking that selector returns a list of `ToolCategoryToolInfo` records containing each tool's name, description, parameter schema, required status, availability metadata, version, function categories, aliases, replacements, and deprecation metadata.

After a model invokes a category selector:

1. Execute it using the same invocation path as any other tool.
2. Return the resulting category descriptor to the model.
3. Include the selected category's tools in the next Responses request, usually alongside the original primary tools.

For example, preserve initial definitions and add the selected category members without duplicating names:

```csharp
if (catalog.TryGetCategory(categoryCall.FunctionName, out GeneratedToolCategory category))
{
	FunctionTool[] nextResponseTools = catalog.CreatePrimaryTools()
		.Concat(category.CreateTools())
		.DistinctBy(tool => tool.ResponseTool.FunctionName, StringComparer.Ordinal)
		.Select(tool => tool.ResponseTool)
		.ToArray();

	// Add nextResponseTools to the next CreateResponseOptions.Tools collection.
}
```

Use `CreateAllTools()` instead when the host does not need progressive disclosure.

## Find tools by function category

Use `ToolAttribute.Categories` on each function for cross-cutting discovery. A function can have zero or more categories regardless of its declaring type, namespace, or toolbox membership.

```csharp
public static class ImageGenerationTools
{
	[Tool(
		"generate_image",
		"Generates an image from a prompt.",
		"Generating an image",
		Categories = ["ImageToolsCategory", "ImageGenerationCategory"])]
	public static Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken) =>
		Task.FromResult(prompt);
}

public static class ImageEditingTools
{
	[Tool(
		"edit_image",
		"Edits an existing image.",
		"Editing an image",
		Categories = ["ImageToolsCategory", "ImageEditingCategory"])]
	public static Task<string> EditAsync(string imageId, CancellationToken cancellationToken) =>
		Task.FromResult(imageId);
}
```

Retrieve every generated tool definition for a category through the catalog:

```csharp
GeneratedTool[] imageTools = catalog.CreateToolsForCategory("ImageToolsCategory");

if (catalog.TryGetToolsForCategory("ImageEditingCategory", out IReadOnlyList<GeneratedTool> editingTools))
{
	// Add editingTools.Select(tool => tool.ResponseTool) to a Responses request as needed.
}
```

Function categories are normalized by trimming whitespace, blank values are ignored, and duplicate names on one function are removed. Lookup is ordinal and case-sensitive. The index uses `AllTools`, so a function inside a toolbox is discoverable by category even when it is not initially exposed through `PrimaryTools`.

## Select tools for model profiles

Perform discovery once, then select a host-side `ToolSet` for each model. `ToolAttribute.Categories` are capability labels used by the host; they are not model-facing tools.

```csharp
GeneratedToolCatalog catalog = ResponseToolDiscovery.CreateToolCatalog([toolsAssembly]);

ToolSet modelATools = catalog.SelectTools(ToolSelection.ForCategories("A"));
ToolSet modelABTools = catalog.SelectTools(ToolSelection.ForCategories("A", "B"));
ToolSet allTools = catalog.SelectTools(ToolSelection.All);

FunctionTool[] responseTools = modelABTools.CreateResponsesTools();
```

Category selection uses a union: `ForCategories("A", "B")` selects tools in `A` or `B`; it does not look for a category named `AB`. Toolbox selector tools are excluded by default. Resolve calls against the selected `ToolSet`, rather than the complete catalog, so a model cannot invoke a globally discovered tool that was not assigned to its profile.

## Resolve targets and invoke calls

`FunctionCallResponseItem` values arrive in `ResponseResult.OutputItems`. Resolve
them through the assigned `ToolSet`, not the entire catalog. The following
one-shot helper creates a temporary binding; retain a binding at the host's
session scope when repeated calls to one target should serialize:

```csharp
using OpenAI.Responses;
using TripleG3.AI.Tools.AutoGeneration;

public static class ResponseToolCallExecutor
{
	public static async Task<FunctionCallOutputResponseItem> ExecuteAsync(
		ToolSet assignedTools,
		FunctionCallResponseItem call,
		ToolTargetResolver targetResolver,
		CancellationToken cancellationToken)
	{
		if (!assignedTools.TryGetTool(call.FunctionName, out GeneratedTool tool))
		{
			throw new InvalidOperationException($"The model called unknown tool '{call.FunctionName}'.");
		}

		await using ToolBinding binding = tool.Bind(targetResolver);
		ToolInvocationResult result = await binding.InvokeAsync(
			new ToolCall(call.FunctionName, call.CallId, call.FunctionArguments.ToString()),
			cancellationToken);
		return ResponseItem.CreateFunctionCallOutputItem(call.CallId, result.SerializedResult);
	}
}
```

`GeneratedTool.InvokeAsync(...)` binds the JSON object in `FunctionCallResponseItem.FunctionArguments` to the method parameters. `GeneratedTool.InvokeAndCreateFunctionCallOutputAsync(...)` adds the normal Responses API result step by returning a `FunctionCallOutputResponseItem` linked to `FunctionCallResponseItem.CallId`. It:

- Requires the exact selected function name. Aliases have their own discovered definitions.
- Requires a JSON object for arguments.
- Rejects duplicate properties, unknown properties, wrong-case wire names, missing
  required values, and schema/type/nullability mismatches before invoking handlers.
- Uses parameter-level metadata first, then matching method-level metadata.
- Applies usable C# defaults when an optional argument is absent.
- Injects the supplied cancellation token into the trailing `CancellationToken` parameter.
- Converts primitives, object graphs, `JsonElement`, `JsonNode`, `BinaryData`, enums, collections, and dictionaries with `System.Text.Json`.
- Unwraps `TargetInvocationException` so application exceptions retain their original type.

`ToolBinding.InvokeAsync(ToolCall, token)` returns `ToolInvocationResult`, including
the typed result, serialized result, canonical effective argument JSON, and
invocation snapshot. Domain exceptions and cooperative cancellation propagate
with their original types; binding state also reports an error for a failed
transition. Bindings serialize their own invocations but never serialize other
bindings that happen to share the definition.

`GeneratedTool.InvokeAsync(ToolCall, ...)` and the Responses convenience overloads
use temporary bindings and retain no state on the definition. The overload
accepting `FunctionCallResponseItem` still returns a completed `State<ToolInvocationState>`
on success. Failures now throw instead of being hidden in a non-ready result.
`GetReadyInvocationValueOrThrow` remains available for reading externally supplied
state values.

For each function output, create the next Responses request with the previous response ID and the output item:

```csharp
CreateResponseOptions continuationRequest = new()
{
	Model = "gpt-5.1",
	PreviousResponseId = response.Id
};

continuationRequest.InputItems.Add(functionOutput);
ResponseResult nextResponse = await responsesClient.CreateResponseAsync(continuationRequest, cancellationToken);
```

### Target-resolution options

Use one of the supplied resolvers, or write one that fits the host lifetime and tenant model:

| Resolver | Behavior |
| --- | --- |
| `ToolTargetResolvers.Default` | Uses a captured delegate target when present; otherwise returns `null` for static methods and calls `Activator.CreateInstance` for instance methods. |
| `ToolTargetResolvers.FromServiceProvider(services)` | Uses a captured target when present; otherwise obtains an instance-method target from the specified `IServiceProvider`. |
| `ToolTargetResolvers.FromLookup(lookup)` | Calls host-supplied `Func<GeneratedTool, FunctionCallResponseItem, object?>` logic for each call. |

For a dependency-injected host, resolve targets from the service provider that owns the active request or scope:

```csharp
ToolTargetResolver targetResolver = ToolTargetResolvers.FromServiceProvider(serviceProvider);
FunctionCallOutputResponseItem functionOutput = await ResponseToolCallExecutor.ExecuteAsync(
	assignedTools,
	toolCall,
	targetResolver,
	cancellationToken);

// Add functionOutput to the next CreateResponseOptions.InputItems collection.
```

The package deliberately does not authorize calls. Validate user identity, tenant boundaries, resource ownership, and side-effect permissions inside the resolver and/or tool implementation before performing work.

### Bind alternative implementations

An alternative handler replaces the entire invocation, including a static
method. It never resolves or falls back to the original target:

```csharp
await using ToolBinding binding = generatedTool.BindHandler(
	(call, arguments, cancellationToken) =>
	{
		cancellationToken.ThrowIfCancellationRequested();
		string city = arguments.Get<string>("city");
		// Return the same declared result type as the original tool.
		return ValueTask.FromResult<object?>(city);
	},
	ToolResultFormat.Plain);

ToolInvocationResult result = await binding.InvokeAsync(
	new ToolCall(generatedTool.Name, "call-1", "{\"city\":\"Paris\"}"),
	cancellationToken);
```

Use the handler for an adapter, fixture, recorder, or simulated implementation.
The host owns any resources it captures. Binding disposal cancels and drains
pending invocations and clears invocation state and handler references; it does
not dispose borrowed DI targets or undo external side effects. Implementations
must honor cancellation. Do not synchronously wait for disposal from inside
the binding's own handler or state observer.

`ToolArgumentBinder.Bind(tool, call, token)` and the existing `ToParameters`
extensions can also be used without invoking the production method.
`ToolArguments.Values` exposes per-invocation converted values; contained DTOs
belong to that call and should not be shared between concurrent calls.
`EffectiveArgumentsJson` records exactly the selected parameters with defaults
applied; it excludes the injected cancellation token.

Environment defaults (device identity, clocks, resource roots, connection
configuration) remain injected target/handler dependencies. The package does
not silently merge them into model arguments.

### Persist tool descriptors

`GeneratedTool.Descriptor` and `Describe(format)` return a JSON-serializable
`ToolDescriptor`. Catalogs and tool sets expose `DescribeTools(format)`.
Descriptors contain the selected name/description, schema version, parameter
schema (including declared defaults), result schema, actual output-wire schema,
effective result format and a deterministic SHA-256 fingerprint.

```csharp
string json = JsonSerializer.Serialize(tool.Descriptor, ToolJsonSerialization.Options);
ToolDescriptor expected = JsonSerializer.Deserialize<ToolDescriptor>(
	json, ToolJsonSerialization.Options)!;
expected.RequireMatch(tool.Descriptor);
```

Persist the descriptor and a host-defined binding identifier, not `GeneratedTool`,
`MethodInfo`, service instances or credentials. A descriptor cannot load code,
grant permission or invoke anything. The fingerprint detects contract changes;
it is not a signature or authorization token. Increment `SchemaVersion` when
the intended contract changes and requalify downstream assumptions.

Catalog discovery rejects conflicting names/aliases instead of silently selecting
the first method. Repeating the exact same definition reference is harmless.
Catalog collections are read-only, and failed assembly type loading is surfaced
rather than silently omitting capabilities.

## Serialize results

Use `GeneratedTool.SerializeResult(result)` after invocation. It selects the annotated method's declared return type and calls `ToolResultJsonSerializer`.

- When the return type and its reachable public properties have no `ToolResult` metadata, the result is ordinary JSON.
- When a result type or reachable public property has `ToolResult`, the payload is a self-describing envelope:

```json
{
  "schema": {
	"$schema": "https://json-schema.org/draft/2020-12/schema"
  },
  "result": {
  }
}
```

The actual `schema` contains the full generated result schema and `result` contains the serialized value. This is particularly useful when an AI model needs both data and a model-readable description of the data shape.

Schema generation, argument conversion and output serialization use the same
read-only `ToolJsonSerialization.Options`: camel-case properties, explicit
`JsonPropertyName` overrides, string enum wire names, base64 binary values,
required constructor members and nullable annotations.

`ToolResultFormat.Automatic` preserves metadata-driven envelopes. Choose
`Plain` or `SchemaEnvelope` explicitly on a binding or `SerializeResult` to use
another documented output shape. Use the same format for production and
alternative handlers; it is included in the descriptor fingerprint.
Alternative results must be assignable to the original declared return type
and satisfy the generated output schema.

The validator covers the schema subset generated by this package; it is not
a general JSON Schema or domain-policy engine. Cyclic/untyped shapes remain
permissive at their recursion boundary. Business constraints and any custom
schemas/converters outside the documented contract require host validation.

## Migration from 0.0.7

This is an intentional pre-1.0 API/wire-contract change:

- `GeneratedTool` no longer inherits `StateService<ToolInvocationState>`.
  Move state subscriptions and per-target serialization to a host-owned
  `ToolBinding`. Do not cache bindings globally with definitions.
- SDK projections are fresh objects, not shared mutable catalog instances.
- Result property names now match the camel-case schema (unless explicitly
  named). Use `ToolResultFormat` deliberately; do not assume annotated results
  are plain DTO JSON.
- Argument names are case-sensitive; duplicates, unknown fields, missing
  required values and non-nullable nulls fail before execution.
- Enum wire names and BinaryData base64 semantics now match their schemas.
  Numeric TimeSpan and numeric enum inputs are not accepted.
- Optional metadata without a usable declaration default, conflicting tool
  names, and unsupported method signatures fail discovery.
- Convenience invocations throw failures/cancellation rather than returning
  error-shaped state. The success return signatures remain available.
- The minimum `TripleG3.Cis` version is now `0.0.7`. OpenAI remains `2.12.0`.

Existing persisted tool fingerprints or result consumers must be reviewed
before upgrading. Publish only after the package tests and representative
consumer contract tests pass. The existing workflow continues to publish
`0.0.<run_number>` on pushes to `main`; local packs are not proof of publication.

## Publishing this package

The GitHub Actions workflow uses [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
instead of a stored publishing API key. It builds and tests first, then the
official `NuGet/login` action exchanges GitHub's OIDC token for a temporary
NuGet API key immediately before pushing.

Repository configuration:

- GitHub environment: `nuget`, with deployment restricted to branch `main`.
- Repository variable `NUGET_USER`: the owning **NuGet.org profile name**, not
  a GitHub login or an email address.
- Only the publishing job requests `id-token: write`; repository access stays
  `contents: read`.
- The workflow still publishes `0.0.<github.run_number>`. Manual dispatch is
  available on `main`; other refs do not publish.

Create a matching policy while privately signed in to
[NuGet.org Trusted Publishing](https://www.nuget.org/account/trustedpublishing):

| Policy field | Value for this repository |
|---|---|
| GitHub repository owner | `TripleG3` |
| Repository | `TripleG3.AI.Tools.AutoGeneration` |
| Workflow filename | `nuget-publish.yml` (filename only) |
| GitHub environment | `nuget` |
| Package pattern | `TripleG3.AI.Tools.AutoGeneration` |
| Scope | Push new versions of this existing package |
| Policy owner | The NuGet account/organization that owns this package |

The policy must be active and the configured NuGet username must match the
publisher account. No password, MFA code, or permanent API key belongs in the
workflow or repository. After a trusted publication succeeds, revoke an unused
old NuGet key and remove its obsolete GitHub secret if nothing else uses it.

A run created before this workflow change still uses its original API-key
workflow when rerun. Use a run from the Trusted Publishing commit, or dispatch
the current workflow on `main`. Temporary keys expire after one hour, so do not
move login ahead of long-running builds.

## Dependency injection

`AddToolAssembly` registers the selected assembly for lazy catalog construction and makes `IToolCatalogService` available. Register every instance-tool declaring type in the same provider as well.

```csharp
using Microsoft.Extensions.DependencyInjection;
using TripleG3.AI.Tools.AutoGeneration;

IServiceCollection services = new ServiceCollection();
services.AddScoped<PlaceTools>();
services.AddToolAssembly(typeof(PlaceTools).Assembly);

using ServiceProvider serviceProvider = services.BuildServiceProvider();

var catalogState = await serviceProvider
	.GetRequiredService<IToolCatalogService>()
	.LoadAsync(cancellationToken);

GeneratedToolCatalog catalog = catalogState.Value
	?? throw new InvalidOperationException(catalogState.ErrorMessage ?? "Tool catalog could not be loaded.");
```

Call `AddToolAssembly<TMarker>()` to identify an assembly by a marker type, or `AddToolAssemblies(...)` for several assemblies. `ToolCatalogService` builds the catalog once per service provider and exposes it through `LoadAsync(...)`.

## Implementation checklist

Before exposing a new application method as a tool, verify all of the following:

1. The tool name is stable, descriptive, and unique across tools, aliases, and category names.
2. The tool description tells the model when to call the tool, what it returns, and important constraints or side effects.
3. The method has exactly one trailing `CancellationToken`.
4. Every model-visible input has an unambiguous name, description, .NET type, and required/default behavior.
5. Input DTOs use public serializable properties and `ToolSchema` descriptions where the model needs help constructing them.
6. Result DTOs use `ToolResult` and explicit `JsonPropertyName` values when the model benefits from a self-describing response.
7. Instance tool types are registered in DI or handled by a custom `ToolTargetResolver`.
8. Cross-cutting categories are assigned on each `[Tool]` through `Categories`, not on the containing toolbox type.
9. The host scans only intended assemblies and resolves calls exclusively with the assigned `ToolSet.TryGetTool(...)`.
10. The host checks authorization and validates any domain-specific constraints before executing side effects.
11. The host returns a `FunctionCallOutputResponseItem` using the model call's `CallId`, then continues its Responses API loop.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE). See [LICENSE](LICENSE) for the complete terms.
