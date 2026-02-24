using Grpc.Core;
using Agents.Communication;
using Onlyspans.TargetsController.Registry;
using Targets.Communication;

namespace Onlyspans.TargetsController.Services;

public sealed class AgentsGrpcService(
    ITargetConnectionRegistry registry,
    ILogger<AgentsGrpcService> logger
) : AgentService.AgentServiceBase
{
    public override async Task Connect(
        IAsyncStreamReader<AgentMessage> requestStream,
        IServerStreamWriter<ControllerMessage> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        string? targetId = null;

        try
        {
            await foreach (var message in requestStream.ReadAllAsync(ct))
            {
                switch (message.PayloadCase)
                {
                    case AgentMessage.PayloadOneofCase.Registration:
                        targetId = message.Registration.TargetId;
                        registry.RegisterAgent(targetId, responseStream);
                        logger.LogInformation(
                            "Agent connected: target={TargetId} type={AgentType} version={Version}",
                            targetId,
                            message.Registration.AgentType,
                            message.Registration.Version);
                        break;

                    case AgentMessage.PayloadOneofCase.Log:
                        if (targetId is null) break;
                        var log = message.Log;
                        logger.LogDebug(
                            "[{TargetId}] [{DeploymentId}] {Message}",
                            targetId, log.DeploymentId, log.Message);

                        registry.PublishResult(log.DeploymentId, new ExecutionResult
                        {
                            DeploymentId = log.DeploymentId,
                            Timestamp = log.Timestamp,
                            Type = ResultType.Log,
                            Message = log.Message
                        });
                        break;

                    case AgentMessage.PayloadOneofCase.Completed:
                        if (targetId is null) break;
                        var completed = message.Completed;
                        logger.LogInformation(
                            "Agent reported completion: target={TargetId} deployment={DeploymentId} success={Success}",
                            targetId, completed.DeploymentId, completed.Success);

                        registry.PublishResult(completed.DeploymentId, new ExecutionResult
                        {
                            DeploymentId = completed.DeploymentId,
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Type = completed.Success ? ResultType.Success : ResultType.Error,
                            Message = completed.HasErrorMessage
                                ? completed.ErrorMessage
                                : "Deployment completed successfully"
                        });
                        registry.CompleteDeployment(completed.DeploymentId);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Agent connection closed: target={TargetId}", targetId ?? "unknown");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent connection error: target={TargetId}", targetId ?? "unknown");
        }
        finally
        {
            if (targetId is not null)
                registry.UnregisterAgent(targetId);
        }
    }
}
