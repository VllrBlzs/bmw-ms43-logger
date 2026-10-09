using System.Globalization;
using System.Text.Json;
using System.Collections.Concurrent;
using BmwMs43Logger.Core;

namespace BmwMs43Logger.GuiStateProbe;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            TestHappyPathAndButtonPolicy();
            TestFailuresAreVisibleAndRecoverable();
            TestStaleCompletionIsRejected();
            TestConcurrentConnectCreatesOneOperation();
            SimulatedLoggerSessionResult session =
                await TestSimulatedLoggerRoundTripAsync();

            Console.WriteLine("BMW-09 OFFLINE GUI STATE-MACHINE DRY RUN");
            Console.WriteLine("COMMUNICATION_ATTEMPTED=False");
            Console.WriteLine("INTERFACE_INSTANCE=None");
            Console.WriteLine("HAPPY_PATH=PASS");
            Console.WriteLine("BUTTON_POLICY=PASS");
            Console.WriteLine("DUPLICATE_START_GUARD=PASS");
            Console.WriteLine("STALE_COMPLETION_GUARD=PASS");
            Console.WriteLine("CONCURRENT_CONNECT_GUARD=PASS");
            Console.WriteLine("FAULT_VISIBILITY=PASS");
            Console.WriteLine("SIMULATED_LOGGER_ROUNDTRIP=PASS");
            Console.WriteLine("APPROVED_PARAMETER_COUNT=22");
            Console.WriteLine($"SIMULATED_CYCLES={session.CyclesWritten}");
            Console.WriteLine($"SIMULATED_SAMPLES={session.SamplesWritten}");
            Console.WriteLine($"CSV={session.CsvPath}");
            Console.WriteLine($"METADATA={session.MetadataPath}");
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

    private static async Task<SimulatedLoggerSessionResult>
        TestSimulatedLoggerRoundTripAsync()
    {
        string output = Path.GetFullPath(Path.Combine("artifacts", "bmw09"));
        await using var runtime = new SimulatedLoggerRuntime();
        await runtime.ConnectAsync();
        string runDirectory = await runtime.StartAsync(output, requestedHz: 5.0);
        await Task.Delay(1250);
        SimulatedLoggerSessionResult result = await runtime.StopAsync();
        await runtime.DisconnectAsync();

        Expect(
            string.Equals(runDirectory, result.RunDirectory, StringComparison.Ordinal),
            "The started and completed run directories must match.");
        Expect(File.Exists(result.CsvPath), "The simulated CSV is missing.");
        Expect(File.Exists(result.MetadataPath), "The simulated metadata is missing.");
        Expect(result.CyclesWritten >= 4, "Too few simulated cycles were written.");
        Expect(
            result.SamplesWritten ==
            result.CyclesWritten * SimulatedLoggerRuntime.Parameters.Count,
            "Every simulated cycle must contain all approved parameters.");

        string[] csvLines = File.ReadAllLines(result.CsvPath);
        Expect(
            csvLines.LongLength == result.SamplesWritten + 1,
            "CSV row count does not match metadata.");
        using JsonDocument metadata = JsonDocument.Parse(
            File.ReadAllText(result.MetadataPath));
        JsonElement root = metadata.RootElement;
        Expect(root.GetProperty("status").GetString() == "completed", "Session not completed.");
        Expect(root.GetProperty("stop_reason").GetString() == "requested", "Wrong stop reason.");
        Expect(
            root.GetProperty("samples_written").GetInt64() == result.SamplesWritten,
            "Metadata sample count mismatch.");
        Expect(
            root.GetProperty("selected_parameter_ids").GetArrayLength() == 22,
            "The semantic hold was not enforced.");
        string durationText = result.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture);
        Expect(durationText.Length > 0, "Duration formatting failed.");
        return result;
    }

    private static void TestHappyPathAndButtonPolicy()
    {
        var machine = new LoggerUiStateMachine();
        var observed = new List<LoggerUiSnapshot>();
        machine.Changed += (_, snapshot) => observed.Add(snapshot);

        AssertState(
            machine.Snapshot,
            LoggerUiState.Disconnected,
            canConnect: true,
            canStart: false,
            canStop: false,
            canDisconnect: false);
        ExpectRejected(() => machine.BeginStart());
        ExpectRejected(() => machine.BeginStop());

        LoggerUiOperation connect = machine.BeginConnect();
        AssertState(
            machine.Snapshot,
            LoggerUiState.Connecting,
            false,
            false,
            false,
            false);
        ExpectRejected(() => machine.BeginConnect());
        machine.CompleteConnect(connect, succeeded: true);

        AssertState(
            machine.Snapshot,
            LoggerUiState.Ready,
            false,
            true,
            false,
            true);
        LoggerUiOperation start = machine.BeginStart();
        ExpectRejected(() => machine.BeginStart());
        machine.CompleteStart(start, succeeded: true);

        AssertState(
            machine.Snapshot,
            LoggerUiState.Logging,
            false,
            false,
            true,
            false);
        ExpectRejected(() => machine.BeginDisconnect());

        LoggerUiOperation stop = machine.BeginStop();
        ExpectRejected(() => machine.BeginStop());
        machine.CompleteStop(stop, succeeded: true);

        LoggerUiOperation disconnect = machine.BeginDisconnect();
        machine.CompleteDisconnect(disconnect, succeeded: true);
        AssertState(
            machine.Snapshot,
            LoggerUiState.Disconnected,
            true,
            false,
            false,
            false);

        Expect(observed.Count == 8, "Every accepted transition must be observable.");
        Expect(
            observed.Select(item => item.Revision).SequenceEqual(
                Enumerable.Range(1, observed.Count).Select(value => (long)value)),
            "State revisions must be strictly monotonic.");
    }

    private static void TestFailuresAreVisibleAndRecoverable()
    {
        var machine = new LoggerUiStateMachine();
        LoggerUiOperation connect = machine.BeginConnect();
        machine.CompleteConnect(connect, succeeded: false, "SIMULATED_CONNECT_ERROR");

        LoggerUiSnapshot fault = machine.Snapshot;
        Expect(fault.State == LoggerUiState.Faulted, "Connect failure must be visible.");
        Expect(
            fault.LastError == "SIMULATED_CONNECT_ERROR",
            "The exact safe error code must be retained.");
        Expect(fault.CanDisconnect, "A faulted backend must remain disposable.");
        ExpectRejected(() => machine.BeginStart());

        LoggerUiOperation disconnect = machine.BeginDisconnect();
        machine.CompleteDisconnect(disconnect, succeeded: true);
        Expect(
            machine.Snapshot.State == LoggerUiState.Disconnected,
            "Disconnect must recover a faulted state.");
        Expect(machine.Snapshot.LastError is null, "Recovery must clear the old error.");

        LoggerUiOperation secondConnect = machine.BeginConnect();
        machine.CompleteConnect(secondConnect, succeeded: true);
        machine.ReportRuntimeFault("SIMULATED_RUNTIME_ERROR");
        Expect(
            machine.Snapshot.LastError == "SIMULATED_RUNTIME_ERROR",
            "Runtime faults must be visible.");
        machine.ResetFault();
        Expect(
            machine.Snapshot.State == LoggerUiState.Disconnected,
            "Explicit reset must return to disconnected.");
    }

    private static void TestStaleCompletionIsRejected()
    {
        var machine = new LoggerUiStateMachine();
        LoggerUiOperation connect = machine.BeginConnect();
        machine.CompleteConnect(connect, succeeded: true);
        ExpectRejected(() => machine.CompleteConnect(connect, succeeded: true));

        LoggerUiOperation start = machine.BeginStart();
        var fake = new LoggerUiOperation(Guid.NewGuid(), LoggerUiOperationKind.Start);
        ExpectRejected(() => machine.CompleteStart(fake, succeeded: true));
        machine.CompleteStart(start, succeeded: true);
    }

    private static void TestConcurrentConnectCreatesOneOperation()
    {
        var machine = new LoggerUiStateMachine();
        var winners = new ConcurrentBag<LoggerUiOperation>();
        int rejected = 0;

        Parallel.For(0, 64, _ =>
        {
            try
            {
                winners.Add(machine.BeginConnect());
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref rejected);
            }
        });

        Expect(winners.Count == 1, "Exactly one concurrent Connect may win.");
        Expect(rejected == 63, "All duplicate Connect calls must be rejected.");
        machine.CompleteConnect(winners.Single(), succeeded: true);
    }

    private static void AssertState(
        LoggerUiSnapshot snapshot,
        LoggerUiState expectedState,
        bool canConnect,
        bool canStart,
        bool canStop,
        bool canDisconnect)
    {
        Expect(snapshot.State == expectedState, $"Expected {expectedState}.");
        Expect(snapshot.CanConnect == canConnect, "Unexpected Connect availability.");
        Expect(snapshot.CanStart == canStart, "Unexpected Start availability.");
        Expect(snapshot.CanStop == canStop, "Unexpected Stop availability.");
        Expect(
            snapshot.CanDisconnect == canDisconnect,
            "Unexpected Disconnect availability.");
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
        throw new InvalidOperationException("An illegal transition was accepted.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
