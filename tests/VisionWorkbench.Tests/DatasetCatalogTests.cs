using VisionWorkbench.Application;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class DatasetCatalogTests
{
    [Fact]
    public void Saves_Annotations_Lists_Images_And_Exports_Yolo_Files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-dataset-{Guid.NewGuid():N}");
        var catalogRoot = Path.Combine(root, "catalog");
        var imagesRoot = Path.Combine(root, "images");
        var exportRoot = Path.Combine(root, "export");
        Directory.CreateDirectory(imagesRoot);
        File.WriteAllBytes(Path.Combine(imagesRoot, "one.png"), [1, 2, 3]);
        try
        {
            var service = new DatasetCatalogService(catalogRoot);
            var dataset = service.Save(new DatasetDefinition
            {
                Name = "demo",
                RootDirectory = imagesRoot,
                Classes = ["scratch"],
            });
            var image = Assert.Single(service.ListImages(dataset));
            service.SaveAnnotation(dataset, new DatasetAnnotation
            {
                ImageRelativePath = image.RelativePath,
                Objects =
                [new DatasetAnnotationObject
                {
                    ClassName = "scratch",
                    X = 0.1,
                    Y = 0.2,
                    Width = 0.3,
                    Height = 0.4,
                }],
            });

            Assert.True(service.ListImages(dataset).Single().HasAnnotation);
            service.ExportYolo(dataset, exportRoot);

            Assert.True(File.Exists(Path.Combine(exportRoot, "data.yaml")));
            var label = Directory.EnumerateFiles(Path.Combine(exportRoot, "labels"), "*.txt", SearchOption.AllDirectories).Single();
            Assert.Contains("0 0.25 0.4 0.3 0.4", File.ReadAllText(label));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
