FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY services/RentalOperations/RentalOperations/RentalOperations.csproj services/RentalOperations/RentalOperations/
RUN dotnet restore services/RentalOperations/RentalOperations/RentalOperations.csproj

FROM restore AS publish
COPY services/RentalOperations/RentalOperations/ services/RentalOperations/RentalOperations/
COPY Shared/ Shared/
COPY contracts/ contracts/
COPY services/risk-pricing/pricing-policy.json services/risk-pricing/
RUN dotnet publish services/RentalOperations/RentalOperations/RentalOperations.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM publish AS development
WORKDIR /app/publish
ENTRYPOINT ["dotnet", "RentalOperations.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8200
ENTRYPOINT ["dotnet", "RentalOperations.dll"]
