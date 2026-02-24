using Grpc.Core;
using Agents.Communication;
using Onlyspans.TargetsController.Registry;
using Targets.Communication;

namespace Onlyspans.TargetsController.Services;

public sealed class TargetsGrpcService(
    ITargetConnectionRegistry registry,
    ILogger<TargetsGrpcService> logger
) : TargetsService.TargetsServiceBase
{
    public override async Task ExecuteOnTarget(
        TargetExecutionRequest request,
        IServerStreamWriter<ExecutionResult> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        logger.LogInformation(
            "ExecuteOnTarget: deployment={DeploymentId} target={TargetId} type={TargetType}",
            request.DeploymentId, request.TargetId, request.TargetType);

        // Look up connected agent for this target
        var agentStream = registry.GetAgentStream(request.TargetId);
        if (agentStream is null)
        {
            logger.LogWarning(
                "No agent connected for target {TargetId} — rejecting deployment {DeploymentId}",
                request.TargetId, request.DeploymentId);

            await responseStream.WriteAsync(new ExecutionResult
            {
                DeploymentId = request.DeploymentId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = ResultType.Error,
                Message = $"No agent connected for target '{request.TargetId}'"
            }, ct);
            return;
        }

        // Create channel to receive results from agent
        var resultReader = registry.CreateDeploymentChannel(request.DeploymentId);

        // Send deploy command to agent
        await agentStream.WriteAsync(new ControllerMessage
        {
            Deploy = new DeployCommand
            {
                DeploymentId = request.DeploymentId,
                SnapshotPath = request.SnapshotPath,
                EnvironmentVariables = { request.EnvironmentVariables }
            }
        }, ct);

        logger.LogInformation(
            "DeployCommand sent to agent for target {TargetId}", request.TargetId);

        // Forward results from agent to Worker
        try
        {
            await foreach (var result in resultReader.ReadAllAsync(ct))
            {
                await responseStream.WriteAsync(result, ct);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Deployment {DeploymentId} cancelled", request.DeploymentId);
            registry.CompleteDeployment(request.DeploymentId);
        }

        logger.LogInformation(
            "ExecuteOnTarget completed: deployment={DeploymentId}", request.DeploymentId);
    }
}
