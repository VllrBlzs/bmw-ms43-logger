using System.Globalization;
using System.Text.Json;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.ParameterManagerProbe;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            TestCatalogAndSearch();
            TestSelectionGuards();
            TestSamplingBudget();
            (SimulatedLoggerSessionResult first, SimulatedLoggerSessionResult second) =
                await TestSchemaIsolationAsync();

            Console.WriteLine("BMW-10 OFFLINE PARAMETER MANAGER DRY RUN");
            Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
            Console.WriteLine("INTERFACE_INSTANCE=None");
            Console.WriteLine($"CATALOG_ENTRIES={Ms43ParameterCatalog.Entries.Count}");
            Console.WriteLine(
                $"SELECTABLE={Ms43ParameterCatalog.ApprovedParameters.Count}");
            Console.WriteLine(
                $"WITHHELD={Ms43ParameterCatalog.Entries.Count - Ms43ParameterCatalog.ApprovedParameters.Count}");
            Console.WriteLine("SEARCH_BY_DISPLAY_INTERNAL_ID=PASS");
            Console.WriteLine("WITHHELD_SELECTION_GUARD=PASS");
            Console.WriteLine("ACTIVE_SESSION_SELECTION_LOCK=PASS");
            Console.WriteLine("SAMPLING_BUDGET_MODEL=PASS");
            Console.WriteLine("RUN1_SELECTED=4");
            Console.WriteLine($"RUN1_CYCLES={first.CyclesWritten}");
            Console.WriteLine($"RUN1_SAMPLES={first.SamplesWritten}");
            Console.WriteLine("RUN2_SELECTED=1");
            Console.WriteLine($"RUN2_CYCLES={second.CyclesWritten}");
            Console.WriteLine($"RUN2_SAMPLES={second.SamplesWritten}");
            Console.WriteLine("CSV_SCHEMA_ISOLATION=PASS");
            Console.WriteLine($"RUN1={first.RunDirectory}");
            Console.WriteLine($"RUN2={second.RunDirectory}");
            Console.WriteLine("RESULT=PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.Error.WriteLine("RESULT=FAIL");
            return 1;
        }
    }

    private static void TestCatalogAndSearch()
    {
        Expect(Ms43ParameterCatalog.Entries.Count == 28, "Catalog count mismatch.");
        Expect(
            Ms43ParameterCatalog.ApprovedParameters.Count == 22,
            "Approved count mismatch.");
        Expect(
            Ms43ParameterCatalog.Entries.Count(entry => !entry.IsSelectable) == 6,
            "Withheld count mismatch.");

        ExpectSingleSearch("Motorfordulat", "engine.rpm");
        ExpectSingleSearch("VehicleSpeed", "vehicle.speed");
        ExpectSingleSearch("electrical.battery.voltage", "electrical.battery.voltage");
        ExpectSingleSearch("Hűtőfolyadék kilépő", "coolant.outlet.temperature");
        Expect(
            Ms43ParameterCatalog.Search("temperature").Count >= 4,
            "ID search did not return temperature entries.");
    }

    private static void TestSelectionGuards()
    {
        var model = new ParameterSelectionModel(
            [new ParameterId("engine.rpm")]);
        Expect(model.Snapshot.SelectedCount == 1, "Initial selection mismatch.");

        ExpectRejected(() => model.SetSelected(
            new ParameterId("coolant.outlet.temperature"),
            selected: true));
        ExpectRejected(() => model.SetSelected(
            new ParameterId("engine.unknown"),
            selected: true));
        ExpectRejected(() => Ms43ParameterCatalog.ValidateSessionSelection(
            [
                new ParameterId("engine.rpm"),
                new ParameterId("engine.rpm")
            ]));

        IReadOnlyList<ParameterId> captured = model.CaptureForSession();
        Expect(captured.Count == 1, "Captured selection mismatch.");
        Expect(model.Snapshot.IsLocked, "Selection was not locked.");
        ExpectRejected(() => model.SetSelected(
            new ParameterId("vehicle.speed"),
            selected: true));
        ExpectRejected(model.SelectAllApproved);
        ExpectRejected(model.Clear);
        model.ReleaseSession();
        model.SetSelected(new ParameterId("vehicle.speed"), selected: true);
        Expect(model.Snapshot.SelectedCount == 2, "Selection did not unlock.");

        model.Clear();
        Expect(model.Snapshot.SelectedCount == 0, "Clear selection failed.");
        ExpectRejected(() => model.CaptureForSession());
    }

    private static void TestSamplingBudget()
    {
        SamplingBudgetEstimate rpm = SamplingBudgetEstimator.Estimate(
            [new ParameterId("engine.rpm")],
            requestedHz: 5.0);
        Expect(rpm.PlannedJobs == 1, "RPM budget job count mismatch.");
        Expect(rpm.ResultRequests == 4, "RPM result-request count mismatch.");
        ExpectNear(rpm.EstimatedCycleMilliseconds, 191.0, "RPM cycle budget");

        SamplingBudgetEstimate batch = SamplingBudgetEstimator.Estimate(
            [new ParameterId("vehicle.speed")],
            requestedHz: 5.0);
        Expect(batch.PlannedJobs == 1, "Batch budget job count mismatch.");
        Expect(batch.ResultRequests == 57, "Batch result-request count mismatch.");
        ExpectNear(batch.EstimatedCycleMilliseconds, 219.25, "Batch cycle budget");

        SamplingBudgetEstimate mixed = SamplingBudgetEstimator.Estimate(
            [
                new ParameterId("engine.rpm"),
                new ParameterId("vehicle.speed")
            ],
            requestedHz: 5.0);
        Expect(mixed.PlannedJobs == 2, "Mixed budget job count mismatch.");
        Expect(mixed.ResultRequests == 61, "Mixed result-request count mismatch.");
        ExpectNear(mixed.EstimatedCycleMilliseconds, 410.25, "Mixed cycle budget");
        ExpectNear(mixed.EstimatedMaximumHz, 1000.0 / 410.25, "Mixed ceiling");
        ExpectNear(
            mixed.EstimatedEffectiveHz,
            mixed.EstimatedMaximumHz,
            "Mixed effective rate");

        SamplingBudgetEstimate empty = SamplingBudgetEstimator.Estimate([], 5.0);
        Expect(empty.PlannedJobs == 0, "Empty budget must dispatch no jobs.");
        Expect(empty.EstimatedEffectiveHz == 0.0, "Empty budget must be zero Hz.");
    }

    private static async Task<(
        SimulatedLoggerSessionResult First,
        SimulatedLoggerSessionResult Second)> TestSchemaIsolationAsync()
    {
        string output = Path.GetFullPath(Path.Combine("artifacts", "bmw10"));
        IReadOnlyList<ParameterId> firstSelection =
            Ms43ParameterCatalog.DefaultSelection;
        IReadOnlyList<ParameterId> secondSelection =
            [new ParameterId("engine.rpm")];

        await using var runtime = new SimulatedLoggerRuntime();
        await runtime.ConnectAsync();

        await runtime.StartAsync(output, 5.0, firstSelection);
        await Task.Delay(850);
        SimulatedLoggerSessionResult first = await runtime.StopAsync();
        ValidateRun(first, firstSelection);

        await runtime.StartAsync(output, 2.0, secondSelection);
        await Task.Delay(1100);
        SimulatedLoggerSessionResult second = await runtime.StopAsync();
        ValidateRun(second, secondSelection);

        await runtime.DisconnectAsync();
        Expect(
            !string.Equals(
                first.RunDirectory,
                second.RunDirectory,
                StringComparison.OrdinalIgnoreCase),
            "Two selections must not share a run directory.");
        return (first, second);
    }

    private static void ValidateRun(
        SimulatedLoggerSessionResult result,
        IReadOnlyList<ParameterId> expectedSelection)
    {
        Expect(File.Exists(result.CsvPath), "CSV is missing.");
        Expect(File.Exists(result.MetadataPath), "Metadata is missing.");
        Expect(result.CyclesWritten >= 2, "Too few simulated cycles were written.");
        Expect(
            result.SamplesWritten == result.CyclesWritten * expectedSelection.Count,
            "Sample count does not match the immutable run schema.");

        string[] csvLines = File.ReadAllLines(result.CsvPath);
        Expect(
            csvLines.LongLength == result.SamplesWritten + 1,
            "CSV row count mismatch.");
        var csvIds = csvLines
            .Skip(1)
            .Select(line => line.Split(',')[6])
            .ToHashSet(StringComparer.Ordinal);
        Expect(
            csvIds.SetEquals(expectedSelection.Select(item => item.Value)),
            "CSV contains a parameter outside the captured selection.");

        using JsonDocument metadata = JsonDocument.Parse(
            File.ReadAllText(result.MetadataPath));
        JsonElement root = metadata.RootElement;
        Expect(root.GetProperty("status").GetString() == "completed", "Run not completed.");
        string[] metadataIds = root
            .GetProperty("selected_parameter_ids")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        Expect(
            metadataIds.SequenceEqual(
                expectedSelection.Select(item => item.Value),
                StringComparer.Ordinal),
            "Metadata selection does not match the captured schema.");
        string duration = result.DurationSeconds.ToString(
            "F2",
            CultureInfo.InvariantCulture);
        Expect(duration.Length > 0, "Duration formatting failed.");
    }

    private static void ExpectSingleSearch(string query, string expectedId)
    {
        IReadOnlyList<LoggerParameterDefinition> matches =
            Ms43ParameterCatalog.Search(query);
        Expect(matches.Count == 1, $"Search '{query}' was not unique.");
        Expect(
            matches[0].ParameterId.Value == expectedId,
            $"Search '{query}' returned the wrong parameter.");
    }

    private static void ExpectRejected(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("A forbidden selection operation succeeded.");
    }

    private static void ExpectNear(double actual, double expected, string label)
    {
        if (Math.Abs(actual - expected) > 0.0001)
        {
            throw new InvalidOperationException(
                $"{label}: expected {expected}, got {actual}.");
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
