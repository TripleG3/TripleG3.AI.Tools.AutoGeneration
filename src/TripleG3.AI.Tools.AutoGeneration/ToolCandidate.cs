namespace TripleG3.AI.Tools.AutoGeneration;

public enum ToolCandidateClassification
{
    Recommended,
    ReviewRequired,
    NotSuitable,
    AlreadyExposed
}

public sealed record ToolParameterCandidate(string Name, string TypeName, bool IsOptional);

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
