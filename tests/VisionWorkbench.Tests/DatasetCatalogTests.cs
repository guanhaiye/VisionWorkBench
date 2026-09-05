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
        File.WriteAllBytes(Path.Combine(imagesRoot, "two.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(imagesRoot, "three.png"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(imagesRoot, ".visionworkbench", "training-data", "run"));
        File.WriteAllBytes(Path.Combine(imagesRoot, ".visionworkbench", "training-data", "run", "one.png"), [1, 2, 3]);
        try
        {
            var service = new DatasetCatalogService(catalogRoot);
            var dataset = service.Save(new DatasetDefinition
            {
                Name = "demo",
                TaskType = "instance_segmentation",
                RootDirectory = imagesRoot,
                Classes = ["scratch"],
            });
            Assert.Equal("instance_segmentation", service.List().Single().TaskType);
            var images = service.ListImages(dataset);
            Assert.Equal(3, images.Count);
            Assert.All(images, image => Assert.Equal("unassigned", image.Split));
            var image = images[0];
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
            service.SaveAnnotation(dataset, new DatasetAnnotation
            {
                ImageRelativePath = "two.png",
                Objects =
                [new DatasetAnnotationObject
                {
                    ClassName = "scratch",
                    X = 0.2,
                    Y = 0.2,
                    Width = 0.2,
                    Height = 0.2,
                }],
            });

            Assert.Contains(service.ListImages(dataset), image => image.HasAnnotation);
            dataset = service.AutoSplit(dataset);
            var splitImages = service.ListImages(dataset);
            Assert.Equal(1, splitImages.Count(image => image.Split == "train"));
            Assert.Equal(1, splitImages.Count(image => image.Split == "val"));
            Assert.Equal("unassigned", splitImages.Single(image => image.RelativePath == "three.png").Split);
            service.ExportYolo(dataset, exportRoot);

            Assert.True(File.Exists(Path.Combine(exportRoot, "data.yaml")));
            var label = Directory.EnumerateFiles(Path.Combine(exportRoot, "labels"), "*.txt", SearchOption.AllDirectories)
                .First(path => File.ReadAllText(path).Contains("0 0.25 0.4 0.3 0.4", StringComparison.Ordinal));
            Assert.Contains("0 0.25 0.4 0.3 0.4", File.ReadAllText(label));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Exports_InstanceSegmentation_PolygonLabel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vw-seg-{Guid.NewGuid():N}");
        var catalogRoot = Path.Combine(root, "catalog");
        var exportRoot = Path.Combine(root, "export");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "sample.png"), [1, 2, 3]);
        try
        {
            var service = new DatasetCatalogService(catalogRoot);
            var dataset = service.Save(new DatasetDefinition
            {
                Name = "segmentation",
                TaskType = "instance_segmentation",
                RootDirectory = root,
                Classes = ["part"],
                ImageSplits = new Dictionary<string, string> { ["sample.png"] = "train" },
            });
            service.SaveAnnotation(dataset, new DatasetAnnotation
            {
                ImageRelativePath = "sample.png",
                Objects =
                [new DatasetAnnotationObject
                {
                    ClassName = "part",
                    Shape = "polygon",
                    Polygon =
                    [
                        new DatasetPoint { X = 0.1, Y = 0.2 },
                        new DatasetPoint { X = 0.6, Y = 0.2 },
                        new DatasetPoint { X = 0.6, Y = 0.8 },
                        new DatasetPoint { X = 0.1, Y = 0.8 },
                    ],
                }],
            });

            service.ExportYolo(dataset, exportRoot);
            var label = Directory.EnumerateFiles(Path.Combine(exportRoot, "labels"), "*.txt", SearchOption.AllDirectories).Single();
            Assert.Equal("0 0.1 0.2 0.6 0.2 0.6 0.8 0.1 0.8", File.ReadAllText(label).Trim());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
