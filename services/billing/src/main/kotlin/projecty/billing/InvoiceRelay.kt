package projecty.billing

import org.apache.kafka.clients.producer.KafkaProducer
import org.apache.kafka.clients.producer.ProducerConfig
import org.apache.kafka.clients.producer.ProducerRecord
import org.apache.kafka.common.header.internals.RecordHeader
import org.apache.kafka.common.serialization.ByteArraySerializer
import org.apache.kafka.common.serialization.StringSerializer
import org.slf4j.LoggerFactory
import java.util.Properties
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import javax.sql.DataSource

/**
 * Publica no Kafka o que a transação da nota deixou no outbox.
 *
 * Mesma forma da relay do rental-core, e pelo mesmo motivo: marcar como
 * publicado é um UPDATE separado, DEPOIS do envio. Cair entre os dois republica
 * o mesmo evento com o mesmo `event_id`, que é o que o inbox de quem consumir
 * reconhece. Marcar antes perderia o evento em silêncio.
 *
 * O filtro por `aggregate_type = 'invoice'` é o que permite dividir a tabela
 * com o rental-core sem que uma relay publique os eventos da outra.
 */
class InvoiceRelay(
    private val dataSource: DataSource,
    bootstrapServers: String,
    private val schemas: SchemaRegistry,
    private val stopping: AtomicBoolean,
) : Runnable {
    private val log = LoggerFactory.getLogger(InvoiceRelay::class.java)

    private val producer =
        KafkaProducer<String, ByteArray>(
            Properties().apply {
                put(ProducerConfig.BOOTSTRAP_SERVERS_CONFIG, bootstrapServers)
                put(ProducerConfig.ENABLE_IDEMPOTENCE_CONFIG, true)
                put(ProducerConfig.ACKS_CONFIG, "all")
                put(ProducerConfig.DELIVERY_TIMEOUT_MS_CONFIG, 5_000)
                put(ProducerConfig.REQUEST_TIMEOUT_MS_CONFIG, 4_000)
                put(ProducerConfig.KEY_SERIALIZER_CLASS_CONFIG, StringSerializer::class.java.name)
                put(ProducerConfig.VALUE_SERIALIZER_CLASS_CONFIG, ByteArraySerializer::class.java.name)
            },
        )

    private class Pending(
        val id: UUID,
        val key: String,
        val topic: String,
        val payload: ByteArray,
        val traceParent: String?,
    )

    override fun run() =
        producer.use { client ->
            while (!stopping.get()) {
                try {
                    for (pending in pending()) {
                        val headers =
                            mutableListOf(
                                RecordHeader("schema-id", schemas.resolve(pending.topic).toString().toByteArray()),
                            )
                        pending.traceParent?.let { headers.add(RecordHeader("traceparent", it.toByteArray())) }
                        client.send(ProducerRecord(pending.topic, null, pending.key, pending.payload, headers)).get()
                        markPublished(pending.id)
                    }
                } catch (error: Exception) {
                    if (stopping.get()) break
                    log.warn("Kafka relay delayed; invoice events retained", error)
                }
                Thread.sleep(2_000)
            }
        }

    private fun pending(): List<Pending> =
        dataSource.connection.use { connection ->
            connection.prepareStatement(
                """
                SELECT id, aggregate_id, topic, payload, trace_parent
                  FROM outbox
                 WHERE published_at IS NULL
                   AND aggregate_type = 'invoice'
                 ORDER BY occurred_at
                 LIMIT 100
                """.trimIndent(),
            ).use { statement ->
                statement.executeQuery().use { rows ->
                    buildList {
                        while (rows.next()) {
                            add(
                                Pending(
                                    rows.getObject(1, UUID::class.java),
                                    rows.getString(2),
                                    rows.getString(3),
                                    rows.getBytes(4),
                                    rows.getString(5),
                                ),
                            )
                        }
                    }
                }
            }
        }

    private fun markPublished(id: UUID) =
        dataSource.connection.use { connection ->
            connection.prepareStatement(
                "UPDATE outbox SET published_at = now() WHERE id = ? AND published_at IS NULL",
            ).use { statement ->
                statement.setObject(1, id)
                statement.executeUpdate()
            }
        }
}
