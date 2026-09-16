# syntax=docker/dockerfile:1

# Restore in its own layer so a code change does not re-download every NuGet package.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS builder

WORKDIR /build

COPY OrderService.sln .
COPY src/OrderService.Domain/*.csproj src/OrderService.Domain/
COPY src/OrderService.Application/*.csproj src/OrderService.Application/
COPY src/OrderService.Infrastructure/*.csproj src/OrderService.Infrastructure/
COPY src/OrderService.Api/*.csproj src/OrderService.Api/
COPY tests/OrderService.Domain.Tests/*.csproj tests/OrderService.Domain.Tests/
COPY tests/OrderService.Api.Tests/*.csproj tests/OrderService.Api.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/OrderService.Api -c Release -o /app/publish --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl is needed by the container healthcheck below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=builder /app/publish .

# Run as an unprivileged user; containers should never need root.
RUN useradd --create-home --uid 10001 appuser
USER appuser

ENV ASPNETCORE_URLS=http://+:8082 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

EXPOSE 8082

HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD curl --fail http://localhost:8082/health/live || exit 1

ENTRYPOINT ["dotnet", "OrderService.Api.dll"]
