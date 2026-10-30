# Multi-stage image for the three runtime processes (webfrontend, apiservice, solver-worker).
# Build args select the project. The AppHost is not included: Compose replaces Aspire.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nuget.config ./
COPY RummiHub.Core/RummiHub.Core.csproj RummiHub.Core/
COPY RummiHub.ServiceDefaults/RummiHub.ServiceDefaults.csproj RummiHub.ServiceDefaults/
COPY RummiHub.ApiService/RummiHub.ApiService.csproj RummiHub.ApiService/
COPY RummiHub.Solver.Worker/RummiHub.Solver.Worker.csproj RummiHub.Solver.Worker/
COPY RummiHub.SolverBench/RummiHub.SolverBench.csproj RummiHub.SolverBench/
COPY RummiHub.Web/RummiHub.Web.csproj RummiHub.Web/

# One restore layer shared by every process image. Web pulls the bench and the worker.
RUN dotnet restore RummiHub.Web/RummiHub.Web.csproj \
    && dotnet restore RummiHub.ApiService/RummiHub.ApiService.csproj \
    && dotnet restore RummiHub.Solver.Worker/RummiHub.Solver.Worker.csproj

COPY . .
ARG PROJECT
# --no-restore drops the Blazor framework static assets (blazor.web.js) from the
# publish manifest, so the interactive circuit 404s. Restore is cached in the layer above.
RUN test -n "$PROJECT" \
    && dotnet publish "$PROJECT" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG ASSEMBLY
RUN test -n "$ASSEMBLY" \
    && apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/rummihub/keys /var/rummihub/seeds \
    && chown -R app:app /var/rummihub

WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
USER app

# The aspnet image listens on 8080 as the non-root user. Production is the default
# for this image; Aspire local dev does not use it.
# GameOptions__SeedsFile: the lobby seed list. Compose mounts there a volume shared by
# production and the test site; the directory exists (owned by app) so a new volume is too.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    GameOptions__SeedsFile=/var/rummihub/seeds/seed-configs.json \
    DOTNET_ENVIRONMENT=Production \
    ASSEMBLY=${ASSEMBLY}

EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$ASSEMBLY\""]
