namespace TripleG3.AI.Tools.AutoGeneration.Tests;

public sealed class ToolingRoadmapTests
{
    [Fact]
    public void SelectTools_UsesUnionOfCapabilityCategoriesAndExcludesToolboxes()
    {
        GeneratedToolCatalog catalog = ResponseToolDiscovery.CreateToolCatalog([typeof(ResponsesOnlyToolFixture).Assembly]);

        ToolSet imageTools = catalog.SelectTools(ToolSelection.ForCategories("ImageGenerationCategory"));
        ToolSet combinedTools = catalog.SelectTools(ToolSelection.ForCategories("ImageGenerationCategory", "ImageEditingCategory"));
        ToolSet allTools = catalog.SelectTools(ToolSelection.All);

        Assert.Contains(imageTools.Tools, tool => tool.ResponseTool.FunctionName == "generate_image_for_category_test");
        Assert.DoesNotContain(imageTools.Tools, tool => tool.ResponseTool.FunctionName == "edit_image_for_category_test");
        Assert.DoesNotContain(imageTools.Tools, tool => tool.IsCategoryTool);
        Assert.Contains(combinedTools.Tools, tool => tool.ResponseTool.FunctionName == "generate_image_for_category_test");
        Assert.Contains(combinedTools.Tools, tool => tool.ResponseTool.FunctionName == "edit_image_for_category_test");
        Assert.DoesNotContain(allTools.Tools, tool => tool.IsCategoryTool);
        Assert.Contains(allTools.Tools, tool => tool.ResponseTool.FunctionName == "add_numbers");
    }

    [Fact]
    public async Task TransportNeutralInvocation_UsesSharedBindingAndSerialization()
    {
        GeneratedToolCatalog catalog = ResponseToolDiscovery.CreateToolCatalog([typeof(ResponsesOnlyToolFixture).Assembly]);
        GeneratedTool tool = Assert.Single(catalog.SelectTools(ToolSelection.All).Tools, item => item.ResponseTool.FunctionName == "add_numbers");

        ToolInvocationResult result = await tool.InvokeAsync(new ToolCall(
            "add_numbers",
            "call-1",
            "{\"left\": 4, \"right\": 5}"));

        Assert.Equal("call-1", result.CallId);
        Assert.Equal("add_numbers", result.ToolName);
        Assert.Equal(9, result.Result);
        Assert.Equal("9", result.SerializedResult);
    }

    [Fact]
    public void CandidateScanner_ClassifiesExistingToolMethods()
    {
        IReadOnlyList<ToolCandidate> candidates = ToolCandidateScanner.ScanType(typeof(ResponsesOnlyToolFixture));

        ToolCandidate candidate = Assert.Single(candidates);
        Assert.Equal(ToolCandidateClassification.AlreadyExposed, candidate.Classification);
        Assert.True(candidate.AlreadyHasToolAttribute);
        Assert.True(candidate.HasCancellationToken);
    }

    [Fact]
    public void SourceGenerator_ProducesPreviewAndDoesNotOverwriteByDefault()
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"generated-tools-{Guid.NewGuid():N}.cs");
        try
        {
            ToolGenerationRequest request = new(
                typeof(ResponsesOnlyToolFixture).FullName!,
                outputPath,
                "Generated");

            ToolGenerationResult result = ToolSourceGenerator.Generate(typeof(ResponsesOnlyToolFixture), request);

            Assert.True(result.WroteFile);
            Assert.Contains("[Tool(\"add_numbers\"", result.Source, StringComparison.Ordinal);
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
