# Imagen única: la API sirve también la interfaz compilada.
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
# Usuario sin privilegios (incluido en la imagen de .NET); el volumen de llaves hereda el dueño de /keys.
# libgssapi: Npgsql la busca al conectar; sin ella deja un error en el log.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/* \
    && mkdir /keys && chown app /keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Apolo.Api.dll"]
