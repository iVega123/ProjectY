package projecty.billing

import com.google.protobuf.InvalidProtocolBufferException
import org.apache.kafka.clients.consumer.Consumer
import org.apache.kafka.clients.consumer.ConsumerConfig
import org.apache.kafka.clients.consumer.ConsumerRecord
import org.apache.kafka.clients.consumer.KafkaConsumer
import org.apache.kafka.common.TopicPartition
import org.apache.kafka.common.errors.WakeupException
import org.apache.kafka.common.serialization.ByteArrayDeserializer
import org.apache.kafka.common.serialization.StringDeserializer
import org.slf4j.LoggerFactory
import project_y.events.Rental.RentalEvent
import java.time.Duration
import java.util.Properties
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * Lê `rental.closed` e emite a nota.
 *
 * O commit do offset vem DEPOIS do commit da transação, e a ordem é a única que
 * funciona: cair entre os dois faz o Kafka reentregar a mensagem, e a linha do
 * inbox -- já gravada -- a reconhece. O inverso perderia a nota em silêncio.
 *
 * Duas falhas chegam por este laço e não podem ser tratadas igual:
 *
 * - **A mensagem está estragada.** Nenhuma repetição a conserta, e insistir
 *   nela trava a partição para todo mundo. Registra e segue; o aluguel fica sem
 *   nota e aparece como tal.
 * - **A dependência caiu.** A mensagem está boa e a repetição a resolve.
 *   Desistir dela perderia uma fatura, então o lote volta ao último offset
 *   confirmado e é tentado de novo.
 *
 * Confundir as duas dá o pior dos dois lados: ou uma nota some porque o banco
 * piscou, ou o faturamento inteiro para por causa de um byte torto.
 */
class RentalClosedConsumer(
    private val invoices: InvoiceIssuer,
    private val stopping: AtomicBoolean,
    private val consumer: Consumer<String, ByteArray>,
    private val backoff: Duration = Duration.ofSeconds(5),
) : Runnable {
    private val log = LoggerFactory.getLogger(RentalClosedConsumer::class.java)
    val lastPollAtMs = AtomicLong(System.currentTimeMillis())
    val issued = AtomicLong(0)
    val skipped = AtomicLong(0)
    val retriedBatches = AtomicLong(0)

    override fun run() =
        consumer.use { client ->
            client.subscribe(listOf(TOPIC))
            while (!stopping.get()) {
                try {
                    val records = client.poll(Duration.ofSeconds(1))
                    lastPollAtMs.set(System.currentTimeMillis())
                    if (records.isEmpty) continue
                    for (record in records) handle(record)
                    client.commitSync()
                } catch (error: WakeupException) {
                    if (!stopping.get()) throw error
                    break
                } catch (error: Exception) {
                    if (stopping.get()) break
                    // O lote não foi confirmado, mas a posição do consumidor já
                    // andou -- o Kafka não rebobina sozinho. Sem este seek a
                    // mensagem só voltaria num rebalanceamento ou num restart, e
                    // até lá a fatura simplesmente não existiria.
                    retriedBatches.incrementAndGet()
                    log.warn("Settlement batch failed; rewinding to the committed offsets", error)
                    rewind(client)
                    sleep()
                }
            }
        }

    private fun handle(record: ConsumerRecord<String, ByteArray>) {
        val traceParent = record.headers().lastHeader("traceparent")?.value()?.decodeToString()
        val rental =
            try {
                ClosedRental.from(RentalEvent.parseFrom(record.value()), traceParent)
            } catch (error: InvalidProtocolBufferException) {
                // O parse acontece aqui dentro, e não na chamada: lá fora ele
                // derrubaria a thread antes de qualquer tratamento, e o mesmo
                // registro envenenado voltaria a cada restart.
                skip(record, "undecodable protobuf", error.message)
                return
            } catch (error: IllegalArgumentException) {
                skip(record, "incomplete settlement", error.message)
                return
            }
        val settled =
            Settlement.settle(
                agreedMinor = rental.agreedTotalMinor,
                planDays = rental.planDays,
                startedAtMs = rental.startedAtMs,
                predictedEndAtMs = rental.predictedEndAtMs,
                endedAtMs = rental.endedAtMs,
            )
        val result = invoices.issue(rental.eventId, rental, settled)
        when (result.outcome) {
            Outcome.ISSUED -> {
                issued.incrementAndGet()
                log.info(
                    "Invoice {} issued for rental {}: {} {} ({})",
                    result.invoiceId,
                    rental.rentalId,
                    rental.currency,
                    settled.totalMinor,
                    settled.reason,
                )
            }
            Outcome.DUPLICATE_MESSAGE ->
                log.debug("rental.closed {} already handled; no second invoice", rental.eventId)
            Outcome.ALREADY_SETTLED ->
                log.warn("Rental {} was already invoiced; refused by one_invoice_per_rental", rental.rentalId)
        }
    }

    private fun skip(
        record: ConsumerRecord<String, ByteArray>,
        reason: String,
        detail: String?,
    ) {
        skipped.incrementAndGet()
        log.error(
            "Skipping {}-{} offset {}: {} ({})",
            record.topic(),
            record.partition(),
            record.offset(),
            reason,
            detail,
        )
    }

    private fun rewind(client: Consumer<String, ByteArray>) {
        runCatching {
            val assignment = client.assignment()
            if (assignment.isEmpty()) return
            val committed = client.committed(assignment)
            for (partition in assignment) {
                val offset = committed[partition]
                if (offset == null) {
                    client.seekToBeginning(
                        listOf<TopicPartition>(partition),
                    )
                } else {
                    client.seek(partition, offset.offset())
                }
            }
        }.onFailure { log.warn("Could not rewind after a failed batch; the next rebalance will", it) }
    }

    private fun sleep() {
        val until = System.nanoTime() + backoff.toNanos()
        while (!stopping.get() && System.nanoTime() < until) {
            Thread.sleep(minOf(200L, backoff.toMillis()))
        }
    }

    fun wakeup() = consumer.wakeup()

    companion object {
        const val TOPIC = "rental.closed"

        fun kafka(
            bootstrapServers: String,
            invoices: InvoiceIssuer,
            stopping: AtomicBoolean,
        ) = RentalClosedConsumer(
            invoices,
            stopping,
            KafkaConsumer<String, ByteArray>(
                Properties().apply {
                    put(ConsumerConfig.BOOTSTRAP_SERVERS_CONFIG, bootstrapServers)
                    put(ConsumerConfig.GROUP_ID_CONFIG, Invoices.CONSUMER)
                    put(ConsumerConfig.ENABLE_AUTO_COMMIT_CONFIG, false)
                    put(ConsumerConfig.AUTO_OFFSET_RESET_CONFIG, "earliest")
                    put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer::class.java.name)
                    put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, ByteArrayDeserializer::class.java.name)
                },
            ),
        )
    }
}
