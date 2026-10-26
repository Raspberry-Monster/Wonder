FROM node:24-bookworm-slim AS frontend
WORKDIR /web
COPY src/Wonder.Web/package*.json ./
COPY src/Wonder.Web/tools/package*.json ./tools/
RUN npm ci --no-fund --no-audit
COPY src/Wonder.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
COPY --from=frontend /web/dist ./src/Wonder.Web/dist
RUN dotnet publish src/Wonder.Host/Wonder.Host.csproj -c Release -o /out -p:SkipFrontendBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
USER root
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p data/keys data/media && chown -R app:app /app
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Wonder.Host.dll"]
