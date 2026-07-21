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

        Assert.True(catalog.TryGetTool("add_numbers", out GeneratedTool generatedTool));
        Assert.Contains(catalog.CreatePrimaryTools(), tool => ReferenceEquals(tool, generatedTool));
        Assert.Empty(generatedTool.CategoryNames);
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

    [Fact]
    public void Discovery_FindsIndependentToolCategoriesAcrossToolboxesAndNamespaces()
    {
        GeneratedToolCatalog catalog = ResponseToolDiscovery.CreateToolCatalog([typeof(ResponsesOnlyToolFixture).Assembly]);

        Assert.True(catalog.TryGetToolsForCategory("ImageToolsCategory", out IReadOnlyList<GeneratedTool> imageTools));
        Assert.Equal(2, imageTools.Count);
        Assert.Contains(imageTools, tool => tool.ResponseTool.FunctionName == "generate_image_for_category_test");
        Assert.Contains(imageTools, tool => tool.ResponseTool.FunctionName == "edit_image_for_category_test");
        Assert.Contains("ImageToolsCategory", catalog.CategoryNames);

        GeneratedTool generationTool = Assert.Single(imageTools, tool => tool.ResponseTool.FunctionName == "generate_image_for_category_test");
        Assert.Equal(["ImageToolsCategory", "ImageGenerationCategory"], generationTool.CategoryNames);

        Assert.True(catalog.TryGetCategory("image_generation_toolbox", out GeneratedToolCategory toolbox));
        ToolCategoryToolInfo categoryToolInfo = Assert.Single(ToolCategoryBuilder.Build(toolbox.CategoryTool), tool => tool.Name == "generate_image_for_category_test");
        Assert.Equal(generationTool.CategoryNames, categoryToolInfo.Categories);

        GeneratedTool[] editingTools = catalog.CreateToolsForCategory("ImageEditingCategory");
        GeneratedTool editingTool = Assert.Single(editingTools);
        Assert.Equal("edit_image_for_category_test", editingTool.ResponseTool.FunctionName);

        Assert.False(catalog.TryGetToolsForCategory("MissingCategory", out IReadOnlyList<GeneratedTool> missingTools));
        Assert.Empty(missingTools);
    }
}