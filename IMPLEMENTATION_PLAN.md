# Targets Controller — Implementation Plan

## Project Overview

**Service**: `Onlyspans.TargetsController`
**Stack**: .NET 10 / C# / ASP.NET Core / gRPC
**Architecture reference**: https://github.com/onlyspans/issues/blob/main/arch/target-arch.md

### Role (from arch doc)

> "Реестр целевых машин. Единый интерфейс: управляет gRPC BiDi соединениями от всех агентов (Target Agent и K8s Agent). Один code path."

TC is a **connection registry and router**, NOT an executor. It:
1. Maintains a persistent registry of gRPC BiDi connections from Target Agents (VM/Bare Metal) and K8s Agents
2. When Worker calls `ExecuteOnTarget`, TC routes the deploy command to the appropriate agent via the existing BiDi connection
3. Receives log chunks and completion status from agents via that BiDi connection
4. Streams those results back to the Worker

```
Worker ──ExecuteOnTarget──▶ TC ──DeployCommand──▶ Target Agent / K8s Agent
Worker ◀──stream ExecutionResult── TC ◀──stream logs/completion── Agent
```

### What TC does NOT do

- ❌ Execute bash scripts itself
- ❌ Connect to SSH/Kubernetes directly
- ❌ Persist logs to a database (Processes owns log persistence — append-only files)
- ❌ Download snapshots (Worker downloads from Artifact Storage and passes the local path)

---

## Phase 0: Documentation Discovery (COMPLETED)

### Allowed APIs — Verified Sources

| API | Version | Source |
|-----|---------|--------|
| `Grpc.AspNetCore` | 2.70.0 | `variables/.../Onlyspans.Variables.Api.csproj` |
| `Grpc.AspNetCore.Server.Reflection` | 2.70.0 | same |
| `Grpc.Tools` | 2.70.0 | same — MUST have `PrivateAssets="all"` |
| `Serilog.AspNetCore` | 9.0.0 | same |
| `Serilog.Formatting.Compact` | 3.0.0 | `worker/.../Onlyspans.Worker.Api.csproj` |
| `Strongly.Options` | 1.0.0 | both .csproj files |
| `AspNetCore.HealthChecks.NpgSql` | — | **NOT NEEDED** — TC has no database |

> **No database packages** — TC has no PostgreSQL dependency. Its state is in-memory only (connection registry).

### Mandatory gRPC Contracts

**Contract 1 — Worker → TC** (already defined by Worker team):

Source: `worker/src/Onlyspans.Worker.Api/Protos/targets.proto`

```protobuf
service TargetsService {
  rpc ExecuteOnTarget(TargetExecutionRequest) returns (stream ExecutionResult);
}
```

TC must implement the **server side** of this exactly. The Worker client was already built against it — field names and numbers are frozen.

**Contract 2 — Agent → TC** (to be designed in Phase 2):

BiDi streaming service. Agents connect at startup and maintain a persistent connection.

### Pattern References

- **Program.cs**: `worker/src/Onlyspans.Worker.Api/Program.cs` (gRPC-only, no REST endpoints)
- **Modular Startup**: `variables/src/Onlyspans.Variables.Api/Startup.*.cs`
- **Server-side streaming gRPC**: `worker/src/Onlyspans.Worker.Api/Services/WorkerService.cs`
- **Dockerfile**: `variables/src/Onlyspans.Variables.Api/Dockerfile` (has health check + curl)
- **Startup.Grpc.cs**: `variables/src/Onlyspans.Variables.Api/Startup.Grpc.cs`
- **Startup.Logging.cs**: `variables/src/Onlyspans.Variables.Api/Startup.Logging.cs`
- **Startup.Healthz.cs**: `variables/src/Onlyspans.Variables.Api/Startup.Healthz.cs`

### Anti-Patterns to Prevent

- ❌ Do NOT add EF Core / PostgreSQL — TC has no database
- ❌ Do NOT implement execution logic (bash, SSH, kubectl) inside TC
- ❌ Do NOT use `AddDbContext` or `AddDbContextPool`
- ❌ Do NOT enable gRPC reflection outside `IsDevelopment()` guard
- ❌ Do NOT swallow `OperationCanceledException` in streaming loops
- ❌ Do NOT call `responseStream.WriteAsync()` without passing `CancellationToken`
- ❌ Do NOT forget `PrivateAssets="all"` on `Grpc.Tools`
- ❌ Do NOT make TC connect to target machines — agents connect TO TC (pull model, ADR-3)

---

## Phase 1: Project Foundation

**Goal**: Update `.csproj`, `appsettings.json`, implement `Program.cs` with modular startup skeleton.

### 1.1 Fix `compose.yaml` Typo

File: `compose.yaml`

Current broken path: `Onlyspans.TargetsContoller/Dockerfile` (missing 'l')
Fix to: `src/Onlyspans.TargetsController/Dockerfile`

Also expand `compose.yaml` to add a minimal dev setup (no postgres needed):

```yaml
services:
  targets-controller:
    image: onlyspans.targets-controller
    build:
      context: .
      dockerfile: src/Onlyspans.TargetsController/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
    ports:
      - "5001:8080"
    profiles:
      - all
```

### 1.2 Update `.csproj`

File: `src/Onlyspans.TargetsController/Onlyspans.TargetsController.csproj`

Replace the scaffolded content:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
  </PropertyGroup>

  <ItemGroup>
    <!-- Server-side: Worker calls TC via this contract -->
    <Protobuf Include="Protos/targets.proto" GrpcServices="Server" />
    <!-- Server-side: Agents connect to TC via this contract -->
    <Protobuf Include="Protos/agents.proto" GrpcServices="Server" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Grpc.AspNetCore" Version="2.70.0" />
    <PackageReference Include="Grpc.AspNetCore.Server.Reflection" Version="2.70.0" />
    <PackageReference Include="Grpc.Tools" Version="2.70.0" PrivateAssets="all" />
    <PackageReference Include="Serilog.AspNetCore" Version="9.0.0" />
    <PackageReference Include="Serilog.Formatting.Compact" Version="3.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.0.0" />
    <PackageReference Include="Strongly.Options" Version="1.0.0" />
  </ItemGroup>
</Project>
```

### 1.3 Update `appsettings.json`

Copy the Serilog + Kestrel pattern from `worker/appsettings.json`:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact"
        }
      }
    ],
    "Properties": {
      "Application": "Onlyspans.TargetsController"
    }
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "EndpointDefaults": {
      "Protocols": "Http2"
    }
  }
}
```

Update `appsettings.Development.json`:
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  }
}
```

### 1.4 Create Startup Skeleton

**`Startup.cs`**: Empty partial class declaration.
```csharp
namespace Onlyspans.TargetsController;
public static partial class Startup { }
```

**`Startup.Logging.cs`**: Copy from `variables/src/Onlyspans.Variables.Api/Startup.Logging.cs`, change namespace to `Onlyspans.TargetsController`.

### 1.5 Implement `Program.cs`

Copy the gRPC-only pattern from `worker/Program.cs`, without database or messaging:

```csharp
using Onlyspans.TargetsController;

var builder = WebApplication.CreateBuilder(args);

builder.Host.AddSerilog();

builder.Services
    .AddGrpcServices(builder.Environment)
    .AddConnectionRegistry()
    .AddHealthz();

var app = builder.Build();

app.UseGrpcServices();
app.UseHealthz();

app.MapGet("/", () => "Onlyspans.TargetsController");

app.Run();
```

### Verification Checklist (Phase 1)

- [ ] `dotnet build src/Onlyspans.TargetsController/Onlyspans.TargetsController.csproj` succeeds
- [ ] `grep "TargetsContoller" compose.yaml` returns nothing (typo fixed)
- [ ] `grep -r "EntityFramework\|Npgsql\|MigrationHosted" src/` returns nothing

---

## Phase 2: Proto Definitions

**Goal**: Define both gRPC contracts TC must implement.

### 2.1 `targets.proto` — Worker-facing contract

File: `src/Onlyspans.TargetsController/Protos/targets.proto`

**Copy exactly** from `worker/src/Onlyspans.Worker.Api/Protos/targets.proto`. Field numbers and names are frozen — the Worker client was already built against them.

```protobuf
syntax = "proto3";

package targets;

option csharp_namespace = "Targets.Communication";

service TargetsService {
  rpc ExecuteOnTarget(TargetExecutionRequest) returns (stream ExecutionResult);
}

message TargetExecutionRequest {
  string deployment_id = 1;
  string target_id = 2;
  string target_type = 3;
  string snapshot_path = 4;
  map<string, string> environment_variables = 5;
}

message ExecutionResult {
  string deployment_id = 1;
  int64 timestamp = 2;
  ResultType type = 3;
  string message = 4;
  optional bytes data = 5;
}

enum ResultType {
  RESULT_TYPE_UNSPECIFIED = 0;
  RESULT_TYPE_LOG = 1;
  RESULT_TYPE_PROGRESS = 2;
  RESULT_TYPE_SUCCESS = 3;
  RESULT_TYPE_ERROR = 4;
}
```

Delete the placeholder `Protos/greet.proto`.

### 2.2 `agents.proto` — Agent-facing BiDi contract

File: `src/Onlyspans.TargetsController/Protos/agents.proto`

This defines the bidirectional stream between agents and TC. Agents connect at startup and hold the connection open. TC sends commands; agents stream logs and completion back.

```protobuf
syntax = "proto3";

package agents;

option csharp_namespace = "Agents.Communication";

service AgentService {
  // Agent establishes BiDi stream on startup and holds it for its lifetime.
  // First message must be AgentRegistration.
  // TC sends DeployCommand messages; agent responds with ExecutionLog and ExecutionCompleted.
  rpc Connect(stream AgentMessage) returns (stream ControllerMessage);
}

// Messages flowing from Agent → TC
message AgentMessage {
  oneof payload {
    AgentRegistration registration = 1;
    ExecutionLog log = 2;
    ExecutionCompleted completed = 3;
  }
}

// Messages flowing from TC → Agent
message ControllerMessage {
  oneof payload {
    DeployCommand deploy = 1;
    Ping ping = 2;
  }
}

message AgentRegistration {
  string target_id = 1;
  string agent_type = 2; // "vm" or "kubernetes"
  string version = 3;
}

message ExecutionLog {
  string deployment_id = 1;
  int64 timestamp = 2;     // Unix milliseconds
  LogLevel level = 3;
  string message = 4;
}

enum LogLevel {
  LOG_LEVEL_UNSPECIFIED = 0;
  LOG_LEVEL_DEBUG = 1;
  LOG_LEVEL_INFO = 2;
  LOG_LEVEL_WARNING = 3;
  LOG_LEVEL_ERROR = 4;
}

message ExecutionCompleted {
  string deployment_id = 1;
  bool success = 2;
  optional string error_message = 3;
}

message DeployCommand {
  string deployment_id = 1;
  string snapshot_path = 2;
  map<string, string> environment_variables = 3;
}

message Ping {}
```

### Verification Checklist (Phase 2)

- [ ] `dotnet build` succeeds (both protos compile, C# generated)
- [ ] `grep "Targets.Communication\|Agents.Communication" obj/` finds generated files
- [ ] `diff <(cat worker/.../targets.proto) <(cat src/.../Protos/targets.proto)` — no differences
- [ ] `grep "greet.proto\|GreeterService" src/` returns nothing

---

## Phase 3: Connection Registry

**Goal**: Implement the in-memory registry that tracks active agent connections and bridges Worker requests to agent streams.

### 3.1 Architecture of the Registry

The registry must bridge two independent gRPC streams:
- **Stream A**: Agent → TC BiDi (`AgentsGrpcService.Connect`)
- **Stream B**: Worker → TC server stream (`TargetsGrpcService.ExecuteOnTarget`)

Bridging mechanism:
- For each active agent, store `IServerStreamWriter<ControllerMessage>` (to send commands to the agent)
- For each active deployment, store `Channel<ExecutionResult>` (to pipe agent logs to Worker)
- When Worker calls `ExecuteOnTarget` → write `DeployCommand` to agent's stream; read from deployment's channel and forward to Worker's response stream
- When agent sends `ExecutionLog`/`ExecutionCompleted` → write to deployment's channel

### 3.2 Interface

File: `src/Onlyspans.TargetsController/Registry/ITargetConnectionRegistry.cs`

```csharp
using System.Threading.Channels;
using Agents.Communication;

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
```

### 3.3 Implementation

File: `src/Onlyspans.TargetsController/Registry/TargetConnectionRegistry.cs`

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;
using Agents.Communication;
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
```

### 3.4 Register in DI

File: `src/Onlyspans.TargetsController/Startup.Registry.cs`

```csharp
using Onlyspans.TargetsController.Registry;

namespace Onlyspans.TargetsController;

public static partial class Startup
{
    public static IServiceCollection AddConnectionRegistry(this IServiceCollection services)
    {
        services.AddSingleton<ITargetConnectionRegistry, TargetConnectionRegistry>();
        return services;
    }
}
```

### Verification Checklist (Phase 3)

- [ ] `grep "ITargetConnectionRegistry\|TargetConnectionRegistry" src/` finds interface + implementation + registration
- [ ] `grep "AddSingleton<ITargetConnectionRegistry" src/` confirms singleton (registry must outlive individual requests)
- [ ] `grep "ConcurrentDictionary\|Channel<" src/Registry/` confirms thread-safe types are used

---

## Phase 4: gRPC Services

**Goal**: Implement both gRPC services using the registry.

### 4.1 `TargetsGrpcService` — Worker-facing

File: `src/Onlyspans.TargetsController/Services/TargetsGrpcService.cs`

Delete placeholder `Services/GreeterService.cs` first.

Pattern: server-side streaming from `worker/Services/WorkerService.cs`.

```csharp
using Grpc.Core;
using Targets.Communication;
using Agents.Communication;
using Onlyspans.TargetsController.Registry;

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
            logger.LogWarning(
                "Deployment {DeploymentId} cancelled", request.DeploymentId);
            registry.CompleteDeployment(request.DeploymentId);
        }

        logger.LogInformation(
            "ExecuteOnTarget completed: deployment={DeploymentId}", request.DeploymentId);
    }
}
```

### 4.2 `AgentsGrpcService` — Agent-facing BiDi

File: `src/Onlyspans.TargetsController/Services/AgentsGrpcService.cs`

Pattern: bidirectional streaming. The agent sends registration first, then log chunks. TC sends commands in the opposite direction.

```csharp
using Grpc.Core;
using Agents.Communication;
using Targets.Communication;
using Onlyspans.TargetsController.Registry;

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
```

### 4.3 `Startup.Grpc.cs`

File: `src/Onlyspans.TargetsController/Startup.Grpc.cs`

Copy from `variables/Startup.Grpc.cs`, register both services:

```csharp
namespace Onlyspans.TargetsController;

public static partial class Startup
{
    public static IServiceCollection AddGrpcServices(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddGrpc();

        if (environment.IsDevelopment())
            services.AddGrpcReflection();

        return services;
    }

    public static WebApplication UseGrpcServices(this WebApplication app)
    {
        app.MapGrpcService<Services.TargetsGrpcService>();
        app.MapGrpcService<Services.AgentsGrpcService>();

        if (app.Environment.IsDevelopment())
            app.MapGrpcReflectionService();

        return app;
    }
}
```

### Verification Checklist (Phase 4)

- [ ] `dotnet build` succeeds
- [ ] `grep "TargetsService.TargetsServiceBase\|AgentService.AgentServiceBase" src/` finds both implementations
- [ ] `grep "MapGrpcService" src/` finds two registrations
- [ ] `grep "GreeterService" src/` returns nothing (placeholder deleted)
- [ ] `grep "catch.*OperationCanceledException" src/Services/AgentsGrpcService.cs` is NOT inside a `when` that swallows it — only in the outer `catch` for logging

---

## Phase 5: Health Checks

**Goal**: Add `/health` and `/health/ready` endpoints (no database check needed).

### 5.1 `Startup.Healthz.cs`

File: `src/Onlyspans.TargetsController/Startup.Healthz.cs`

Copy from `variables/Startup.Healthz.cs`, removing the NpgSql check (no DB):

```csharp
namespace Onlyspans.TargetsController;

public static partial class Startup
{
    public static IServiceCollection AddHealthz(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }

    public static WebApplication UseHealthz(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/health/ready");
        return app;
    }
}
```

### Verification Checklist (Phase 5)

- [ ] `curl http://localhost:5001/health` → 200
- [ ] `grep "MapHealthChecks" src/` finds both endpoints

---

## Phase 6: Dockerfile & Docker Compose

**Goal**: Working container build for local development.

### 6.1 Implement `Dockerfile`

File: `src/Onlyspans.TargetsController/Dockerfile`

Copy from `variables/src/Onlyspans.Variables.Api/Dockerfile`, changing:
- Solution file name: `Onlyspans.TargetsController.slnx`
- Project path: `src/Onlyspans.TargetsController/Onlyspans.TargetsController.csproj`
- Source copy path: `src/Onlyspans.TargetsController/`
- Entry point DLL: `Onlyspans.TargetsController.dll`
- Labels: title/description/source for TargetsController

Key sections to preserve from the Variables Dockerfile:
- 3-stage build (build → publish → runtime)
- `apt-get install curl` in runtime stage (for `HEALTHCHECK`)
- `HEALTHCHECK CMD curl --fail http://localhost:8080/health`
- `ENV ASPNETCORE_URLS=http://+:8080`

### 6.2 Update `compose.yaml`

Finalized version (no postgres, no kafka — TC has no infrastructure dependencies):

```yaml
services:
  targets-controller:
    image: onlyspans.targets-controller
    build:
      context: .
      dockerfile: src/Onlyspans.TargetsController/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
    ports:
      - "5001:8080"
    profiles:
      - api
      - all
```

### Verification Checklist (Phase 6)

- [ ] `docker build -t targets-controller -f src/Onlyspans.TargetsController/Dockerfile .` succeeds
- [ ] `docker run --rm -p 5001:8080 targets-controller` starts without errors
- [ ] `curl http://localhost:5001/health` → 200 from container

---

## Phase 7: Final Verification

### Full Build & Smoke Test

```bash
# Build
dotnet build src/Onlyspans.TargetsController/Onlyspans.TargetsController.csproj

# Confirm no EF Core / database packages crept in
grep -r "EntityFramework\|Npgsql\|MigrationHosted" src/
# Expected: no output

# Confirm both proto services are mapped
grep "MapGrpcService" src/
# Expected: TargetsGrpcService and AgentsGrpcService

# Confirm targets.proto matches Worker's client-side exactly
diff \
  ../worker/src/Onlyspans.Worker.Api/Protos/targets.proto \
  src/Onlyspans.TargetsController/Protos/targets.proto
# Expected: no differences

# Start service and check health
dotnet run --project src/Onlyspans.TargetsController &
sleep 3
curl http://localhost:5001/health     # → 200
curl http://localhost:5001/health/ready  # → 200

# gRPC reflection (dev mode)
grpcurl -plaintext localhost:5001 list
# Expected:
# agents.AgentService
# targets.TargetsService

# Docker build
docker build -t targets-controller -f src/Onlyspans.TargetsController/Dockerfile .
```

### Anti-Pattern Final Check

- [ ] No `AddDbContext`, `AddDbContextPool`, `MigrateAsync` anywhere in `src/`
- [ ] No direct SSH/bash/kubectl execution in TC services
- [ ] `ITargetConnectionRegistry` is registered as **Singleton** (not Scoped/Transient)
- [ ] Both `catch (OperationCanceledException)` in streaming loops do NOT re-throw inside `finally` that calls `UnregisterAgent` — agent is always unregistered
- [ ] `PrivateAssets="all"` present on `Grpc.Tools` in `.csproj`
- [ ] No gRPC reflection outside `IsDevelopment()` guard

---

## Complete File Checklist

| File | Action | Pattern Source |
|------|--------|----------------|
| `compose.yaml` | Fix typo + expand | — |
| `src/.../Onlyspans.TargetsController.csproj` | Replace packages, add 2 Protobuf items | `variables.csproj` minus EF Core |
| `src/.../appsettings.json` | Rewrite (Serilog + Kestrel) | `worker/appsettings.json` |
| `src/.../appsettings.Development.json` | Rewrite | `worker/appsettings.Development.json` |
| `src/.../Program.cs` | Implement | `worker/Program.cs` |
| `src/.../Startup.cs` | Create | `variables/Startup.cs` |
| `src/.../Startup.Logging.cs` | Create | `variables/Startup.Logging.cs` |
| `src/.../Startup.Grpc.cs` | Create (both services) | `variables/Startup.Grpc.cs` |
| `src/.../Startup.Registry.cs` | Create | — |
| `src/.../Startup.Healthz.cs` | Create (no DB check) | `variables/Startup.Healthz.cs` |
| `src/.../Protos/targets.proto` | **Copy exactly** from Worker | `worker/Protos/targets.proto` |
| `src/.../Protos/agents.proto` | Create (new BiDi contract) | — |
| `src/.../Protos/greet.proto` | **DELETE** | — |
| `src/.../Services/TargetsGrpcService.cs` | Create | `worker/Services/WorkerService.cs` |
| `src/.../Services/AgentsGrpcService.cs` | Create | BiDi pattern (unique to TC) |
| `src/.../Services/GreeterService.cs` | **DELETE** | — |
| `src/.../Registry/ITargetConnectionRegistry.cs` | Create | — |
| `src/.../Registry/TargetConnectionRegistry.cs` | Create | — |
| `src/.../Dockerfile` | Implement | `variables/Dockerfile` |
| `src/.../Properties/launchSettings.json` | Implement | ASP.NET Core standard |
