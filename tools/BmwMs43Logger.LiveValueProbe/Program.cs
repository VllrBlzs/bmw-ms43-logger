using System.Diagnostics;
using System.Text.Json;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.LiveValueProbe;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            DeterministicResult deterministic = RunDeterministicChecks();
            RuntimeResult runtime = await RunRuntimeRoundTripAsync();
            string reportPath = WriteReport(deterministic, runtime);

            Console.WriteLine("BMW-11 OFFLINE LIVE VALUE DISPLAY DRY RUN");
            Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
            Console.WriteLine("INTERFACE_INSTANCE=None");
            Console.WriteLine("VALID_FORMATTING=PASS");
            Console.WriteLine("UNIT_AND_TIMESTAMP=PASS");
            Console.WriteLine("STALE_TRANSITION=PASS");
            Console.WriteLine("MISSING_TIMEOUT_ERROR=PASS");
            Console.WriteLine("NON_NUMERIC_FORMATTING=PASS");
            Console.WriteLine("LATEST_FRAME_BUFFER=PASS");
            Console.WriteLine("GUI_READ_DOES_NOT_POLL=PASS");
            Console.WriteLine("SIMULATED_RUNTIME_ROUNDTRIP=PASS");
            Console.WriteLine($"PUBLISHED_FRAMES={deterministic.PublishedFrames}");
            Console.WriteLine($"GUI_SNAPSHOT_READS={deterministic.SnapshotReads}");
            Console.WriteLine($"RUNTIME_CYCLES={runtime.Cycles}");
            Console.WriteLine($"RUNTIME_SAMPLES={runtime.Samples}");
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
        DateTimeOffset acquiredUtc = new(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        long acquiredTicks = Stopwatch.GetTimestamp();
        Guid epoch = Guid.NewGuid();
        LoggerParameterDefinition rpm = Resolve("engine.rpm");
        var buffer = new LatestLiveValueBuffer();

        buffer.Publish(Cycle(
            epoch,
            1,
            acquiredUtc.AddMilliseconds(45.0),
            acquiredTicks + Ticks(45.0),
            ValidSample(
                epoch,
                1,
                rpm,
                acquiredUtc,
                acquiredTicks,
                812.4)));

        LiveValueFrame frame = buffer.ReadLatest() ?? throw new InvalidOperationException(
            "The live-value buffer did not retain its first frame.");
        LiveValueObservation rpmObservation = frame.Values[rpm.ParameterId.Value];
        TimeSpan staleAfter = LiveValuePresenter.StaleAfterForHz(5.0);
        LiveValuePresentation valid = LiveValuePresenter.Project(
            rpm,
            rpmObservation,
            acquiredTicks + Ticks(100.0),
            staleAfter);
        Require(valid.EffectiveQuality == SampleQuality.Valid, "VALID_QUALITY");
        Require(valid.ValueText == "812", "RPM_FORMAT");
        Require(valid.UnitText == "1/min", "RPM_UNIT");
        Require(valid.AcquiredAtText.EndsWith("UTC", StringComparison.Ordinal),
            "UTC_TIMESTAMP");
        Require(valid.AgeMilliseconds is >= 99.0 and <= 101.0, "MONOTONIC_AGE");

        LiveValuePresentation stale = LiveValuePresenter.Project(
            rpm,
            rpmObservation,
            acquiredTicks + Ticks(staleAfter.TotalMilliseconds + 1.0),
            staleAfter);
        Require(stale.EffectiveQuality == SampleQuality.Stale, "STALE_TRANSITION");
        Require(stale.ValueText == "812", "STALE_RETAINS_LAST_VALUE");
        Require(stale.ErrorCode == "AGE_LIMIT_EXCEEDED", "STALE_REASON");

        LoggerParameterDefinition speed = Resolve("vehicle.speed");
        LoggerParameterDefinition coolant = Resolve("engine.coolant.temperature");
        LoggerParameterDefinition battery = Resolve("electrical.battery.voltage");
        buffer.Reset();
        buffer.Publish(Cycle(
            epoch,
            2,
            acquiredUtc.AddSeconds(1),
            acquiredTicks + Ticks(1000.0),
            UnavailableSample(epoch, 2, speed, SampleQuality.Missing, "NO_RESULT"),
            UnavailableSample(epoch, 2, coolant, SampleQuality.Timeout, "TIMEOUT"),
            UnavailableSample(epoch, 2, battery, SampleQuality.Error, "EDIABAS_ERROR")));
        LiveValueFrame unavailableFrame = buffer.ReadLatest() ??
            throw new InvalidOperationException("The unavailable frame was not retained.");
        RequireQuality(unavailableFrame, speed, SampleQuality.Missing, staleAfter);
        RequireQuality(unavailableFrame, coolant, SampleQuality.Timeout, staleAfter);
        RequireQuality(unavailableFrame, battery, SampleQuality.Error, staleAfter);

        var booleanDefinition = new LoggerParameterDefinition(
            new ParameterId("test.switch.state"),
            "TestSwitchState",
            "Tesztkapcsoló",
            "-",
            "boolean",
            Ms43ParameterCatalog.ApprovedLiveObserved,
            "TEST_READ_ONLY");
        var stringDefinition = new LoggerParameterDefinition(
            new ParameterId("test.mode.name"),
            "TestModeName",
            "Tesztmód",
            "-",
            "string",
            Ms43ParameterCatalog.ApprovedLiveObserved,
            "TEST_READ_ONLY");
        Require(
            LiveValuePresenter.TryFormatValue(
                booleanDefinition,
                true,
                out string booleanText) && booleanText == "ON",
            "BOOLEAN_FORMAT");
        Require(
            LiveValuePresenter.TryFormatValue(
                stringDefinition,
                " closed-loop ",
                out string stringText) && stringText == "closed-loop",
            "STRING_FORMAT");

        var isolationBuffer = new LatestLiveValueBuffer();
        for (long cycleId = 1; cycleId <= 3; cycleId++)
        {
            long tick = acquiredTicks + Ticks(cycleId * 200.0);
            isolationBuffer.Publish(Cycle(
                epoch,
                cycleId,
                acquiredUtc.AddMilliseconds(cycleId * 200.0),
                tick,
                ValidSample(
                    epoch,
                    cycleId,
                    rpm,
                    acquiredUtc.AddMilliseconds(cycleId * 200.0 - 20.0),
                    tick - Ticks(20.0),
                    800.0 + cycleId)));
        }
        LiveValueFrame? latestIsolation = null;
        for (int read = 0; read < 1000; read++)
        {
            latestIsolation = isolationBuffer.ReadLatest();
        }
        Require(isolationBuffer.PublishedFrames == 3, "PUBLISH_COUNT");
        Require(isolationBuffer.SnapshotReads == 1000, "READ_COUNT");
        Require(
            latestIsolation?.CycleId == 3,
            "LATEST_FRAME_WINS");
        Require(isolationBuffer.PublishedFrames == 3, "READ_DID_NOT_PUBLISH");

        return new DeterministicResult(
            isolationBuffer.PublishedFrames,
            isolationBuffer.SnapshotReads);
    }

    private static async Task<RuntimeResult> RunRuntimeRoundTripAsync()
    {
        string outputRoot = Path.GetFullPath(Path.Combine("artifacts", "bmw11"));
        Directory.CreateDirectory(outputRoot);
        var buffer = new LatestLiveValueBuffer();
        await using var runtime = new SimulatedLoggerRuntime();
        runtime.ProgressChanged += (_, progress) => buffer.Publish(progress.Cycle);

        await runtime.ConnectAsync();
        string runDirectory = await runtime.StartAsync(
            outputRoot,
            5.0,
            Ms43ParameterCatalog.DefaultSelection);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            while (buffer.PublishedFrames < 5)
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        SimulatedLoggerSessionResult result = await runtime.StopAsync();
        await runtime.DisconnectAsync();

        LiveValueFrame latest = buffer.ReadLatest() ?? throw new InvalidOperationException(
            "The simulated runtime produced no live-value frame.");
        Require(latest.Values.Count == Ms43ParameterCatalog.DefaultSelection.Count,
            "RUNTIME_SELECTION_COUNT");
        Require(result.CyclesWritten >= 5, "RUNTIME_CYCLE_COUNT");
        Require(
            result.SamplesWritten == result.CyclesWritten *
                Ms43ParameterCatalog.DefaultSelection.Count,
            "RUNTIME_SAMPLE_COUNT");
        Require(Directory.Exists(runDirectory), "RUNTIME_DIRECTORY");
        Require(File.Exists(result.CsvPath), "RUNTIME_CSV");
        Require(File.Exists(result.MetadataPath), "RUNTIME_METADATA");

        long nowTicks = Stopwatch.GetTimestamp();
        TimeSpan staleAfter = LiveValuePresenter.StaleAfterForHz(5.0);
        foreach (ParameterId parameterId in Ms43ParameterCatalog.DefaultSelection)
        {
            LoggerParameterDefinition definition =
                Ms43ParameterCatalog.Resolve(parameterId);
            LiveValuePresentation presentation = LiveValuePresenter.Project(
                definition,
                latest.Values[parameterId.Value],
                nowTicks,
                staleAfter);
            Require(
                presentation.EffectiveQuality == SampleQuality.Valid,
                $"RUNTIME_QUALITY_{parameterId.Value}");
            Require(presentation.ValueText != "—", $"RUNTIME_VALUE_{parameterId.Value}");
            Require(presentation.UnitText == definition.Unit,
                $"RUNTIME_UNIT_{parameterId.Value}");
            Require(presentation.AcquiredAtText != "—",
                $"RUNTIME_TIMESTAMP_{parameterId.Value}");
        }

        return new RuntimeResult(
            result.CyclesWritten,
            result.SamplesWritten,
            result.RunDirectory);
    }

    private static string WriteReport(
        DeterministicResult deterministic,
        RuntimeResult runtime)
    {
        string path = Path.GetFullPath(Path.Combine(
            "artifacts",
            "bmw11",
            "bmw11-live-value-display-dryrun.json"));
        var report = new
        {
            schema = "bmw11.live_value_display_dryrun.v1",
            generated_utc = DateTimeOffset.UtcNow,
            communication_attempted = false,
            interface_instance = "none",
            gui_refresh_milliseconds = LiveValuePresenter.GuiRefreshMilliseconds,
            checks = new
            {
                valid_formatting = "pass",
                unit_and_timestamp = "pass",
                stale_transition = "pass",
                missing_timeout_error = "pass",
                non_numeric_formatting = "pass",
                latest_frame_buffer = "pass",
                gui_read_does_not_poll = "pass",
                simulated_runtime_roundtrip = "pass"
            },
            deterministic.PublishedFrames,
            deterministic.SnapshotReads,
            runtime.Cycles,
            runtime.Samples,
            runtime.RunDirectory
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static void RequireQuality(
        LiveValueFrame frame,
        LoggerParameterDefinition definition,
        SampleQuality expected,
        TimeSpan staleAfter)
    {
        LiveValuePresentation presentation = LiveValuePresenter.Project(
            definition,
            frame.Values[definition.ParameterId.Value],
            Stopwatch.GetTimestamp(),
            staleAfter);
        Require(presentation.EffectiveQuality == expected,
            $"QUALITY_{definition.ParameterId.Value}");
        Require(presentation.ValueText == "—",
            $"NO_FALSE_VALUE_{definition.ParameterId.Value}");
    }

    private static NormalizedCycle Cycle(
        Guid epoch,
        long cycleId,
        DateTimeOffset completedUtc,
        long completedTicks,
        params NormalizedSample[] samples) =>
        new(epoch, cycleId, completedUtc, completedTicks, samples);

    private static NormalizedSample ValidSample(
        Guid epoch,
        long cycleId,
        LoggerParameterDefinition definition,
        DateTimeOffset acquiredUtc,
        long acquiredTicks,
        object value) =>
        new(
            epoch,
            cycleId,
            definition.ParameterId,
            1,
            definition.SourceJob,
            acquiredUtc.AddMilliseconds(-20.0),
            acquiredUtc,
            acquiredUtc,
            acquiredTicks,
            value,
            definition.Unit,
            SampleQuality.Valid,
            null,
            20.0,
            20.0,
            0.0);

    private static NormalizedSample UnavailableSample(
        Guid epoch,
        long cycleId,
        LoggerParameterDefinition definition,
        SampleQuality quality,
        string errorCode) =>
        new(
            epoch,
            cycleId,
            definition.ParameterId,
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

    private static LoggerParameterDefinition Resolve(string id) =>
        Ms43ParameterCatalog.Resolve(new ParameterId(id));

    private static long Ticks(double milliseconds) => checked((long)Math.Round(
        milliseconds * Stopwatch.Frequency / 1000.0,
        MidpointRounding.AwayFromZero));

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"BMW11_ASSERTION_FAILED={code}");
        }
    }

    private sealed record DeterministicResult(
        long PublishedFrames,
        long SnapshotReads);

    private sealed record RuntimeResult(
        long Cycles,
        long Samples,
        string RunDirectory);
}
