FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the packages layer is cached until a csproj changes.
COPY JewelryManager.Api/JewelryManager.Api.csproj JewelryManager.Api/
RUN dotnet restore JewelryManager.Api/JewelryManager.Api.csproj

COPY JewelryManager.Api/ JewelryManager.Api/
RUN dotnet publish JewelryManager.Api/JewelryManager.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Render sends traffic to port 10000 by default.
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "JewelryManager.Api.dll"]
