# syntax=docker/dockerfile:1
# OrderService — HTTP API entry point of the pipeline.
# Build context is repo root so we can copy the shared library alongside the service.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj first so `dotnet restore` can be cached across source edits.
COPY shared/Reservoir.BuildingBlocks/Reservoir.BuildingBlocks.csproj shared/Reservoir.BuildingBlocks/
COPY services/order/OrderService.csproj services/order/
RUN dotnet restore services/order/OrderService.csproj

COPY shared/Reservoir.BuildingBlocks/ shared/Reservoir.BuildingBlocks/
COPY services/order/ services/order/

RUN dotnet publish services/order/OrderService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5294
EXPOSE 5294

ENTRYPOINT ["dotnet", "OrderService.dll"]
