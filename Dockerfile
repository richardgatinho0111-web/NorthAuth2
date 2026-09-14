FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY NorthAuthServer.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PORT=8080
EXPOSE 8080
COPY --from=build /app/publish .
RUN mkdir -p /app/Data /app/Storage /app/Backups
ENTRYPOINT ["dotnet", "NorthAuthServer.dll"]
