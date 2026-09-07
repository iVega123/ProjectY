import json
from pathlib import Path
from urllib.request import Request, urlopen


class RegisteredSchemas:
    """Lookup once per topic; an unavailable registry never loses an outbox row."""

    def __init__(self, url, contracts=Path('/event-contracts')):
        self.url = url.rstrip('/')
        self.contracts = contracts
        self.ids = {}

    def resolve(self, topic):
        if topic not in self.ids:
            topics = json.loads((self.contracts / 'topics.json').read_text())
            schema = (self.contracts / 'events' / topics[topic]['schema']).read_text()
            request = Request(self.url + '/subjects/' + topic + '-value',
                              json.dumps({'schemaType': 'PROTOBUF', 'schema': schema}).encode(),
                              {'Content-Type': 'application/json'}, method='POST')
            with urlopen(request, timeout=5) as response:
                schema_id = json.load(response)['id']
            if not isinstance(schema_id, int) or schema_id <= 0:
                raise ValueError('Invalid registry schema ID')
            self.ids[topic] = schema_id
        return self.ids[topic]
