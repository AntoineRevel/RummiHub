using System.Diagnostics;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using RummiHub.Solver.Worker;
using StackExchange.Redis;

namespace RummiHub.Tests.Worker;

/// <summary>
/// Worker traces: the polling commands stay out of the export, and a solve span joins the
/// trace of the API request that enqueued it.
/// </summary>
public class SolveTracingTests
{
    [Fact]
    public void RedisPollingFilter_DropsPollingCommands_KeepsTheOthers()
    {
        var exported = new List<string>();
        using var redisSource = new ActivitySource(RedisPollingFilter.RedisSourceName);
        using var otherSource = new ActivitySource("not-redis");

        // Export registered before the filter: the decision must not depend on that order.
        using (Sdk.CreateTracerProviderBuilder()
                   .AddSource(RedisPollingFilter.RedisSourceName, "not-redis")
                   .AddProcessor(new SimpleActivityExportProcessor(new NameExporter(exported)))
                   .AddProcessor(new RedisPollingFilter())
                   .Build())
        {
            foreach (var command in new[] { "XREADGROUP", "XAUTOCLAIM", "JSON.SET", "XACK" })
                redisSource.StartActivity(command, ActivityKind.Client)?.Dispose();

            otherSource.StartActivity("XREADGROUP")?.Dispose();
        }

        exported.Should().Equal("JSON.SET", "XACK", "XREADGROUP");
    }

    [Fact]
    public void StartProcessActivity_JoinsTheTraceOfTheEnqueuingRequest()
    {
        using var listener = ListenToWorkerSource();
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var entry = new StreamEntry("1-0", [
            new NameValueEntry("contentHash", "abc"),
            new NameValueEntry("traceparent", $"00-{traceId}-{spanId}-01")
        ]);

        using var activity = SolverWorker.StartProcessActivity("solve-stream", entry);

        activity.Should().NotBeNull();
        activity!.TraceId.Should().Be(traceId);
        activity.ParentSpanId.Should().Be(spanId);
        activity.Kind.Should().Be(ActivityKind.Consumer);
        activity.OperationName.Should().Be("process solve-stream");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-traceparent")]
    public void StartProcessActivity_WithoutAReadableTraceparent_StartsItsOwnTrace(string? traceparent)
    {
        using var listener = ListenToWorkerSource();
        NameValueEntry[] values = traceparent is null
            ? [new NameValueEntry("contentHash", "abc")]
            : [new NameValueEntry("contentHash", "abc"), new NameValueEntry("traceparent", traceparent)];

        using var activity = SolverWorker.StartProcessActivity("solve-stream", new StreamEntry("1-0", values));

        activity.Should().NotBeNull();
        activity!.ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    private static ActivityListener ListenToWorkerSource()
    {
        // Read before the listener exists: the first read creates the source, and a source
        // created while the listener is being added would be tested against a null field.
        var workerSource = SolverWorker.ActivitySource;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source == workerSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class NameExporter(List<string> names) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                names.Add(activity.OperationName);
            return ExportResult.Success;
        }
    }
}
