package projecty.billing

import org.apache.kafka.clients.producer.KafkaProducer
import org.apache.kafka.clients.producer.ProducerConfig
import org.apache.kafka.clients.producer.ProducerRecord
import org.apache.kafka.clients.producer.RecordMetadata
import org.apache.kafka.common.header.internals.RecordHeader
import org.apache.kafka.common.serialization.ByteArraySerializer
import org.apache.kafka.common.serialization.StringSerializer
import org.slf4j.LoggerFactory
import java.util.Properties
import java.util.concurrent.ExecutionException
import java.util.concurrent.Future
import java.util.concurrent.atomic.AtomicBoolean
import javax.sql.DataSource

/**
 * Publica no Kafka o que a transação da nota deixou no outbox.
 *
 * Quem decide o que sai e quando é o [OutboxDispatcher]: reivindicar antes de
 * enviar é o que deixa duas réplicas rodarem sem publicar tudo duas vezes. Esta
 * classe é o laço e o produtor.
 *
 * O filtro por `aggregate_type = 'invoice'` é o que permite dividir a tabela
 * com o rental-core sem que uma relay publique os eventos da outra.
 */
class InvoiceRelay(
    dataSource: DataSource,
    bootstrapServers: String,
    private val schemas: SchemaRegistry,
    private val stopping: AtomicBoolean,
) : Runnable {
    private val log = LoggerFactory.getLogger(InvoiceRelay::class.java)

    private val dispatcher = OutboxDispatcher(dataSource, "invoice")

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

    override fun run() =
        producer.use {
            while (!stopping.get()) {
                val pass =
                    try {
                        dispatcher.dispatchOnce(::send)
                    } catch (error: Exception) {
                        if (stopping.get()) break
                        log.warn("Kafka relay delayed; invoice events retained", error)
                        null
                    }
                pass?.failure?.let { log.warn("Kafka relay delayed; invoice events retained", it) }
                // Um lote cheio quer dizer que pode haver mais, e a próxima passada
                // vem já. Esperar depois de toda passada limitava a relay a um lote
                // por intervalo.
                if (pass == null || pass.failure != null || pass.claimed < OutboxDispatcher.BATCH_SIZE) {
                    Thread.sleep(2_000)
                }
            }
        }

    /**
     * O lote inteiro no produtor, e uma espera só.
     *
     * `send().get()` por registro esperava o broker cem vezes por lote e anulava
     * o agrupamento do próprio produtor. Aqui os registros entram todos, `flush`
     * espera uma vez, e cada future responde pela sua linha.
     */
    private fun send(batch: List<OutboxRow>): List<Throwable?> {
        val sending =
            batch.map { row ->
                runCatching {
                    val headers =
                        mutableListOf(
                            RecordHeader("schema-id", schemas.resolve(row.topic).toString().toByteArray()),
                        )
                    row.traceParent?.let { headers.add(RecordHeader("traceparent", it.toByteArray())) }
                    producer.send(ProducerRecord(row.topic, null, row.key, row.payload, headers))
                }
            }
        producer.flush()
        return sending.map { attempt -> attempt.fold(onSuccess = this::refusal, onFailure = { it }) }
    }

    /** null quando o broker confirmou; a causa da recusa quando não. */
    private fun refusal(acknowledgement: Future<RecordMetadata>): Throwable? =
        try {
            acknowledgement.get()
            null
        } catch (error: ExecutionException) {
            error.cause ?: error
        }
}
