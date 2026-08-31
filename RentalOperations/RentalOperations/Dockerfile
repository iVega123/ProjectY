FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src

COPY RentalOperations/RentalOperations/RentalOperations.csproj RentalOperations/RentalOperations/
RUN dotnet restore RentalOperations/RentalOperations/RentalOperations.csproj

FROM restore AS publish
COPY RentalOperations/RentalOperations/ RentalOperations/RentalOperations/
COPY Shared/ Shared/
RUN dotnet publish RentalOperations/RentalOperations/RentalOperations.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled-extra AS final
WORKDIR /app
COPY --chown=app:app --from=publish /app/publish ./
USER app
EXPOSE 8200
ENTRYPOINT ["dotnet", "RentalOperations.dll"]
