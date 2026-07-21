using TripleG3.AI.Tools.AutoGeneration;

namespace TripleG3.AI.Tools.AutoGeneration.Tests;

public static class ResponsesOnlyToolFixture
{
    [Tool("add_numbers", "Adds two whole numbers.", "Adding numbers")]
    public static Task<int> AddAsync(int left, int right, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(left + right);
    }
}