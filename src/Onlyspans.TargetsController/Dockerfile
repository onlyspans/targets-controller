# Build stage
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution file
COPY Onlyspans.TargetsController.slnx ./

# Copy project file
COPY src/Onlyspans.TargetsController/Onlyspans.TargetsController.csproj src/Onlyspans.TargetsController/

# Restore dependencies
RUN dotnet restore src/Onlyspans.TargetsController/Onlyspans.TargetsController.csproj

# Copy source code
COPY src/Onlyspans.TargetsController/ src/Onlyspans.TargetsController/

# Build application
WORKDIR /src/src/Onlyspans.TargetsController
RUN dotnet build -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=publish /app/publish .

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Expose ports
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl --fail http://localhost:8080/health || exit 1

# Labels
LABEL org.opencontainers.image.title="Targets Controller" \
      org.opencontainers.image.description="Connection registry for deployment target agents" \
      org.opencontainers.image.vendor="Onlyspans"

# Run application
ENTRYPOINT ["dotnet", "Onlyspans.TargetsController.dll"]
