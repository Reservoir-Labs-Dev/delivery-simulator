# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY shared/Reservoir.BuildingBlocks/Reservoir.BuildingBlocks.csproj shared/Reservoir.BuildingBlocks/
COPY services/kitchen/KitchenService.csproj services/kitchen/
RUN dotnet restore services/kitchen/KitchenService.csproj

COPY shared/Reservoir.BuildingBlocks/ shared/Reservoir.BuildingBlocks/
COPY services/kitchen/ services/kitchen/

RUN dotnet publish services/kitchen/KitchenService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5233
EXPOSE 5233

ENTRYPOINT ["dotnet", "KitchenService.dll"]
