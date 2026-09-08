import io
import json
from pathlib import Path
from unittest.mock import patch
import pytest
from schema_registry import RegisteredSchemas


def test_cache_keeps_warm_producer_independent_of_registry(tmp_path):
    (tmp_path / 'events').mkdir()
    (tmp_path / 'topics.json').write_text(json.dumps({'risk.scored': {'schema': 'rider.proto'}}))
    (tmp_path / 'events' / 'rider.proto').write_text('syntax = "proto3";')
    cache = RegisteredSchemas('http://registry/apis/ccompat/v7', tmp_path)
    with patch('schema_registry.urlopen', return_value=io.BytesIO(b'{"id":42}')) as request:
        assert cache.resolve('risk.scored') == 42
        request.side_effect = OSError('registry down')
        assert cache.resolve('risk.scored') == 42
        assert request.call_count == 1
    cold = RegisteredSchemas('http://registry/apis/ccompat/v7', tmp_path)
    with patch('schema_registry.urlopen', side_effect=OSError('registry down')):
        with pytest.raises(OSError):
            cold.resolve('risk.scored')
