# TripleG3.AI.Tools.AutoGeneration

Host-neutral reflection infrastructure for exposing annotated .NET methods as OpenAI function tools.

The project owns reusable tool attributes, OpenAI definitions, schema/result generation, argument binding, category generation, reflected invocation, catalogs, and target resolution. It does not reference application-specific projects or include application-specific tools.

Use `TripleG3.AI.Tools.AutoGeneration` and scan explicitly supplied assemblies with `ChatToolDiscovery.CreateToolCatalog(...)`.
