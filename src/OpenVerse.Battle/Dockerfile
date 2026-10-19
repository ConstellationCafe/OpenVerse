# Build context is the repository root:
#   docker build -f src/OpenVerse.Battle/Dockerfile .
# The engine assemblies (src/OpenVerse.Engine/lib) are not in the repository, so this image relays only, exactly like
# a host without them. Mount the card master at /app/data/card_master_full.csv.gz.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OpenVerse.Battle/OpenVerse.Battle.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# 0.0.0.0, not [::]: the seat check compares plain IPv4 strings
ENV ASPNETCORE_URLS=http://0.0.0.0:3001
EXPOSE 3001
USER $APP_UID
ENTRYPOINT ["dotnet", "OpenVerse.Battle.dll"]
