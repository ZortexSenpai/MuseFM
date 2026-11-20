# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MuseFM/MuseFM.csproj src/MuseFM/
RUN dotnet restore src/MuseFM/MuseFM.csproj
COPY src/MuseFM/ src/MuseFM/
RUN dotnet publish src/MuseFM/MuseFM.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    MuseFM__DataDirectory=/data
# The image's non-root "app" user must own the data volume.
RUN mkdir -p /data && chown "$APP_UID" /data
COPY --from=build /app .
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "MuseFM.dll"]
