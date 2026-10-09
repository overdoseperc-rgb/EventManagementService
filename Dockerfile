FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/EventManagementService.Api/EventManagementService.Api.csproj src/EventManagementService.Api/packages.lock.json ./src/EventManagementService.Api/
RUN dotnet restore src/EventManagementService.Api/EventManagementService.Api.csproj --locked-mode
COPY src/EventManagementService.Api/ ./src/EventManagementService.Api/
RUN dotnet publish src/EventManagementService.Api/EventManagementService.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "EventManagementService.Api.dll"]

