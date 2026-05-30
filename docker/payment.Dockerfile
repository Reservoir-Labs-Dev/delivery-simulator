# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY shared/Reservoir.BuildingBlocks/Reservoir.BuildingBlocks.csproj shared/Reservoir.BuildingBlocks/
COPY services/payment/PaymentService.csproj services/payment/
RUN dotnet restore services/payment/PaymentService.csproj

COPY shared/Reservoir.BuildingBlocks/ shared/Reservoir.BuildingBlocks/
COPY services/payment/ services/payment/

RUN dotnet publish services/payment/PaymentService.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5203
EXPOSE 5203

ENTRYPOINT ["dotnet", "PaymentService.dll"]
