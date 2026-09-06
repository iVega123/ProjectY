FROM python:3.13-alpine AS development
WORKDIR /workspace
RUN apk upgrade --no-cache && apk add --no-cache tesseract-ocr tesseract-ocr-data-eng libstdc++ openssl zlib zstd-libs lz4-libs cyrus-sasl
COPY services/risk-pricing/requirements.lock ./
RUN apk add --no-cache --virtual .build-deps build-base bash curl openssl-dev zlib-dev zstd-dev lz4-dev cyrus-sasl-dev \
    && curl --fail --location --retry 3 https://codeload.github.com/confluentinc/librdkafka/tar.gz/refs/tags/v2.15.0 -o /tmp/librdkafka.tar.gz \
    && echo '259015220cdca708afe838b5aa79ebf1a5fb710fb4179cf918d390aed85d5dbc  /tmp/librdkafka.tar.gz' | sha256sum -c - \
    && tar -xzf /tmp/librdkafka.tar.gz -C /tmp \
    && cd /tmp/librdkafka-2.15.0 && ./configure --prefix=/usr && make -j2 && make install \
    && cd /workspace \
    && pip install --no-cache-dir -r requirements.lock \
    && rm -rf /tmp/librdkafka-2.15.0 /tmp/librdkafka.tar.gz \
    && apk del .build-deps
COPY contracts/events /contracts
RUN python -m grpc_tools.protoc -I/contracts --python_out=/workspace /contracts/rental.proto /contracts/rider.proto
COPY services/risk-pricing/ ./
RUN mkdir -p /data && chown 65532:65532 /data
ENTRYPOINT ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8093"]

FROM development AS final
USER 65532:65532
EXPOSE 8093
