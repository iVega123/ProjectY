FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY services/AuthGate/AuthGate/AuthGate.csproj services/AuthGate/AuthGate/
RUN dotnet restore services/AuthGate/AuthGate/AuthGate.csproj

FROM restore AS publish
COPY services/AuthGate/AuthGate/ services/AuthGate/AuthGate/
COPY Shared/ Shared/
RUN dotnet publish services/AuthGate/AuthGate/AuthGate.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM publish AS development
WORKDIR /app/publish
ENTRYPOINT ["dotnet", "AuthGate.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuthGate.dll"]
