FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/TabService/TabService.csproj src/TabService/
RUN dotnet restore src/TabService/TabService.csproj
COPY src/ src/
RUN dotnet publish src/TabService/TabService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "TabService.dll"]
