package projecty.billing

import com.fasterxml.jackson.databind.ObjectMapper
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.file.Files
import java.nio.file.Path
import java.time.Duration
import java.util.concurrent.ConcurrentHashMap

/**
 * O id de schema que acompanha cada mensagem no header `schema-id`.
 *
 * O #132 decidiu Protobuf cru com o id num header, e não o envelope binário do
 * Confluent: quem consome decodifica localmente e a mensagem continua legível
 * sem o registry no caminho. O registry serve para uma coisa só -- recusar um
 * contrato incompatível no momento do registro.
 *
 * A resolução é uma vez por tópico e fica em memória. Um registry fora do ar
 * depois disso não segura nenhuma linha do outbox.
 */
class SchemaRegistry(
    private val baseUrl: String,
    private val contracts: Path = Path.of("/event-contracts"),
) {
    private val json = ObjectMapper()
    private val ids = ConcurrentHashMap<String, Int>()
    private val http: HttpClient =
        HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(5))
            .build()

    fun resolve(topic: String): Int =
        ids.computeIfAbsent(topic) { name ->
            val topics = json.readTree(Files.readAllBytes(contracts.resolve("topics.json")))
            val file =
                topics.path(name).path("schema").asText(null)
                    ?: error("Topic $name is not declared in topics.json")
            val schema = Files.readString(contracts.resolve("events").resolve(file))
            val body =
                json.createObjectNode()
                    .put("schemaType", "PROTOBUF")
                    .put("schema", schema)
                    .toString()

            val response =
                http.send(
                    HttpRequest.newBuilder(URI.create("${baseUrl.trimEnd('/')}/subjects/$name-value"))
                        .timeout(Duration.ofSeconds(5))
                        .header("Content-Type", "application/json")
                        .POST(HttpRequest.BodyPublishers.ofString(body))
                        .build(),
                    HttpResponse.BodyHandlers.ofString(),
                )
            check(response.statusCode() in 200..299) {
                "Schema registry refused $name: ${response.statusCode()} ${response.body()}"
            }
            val id = json.readTree(response.body()).path("id").asInt(0)
            check(id > 0) { "Schema registry returned no usable id for $name" }
            id
        }
}
