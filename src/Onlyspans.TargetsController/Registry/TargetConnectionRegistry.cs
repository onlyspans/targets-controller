using System.Collections.Concurrent;
using System.Threading.Channels;
using Agents.Communication;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Targets.Communication;

namespace Onlyspans.TargetsController.Registry;

public sealed class TargetConnectionRegistry(ILogger<TargetConnectionRegistry> logger)
    : ITargetConnectionRegistry
{
    // target_id → agent's command stream (TC → Agent direction)
    private readonly ConcurrentDictionary<string, IServerStreamWriter<ControllerMessage>> _agents = new();

    // deployment_id → channel bridging agent logs to waiting Worker stream
    private readonly ConcurrentDictionary<string, Channel<ExecutionResult>> _deployments = new();

    public void RegisterAgent(string targetId, IServerStreamWriter<ControllerMessage> commandStream)
    {
        _agents[targetId] = commandStream;
        logger.LogInformation("Agent registered for target {TargetId}", targetId);
    }

    public void UnregisterAgent(string targetId)
    {
        _agents.TryRemove(targetId, out _);
        logger.LogInformation("Agent unregistered for target {TargetId}", targetId);
    }

    public IServerStreamWriter<ControllerMessage>? GetAgentStream(string targetId)
        => _agents.TryGetValue(targetId, out var stream) ? stream : null;

    public ChannelReader<ExecutionResult> CreateDeploymentChannel(string deploymentId)
    {
        var channel = Channel.CreateUnbounded<ExecutionResult>();
        _deployments[deploymentId] = channel;
        return channel.Reader;
    }

    public void PublishResult(string deploymentId, ExecutionResult result)
    {
        if (_deployments.TryGetValue(deploymentId, out var channel))
            channel.Writer.TryWrite(result);
    }

    public void CompleteDeployment(string deploymentId)
    {
        if (_deployments.TryRemove(deploymentId, out var channel))
            channel.Writer.Complete();
    }
}
