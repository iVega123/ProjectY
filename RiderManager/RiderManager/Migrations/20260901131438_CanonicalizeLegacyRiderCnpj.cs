using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RiderManager.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalizeLegacyRiderCnpj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Riders_CNPJ";

                CREATE TABLE "LegacyRiderCnpjReconciliations" (
                    "RiderId" text PRIMARY KEY,
                    "UserId" text NOT NULL,
                    "OriginalCnpj" text NOT NULL,
                    "CanonicalCnpj" character varying(14) NOT NULL,
                    "WasDuplicate" boolean NOT NULL,
                    "ReconciledAtUtc" timestamp with time zone NOT NULL,
                    "Reason" text NOT NULL
                );

                WITH ranked AS (
                    SELECT
                        "Id",
                        "UserId",
                        "CNPJ",
                        regexp_replace("CNPJ", '[^0-9]', '', 'g') AS canonical_cnpj,
                        row_number() OVER (
                            PARTITION BY regexp_replace("CNPJ", '[^0-9]', '', 'g')
                            ORDER BY "UserId", "Id") AS duplicate_rank
                    FROM "Riders"
                    WHERE regexp_replace("CNPJ", '[^0-9]', '', 'g') ~ '^[0-9]{14}$'
                )
                INSERT INTO "LegacyRiderCnpjReconciliations" (
                    "RiderId",
                    "UserId",
                    "OriginalCnpj",
                    "CanonicalCnpj",
                    "WasDuplicate",
                    "ReconciledAtUtc",
                    "Reason")
                SELECT
                    ranked."Id",
                    ranked."UserId",
                    ranked."CNPJ",
                    ranked.canonical_cnpj,
                    ranked.duplicate_rank > 1,
                    statement_timestamp(),
                    CASE
                        WHEN ranked.duplicate_rank > 1
                            THEN 'Duplicate legacy CNPJ projection quarantined during canonicalization'
                        ELSE 'Legacy CNPJ projection normalized'
                    END
                FROM ranked;

                UPDATE "Riders" AS riders
                SET "CNPJ" = 'QUAR:' || substring(md5(riders."Id"), 1, 27)
                FROM "LegacyRiderCnpjReconciliations" AS reconciliation
                WHERE riders."Id" = reconciliation."RiderId"
                  AND reconciliation."WasDuplicate";

                UPDATE "Riders" AS riders
                SET "CNPJ" = reconciliation."CanonicalCnpj"
                FROM "LegacyRiderCnpjReconciliations" AS reconciliation
                WHERE riders."Id" = reconciliation."RiderId"
                  AND NOT reconciliation."WasDuplicate";

                CREATE UNIQUE INDEX "IX_Riders_CNPJ" ON "Riders" ("CNPJ");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Riders_CNPJ";

                UPDATE "Riders" AS riders
                SET "CNPJ" = reconciliation."OriginalCnpj"
                FROM "LegacyRiderCnpjReconciliations" AS reconciliation
                WHERE riders."Id" = reconciliation."RiderId";

                DROP TABLE "LegacyRiderCnpjReconciliations";
                CREATE UNIQUE INDEX "IX_Riders_CNPJ" ON "Riders" ("CNPJ");
                """);
        }
    }
}
