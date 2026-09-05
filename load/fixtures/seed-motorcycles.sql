INSERT INTO "Motorcycles" ("Id", "Year", "Model", "LicensePlate", "RegistrationDate")
SELECT 'load-moto-' || n, 2025, 'Load fixture', 'KAA' || lpad(n::text, 4, '0'), now()
FROM generate_series(0, 9999) n
ON CONFLICT ("LicensePlate") DO NOTHING;
