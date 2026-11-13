FROM node:24-alpine AS frontend-build
WORKDIR /source
COPY package.json package-lock.json ./
RUN npm ci
COPY angular.json tsconfig.json tsconfig.app.json tsconfig.spec.json ./
COPY scripts/ scripts/
COPY client/ client/
COPY public/ public/
# Es un token público pk; formará parte del frontend distribuido.
ARG MAPBOX_PUBLIC_TOKEN
ENV MAPBOX_PUBLIC_TOKEN=$MAPBOX_PUBLIC_TOKEN
RUN npm run build:clean

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /source
COPY server/MineApp.Api.csproj server/
RUN dotnet restore server/MineApp.Api.csproj
COPY server/ server/
RUN dotnet publish server/MineApp.Api.csproj -c Release -o /publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend-build /publish/ ./
COPY --from=frontend-build /source/dist/mine-app/browser/ /app/wwwroot/
RUN mkdir -p /var/lib/mine-app/keys && chown -R app:app /var/lib/mine-app
USER app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DataProtection__KeyRingPath=/var/lib/mine-app/keys \
    Spa__Enabled=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "MineApp.Api.dll"]
