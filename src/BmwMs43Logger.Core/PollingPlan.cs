namespace BmwMs43Logger.Core;

public enum PollingPlanMode
{
    RuntimeApprovedOnly,
    ControlledTest
}

public sealed record PollingParameterDefinition(
    ParameterId ParameterId,
    string Job,
    string Argument,
    string ValueResult,
    string UnitResult,
    string SupportStatus);

public sealed record ReviewedJobDefinition(
    string Job,
    string Argument,
    IReadOnlyList<string> ResultNames);

public sealed record PlannedJob(
    int Sequence,
    string Job,
    string Argument,
    IReadOnlyList<string> ResultNames,
    IReadOnlyList<ParameterId> ParameterIds);

public sealed record PlannedParameterRoute(
    ParameterId ParameterId,
    int JobSequence,
    string ValueResult,
    string UnitResult);

public sealed record PollingPlan(
    PollingPlanMode Mode,
    string ExecutionPolicy,
    IReadOnlyList<PlannedJob> Jobs,
    IReadOnlyList<PlannedParameterRoute> Routes)
{
    public int SelectedParameterCount => Routes.Count;
}

public static class PollingPlanner
{
    public const string ApprovedStatus = "approved";
    public const string SerialExecutionPolicy = "serial_single_worker";

    public static PollingPlan Build(
        IReadOnlyCollection<PollingParameterDefinition> catalog,
        IReadOnlyCollection<ReviewedJobDefinition> reviewedJobs,
        IReadOnlyCollection<ParameterId> selectedParameterIds,
        PollingPlanMode mode)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reviewedJobs);
        ArgumentNullException.ThrowIfNull(selectedParameterIds);
        if (selectedParameterIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one parameter must be selected.",
                nameof(selectedParameterIds));
        }

        var catalogById = new Dictionary<ParameterId, PollingParameterDefinition>();
        foreach (PollingParameterDefinition definition in catalog)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!catalogById.TryAdd(definition.ParameterId, definition))
            {
                throw new InvalidOperationException(
                    $"Duplicate catalog parameter: {definition.ParameterId.Value}");
            }
        }

        var reviewedByTuple = new Dictionary<(string Job, string Argument), ReviewedJobDefinition>();
        foreach (ReviewedJobDefinition reviewedJob in reviewedJobs)
        {
            ArgumentNullException.ThrowIfNull(reviewedJob);
            var key = (reviewedJob.Job, reviewedJob.Argument);
            if (!reviewedByTuple.TryAdd(key, reviewedJob))
            {
                throw new InvalidOperationException(
                    $"Duplicate reviewed job tuple: {reviewedJob.Job}");
            }
        }

        var orderedDefinitions = new List<PollingParameterDefinition>();
        var selectedSet = new HashSet<ParameterId>();
        foreach (ParameterId parameterId in selectedParameterIds)
        {
            ArgumentNullException.ThrowIfNull(parameterId);
            if (!selectedSet.Add(parameterId))
            {
                continue;
            }
            if (!catalogById.TryGetValue(
                    parameterId,
                    out PollingParameterDefinition? definition))
            {
                throw new InvalidOperationException(
                    $"Unknown parameter: {parameterId.Value}");
            }
            if (mode == PollingPlanMode.RuntimeApprovedOnly &&
                !string.Equals(
                    definition.SupportStatus,
                    ApprovedStatus,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Parameter is not runtime-approved: {parameterId.Value}");
            }
            orderedDefinitions.Add(definition);
        }

        var tupleOrder = new List<(string Job, string Argument)>();
        var grouped = new Dictionary<
            (string Job, string Argument),
            List<PollingParameterDefinition>>();
        foreach (PollingParameterDefinition definition in orderedDefinitions)
        {
            var key = (definition.Job, definition.Argument);
            if (!grouped.TryGetValue(
                    key,
                    out List<PollingParameterDefinition>? group))
            {
                group = [];
                grouped.Add(key, group);
                tupleOrder.Add(key);
            }
            group.Add(definition);
        }

        var jobs = new List<PlannedJob>(tupleOrder.Count);
        var routes = new List<PlannedParameterRoute>(orderedDefinitions.Count);
        for (int index = 0; index < tupleOrder.Count; index++)
        {
            (string job, string argument) = tupleOrder[index];
            if (!reviewedByTuple.TryGetValue(
                    (job, argument),
                    out ReviewedJobDefinition? reviewedJob))
            {
                throw new InvalidOperationException(
                    $"Source tuple is not reviewed: {job}");
            }
            if (reviewedJob.ResultNames.Count < 2 ||
                reviewedJob.ResultNames[0] != "JOB_STATUS" ||
                reviewedJob.ResultNames.Distinct(StringComparer.Ordinal).Count() !=
                reviewedJob.ResultNames.Count)
            {
                throw new InvalidOperationException(
                    $"Invalid reviewed result tuple: {job}");
            }

            int sequence = index + 1;
            IReadOnlyList<PollingParameterDefinition> parameters = grouped[(job, argument)];
            foreach (PollingParameterDefinition parameter in parameters)
            {
                if (!reviewedJob.ResultNames.Contains(
                        parameter.ValueResult,
                        StringComparer.Ordinal) ||
                    !reviewedJob.ResultNames.Contains(
                        parameter.UnitResult,
                        StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Reviewed tuple {job} does not contain both results for " +
                        $"{parameter.ParameterId.Value}.");
                }
                routes.Add(new PlannedParameterRoute(
                    parameter.ParameterId,
                    sequence,
                    parameter.ValueResult,
                    parameter.UnitResult));
            }

            jobs.Add(new PlannedJob(
                sequence,
                job,
                argument,
                reviewedJob.ResultNames.ToArray(),
                parameters.Select(parameter => parameter.ParameterId).ToArray()));
        }

        return new PollingPlan(
            mode,
            SerialExecutionPolicy,
            jobs,
            routes);
    }
}
