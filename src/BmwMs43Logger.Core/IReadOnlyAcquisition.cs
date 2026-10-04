namespace BmwMs43Logger.Core;

public interface IReadOnlyAcquisition : IAsyncDisposable
{
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask<AcquisitionBatch> ReadAsync(
        IReadOnlyCollection<ParameterId> parameterIds,
        CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}
