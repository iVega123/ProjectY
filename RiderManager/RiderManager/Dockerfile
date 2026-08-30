FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src

COPY RiderManager/RiderManager.csproj RiderManager/RiderManager/
RUN dotnet restore RiderManager/RiderManager/RiderManager.csproj

FROM restore AS publish
COPY RiderManager/ RiderManager/RiderManager/
COPY --from=shared Messaging/ Shared/Messaging/
RUN dotnet publish RiderManager/RiderManager/RiderManager.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8000
ENTRYPOINT ["dotnet", "RiderManager.dll"]
