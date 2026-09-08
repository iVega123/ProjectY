# O contexto de build é a raiz do repositório: o serviço compila contra os
# .proto de contracts/ e o teste aplica o schema de deploy/db/sql. Copiar os
# dois para cá seria manter duas cópias do que o #132 e o ADR 0004 decidiram ter
# uma só.
FROM gradle:8.14.3-jdk21-alpine AS development
WORKDIR /src/services/billing
COPY contracts /src/contracts
COPY deploy/db/sql /src/deploy/db/sql
COPY services/billing/settings.gradle.kts services/billing/build.gradle.kts \
     services/billing/gradle.properties services/billing/.editorconfig ./
# As dependências antes do código: mexer numa linha de Kotlin não deve baixar o
# Maven Central de novo.
RUN gradle --no-daemon --quiet dependencies --configuration runtimeClasspath > /dev/null
COPY services/billing/src ./src
RUN gradle --no-daemon --quiet ktlintCheck classes
ENTRYPOINT ["gradle", "--no-daemon", "run"]

FROM development AS builder
RUN gradle --no-daemon --quiet installDist

FROM eclipse-temurin:21-jre-alpine AS final
RUN apk upgrade --no-cache && apk add --no-cache curl
WORKDIR /app
# O agente do OpenTelemetry é a única parte da observabilidade que a JVM entrega
# de graça: instrumenta JDBC, Kafka e HTTP sem uma linha de código. É o lugar
# onde o custo do poliglota é MENOR, e não maior -- vale registrar, já que o
# resto do repositório mede esse custo na direção oposta.
#
# O checksum está fixado pela mesma razão que o do librdkafka no risk-pricing:
# um artefato baixado no build sem conferência é uma dependência que ninguém
# revisou.
RUN curl --fail --location --retry 3 --output /app/opentelemetry-javaagent.jar \
      https://github.com/open-telemetry/opentelemetry-java-instrumentation/releases/download/v2.31.1/opentelemetry-javaagent.jar \
 && echo 'bbf83c151b6400709e2f225bdd07a04f839d9d13b8b93464241333fd25d3e3ba  /app/opentelemetry-javaagent.jar' | sha256sum -c -
COPY --from=builder /src/services/billing/build/install/billing /app
COPY contracts /event-contracts
ENV JAVA_TOOL_OPTIONS="-javaagent:/app/opentelemetry-javaagent.jar"
USER 65532:65532
EXPOSE 8094
ENTRYPOINT ["/app/bin/billing"]
