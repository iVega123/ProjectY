FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY services/RiderManager/RiderManager/RiderManager.csproj services/RiderManager/RiderManager/
RUN dotnet restore services/RiderManager/RiderManager/RiderManager.csproj

FROM restore AS publish
COPY services/RiderManager/RiderManager/ services/RiderManager/RiderManager/
COPY Shared/ Shared/
COPY contracts/ contracts/
RUN dotnet publish services/RiderManager/RiderManager/RiderManager.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM publish AS development
WORKDIR /app/publish
ENTRYPOINT ["dotnet", "RiderManager.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8000
ENTRYPOINT ["dotnet", "RiderManager.dll"]
