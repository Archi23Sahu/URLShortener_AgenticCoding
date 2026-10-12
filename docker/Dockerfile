# Build context is the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Restore first so the NuGet layer is cached until the csproj changes.
COPY src/UrlShortener.csproj src/
RUN dotnet restore src/UrlShortener.csproj

COPY src/ src/
RUN dotnet publish src/UrlShortener.csproj -c Release -o /out --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The aspnet image ships a non-root "app" user from .NET 8 onward.
USER app

ENTRYPOINT ["dotnet", "UrlShortener.dll"]