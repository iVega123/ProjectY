-- Prepara o caso e faz o primeiro aluguel, que deve ser aceito.
DELETE FROM rentals WHERE motorcycle_id IN (SELECT id FROM motorcycles WHERE license_plate = 'CI0T35T');
DELETE FROM motorcycles WHERE license_plate = 'CI0T35T';

INSERT INTO motorcycles (license_plate, model, year)
VALUES ('CI0T35T', 'Portability Test', 2024);

INSERT INTO rentals (rider_id, motorcycle_id, starts_at, predicted_ends_at, init_cost)
SELECT 'rider-ci', id, now(), now() + INTERVAL '7 days', 210.00
  FROM motorcycles WHERE license_plate = 'CI0T35T';
