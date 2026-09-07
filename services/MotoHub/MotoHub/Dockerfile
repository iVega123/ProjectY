FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY services/MotoHub/MotoHub/MotoHub.csproj services/MotoHub/MotoHub/
RUN dotnet restore services/MotoHub/MotoHub/MotoHub.csproj

FROM restore AS publish
COPY services/MotoHub/MotoHub/ services/MotoHub/MotoHub/
COPY Shared/ Shared/
RUN dotnet publish services/MotoHub/MotoHub/MotoHub.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM publish AS development
WORKDIR /app/publish
ENTRYPOINT ["dotnet", "MotoHub.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8100
ENTRYPOINT ["dotnet", "MotoHub.dll"]
