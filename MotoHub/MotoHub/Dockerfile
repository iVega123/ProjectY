FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src

COPY MotoHub/MotoHub.csproj MotoHub/MotoHub/
RUN dotnet restore MotoHub/MotoHub/MotoHub.csproj

FROM restore AS publish
COPY MotoHub/ MotoHub/MotoHub/
COPY --from=shared Health/ Shared/Health/
COPY --from=shared Hosting/ Shared/Hosting/
RUN dotnet publish MotoHub/MotoHub/MotoHub.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8100
ENTRYPOINT ["dotnet", "MotoHub.dll"]
