-- O estado que o benchmark precisa, no banco do rental-core.
--
-- Eram dois arquivos e dois engines: as motos iam para o Postgres do MotoHub e
-- os aluguéis eram limpos no MongoDB por um script mongosh. Com um banco só,
-- é um arquivo e uma conexão.
--
-- A projeção do piloto é semeada aqui, e não por um evento: nesta pilha nada
-- passa pelo Kafka, então a projeção nunca receberia o rider.verified e o
-- benchmark aqueceria contra "awaiting processing". A projeção é estado de que
-- o aluguel depende, então a fixture a semeia como semeia as motos.

TRUNCATE TABLE rentals, outbox, inbox, rider_projection, projection_snapshots;

INSERT INTO motorcycles (id, year, model, license_plate, registered_at)
SELECT ('00000000-0000-4000-8000-' || lpad(n::text, 12, '0'))::uuid,
       2025,
       'Load fixture',
       'KAA' || lpad(n::text, 4, '0'),
       now()
  FROM generate_series(0, 9999) AS n
    ON CONFLICT (license_plate) DO NOTHING;

INSERT INTO rider_projection (rider_id, verified, verified_at_ms, rider_name)
VALUES ('load-rider', true, 1, 'Load rider')
    ON CONFLICT (rider_id) DO UPDATE
   SET verified = true, verified_at_ms = 1, rider_name = 'Load rider';
