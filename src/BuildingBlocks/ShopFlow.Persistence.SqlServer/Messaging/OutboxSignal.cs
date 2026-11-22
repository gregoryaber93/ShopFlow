using System.Threading.Channels;

namespace ShopFlow.Persistence.SqlServer.Messaging;

/// <summary>"Something new appeared in the outbox" signal within one instance.</summary>
public interface IOutboxSignal
{
    void Notify();

    /// <summary>Completes after a signal or after <paramref name="timeout"/>.</summary>
    Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken);
}

internal sealed class OutboxSignal : IOutboxSignal
{
    // A channel with capacity 1 that drops the excess: ten writes during one poller run
    // give one extra wake-up, not ten.
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await _channel.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The polling interval elapsed without a signal: time for a regular run.
        }
    }
}
