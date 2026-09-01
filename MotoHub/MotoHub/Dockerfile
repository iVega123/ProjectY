FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY MotoHub/MotoHub/MotoHub.csproj MotoHub/MotoHub/
RUN dotnet restore MotoHub/MotoHub/MotoHub.csproj

FROM restore AS publish
COPY MotoHub/MotoHub/ MotoHub/MotoHub/
COPY Shared/ Shared/
RUN dotnet publish MotoHub/MotoHub/MotoHub.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8100
ENTRYPOINT ["dotnet", "MotoHub.dll"]
