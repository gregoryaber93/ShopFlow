# syntax=docker/dockerfile:1
#
# readmodels-api image for Azure Container Apps. Built from the repository root:
#   docker build -f src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Api/Dockerfile -t readmodels-api .
# In CI the runner builds the image (.github/workflows/_build.yml), Trivy scans it, and only then is it pushed to
# Azure Container Registry. The tag is the commit SHA: the same image goes through dev, test and prod (build once, deploy many).

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Simple variant: whole projects are copied before restore, so every code change invalidates the NuGet package
# layer. The optimization (copy *.csproj first, then restore, then the rest) shortens the build, but in a monorepo
# it requires recreating the project directory structure and is skipped here for readability.
COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/CustomerReadModels/ src/Services/CustomerReadModels/

RUN dotnet restore src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Api/ShopFlow.CustomerReadModels.Api.csproj
RUN dotnet publish src/Services/CustomerReadModels/ShopFlow.CustomerReadModels.Api/ShopFlow.CustomerReadModels.Api.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false

# AZURE/WHY: the "chiseled" image (Ubuntu without a shell, package manager or root) contains only the .NET runtime.
# A smaller attack surface and fewer vulnerabilities to patch; the image scanner in CI (Trivy) has less to report.
# Consequence: a running container has no `sh`, so diagnostics go through logs and OpenTelemetry rather than
# `az containerapp exec`.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .

# .NET images listen on 8080 and run as the "app" user (UID 1654). targetPort in Container Apps = 8080.
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShopFlow.CustomerReadModels.Api.dll"]
