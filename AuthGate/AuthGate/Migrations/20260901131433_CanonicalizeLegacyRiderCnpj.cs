using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthGate.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalizeLegacyRiderCnpj : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_AspNetUsers_CNPJ";

                CREATE TABLE "LegacyRiderCnpjReconciliations" (
                    "UserId" text PRIMARY KEY,
                    "OriginalCnpj" character varying(20) NOT NULL,
                    "CanonicalCnpj" character varying(14) NOT NULL,
                    "OriginalLockoutEnabled" boolean NOT NULL,
                    "OriginalLockoutEnd" timestamp with time zone NULL,
                    "WasDuplicate" boolean NOT NULL,
                    "ReconciledAtUtc" timestamp with time zone NOT NULL,
                    "Reason" text NOT NULL
                );

                WITH ranked AS (
                    SELECT
                        "Id",
                        "CNPJ",
                        regexp_replace("CNPJ", '[^0-9]', '', 'g') AS canonical_cnpj,
                        row_number() OVER (
                            PARTITION BY regexp_replace("CNPJ", '[^0-9]', '', 'g')
                            ORDER BY "Id") AS duplicate_rank
                    FROM "AspNetUsers"
                    WHERE "Discriminator" = 'RiderUser'
                      AND "CNPJ" IS NOT NULL
                      AND regexp_replace("CNPJ", '[^0-9]', '', 'g') ~ '^[0-9]{14}$'
                )
                INSERT INTO "LegacyRiderCnpjReconciliations" (
                    "UserId",
                    "OriginalCnpj",
                    "CanonicalCnpj",
                    "OriginalLockoutEnabled",
                    "OriginalLockoutEnd",
                    "WasDuplicate",
                    "ReconciledAtUtc",
                    "Reason")
                SELECT
                    ranked."Id",
                    ranked."CNPJ",
                    ranked.canonical_cnpj,
                    users."LockoutEnabled",
                    users."LockoutEnd",
                    ranked.duplicate_rank > 1,
                    statement_timestamp(),
                    CASE
                        WHEN ranked.duplicate_rank > 1
                            THEN 'Duplicate legacy CNPJ quarantined during canonicalization'
                        ELSE 'Legacy CNPJ normalized'
                    END
                FROM ranked
                INNER JOIN "AspNetUsers" AS users ON users."Id" = ranked."Id";

                UPDATE "AspNetUsers" AS users
                SET "CNPJ" = 'QUAR:' || substring(md5(users."Id"), 1, 15),
                    "LockoutEnabled" = TRUE,
                    "LockoutEnd" = 'infinity'::timestamp with time zone
                FROM "LegacyRiderCnpjReconciliations" AS reconciliation
                WHERE users."Id" = reconciliation."UserId"
                  AND reconciliation."WasDuplicate";

                UPDATE "AspNetUsers" AS users
                SET "CNPJ" = reconciliation."CanonicalCnpj"
                FROM "LegacyRiderCnpjReconciliations" AS reconciliation
                WHERE users."Id" = reconciliation."UserId"
                  AND NOT reconciliation."WasDuplicate";

                CREATE UNIQUE INDEX "IX_AspNetUsers_CNPJ" ON "AspNetUsers" ("CNPJ");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_AspNetUsers_CNPJ";

                UPDATE "AspNetUsers" AS users
                SET "CNPJ" = reconciliation."OriginalCnpj",
                    "LockoutEnabled" = reconciliation."OriginalLockoutEnabled",
                    "LockoutEnd" = reconciliation."OriginalLockoutEnd"
                FROM "LegacyRiderCnpjReconciliations" AS reconciliation
                WHERE users."Id" = reconciliation."UserId";

                DROP TABLE "LegacyRiderCnpjReconciliations";
                CREATE UNIQUE INDEX "IX_AspNetUsers_CNPJ" ON "AspNetUsers" ("CNPJ");
                """);
        }
    }
}
