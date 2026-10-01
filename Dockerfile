# syntax=docker/dockerfile:1
# Same image locally (docker compose) and in production (Render/Railway/Fly).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json SeatReservation.slnx ./
COPY src/SeatReservation.Api/SeatReservation.Api.csproj src/SeatReservation.Api/
RUN dotnet restore src/SeatReservation.Api/SeatReservation.Api.csproj
COPY src/ src/
RUN dotnet publish src/SeatReservation.Api/SeatReservation.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
# Workstation GC suits small (≤1 vCPU) containers; PORT is honoured if the platform sets it.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s \
  CMD wget -qO- http://127.0.0.1:${PORT:-8080}/health/live >/dev/null || exit 1
ENTRYPOINT ["dotnet", "SeatReservation.Api.dll"]
