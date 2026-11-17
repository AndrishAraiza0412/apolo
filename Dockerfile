# Single image: the API also serves the built interface.
FROM node:24-alpine AS web
WORKDIR /web
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY backend/Apolo.Api/Apolo.Api.csproj Apolo.Api/
RUN dotnet restore Apolo.Api/Apolo.Api.csproj
COPY backend/Apolo.Api/ Apolo.Api/
RUN dotnet publish Apolo.Api/Apolo.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8080 DataProtection__Path=/keys
# Unprivileged user (included in the .NET image); the keys volume inherits the owner of /keys.
# libgssapi: Npgsql looks for it when connecting; without it, it leaves an error in the log.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/* \
    && mkdir /keys && chown app /keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Apolo.Api.dll"]
