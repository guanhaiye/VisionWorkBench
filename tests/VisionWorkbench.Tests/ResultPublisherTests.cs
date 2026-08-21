using VisionWorkbench.Application;
using VisionWorkbench.Contracts.Results;
using Xunit;

namespace VisionWorkbench.Tests;

public sealed class ResultPublisherTests
{
    [Fact]
    public async Task Publishes_Envelope_To_Jsonl_With_Stable_Source_Identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionWorkbenchTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "results.jsonl");
        try
        {
            await using (var publisher = new ResultPublisher(path))
            {
                await publisher.PublishAsync(new ResultEnvelope
                {
                    ProjectId = "project-a",
                    StationCode = "ST-001",
                    TaskId = 7,
                    RecordId = 8,
                    Output = new AlgorithmOutput
                    {
                        OutputId = "o1",
                        InputId = "i1",
                    },
                });
            }

            var line = await File.ReadAllTextAsync(path);
            Assert.Contains("project-a", line);
            Assert.Contains("ST-001", line);
            Assert.Contains("\"recordId\":8", line);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
