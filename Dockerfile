# Build ------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so the restore layer is cached against dependency changes rather than against
# every source edit. Without this, changing one line of C# re-downloads every package.
COPY Directory.Build.props global.json NuGet.Config GeopoliticsDashboard.sln ./
COPY src/Geopolitics.Domain/*.csproj src/Geopolitics.Domain/
COPY src/Geopolitics.Application/*.csproj src/Geopolitics.Application/
COPY src/Geopolitics.Infrastructure/*.csproj src/Geopolitics.Infrastructure/
COPY src/Geopolitics.Api/*.csproj src/Geopolitics.Api/
COPY src/Geopolitics.Workers/*.csproj src/Geopolitics.Workers/
COPY tests/Geopolitics.UnitTests/*.csproj tests/Geopolitics.UnitTests/
COPY tests/Geopolitics.IntegrationTests/*.csproj tests/Geopolitics.IntegrationTests/
COPY tests/Geopolitics.AiEvaluationTests/*.csproj tests/Geopolitics.AiEvaluationTests/
COPY tests/Geopolitics.ConflictBenchmark/*.csproj tests/Geopolitics.ConflictBenchmark/
RUN dotnet restore GeopoliticsDashboard.sln

COPY . .
RUN dotnet publish src/Geopolitics.Api/Geopolitics.Api.csproj \
    --configuration Release --no-restore --output /app/publish

# Runtime -----------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    # SQLite needs a writable path. Named here rather than left to the default so the volume a
    # deployment has to mount is explicit instead of discovered when data vanishes on restart.
    ConnectionStrings__Geopolitics="Data Source=/app/data/geoconflux.db"

COPY --from=build /app/publish .

# The image ships a non-root user; the data directory is created and handed to it before the switch,
# because a process that cannot write its own database starts and then fails on first request.
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
USER $APP_UID

EXPOSE 8080

# Liveness, not readiness. /health/live deliberately runs no checks, so a saturated queue or a slow
# database cannot cause an orchestrator to restart a process that is working correctly. Readiness
# belongs to /api/health, which an orchestrator should poll separately and treat differently.
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/Geopolitics.Api.dll", "--health-probe"]

ENTRYPOINT ["dotnet", "Geopolitics.Api.dll"]
