# Frunchy for self-hosting (deploy/compose.yaml builds it). Build context is the repository root:
#   docker build -t frunchy .
# Only FrunchyBackend.fsproj is restored and published: the tests and the AppHost are not part of the image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first, so the packages layer is cached until the project file changes
COPY global.json FrunchyBackend.fsproj ./
RUN dotnet restore FrunchyBackend.fsproj
COPY . .
RUN dotnet publish FrunchyBackend.fsproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The aspnet image listens on 8080 as an unprivileged user ("app"); no sample data is read at runtime
ENV ASPNETCORE_HTTP_PORTS=8080
# Where deploy/compose.yaml keeps the data-protection keys (Frunchy:DataProtection:KeysPath), so sessions survive
# a restart. Created here so a fresh named volume mounted on it is writable by the unprivileged user.
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FrunchyBackend.dll"]
