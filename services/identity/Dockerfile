# O contexto de build é a raiz do repositório: os testes aplicam os arquivos de
# deploy/db/sql, e metade do que este serviço promete está escrito naquele SQL.
# Copiá-los para cá seria manter duas cópias do que o ADR 0004 decidiu ter uma
# só.
FROM golang:1.27-alpine AS development
WORKDIR /src/services/identity
# As dependências antes do código: mexer numa linha de Go não deve baixar o
# módulo de novo.
COPY services/identity/go.mod services/identity/go.sum ./
RUN go mod download
COPY services/identity ./
COPY deploy/db/sql /src/deploy/db/sql
# gofmt é o `dotnet format --verify-no-changes` desta linguagem, e `go vet` pega
# a classe de erro que o compilador deixa passar. Os dois rodam no build para o
# desenvolvedor não descobrir só no CI.
RUN test -z "$(gofmt -l .)" && go vet ./... && go build ./...
ENTRYPOINT ["go", "run", "./cmd/identity"]

FROM development AS builder
# CGO desligado porque a imagem final não tem libc. -trimpath tira os caminhos
# da máquina de build do binário, que é ruído e é informação de graça para quem
# o examinar.
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/identity ./cmd/identity

FROM gcr.io/distroless/static-debian13:nonroot AS final
WORKDIR /app
COPY --from=builder --chown=nonroot:nonroot /out/identity /app/identity
USER nonroot:nonroot
EXPOSE 8095
# Sem curl e sem shell: o probe do Compose é `identity healthcheck`, o próprio
# binário consultando a si mesmo. É o que permite a imagem que emite os tokens
# de todo mundo não carregar um cliente HTTP de propósito geral.
ENTRYPOINT ["/app/identity"]
