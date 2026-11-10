FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MsOrders/MsOrders.csproj src/MsOrders/
RUN dotnet restore src/MsOrders/MsOrders.csproj
COPY src/ src/
RUN dotnet publish src/MsOrders/MsOrders.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MsOrders.dll"]
