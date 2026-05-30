# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY shared/Reservoir.BuildingBlocks/Reservoir.BuildingBlocks.csproj shared/Reservoir.BuildingBlocks/
COPY services/delivery/DeliveryService.csproj services/delivery/
RUN dotnet restore services/delivery/DeliveryService.csproj

COPY shared/Reservoir.BuildingBlocks/ shared/Reservoir.BuildingBlocks/
COPY services/delivery/ services/delivery/

RUN dotnet publish services/delivery/DeliveryService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5097
EXPOSE 5097

ENTRYPOINT ["dotnet", "DeliveryService.dll"]
