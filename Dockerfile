FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Copy solution and project files
COPY UIBackend.slnx .
COPY src/UIBackend.Domain/UIBackend.Domain.csproj src/UIBackend.Domain/
COPY src/UIBackend.Application/UIBackend.Application.csproj src/UIBackend.Application/
COPY src/UIBackend.Infrastructure/UIBackend.Infrastructure.csproj src/UIBackend.Infrastructure/
COPY src/UIBackend.Api/UIBackend.Api.csproj src/UIBackend.Api/
COPY tests/UIBackend.UnitTests/UIBackend.UnitTests.csproj tests/UIBackend.UnitTests/

# Restore dependencies
RUN dotnet restore UIBackend.slnx

# Copy source code and build
COPY . .
WORKDIR /src/src/UIBackend.Api
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV MEDIA_STORAGE_PATH=/opt/visionx/media

# Create media storage volume directory
RUN mkdir -p /opt/visionx/media && chown -R $APP_UID:$APP_UID /opt/visionx/media

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "UIBackend.Api.dll"]
