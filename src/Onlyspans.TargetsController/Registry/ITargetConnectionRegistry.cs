using System.Threading.Channels;
using Agents.Communication;
using Grpc.Core;
using Targets.Communication;

namespace Onlyspans.TargetsController.Registry;

public interface ITargetConnectionRegistry
{
    // Called by AgentsGrpcService when agent connects
    void RegisterAgent(string targetId, IServerStreamWriter<ControllerMessage> commandStream);

    // Called by AgentsGrpcService when agent disconnects
    void UnregisterAgent(string targetId);

    // Called by TargetsGrpcService before sending DeployCommand
    // Returns null if no agent is connected for this target
    IServerStreamWriter<ControllerMessage>? GetAgentStream(string targetId);

    // Called by TargetsGrpcService to receive logs for a deployment
    ChannelReader<ExecutionResult> CreateDeploymentChannel(string deploymentId);

    // Called by AgentsGrpcService to push log chunks from agent to waiting Worker stream
    void PublishResult(string deploymentId, ExecutionResult result);

    // Called by TargetsGrpcService after deployment completes (success or error)
    void CompleteDeployment(string deploymentId);
}
