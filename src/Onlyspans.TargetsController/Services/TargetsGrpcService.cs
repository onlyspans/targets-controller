using Grpc.Core;
using Onlyspans.TargetsController.Registry;
using Targets.Communication;
using AgentChunk = Agents.Communication.SnapshotChunk;
using AgentCommand = Agents.Communication.DeployCommand;
using AgentMessage = Agents.Communication.ControllerMessage;

namespace Onlyspans.TargetsController.Services;

public sealed class TargetsGrpcService(
    ITargetConnectionRegistry registry,
    ILogger<TargetsGrpcService> logger
) : TargetsService.TargetsServiceBase
{
    public override async Task ExecuteOnTarget(
        IAsyncStreamReader<DeploymentInput> requestStream,
        IServerStreamWriter<ExecutionResult> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        // First message must be DeploymentMetadata
        if (!await requestStream.MoveNext(ct) ||
            requestStream.Current.PayloadCase != DeploymentInput.PayloadOneofCase.Metadata)
        {
            logger.LogWarning("ExecuteOnTarget: stream opened without metadata as first message");
            return;
        }

        var metadata = requestStream.Current.Metadata;

        logger.LogInformation(
            "ExecuteOnTarget: deployment={DeploymentId} target={TargetId} type={TargetType}",
            metadata.DeploymentId, metadata.TargetId, metadata.TargetType);

        var agentStream = registry.GetAgentStream(metadata.TargetId);
        if (agentStream is null)
        {
            logger.LogWarning(
                "No agent connected for target {TargetId} — rejecting deployment {DeploymentId}",
                metadata.TargetId, metadata.DeploymentId);

            await responseStream.WriteAsync(new ExecutionResult
            {
                DeploymentId = metadata.DeploymentId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = ResultType.Error,
                Message = $"No agent connected for target '{metadata.TargetId}'"
            }, ct);
            return;
        }

        // Create channel to receive results from agent
        var resultReader = registry.CreateDeploymentChannel(metadata.DeploymentId);

        // Route snapshot chunks from Worker → agent as they arrive
        await foreach (var input in requestStream.ReadAllAsync(ct))
        {
            if (input.PayloadCase != DeploymentInput.PayloadOneofCase.SnapshotChunk)
                continue;

            var chunk = input.SnapshotChunk;
            await agentStream.WriteAsync(new AgentMessage
            {
                SnapshotChunk = new AgentChunk
                {
                    DeploymentId = metadata.DeploymentId,
                    Data = chunk.Data,
                    IsLast = chunk.IsLast
                }
            }, ct);
        }

        logger.LogInformation(
            "Snapshot transfer complete for deployment={DeploymentId}, sending DeployCommand",
            metadata.DeploymentId);

        // All chunks forwarded — send deploy command to agent
        await agentStream.WriteAsync(new AgentMessage
        {
            Deploy = new AgentCommand
            {
                DeploymentId = metadata.DeploymentId,
                EnvironmentVariables = { metadata.EnvironmentVariables }
            }
        }, ct);

        // Forward execution results from agent back to Worker
        try
        {
            await foreach (var result in resultReader.ReadAllAsync(ct))
            {
                await responseStream.WriteAsync(result, ct);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Deployment {DeploymentId} cancelled", metadata.DeploymentId);
            registry.CompleteDeployment(metadata.DeploymentId);
        }

        logger.LogInformation(
            "ExecuteOnTarget completed: deployment={DeploymentId}", metadata.DeploymentId);
    }
}
