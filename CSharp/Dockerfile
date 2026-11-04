FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Xbank.DevAgent/Xbank.DevAgent.Api/Xbank.DevAgent.Api.csproj \
    --configuration Release --output /app/publish

FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Xbank.DevAgent.Api.dll"]

