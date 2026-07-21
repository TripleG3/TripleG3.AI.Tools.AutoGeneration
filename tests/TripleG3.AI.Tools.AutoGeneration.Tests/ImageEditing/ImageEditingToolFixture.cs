using TripleG3.AI.Tools.AutoGeneration;

namespace TripleG3.AI.Tools.AutoGeneration.Tests.ImageEditing;

public static class ImageEditingToolFixture
{
    [Tool(
        "edit_image_for_category_test",
        "Edits an existing image.",
        "Editing an image",
        Categories = ["ImageToolsCategory", "ImageEditingCategory"])]
    public static Task<string> EditAsync(string imageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(imageId);
    }
}