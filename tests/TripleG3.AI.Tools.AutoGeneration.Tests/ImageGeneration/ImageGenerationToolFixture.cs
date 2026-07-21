using TripleG3.AI.Tools.AutoGeneration;

namespace TripleG3.AI.Tools.AutoGeneration.Tests.ImageGeneration;

[ToolCategory("image_generation_toolbox", "Provides image generation tools.")]
public static class ImageGenerationToolFixture
{
    [Tool(
        "generate_image_for_category_test",
        "Generates an image from a prompt.",
        "Generating an image",
        Categories = ["ImageToolsCategory", "ImageGenerationCategory"])]
    public static Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(prompt);
    }
}