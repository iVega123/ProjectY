FROM python:3.13-slim-bookworm AS development
WORKDIR /workspace
RUN apt-get update && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng && rm -rf /var/lib/apt/lists/*
COPY services/risk-pricing/requirements.lock ./
RUN pip install --no-cache-dir -r requirements.lock
COPY contracts/events /contracts
RUN python -m grpc_tools.protoc -I/contracts --python_out=/workspace /contracts/rental.proto /contracts/rider.proto
COPY services/risk-pricing/ ./
RUN mkdir -p /data && chown 65532:65532 /data
ENTRYPOINT ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8093"]

FROM development AS final
USER 65532:65532
EXPOSE 8093
