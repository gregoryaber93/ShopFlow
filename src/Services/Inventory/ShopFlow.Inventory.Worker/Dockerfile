# syntax=docker/dockerfile:1
#
# inventory-worker image for Azure Container Apps (no ingress, KEDA scaling).
#   docker build -f src/Services/Inventory/ShopFlow.Inventory.Worker/Dockerfile -t inventory-worker .
# Same structure as inventory-api; the comments about layers and the chiseled image are there.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Inventory/ src/Services/Inventory/

RUN dotnet restore src/Services/Inventory/ShopFlow.Inventory.Worker/ShopFlow.Inventory.Worker.csproj
RUN dotnet publish src/Services/Inventory/ShopFlow.Inventory.Worker/ShopFlow.Inventory.Worker.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .

# Port only for Container Apps health probes (liveness/readiness), no ingress.
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShopFlow.Inventory.Worker.dll"]
