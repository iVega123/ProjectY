-- Segundo aluguel ativo para a mesma placa. TEM que falhar.
--
-- Não há lock na aplicação e não há checagem prévia: quem recusa é o índice
-- único parcial de 001_schema.sql. Se este arquivo for aceito por um engine,
-- a garantia central do sistema não existe naquele engine.
INSERT INTO rentals (rider_id, license_plate, starts_at, predicted_ends_at, init_cost)
VALUES ('rider-ci-2', 'CI0T35T', now(), now() + INTERVAL '7 days', 210.00);
