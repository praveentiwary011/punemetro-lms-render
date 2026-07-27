# ------------------------------------------------------------------
# Pune Metro LMS — container image (works on any Docker host / cloud)
# Build:  docker build -t punemetro-lms .
# Run:    docker compose up -d        (see docker-compose.yml)
# ------------------------------------------------------------------

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LMS.Web/LMS.Web.csproj LMS.Web/
RUN dotnet restore LMS.Web/LMS.Web.csproj
COPY LMS.Web/ LMS.Web/
RUN dotnet publish LMS.Web/LMS.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Data & user content live outside the image (mount volumes here)
RUN mkdir -p /data /app/wwwroot/uploads /app/wwwroot/scorm

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    LMS_DISABLE_HTTPS_REDIRECT=true \
    DatabaseProvider=Sqlite \
    ConnectionStrings__Sqlite="Data Source=/data/lms.db"

EXPOSE 8080
# Honour the PORT the hosting platform (e.g. Render) provides; default 8080 locally.
ENTRYPOINT ["/bin/sh", "-c", "export ASPNETCORE_URLS=http://+:${PORT:-8080}; exec dotnet LMS.Web.dll"]
