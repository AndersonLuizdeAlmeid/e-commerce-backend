# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ECommerce.Domain/ECommerce.Domain.csproj src/ECommerce.Domain/
COPY src/ECommerce.Application/ECommerce.Application.csproj src/ECommerce.Application/
COPY src/ECommerce.Infrastructure/ECommerce.Infrastructure.csproj src/ECommerce.Infrastructure/
COPY src/ECommerce.Api/ECommerce.Api.csproj src/ECommerce.Api/
COPY src/ECommerce.Migrator/ECommerce.Migrator.csproj src/ECommerce.Migrator/
RUN dotnet restore src/ECommerce.Api/ECommerce.Api.csproj \
 && dotnet restore src/ECommerce.Migrator/ECommerce.Migrator.csproj
COPY src/ src/
RUN dotnet publish src/ECommerce.Api/ECommerce.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish src/ECommerce.Migrator/ECommerce.Migrator.csproj -c Release -o /out/migrator --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS migrator
WORKDIR /app
COPY --from=build /out/migrator .
ENTRYPOINT ["dotnet", "ECommerce.Migrator.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ECommerce.Api.dll"]
