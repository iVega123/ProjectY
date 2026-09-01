using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MotoHub.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalizeLegacyMotorcyclePlates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Motorcycles_LicensePlate";

                CREATE TABLE "LegacyMotorcyclePlateReconciliations" (
                    "MotorcycleId" text PRIMARY KEY,
                    "OriginalLicensePlate" text NOT NULL,
                    "CanonicalLicensePlate" text NOT NULL,
                    "OriginalRetiredAtUtc" timestamp with time zone NULL,
                    "OriginalRetirementReason" text NULL,
                    "WasDuplicate" boolean NOT NULL,
                    "ReconciledAtUtc" timestamp with time zone NOT NULL,
                    "Reason" text NOT NULL
                );

                WITH ranked AS (
                    SELECT
                        "Id",
                        "LicensePlate",
                        upper(btrim("LicensePlate")) AS canonical_plate,
                        row_number() OVER (
                            PARTITION BY upper(btrim("LicensePlate"))
                            ORDER BY
                                CASE WHEN "RetiredAtUtc" IS NULL THEN 0 ELSE 1 END,
                                "RegistrationDate",
                                "Id") AS duplicate_rank
                    FROM "Motorcycles"
                )
                INSERT INTO "LegacyMotorcyclePlateReconciliations" (
                    "MotorcycleId",
                    "OriginalLicensePlate",
                    "CanonicalLicensePlate",
                    "OriginalRetiredAtUtc",
                    "OriginalRetirementReason",
                    "WasDuplicate",
                    "ReconciledAtUtc",
                    "Reason")
                SELECT
                    ranked."Id",
                    ranked."LicensePlate",
                    ranked.canonical_plate,
                    motorcycles."RetiredAtUtc",
                    motorcycles."RetirementReason",
                    ranked.duplicate_rank > 1,
                    statement_timestamp(),
                    CASE
                        WHEN ranked.duplicate_rank > 1
                            THEN 'Duplicate legacy license plate quarantined during canonicalization'
                        ELSE 'Legacy license plate normalized'
                    END
                FROM ranked
                INNER JOIN "Motorcycles" AS motorcycles ON motorcycles."Id" = ranked."Id";

                UPDATE "Motorcycles" AS motorcycles
                SET "LicensePlate" = '~QUARANTINED~' || motorcycles."Id",
                    "RetiredAtUtc" = COALESCE(motorcycles."RetiredAtUtc", statement_timestamp()),
                    "RetirementReason" = 'Duplicate legacy license plate quarantined during canonicalization'
                FROM "LegacyMotorcyclePlateReconciliations" AS reconciliation
                WHERE motorcycles."Id" = reconciliation."MotorcycleId"
                  AND reconciliation."WasDuplicate";

                UPDATE "Motorcycles" AS motorcycles
                SET "LicensePlate" = reconciliation."CanonicalLicensePlate"
                FROM "LegacyMotorcyclePlateReconciliations" AS reconciliation
                WHERE motorcycles."Id" = reconciliation."MotorcycleId"
                  AND NOT reconciliation."WasDuplicate";

                CREATE UNIQUE INDEX "IX_Motorcycles_LicensePlate" ON "Motorcycles" ("LicensePlate");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Motorcycles_LicensePlate";

                UPDATE "Motorcycles" AS motorcycles
                SET "LicensePlate" = reconciliation."OriginalLicensePlate",
                    "RetiredAtUtc" = reconciliation."OriginalRetiredAtUtc",
                    "RetirementReason" = reconciliation."OriginalRetirementReason"
                FROM "LegacyMotorcyclePlateReconciliations" AS reconciliation
                WHERE motorcycles."Id" = reconciliation."MotorcycleId";

                DROP TABLE "LegacyMotorcyclePlateReconciliations";
                CREATE UNIQUE INDEX "IX_Motorcycles_LicensePlate" ON "Motorcycles" ("LicensePlate");
                """);
        }
    }
}
