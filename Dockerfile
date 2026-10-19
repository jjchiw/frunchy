# Frunchy for self-hosting and releases (deploy/compose.yaml builds it; .github/workflows/release.yml pushes it).
# Build context is the repository root:
#   docker build --build-arg VERSION=0.1.0 -t frunchy .
# Only FrunchyBackend.fsproj is restored and published: the tests and the AppHost are not part of the image.
#
# Multi-platform without emulation: the build stage always runs on the builder's own platform and cross-publishes
# for the target (`-a $TARGETARCH`); the final stage only copies files (no RUN), so no QEMU is needed for arm64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# The release version, stamped into the assembly and named in the startup log (the workflow passes the tag without its `v`)
ARG VERSION=0.0.0-dev
WORKDIR /src
# Restore first, so the packages layer is cached until the project file changes
COPY global.json FrunchyBackend.fsproj ./
RUN dotnet restore FrunchyBackend.fsproj -a $TARGETARCH
COPY . .
RUN dotnet publish FrunchyBackend.fsproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false /p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The aspnet image listens on 8080 as an unprivileged user ("app"); no sample data is read at runtime.
# The data-protection keys are in Postgres, so the container needs no volume.
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FrunchyBackend.dll"]
