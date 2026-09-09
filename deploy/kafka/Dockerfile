FROM apache/kafka:4.3.1
COPY topics.txt /topics.txt
COPY create-topics.sh /create-topics.sh
USER 1000:1000
ENTRYPOINT ["/bin/bash", "/create-topics.sh"]
