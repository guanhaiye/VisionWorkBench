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

    [Fact]
    public async Task Product_ResultId_Is_Idempotent_In_Local_Jsonl()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionWorkbenchTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "results.jsonl");
        try
        {
            var envelope = new ProductResultEnvelope
            {
                ResultId = "sop-product:42:final",
                ProjectId = "project-a",
                StationCode = "ST-001",
                TaskId = 7,
                SopRunId = 42,
                CycleId = "cycle-42",
                Decision = new DecisionResult { Status = DecisionStatus.Ng },
                WorkflowResultJson = "{}",
            };
            await using (var publisher = new ResultPublisher(path))
            {
                await publisher.PublishProductAsync(envelope);
                await publisher.PublishProductAsync(envelope);
            }

            Assert.Single(await File.ReadAllLinesAsync(path));
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
