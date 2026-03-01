using Grpc.Core;

namespace Onlyspans.TargetsController.Tests.Helpers;

/// <summary>
/// Fake IAsyncStreamReader that returns a pre-set list of messages sequentially.
/// </summary>
public sealed class FakeAsyncStreamReader<T>(IEnumerable<T> items) : IAsyncStreamReader<T>
{
    private readonly IEnumerator<T> _enumerator = items.GetEnumerator();

    public T Current => _enumerator.Current;

    public Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_enumerator.MoveNext());
    }
}
