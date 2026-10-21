# Build context is the repository root:
#   docker build -f src/OpenVerse.Battle/Dockerfile .
# Mount the card master at /app/data/card_master_full.csv.gz.
#
# The engine assemblies (src/OpenVerse.Engine/lib: the client's own DLLs plus the headless Unity shim) are not in the
# repository, so by default this image relays only. A host that has built them hands the folder in as a named build
# context and the battle server is built with the engine, which fills in what the relay cannot (costs, condition
# answers) and cuts the desyncs:
#   docker build -f src/OpenVerse.Battle/Dockerfile --build-arg ENGINE=engine --build-context engine=/path/to/engine-lib .
# The folder never enters the repository; it ends up only in the image built on that host.
ARG ENGINE=engine-none

FROM scratch AS engine-none

FROM ${ENGINE} AS engine-src

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=engine-src . /engine-lib/
RUN if [ -f /engine-lib/Assembly-CSharp.dll ]; then \
      echo "building with the engine from the engine build context"; export OPENVERSE_ENGINE_DIR=/engine-lib/; \
    else echo "no engine: relay only"; fi; \
    dotnet publish src/OpenVerse.Battle/OpenVerse.Battle.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# 0.0.0.0, not [::]: the seat check compares plain IPv4 strings
ENV ASPNETCORE_URLS=http://0.0.0.0:3001
EXPOSE 3001
USER $APP_UID
ENTRYPOINT ["dotnet", "OpenVerse.Battle.dll"]
