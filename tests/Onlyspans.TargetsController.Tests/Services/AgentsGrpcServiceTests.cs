using Agents.Communication;
using FluentAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Onlyspans.TargetsController.Registry;
using Onlyspans.TargetsController.Services;
using Onlyspans.TargetsController.Tests.Helpers;
using Targets.Communication;

namespace Onlyspans.TargetsController.Tests.Services;

public sealed class AgentsGrpcServiceTests
{
    private readonly ITargetConnectionRegistry _registry = Substitute.For<ITargetConnectionRegistry>();
    private readonly AgentsGrpcService _sut;

    public AgentsGrpcServiceTests()
    {
        _sut = new AgentsGrpcService(_registry, NullLogger<AgentsGrpcService>.Instance);
    }

    private static ServerCallContext MakeContext(CancellationToken ct = default)
    {
        var ctx = Substitute.For<ServerCallContext>();
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    private static AgentMessage Registration(string targetId) =>
        new() { Registration = new AgentRegistration { TargetId = targetId } };

    private static AgentMessage Log(string deploymentId, string message, long timestamp = 0, Agents.Communication.LogLevel level = Agents.Communication.LogLevel.Unspecified) =>
        new() { Log = new ExecutionLog { DeploymentId = deploymentId, Message = message, Timestamp = timestamp, Level = level } };

    private static AgentMessage Completed(string deploymentId, bool success, string? error = null)
    {
        var msg = new ExecutionCompleted { DeploymentId = deploymentId, Success = success };
        if (error is not null) msg.ErrorMessage = error;
        return new AgentMessage { Completed = msg };
    }

    [Fact]
    public async Task Connect_RegistrationMessage_RegistersAgentWithCorrectTargetId()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([Registration("t1")]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).RegisterAgent("t1", Arg.Any<IServerStreamWriter<ControllerMessage>>());
    }

    [Fact]
    public async Task Connect_RegistrationMessage_StoresResponseStreamInRegistry()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([Registration("t1")]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).RegisterAgent("t1", responseStream);
    }

    [Fact]
    public async Task Connect_LogMessage_PublishesExecutionResult()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([
            Registration("t1"),
            Log("d1", "hello"),
        ]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).PublishResult("d1", Arg.Is<ExecutionResult>(r =>
            r.Type == ResultType.Log && r.Message == "hello"));
    }

    [Fact]
    public async Task Connect_LogMessage_PreservesAgentTimestamp()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([
            Registration("t1"),
            Log("d1", "msg", timestamp: 12345L),
        ]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).PublishResult("d1", Arg.Is<ExecutionResult>(r => r.Timestamp == 12345L));
    }

    [Fact]
    public async Task Connect_LogMessage_PreservesLogLevel()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([
            Registration("t1"),
            Log("d1", "msg", level: Agents.Communication.LogLevel.Warning),
        ]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).PublishResult("d1", Arg.Is<ExecutionResult>(r =>
            r.LogLevel == Targets.Communication.LogLevel.Warning));
    }

    [Fact]
    public async Task Connect_LogMessageBeforeRegistration_DoesNotPublish()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([
            Log("d1", "hello"),
        ]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.DidNotReceive().PublishResult(Arg.Any<string>(), Arg.Any<ExecutionResult>());
    }

    [Fact]
    public async Task Connect_CompletedMessage_Success_PublishesSuccessAndCompletesDeployment()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([
            Registration("t1"),
            Completed("d1", success: true),
        ]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).PublishResult("d1", Arg.Is<ExecutionResult>(r => r.Type == ResultType.Success));
        _registry.Received(1).CompleteDeployment("d1");
    }

    [Fact]
    public async Task Connect_CompletedMessage_Failure_PublishesErrorWithMessage()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([
            Registration("t1"),
            Completed("d1", success: false, error: "boom"),
        ]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).PublishResult("d1", Arg.Is<ExecutionResult>(r =>
            r.Type == ResultType.Error && r.Message == "boom"));
    }

    [Fact]
    public async Task Connect_StreamEndsNormally_UnregistersAgent()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([Registration("t1")]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.Received(1).UnregisterAgent("t1");
    }

    [Fact]
    public async Task Connect_OperationCanceledException_StillUnregistersAgent()
    {
        using var cts = new CancellationTokenSource();
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();

        // Use NSubstitute: first MoveNext returns Registration, second blocks until CT is cancelled
        var requestStream = Substitute.For<IAsyncStreamReader<AgentMessage>>();
        var tcs = new TaskCompletionSource<bool>();
        cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
        requestStream.MoveNext(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(true), // Registration consumed
                tcs.Task);             // blocks until cancelled
        requestStream.Current.Returns(Registration("t1"));

        // Start service — it will process Registration then suspend waiting for next message
        var serviceTask = _sut.Connect(requestStream, responseStream, MakeContext(cts.Token));

        // Cancel while service is blocked — OperationCanceledException is caught, finally runs
        await cts.CancelAsync();
        await serviceTask;

        _registry.Received(1).UnregisterAgent("t1");
    }

    [Fact]
    public async Task Connect_StreamEndsWithoutRegistration_DoesNotCallUnregister()
    {
        var responseStream = new FakeServerStreamWriter<ControllerMessage>();
        var requestStream = new FakeAsyncStreamReader<AgentMessage>([]);

        await _sut.Connect(requestStream, responseStream, MakeContext());

        _registry.DidNotReceive().UnregisterAgent(Arg.Any<string>());
    }
}
