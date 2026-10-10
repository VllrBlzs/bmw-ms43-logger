using System.Diagnostics;
using System.Text.Json;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.LivePlotProbe;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            DeterministicResult deterministic = RunDeterministicChecks();
            RuntimeResult runtime = await RunRuntimeRoundTripAsync();
            string reportPath = WriteReport(deterministic, runtime);

            Console.WriteLine("BMW-12 OFFLINE BOUNDED LIVE PLOT DRY RUN");
            Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
            Console.WriteLine("INTERFACE_INSTANCE=None");
            Console.WriteLine("CHANNEL_LIMIT=PASS");
            Console.WriteLine("BOUNDED_HISTORY=PASS");
            Console.WriteLine("ACQUISITION_TIME_AXIS=PASS");
            Console.WriteLine("QUALITY_GAPS=PASS");
            Console.WriteLine("NON_NUMERIC_GAP=PASS");
            Console.WriteLine("GUI_REDRAW_DOES_NOT_POLL=PASS");
            Console.WriteLine("CSV_SOURCE_ALIGNMENT=PASS");
            Console.WriteLine($"MAX_CHANNELS={LivePlotHistory.MaximumChannels}");
            Console.WriteLine($"MAX_POINTS_PER_SERIES={deterministic.MaximumPoints}");
            Console.WriteLine($"PUBLISHED_CYCLES={deterministic.PublishedCycles}");
            Console.WriteLine($"GUI_SNAPSHOT_READS={deterministic.SnapshotReads}");
            Console.WriteLine(
                $"SNAPSHOT_READ_MEAN_US={deterministic.SnapshotReadMeanMicroseconds:F2}");
            Console.WriteLine($"RUNTIME_CYCLES={runtime.Cycles}");
            Console.WriteLine($"RUNTIME_SAMPLES={runtime.Samples}");
            Console.WriteLine($"RUNTIME_PLOT_CHANNELS={runtime.PlotChannels}");
            Console.WriteLine($"REPORT={reportPath}");
            Console.WriteLine("RESULT=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.WriteLine("RESULT=FAIL");
            return 1;
        }
    }

    private static DeterministicResult RunDeterministicChecks()
    {
        ParameterId[] threeChannels =
        [
            Id("engine.rpm"),
            Id("vehicle.speed"),
            Id("engine.coolant.temperature")
        ];
        ParameterId[] sixChannels = Ms43ParameterCatalog.ApprovedParameters
            .Take(LivePlotHistory.MaximumChannels + 1)
            .ToArray();
        var history = new LivePlotHistory();
        history.Reset(threeChannels);
        ExpectThrows(() => history.Reset(sixChannels), "SIX_CHANNEL_REJECTION");
        history.Reset(threeChannels);

        Guid epoch = Guid.NewGuid();
        DateTimeOffset baseline = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
        for (long cycleId = 1; cycleId <= 700; cycleId++)
        {
            DateTimeOffset acquired = baseline.AddMilliseconds(cycleId * 200.0);
            DateTimeOffset completed = acquired.AddMilliseconds(50.0);
            history.Publish(Cycle(
                epoch,
                cycleId,
                completed,
                Valid(epoch, cycleId, threeChannels[0], acquired, 800.0 + cycleId),
                Valid(epoch, cycleId, threeChannels[1], acquired, cycleId / 10.0),
                Valid(epoch, cycleId, threeChannels[2], acquired, 90.0)));
        }

        const int snapshotReadCount = 1000;
        LivePlotSnapshot? lastSnapshot = null;
        long snapshotReadStarted = Stopwatch.GetTimestamp();
        for (int read = 0; read < snapshotReadCount; read++)
        {
            lastSnapshot = history.ReadSnapshot();
        }
        double snapshotReadMeanMicroseconds =
            Stopwatch.GetElapsedTime(snapshotReadStarted).TotalMilliseconds * 1000.0 /
            snapshotReadCount;
        LivePlotSnapshot bounded = lastSnapshot ?? throw new InvalidOperationException(
            "The bounded history returned no snapshot.");
        Require(history.PublishedCycles == 700, "PUBLISHED_CYCLE_COUNT");
        Require(history.SnapshotReads == snapshotReadCount, "SNAPSHOT_READ_COUNT");
        Require(
            double.IsFinite(snapshotReadMeanMicroseconds) &&
            snapshotReadMeanMicroseconds >= 0.0,
            "SNAPSHOT_READ_MEASUREMENT");
        Require(bounded.Series.Count == 3, "BOUNDED_SERIES_COUNT");
        foreach (LivePlotSeries series in bounded.Series)
        {
            Require(
                series.Points.Count == LivePlotHistory.DefaultMaximumPointsPerSeries,
                $"BOUNDED_POINTS_{series.ParameterId.Value}");
        }
        LivePlotPoint lastRpm = bounded.Series[0].Points[^1];
        DateTimeOffset expectedAcquired = baseline.AddMilliseconds(700 * 200.0);
        Require(lastRpm.UsesAcquisitionTimestamp, "ACQUISITION_TIMESTAMP_FLAG");
        Require(lastRpm.PlotTimeUtc == expectedAcquired, "ACQUISITION_TIMESTAMP_VALUE");

        var gapHistory = new LivePlotHistory(
            TimeSpan.FromMinutes(5),
            maximumPointsPerSeries: 20);
        ParameterId rpm = Id("engine.rpm");
        gapHistory.Reset([rpm]);
        gapHistory.Publish(Cycle(
            epoch,
            1,
            baseline.AddSeconds(1),
            Valid(epoch, 1, rpm, baseline.AddSeconds(0.9), 800.0)));
        gapHistory.Publish(Cycle(
            epoch,
            2,
            baseline.AddSeconds(2),
            Valid(epoch, 2, rpm, baseline.AddSeconds(1.9), 810.0)));
        gapHistory.Publish(Cycle(
            epoch,
            3,
            baseline.AddSeconds(3),
            Unavailable(epoch, 3, rpm, SampleQuality.Missing, "NO_RESULT")));
        gapHistory.Publish(Cycle(
            epoch,
            4,
            baseline.AddSeconds(4),
            Valid(epoch, 4, rpm, baseline.AddSeconds(3.9), 820.0)));
        gapHistory.Publish(Cycle(
            epoch,
            5,
            baseline.AddSeconds(5),
            Unavailable(epoch, 5, rpm, SampleQuality.Timeout, "TIMEOUT")));
        gapHistory.Publish(Cycle(
            epoch,
            6,
            baseline.AddSeconds(6),
            Valid(epoch, 6, rpm, baseline.AddSeconds(5.9), 830.0)));
        gapHistory.Publish(Cycle(
            epoch,
            7,
            baseline.AddSeconds(7),
            Valid(epoch, 7, rpm, baseline.AddSeconds(6.9), 840.0)));

        LivePlotSeries gapSeries = gapHistory.ReadSnapshot().Series.Single();
        IReadOnlyList<IReadOnlyList<LivePlotPoint>> segments =
            LivePlotSegmenter.CreateValidSegments(gapSeries.Points);
        Require(segments.Count == 3, "GAP_SEGMENT_COUNT");
        Require(
            segments.Select(segment => segment.Count).SequenceEqual(new[] { 2, 1, 2 }),
            "GAP_SEGMENT_SHAPE");

        var nonNumericHistory = new LivePlotHistory();
        nonNumericHistory.Reset([rpm]);
        nonNumericHistory.Publish(Cycle(
            epoch,
            1,
            baseline.AddSeconds(1),
            Valid(epoch, 1, rpm, baseline.AddSeconds(0.9), "not-a-number")));
        LivePlotPoint nonNumeric = nonNumericHistory
            .ReadSnapshot()
            .Series.Single()
            .Points.Single();
        Require(nonNumeric.Quality == SampleQuality.Error, "NON_NUMERIC_QUALITY");
        Require(!nonNumeric.Value.HasValue, "NON_NUMERIC_VALUE");
        Require(nonNumeric.ErrorCode == "NON_NUMERIC_PLOT_VALUE", "NON_NUMERIC_CODE");

        return new DeterministicResult(
            history.PublishedCycles,
            history.SnapshotReads,
            bounded.MaximumPointsPerSeries,
            snapshotReadMeanMicroseconds);
    }

    private static async Task<RuntimeResult> RunRuntimeRoundTripAsync()
    {
        ParameterId[] channels = Ms43ParameterCatalog.DefaultSelection.ToArray();
        var plotHistory = new LivePlotHistory();
        plotHistory.Reset(channels);
        string outputRoot = Path.GetFullPath(Path.Combine("artifacts", "bmw12"));
        Directory.CreateDirectory(outputRoot);

        await using var runtime = new SimulatedLoggerRuntime();
        runtime.ProgressChanged += (_, progress) =>
            plotHistory.Publish(progress.Cycle);
        await runtime.ConnectAsync();
        _ = await runtime.StartAsync(outputRoot, 5.0, channels);

        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            while (plotHistory.PublishedCycles < 6)
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        SimulatedLoggerSessionResult result = await runtime.StopAsync();
        await runtime.DisconnectAsync();
        LivePlotSnapshot snapshot = plotHistory.ReadSnapshot();

        Require(snapshot.Series.Count == channels.Length, "RUNTIME_PLOT_CHANNEL_COUNT");
        Require(snapshot.PublishedCycles == result.CyclesWritten,
            "RUNTIME_PLOT_CYCLE_ALIGNMENT");
        Require(
            result.SamplesWritten == result.CyclesWritten * channels.Length,
            "RUNTIME_CSV_SAMPLE_ALIGNMENT");
        foreach (LivePlotSeries series in snapshot.Series)
        {
            Require(series.Points.Count == result.CyclesWritten,
                $"RUNTIME_SERIES_LENGTH_{series.ParameterId.Value}");
            Require(series.Points.All(point =>
                    point.Quality == SampleQuality.Valid &&
                    point.Value.HasValue &&
                    point.UsesAcquisitionTimestamp),
                $"RUNTIME_SERIES_QUALITY_{series.ParameterId.Value}");
        }

        return new RuntimeResult(
            result.CyclesWritten,
            result.SamplesWritten,
            snapshot.Series.Count,
            result.RunDirectory);
    }

    private static string WriteReport(
        DeterministicResult deterministic,
        RuntimeResult runtime)
    {
        string path = Path.GetFullPath(Path.Combine(
            "artifacts",
            "bmw12",
            "bmw12-bounded-live-plot-dryrun.json"));
        var report = new
        {
            schema = "bmw12.bounded_live_plot_dryrun.v1",
            generated_utc = DateTimeOffset.UtcNow,
            communication_attempted = false,
            interface_instance = "none",
            maximum_channels = LivePlotHistory.MaximumChannels,
            time_window_seconds = LivePlotHistory.DefaultTimeWindow.TotalSeconds,
            checks = new
            {
                channel_limit = "pass",
                bounded_history = "pass",
                acquisition_time_axis = "pass",
                quality_gaps = "pass",
                non_numeric_gap = "pass",
                gui_redraw_does_not_poll = "pass",
                csv_source_alignment = "pass"
            },
            deterministic.PublishedCycles,
            deterministic.SnapshotReads,
            deterministic.MaximumPoints,
            deterministic.SnapshotReadMeanMicroseconds,
            runtime.Cycles,
            runtime.Samples,
            runtime.PlotChannels,
            runtime.RunDirectory
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static NormalizedCycle Cycle(
        Guid epoch,
        long cycleId,
        DateTimeOffset completedUtc,
        params NormalizedSample[] samples) =>
        new(
            epoch,
            cycleId,
            completedUtc,
            Stopwatch.GetTimestamp(),
            samples);

    private static NormalizedSample Valid(
        Guid epoch,
        long cycleId,
        ParameterId parameterId,
        DateTimeOffset acquiredUtc,
        object value)
    {
        LoggerParameterDefinition definition =
            Ms43ParameterCatalog.Resolve(parameterId);
        long ticks = Stopwatch.GetTimestamp();
        return new NormalizedSample(
            epoch,
            cycleId,
            parameterId,
            1,
            definition.SourceJob,
            acquiredUtc.AddMilliseconds(-20),
            acquiredUtc,
            acquiredUtc,
            ticks,
            value,
            definition.Unit,
            SampleQuality.Valid,
            null,
            20.0,
            20.0,
            0.0);
    }

    private static NormalizedSample Unavailable(
        Guid epoch,
        long cycleId,
        ParameterId parameterId,
        SampleQuality quality,
        string errorCode)
    {
        LoggerParameterDefinition definition =
            Ms43ParameterCatalog.Resolve(parameterId);
        return new NormalizedSample(
            epoch,
            cycleId,
            parameterId,
            1,
            definition.SourceJob,
            null,
            null,
            null,
            null,
            null,
            null,
            quality,
            errorCode,
            null,
            null,
            null);
    }

    private static ParameterId Id(string value) => new(value);

    private static void ExpectThrows(Action action, string code)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"BMW12_ASSERTION_FAILED={code}");
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"BMW12_ASSERTION_FAILED={code}");
        }
    }

    private sealed record DeterministicResult(
        long PublishedCycles,
        long SnapshotReads,
        int MaximumPoints,
        double SnapshotReadMeanMicroseconds);

    private sealed record RuntimeResult(
        long Cycles,
        long Samples,
        int PlotChannels,
        string RunDirectory);
}
