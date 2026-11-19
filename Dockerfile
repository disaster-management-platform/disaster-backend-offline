# syntax=docker/dockerfile:1.7
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.14-noble
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY NuGet.Config ./
COPY ["DisasterLocation/DisasterLocation.csproj", "DisasterLocation/"]
COPY ["DisasterLocation.BLL/DisasterLocation.BLL.csproj", "DisasterLocation.BLL/"]
COPY ["DisasterLocation.Core/DisasterLocation.Core.csproj", "DisasterLocation.Core/"]
RUN --mount=type=secret,id=nuget_credentials,required=true \
    NuGetPackageSourceCredentials_GitHubPackages="$(cat /run/secrets/nuget_credentials)" \
    dotnet restore "DisasterLocation/DisasterLocation.csproj" --configfile NuGet.Config
COPY DisasterLocation/ DisasterLocation/
COPY DisasterLocation.BLL/ DisasterLocation.BLL/
COPY DisasterLocation.Core/ DisasterLocation.Core/
RUN dotnet publish "DisasterLocation/DisasterLocation.csproj" -c Release --no-restore -o /out /p:UseAppHost=false
FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "DisasterLocation.dll"]
