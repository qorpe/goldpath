using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// The tracing contract (H4, the <c>Goldpath.Bulk</c> activity source): every span over a
/// batch — validate, execute-range, replay-row — is LINKED to the upload request's trace, so
/// one trace id follows an instruction from the HTTP entry to its downstream effect, and
/// each span names the coordinates an operator searches by.
/// </summary>
public class BulkTracingTests : IDisposable
{
    private readonly BulkFixture _fixture = new(chunkSize: 2);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Every_span_over_a_batch_links_to_the_upload_trace_and_names_its_coordinates()
    {
        using var uploadSource = new ActivitySource("Goldpath.Bulk.Tests.Upload");
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Goldpath.Bulk" or "Goldpath.Bulk.Tests.Upload",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        GoldpathBulkBatch batch;
        ActivityTraceId uploadTrace;
        using (var scope = _fixture.Scope())
        {
            using (var upload = uploadSource.StartActivity("POST /goldpath/admin/bulk/batches/payments"))
            {
                uploadTrace = upload!.TraceId;
                (batch, _) = await _fixture.Engine.IngestAsync(scope.ServiceProvider, "payments",
                    BulkFixture.Csv(("E1", "TR1", "10", "FAIL"), ("E2", "TR2", "20", null)), "payments.csv", null, CancellationToken.None);
            }

            // Everything after the upload runs OUTSIDE its trace (the jobs runner's own spans).
            await _fixture.Engine.ValidateBatchAsync(scope.ServiceProvider, batch.Id, CancellationToken.None);
            Assert.True((await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "treasurer", null, CancellationToken.None)).Ok);
            await _fixture.Engine.AdoptForExecutionAsync(scope.ServiceProvider, Guid.NewGuid(), CancellationToken.None);
            await _fixture.Engine.ExecuteRangeAsync(scope.ServiceProvider,
                BulkFixture.MakeChunk(0, $"{batch.Id:N}|1:3"), batch.Id, 1, 3, CancellationToken.None);
        }

        _fixture.Mutate(db =>
        {
            var row = db.Set<GoldpathBulkRow>().Single(r => r.BatchId == batch.Id && r.RowNumber == 1);
            row.Payload = row.Payload.Replace("FAIL", "ok", StringComparison.Ordinal);
        });
        using (var scope = _fixture.Scope())
        {
            await _fixture.Engine.ReplayRowAsync(scope.ServiceProvider,
                GoldpathBulkEngine<BulkTestContext>.ItemKey(batch.Id, 1), CancellationToken.None);
        }

        var linked = stopped.Where(a => a.Links.Any(l => l.Context.TraceId == uploadTrace)).ToList();
        Assert.All(linked, span => Assert.Equal("Goldpath.Bulk", span.Source.Name));

        var validate = Assert.Single(linked, a => a.OperationName == "goldpath.bulk.validate");
        Assert.Equal(batch.Id, validate.GetTagItem("goldpath.batch_id"));
        Assert.Equal("payments", validate.GetTagItem("goldpath.definition"));

        var execute = Assert.Single(linked, a => a.OperationName == "goldpath.bulk.execute-range");
        Assert.Equal(batch.Id, execute.GetTagItem("goldpath.batch_id"));
        Assert.Equal("payments", execute.GetTagItem("goldpath.definition"));
        Assert.Equal("1:3", execute.GetTagItem("goldpath.range"));

        var replay = Assert.Single(linked, a => a.OperationName == "goldpath.bulk.replay-row");
        Assert.Equal(batch.Id, replay.GetTagItem("goldpath.batch_id"));
        Assert.Equal(1, replay.GetTagItem("goldpath.row"));
    }
}
