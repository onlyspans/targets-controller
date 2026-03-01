using System.Threading.Channels;
using Agents.Communication;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Onlyspans.TargetsController.Registry;
using Onlyspans.TargetsController.Services;
using Onlyspans.TargetsController.Tests.Helpers;
using Targets.Communication;
using TargetSnapshotChunk = Targets.Communication.SnapshotChunk;

namespace Onlyspans.TargetsController.Tests.Services;

public sealed class TargetsGrpcServiceTests
{
    private readonly ITargetConnectionRegistry _registry = Substitute.For<ITargetConnectionRegistry>();
    private readonly TargetsGrpcService _sut;

    public TargetsGrpcServiceTests()
    {
        _sut = new TargetsGrpcService(_registry, NullLogger<TargetsGrpcService>.Instance);
    }

    private static ServerCallContext MakeContext(CancellationToken ct = default)
    {
        var ctx = Substitute.For<ServerCallContext>();
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    [Fact]
    public async Task ExecuteOnTarget_EmptyStream_ReturnsWithoutWritingResponse()
    {
        var requestStream = new FakeAsyncStreamReader<DeploymentInput>([]);
        var responseStream = new FakeServerStreamWriter<ExecutionResult>();

        await _sut.ExecuteOnTarget(requestStream, responseStream, MakeContext());

        responseStream.Messages.Should().BeEmpty();
        _registry.DidNotReceive().GetAgentStream(Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteOnTarget_FirstMessageIsNotMetadata_ReturnsWithoutWritingResponse()
    {
        var requestStream = new FakeAsyncStreamReader<DeploymentInput>([
            new DeploymentInput { SnapshotChunk = new TargetSnapshotChunk { Data = ByteString.Empty } }
        ]);
        var responseStream = new FakeServerStreamWriter<ExecutionResult>();

        await _sut.ExecuteOnTarget(requestStream, responseStream, MakeContext());

        responseStream.Messages.Should().BeEmpty();
        _registry.DidNotReceive().GetAgentStream(Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteOnTarget_NoAgentConnected_WritesErrorResult()
    {
        var metadata = new DeploymentMetadata { DeploymentId = "d1", TargetId = "t1", TargetType = "k8s" };
        var requestStream = new FakeAsyncStreamReader<DeploymentInput>([
            new DeploymentInput { Metadata = metadata }
        ]);
        var responseStream = new FakeServerStreamWriter<ExecutionResult>();
        _registry.GetAgentStream("t1").Returns((IServerStreamWriter<ControllerMessage>?)null);

        await _sut.ExecuteOnTarget(requestStream, responseStream, MakeContext());

        responseStream.Messages.Should().HaveCount(1);
        responseStream.Messages[0].Type.Should().Be(ResultType.Error);
        responseStream.Messages[0].DeploymentId.Should().Be("d1");
        responseStream.Messages[0].Message.Should().Contain("t1");
    }

    [Fact]
    public async Task ExecuteOnTarget_HappyPath_ForwardsChunksToAgentStream()
    {
        var agentStream = Substitute.For<IServerStreamWriter<ControllerMessage>>();
        var channel = Channel.CreateUnbounded<ExecutionResult>();
        channel.Writer.Complete();

        _registry.GetAgentStream("t1").Returns(agentStream);
        _registry.CreateDeploymentChannel("d1").Returns(channel.Reader);

        var chunk1Data = ByteString.CopyFrom([1, 2, 3]);
        var chunk2Data = ByteString.CopyFrom([4, 5, 6]);
        var requestStream = new FakeAsyncStreamReader<DeploymentInput>([
            new DeploymentInput { Metadata = new DeploymentMetadata { DeploymentId = "d1", TargetId = "t1" } },
            new DeploymentInput { SnapshotChunk = new TargetSnapshotChunk { Data = chunk1Data, IsLast = false } },
            new DeploymentInput { SnapshotChunk = new TargetSnapshotChunk { Data = chunk2Data, IsLast = true } },
        ]);
        var responseStream = new FakeServerStreamWriter<ExecutionResult>();

        await _sut.ExecuteOnTarget(requestStream, responseStream, MakeContext());

        var calls = agentStream.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(agentStream.WriteAsync))
            .Select(c => (ControllerMessage)c.GetArguments()[0]!)
            .Where(m => m.PayloadCase == ControllerMessage.PayloadOneofCase.SnapshotChunk)
            .ToList();

        calls.Should().HaveCount(2);
        calls[0].SnapshotChunk.DeploymentId.Should().Be("d1");
        calls[0].SnapshotChunk.Data.Should().Equal(chunk1Data);
        calls[0].SnapshotChunk.IsLast.Should().BeFalse();
        calls[1].SnapshotChunk.IsLast.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteOnTarget_HappyPath_SendsDeployCommandAfterLastChunk()
    {
        var agentStream = Substitute.For<IServerStreamWriter<ControllerMessage>>();
        var channel = Channel.CreateUnbounded<ExecutionResult>();
        channel.Writer.Complete();

        _registry.GetAgentStream("t1").Returns(agentStream);
        _registry.CreateDeploymentChannel("d1").Returns(channel.Reader);

        var envVars = new Dictionary<string, string> { ["KEY"] = "VALUE" };
        var metadata = new DeploymentMetadata { DeploymentId = "d1", TargetId = "t1" };
        metadata.EnvironmentVariables.Add(envVars);

        var requestStream = new FakeAsyncStreamReader<DeploymentInput>([
            new DeploymentInput { Metadata = metadata },
            new DeploymentInput { SnapshotChunk = new TargetSnapshotChunk { Data = ByteString.Empty, IsLast = true } },
        ]);
        var responseStream = new FakeServerStreamWriter<ExecutionResult>();

        await _sut.ExecuteOnTarget(requestStream, responseStream, MakeContext());

        var deployCall = agentStream.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(agentStream.WriteAsync))
            .Select(c => (ControllerMessage)c.GetArguments()[0]!)
            .FirstOrDefault(m => m.PayloadCase == ControllerMessage.PayloadOneofCase.Deploy);

        deployCall.Should().NotBeNull();
        deployCall!.Deploy.DeploymentId.Should().Be("d1");
        deployCall.Deploy.EnvironmentVariables.Should().ContainKey("KEY").WhoseValue.Should().Be("VALUE");
    }

    [Fact]
    public async Task ExecuteOnTarget_HappyPath_ForwardsExecutionResultsToWorker()
    {
        var agentStream = Substitute.For<IServerStreamWriter<ControllerMessage>>();
        var channel = Channel.CreateUnbounded<ExecutionResult>();
        await channel.Writer.WriteAsync(new ExecutionResult { DeploymentId = "d1", Type = ResultType.Log, Message = "msg1" });
        await channel.Writer.WriteAsync(new ExecutionResult { DeploymentId = "d1", Type = ResultType.Success });
        channel.Writer.Complete();

        _registry.GetAgentStream("t1").Returns(agentStream);
        _registry.CreateDeploymentChannel("d1").Returns(channel.Reader);

        var requestStream = new FakeAsyncStreamReader<DeploymentInput>([
            new DeploymentInput { Metadata = new DeploymentMetadata { DeploymentId = "d1", TargetId = "t1" } },
        ]);
        var responseStream = new FakeServerStreamWriter<ExecutionResult>();

        await _sut.ExecuteOnTarget(requestStream, responseStream, MakeContext());

        responseStream.Messages.Should().HaveCount(2);
        responseStream.Messages[0].Type.Should().Be(ResultType.Log);
        responseStream.Messages[1].Type.Should().Be(ResultType.Success);
    }

    [Fact]
    public async Task ExecuteOnTarget_Cancellation_CallsCompleteDeployment()
    {
        using var cts = new CancellationTokenSource();
        var agentStream = Substitute.For<IServerStreamWriter<ControllerMessage>>();
        // Channel is not completed — service will suspend waiting for results
        var channel = Channel.CreateUnbounded<ExecutionResult>();

        _registry.GetAgentStream("t1").Returns(agentStream);
        _registry.CreateDeploymentChannel("d1").Returns(channel.Reader);

        // Use NSubstitute stream: metadata returns true, then MoveNext returns false (no chunks)
        var requestStream = Substitute.For<IAsyncStreamReader<DeploymentInput>>();
        requestStream.MoveNext(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true), Task.FromResult(false));
        requestStream.Current.Returns(
            new DeploymentInput { Metadata = new DeploymentMetadata { DeploymentId = "d1", TargetId = "t1" } });

        var responseStream = new FakeServerStreamWriter<ExecutionResult>();

        // Start the service — it will process metadata synchronously and suspend at result channel
        var serviceTask = _sut.ExecuteOnTarget(requestStream, responseStream, MakeContext(cts.Token));

        // Cancel while service is blocked on result channel ReadAllAsync
        await cts.CancelAsync();
        await serviceTask;

        _registry.Received(1).CompleteDeployment("d1");
    }
}
