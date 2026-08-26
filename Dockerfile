FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# Data Protection keys live here (mounted as a volume in compose.yaml); create it owned by the
# non-root app user so a fresh named volume inherits writable permissions.
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys && chown -R $APP_UID:$APP_UID /home/app/.aspnet \
    && mkdir -p /app/data && chown $APP_UID:$APP_UID /app/data
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["DiSkyAtlas.csproj", "./"]
RUN dotnet restore "DiSkyAtlas.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "./DiSkyAtlas.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./DiSkyAtlas.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DiSkyAtlas.dll"]
