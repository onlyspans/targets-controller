using Agents.Communication;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Onlyspans.TargetsController.Registry;
using Targets.Communication;
using FluentAssertions;

namespace Onlyspans.TargetsController.Tests.Registry;

public sealed class TargetConnectionRegistryTests
{
    private readonly TargetConnectionRegistry _sut =
        new(NullLogger<TargetConnectionRegistry>.Instance);

    [Fact]
    public void RegisterAgent_StoresStream_GetAgentStreamReturnsSameInstance()
    {
        var stream = Substitute.For<IServerStreamWriter<ControllerMessage>>();

        _sut.RegisterAgent("t1", stream);

        _sut.GetAgentStream("t1").Should().BeSameAs(stream);
    }

    [Fact]
    public void UnregisterAgent_AfterRegister_GetAgentStreamReturnsNull()
    {
        var stream = Substitute.For<IServerStreamWriter<ControllerMessage>>();
        _sut.RegisterAgent("t1", stream);

        _sut.UnregisterAgent("t1");

        _sut.GetAgentStream("t1").Should().BeNull();
    }

    [Fact]
    public void GetAgentStream_NeverRegistered_ReturnsNull()
    {
        _sut.GetAgentStream("unknown").Should().BeNull();
    }

    [Fact]
    public void RegisterAgent_CalledTwice_ReturnsLatestStream()
    {
        var first = Substitute.For<IServerStreamWriter<ControllerMessage>>();
        var second = Substitute.For<IServerStreamWriter<ControllerMessage>>();

        _sut.RegisterAgent("t1", first);
        _sut.RegisterAgent("t1", second);

        _sut.GetAgentStream("t1").Should().BeSameAs(second);
    }

    [Fact]
    public void UnregisterAgent_NeverRegistered_DoesNotThrow()
    {
        var act = () => _sut.UnregisterAgent("unknown");

        act.Should().NotThrow();
    }

    [Fact]
    public void CreateDeploymentChannel_ReturnsReadableChannel()
    {
        var reader = _sut.CreateDeploymentChannel("d1");

        reader.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishResult_WritesResultToChannel_CanBeRead()
    {
        var reader = _sut.CreateDeploymentChannel("d1");
        var result = new ExecutionResult { DeploymentId = "d1", Type = ResultType.Log, Message = "hello" };

        _sut.PublishResult("d1", result);
        _sut.CompleteDeployment("d1");

        var items = new List<ExecutionResult>();
        await foreach (var item in reader.ReadAllAsync(CancellationToken.None))
            items.Add(item);

        items.Should().HaveCount(1);
        items[0].Message.Should().Be("hello");
    }

    [Fact]
    public void PublishResult_UnknownDeploymentId_DoesNotThrow()
    {
        var act = () => _sut.PublishResult("unknown", new ExecutionResult());

        act.Should().NotThrow();
    }

    [Fact]
    public async Task CompleteDeployment_CompletesChannel_ReadAllAsyncTerminates()
    {
        var reader = _sut.CreateDeploymentChannel("d1");

        _sut.CompleteDeployment("d1");

        var items = new List<ExecutionResult>();
        await foreach (var item in reader.ReadAllAsync(CancellationToken.None))
            items.Add(item);

        items.Should().BeEmpty();
    }

    [Fact]
    public void CompleteDeployment_UnknownDeploymentId_DoesNotThrow()
    {
        var act = () => _sut.CompleteDeployment("unknown");

        act.Should().NotThrow();
    }
}
