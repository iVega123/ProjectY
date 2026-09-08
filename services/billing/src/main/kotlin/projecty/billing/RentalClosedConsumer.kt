package projecty.billing

import org.apache.kafka.clients.consumer.ConsumerConfig
import org.apache.kafka.clients.consumer.KafkaConsumer
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
 */
class RentalClosedConsumer(
    bootstrapServers: String,
    private val invoices: Invoices,
    private val stopping: AtomicBoolean,
) : Runnable {
    private val log = LoggerFactory.getLogger(RentalClosedConsumer::class.java)
    val lastPollAtMs = AtomicLong(System.currentTimeMillis())
    val issued = AtomicLong(0)

    private val consumer =
        KafkaConsumer<String, ByteArray>(
            Properties().apply {
                put(ConsumerConfig.BOOTSTRAP_SERVERS_CONFIG, bootstrapServers)
                put(ConsumerConfig.GROUP_ID_CONFIG, Invoices.CONSUMER)
                put(ConsumerConfig.ENABLE_AUTO_COMMIT_CONFIG, false)
                put(ConsumerConfig.AUTO_OFFSET_RESET_CONFIG, "earliest")
                put(ConsumerConfig.KEY_DESERIALIZER_CLASS_CONFIG, StringDeserializer::class.java.name)
                put(ConsumerConfig.VALUE_DESERIALIZER_CLASS_CONFIG, ByteArrayDeserializer::class.java.name)
            },
        )

    override fun run() =
        consumer.use { client ->
            client.subscribe(listOf(TOPIC))
            while (!stopping.get()) {
                val records =
                    try {
                        client.poll(Duration.ofSeconds(1))
                    } catch (error: Exception) {
                        if (stopping.get()) break
                        throw error
                    }
                lastPollAtMs.set(System.currentTimeMillis())
                if (records.isEmpty) continue
                for (record in records) {
                    val traceParent = record.headers().lastHeader("traceparent")?.value()?.decodeToString()
                    handle(RentalEvent.parseFrom(record.value()), traceParent)
                }
                client.commitSync()
            }
        }

    private fun handle(
        event: RentalEvent,
        traceParent: String?,
    ) {
        val rental =
            try {
                ClosedRental.from(event, traceParent)
            } catch (error: IllegalArgumentException) {
                // Um evento malformado não melhora com repetição, e travar o grupo
                // nele pararia o faturamento de todo mundo. Registrar e seguir é a
                // escolha; o aluguel fica sem nota e aparece como tal.
                log.error("Refusing malformed rental.closed: {}", error.message)
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

    fun wakeup() = consumer.wakeup()

    companion object {
        const val TOPIC = "rental.closed"
    }
}
