FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY AuthGate/AuthGate/AuthGate.csproj AuthGate/AuthGate/
RUN dotnet restore AuthGate/AuthGate/AuthGate.csproj

FROM restore AS publish
COPY AuthGate/AuthGate/ AuthGate/AuthGate/
COPY Shared/ Shared/
RUN dotnet publish AuthGate/AuthGate/AuthGate.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuthGate.dll"]
