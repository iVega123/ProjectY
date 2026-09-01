-- Prepara o caso e faz o primeiro aluguel, que deve ser aceito.
DELETE FROM rentals WHERE license_plate = 'CI0T35T';
DELETE FROM motorcycles WHERE license_plate = 'CI0T35T';

INSERT INTO motorcycles (license_plate, model, year)
VALUES ('CI0T35T', 'Portability Test', 2024);

INSERT INTO rentals (rider_id, license_plate, starts_at, predicted_ends_at, init_cost)
VALUES ('rider-ci', 'CI0T35T', now(), now() + INTERVAL '7 days', 210.00);
