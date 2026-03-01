using Grpc.Core;

namespace Onlyspans.TargetsController.Tests.Helpers;

/// <summary>
/// Fake IServerStreamWriter that captures written messages into a list for assertion.
/// </summary>
public sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
{
    private readonly List<T> _messages = [];
    public IReadOnlyList<T> Messages => _messages;

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message)
    {
        _messages.Add(message);
        return Task.CompletedTask;
    }

    public Task WriteAsync(T message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _messages.Add(message);
        return Task.CompletedTask;
    }
}
