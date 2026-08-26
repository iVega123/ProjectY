-- Telemetria de posição dos entregadores.
--
-- A chave de partição inclui o dia para manter as partições limitadas: sem isso,
-- um entregador antigo teria uma partição crescendo para sempre, que é o modo
-- clássico de usar Cassandra errado.

CREATE KEYSPACE IF NOT EXISTS projecty
  WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};

USE projecty;

CREATE TABLE IF NOT EXISTS rider_positions (
    rider_id    text,
    day         date,
    recorded_at timestamp,
    latitude    double,
    longitude   double,
    accuracy_m  float,
    rental_id   text,
    PRIMARY KEY ((rider_id, day), recorded_at)
) WITH CLUSTERING ORDER BY (recorded_at DESC)
  AND default_time_to_live = 7776000        -- 90 dias: o histórico expira sozinho
  AND compaction = {'class': 'TimeWindowCompactionStrategy',
                    'compaction_window_unit': 'DAYS',
                    'compaction_window_size': 1};

-- Última posição conhecida, para o mapa carregar instantaneamente sem varrer
-- o histórico. Uma linha por entregador, sobrescrita a cada atualização.
CREATE TABLE IF NOT EXISTS rider_last_position (
    rider_id    text PRIMARY KEY,
    recorded_at timestamp,
    latitude    double,
    longitude   double,
    rental_id   text
);
