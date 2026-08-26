-- Esquema mínimo do núcleo transacional.
--
-- O ponto desta migração é a última restrição: a sobreposição de aluguéis, que
-- na versão auditada era um `foreach` em C# sujeito a corrida (M11), passa a ser
-- garantida pelo banco sob concorrência real.

CREATE DATABASE IF NOT EXISTS projecty;
SET DATABASE = projecty;

-- Um usuário por serviço, com permissão só no que lhe cabe (A2).
CREATE USER IF NOT EXISTS rental_core;
CREATE USER IF NOT EXISTS media_guard;

CREATE TABLE IF NOT EXISTS motorcycles (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    license_plate STRING NOT NULL,
    model         STRING,
    year          INT NOT NULL CHECK (year BETWEEN 1950 AND 2100),
    registered_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    retired_at    TIMESTAMPTZ,
    UNIQUE (license_plate)
);

CREATE TABLE IF NOT EXISTS rentals (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rider_id          STRING NOT NULL,
    license_plate     STRING NOT NULL REFERENCES motorcycles (license_plate),
    starts_at         TIMESTAMPTZ NOT NULL,
    predicted_ends_at TIMESTAMPTZ NOT NULL,
    ends_at           TIMESTAMPTZ,
    init_cost         DECIMAL(12, 2) NOT NULL,
    final_cost        DECIMAL(12, 2),
    status            STRING NOT NULL DEFAULT 'active'
                      CHECK (status IN ('active', 'closed', 'cancelled')),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (predicted_ends_at > starts_at)
);

-- Impede que a mesma placa tenha dois aluguéis ativos ao mesmo tempo.
-- Duas requisições simultâneas: uma passa, a outra recebe violação de unicidade.
-- Sem lock na aplicação, sem janela de corrida.
CREATE UNIQUE INDEX IF NOT EXISTS one_active_rental_per_plate
    ON rentals (license_plate)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS rentals_by_rider ON rentals (rider_id, created_at DESC);

-- Outbox: a linha é gravada na MESMA transação do aluguel, e um publicador
-- separado a envia ao Kafka. É o que impede a divergência de escrita dupla (M3).
CREATE TABLE IF NOT EXISTS outbox (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_type STRING NOT NULL,
    aggregate_id   STRING NOT NULL,
    event_type     STRING NOT NULL,
    payload        JSONB NOT NULL,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    published_at   TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS outbox_pending ON outbox (occurred_at) WHERE published_at IS NULL;

-- Inbox: deduplicação no consumo. Junto com o outbox, dá efeito de "exatamente
-- uma vez" sem que o broker precise prometer entrega exatamente uma vez.
CREATE TABLE IF NOT EXISTS inbox (
    message_id  STRING PRIMARY KEY,
    consumer    STRING NOT NULL,
    handled_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE motorcycles, rentals, outbox, inbox TO rental_core;
GRANT SELECT ON TABLE rentals TO media_guard;
