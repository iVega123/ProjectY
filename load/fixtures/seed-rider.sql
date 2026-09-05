INSERT INTO "Riders" ("Id", "UserId", "Email", "Name", "CNPJ", "DateOfBirth", "CNHNumber", "CNHType")
VALUES ('load-rider', 'load-rider', 'load@example.test', 'Load rider', '11444777000161', '1990-01-01', '12345678901', 'A')
ON CONFLICT ("Id") DO NOTHING;
