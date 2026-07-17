# Repository Instructions

- 2026-07-17: Keep `TripleG3.AI.Tools.AutoGeneration` host-neutral. Its source, project metadata, documentation, public APIs, and generated messages must not contain application-specific names or behavior. Applications integrate through `GeneratedTool`, `GeneratedToolCatalog`, `ToolTargetResolver`, `IToolCatalogService`, `AddToolAssembly`, and `ChatToolDiscovery`.