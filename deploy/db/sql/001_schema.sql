-- Esquema mínimo do núcleo transacional.
--
-- O ponto desta migração é a restrição de unicidade parcial: a sobreposição de
-- aluguéis, que na versão auditada era um `foreach` em C# sujeito a corrida
-- (M11), passa a ser garantida pelo banco sob concorrência real.
--
-- Este arquivo é escrito no subconjunto que CockroachDB e PostgreSQL aceitam.
-- Não é elegância: é a última coluna da tabela do ADR 0004. O engine local e o
-- gerenciado são o mesmo (CockroachDB), então nada aqui falharia se o dialeto
-- escorregasse — o CI aplica o arquivo nos dois engines justamente porque
-- nenhuma outra coisa defenderia essa portabilidade.
--
-- A criação de banco e de papéis mora em 000_bootstrap.<engine>.sql.

CREATE TABLE IF NOT EXISTS motorcycles (
    id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    license_plate TEXT NOT NULL,
    model         TEXT,
    year          INT NOT NULL CHECK (year BETWEEN 1950 AND 2100),
    registered_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    retired_at    TIMESTAMPTZ,
    UNIQUE (license_plate)
);

-- A referência é o id da moto, não a placa. A placa é identificador de
-- negócio e já foi reescrita neste sistema uma vez (a migração
-- CanonicalizeLegacyMotorcyclePlates, no MotoHub). Chave de negócio mutável
-- não serve como identidade: quebraria a integridade referencial na correção,
-- e quebraria a ordenação no Kafka, onde esta mesma coluna é a partition key.
-- A placa continua acessível por junção, e uma correção de placa passa a
-- aparecer corretamente no histórico em vez de bifurcá-lo.
CREATE TABLE IF NOT EXISTS rentals (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    rider_id          TEXT NOT NULL,
    motorcycle_id     UUID NOT NULL REFERENCES motorcycles (id),
    starts_at         TIMESTAMPTZ NOT NULL,
    predicted_ends_at TIMESTAMPTZ NOT NULL,
    ends_at           TIMESTAMPTZ,
    init_cost         DECIMAL(12, 2) NOT NULL,
    final_cost        DECIMAL(12, 2),
    status            TEXT NOT NULL DEFAULT 'active'
                      CHECK (status IN ('active', 'closed', 'cancelled')),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    CHECK (predicted_ends_at > starts_at)
);

-- Impede que a mesma moto tenha dois aluguéis ativos ao mesmo tempo.
-- Duas requisições simultâneas: uma passa, a outra recebe violação de unicidade.
-- Sem lock na aplicação, sem janela de corrida.
--
-- O predicado é o que torna a restrição correta: sem ele, uma moto devolvida
-- nunca mais poderia ser alugada. deploy/db/tests cobre os dois lados.
CREATE UNIQUE INDEX IF NOT EXISTS one_active_rental_per_motorcycle
    ON rentals (motorcycle_id)
    WHERE status = 'active';

CREATE INDEX IF NOT EXISTS rentals_by_rider ON rentals (rider_id, created_at DESC);

-- Outbox: a linha é gravada na MESMA transação do aluguel, e um publicador
-- separado a envia ao Kafka. É o que impede a divergência de escrita dupla (M3).
CREATE TABLE IF NOT EXISTS outbox (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_type TEXT NOT NULL,
    aggregate_id   TEXT NOT NULL,
    event_type     TEXT NOT NULL,
    payload        JSONB NOT NULL,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    published_at   TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS outbox_pending ON outbox (occurred_at) WHERE published_at IS NULL;

-- Inbox: deduplicação no consumo. Junto com o outbox, dá efeito de "exatamente
-- uma vez" sem que o broker precise prometer entrega exatamente uma vez.
CREATE TABLE IF NOT EXISTS inbox (
    message_id  TEXT NOT NULL,
    consumer    TEXT NOT NULL,
    handled_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (message_id, consumer)
);

CREATE INDEX IF NOT EXISTS inbox_retention ON inbox (handled_at);

GRANT USAGE ON SCHEMA public TO rental_core;
GRANT USAGE ON SCHEMA public TO media_guard;

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE motorcycles, rentals, outbox, inbox TO rental_core;
GRANT SELECT ON TABLE rentals TO media_guard;
