# syntax=docker/dockerfile:1
# DashboardApi — hosts the SignalR hub at /hubs/orders and fans the message bus
# into connected browser clients.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY shared/Reservoir.BuildingBlocks/Reservoir.BuildingBlocks.csproj shared/Reservoir.BuildingBlocks/
COPY services/dashboard-api/DashboardApi.csproj services/dashboard-api/
RUN dotnet restore services/dashboard-api/DashboardApi.csproj

COPY shared/Reservoir.BuildingBlocks/ shared/Reservoir.BuildingBlocks/
COPY services/dashboard-api/ services/dashboard-api/

RUN dotnet publish services/dashboard-api/DashboardApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "DashboardApi.dll"]
