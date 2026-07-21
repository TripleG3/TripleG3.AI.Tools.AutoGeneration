using System.Text.Json;
using OpenAI.Responses;
using TripleG3.AI.Tools.AutoGeneration;

namespace TripleG3.AI.Tools.AutoGeneration.Tests;

public sealed class ResponseToolDiscoveryTests
{
    [Fact]
    public async Task DiscoveryAndInvocation_CreateResponsesApiFunctionItems()
    {
        GeneratedToolCatalog catalog = ResponseToolDiscovery.CreateToolCatalog([typeof(ResponsesOnlyToolFixture).Assembly]);

        GeneratedTool generatedTool = Assert.Single(catalog.CreatePrimaryTools());
        FunctionTool functionTool = generatedTool.ResponseTool;

        Assert.Equal("add_numbers", functionTool.FunctionName);
        Assert.Equal("Adds two whole numbers.", functionTool.FunctionDescription);

        using JsonDocument parameters = JsonDocument.Parse(functionTool.FunctionParameters.ToString());
        JsonElement root = parameters.RootElement;
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.True(root.GetProperty("properties").TryGetProperty("left", out _));
        Assert.True(root.GetProperty("properties").TryGetProperty("right", out _));
        Assert.False(root.GetProperty("properties").TryGetProperty("cancellationToken", out _));

        CreateResponseOptions initialRequest = new()
        {
            Model = "gpt-5.1"
        };
        initialRequest.Tools.Add(functionTool);
        Assert.Same(functionTool, Assert.Single(initialRequest.Tools));

        FunctionCallResponseItem functionCall = ResponseItem.CreateFunctionCallItem(
            callId: "call_add_numbers",
            functionName: functionTool.FunctionName,
            functionArguments: BinaryData.FromString("{\"left\":2,\"right\":3}"));

        FunctionCallOutputResponseItem functionOutput = await generatedTool.InvokeAndCreateFunctionCallOutputAsync(functionCall);

        Assert.Equal(functionCall.CallId, functionOutput.CallId);
        Assert.Equal("5", functionOutput.FunctionOutput);

        CreateResponseOptions continuationRequest = new()
        {
            Model = "gpt-5.1",
            PreviousResponseId = "resp_example"
        };
        continuationRequest.InputItems.Add(functionOutput);
        Assert.Same(functionOutput, Assert.Single(continuationRequest.InputItems));
    }
}