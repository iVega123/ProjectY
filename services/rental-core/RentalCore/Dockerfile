FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY services/rental-core/RentalCore/RentalCore.csproj services/rental-core/RentalCore/
RUN dotnet restore services/rental-core/RentalCore/RentalCore.csproj

FROM restore AS publish
COPY services/rental-core/RentalCore/ services/rental-core/RentalCore/
COPY Shared/ Shared/
COPY contracts/ contracts/
COPY services/risk-pricing/pricing-policy.json services/risk-pricing/
RUN dotnet publish services/rental-core/RentalCore/RentalCore.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM publish AS development
WORKDIR /app/publish
ENTRYPOINT ["dotnet", "RentalCore.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8200
ENTRYPOINT ["dotnet", "RentalCore.dll"]
