import {test} from 'node:test';
import assert from 'node:assert/strict';
import {validate} from './check.mjs';
const original = 'message Fact {\n optional string event_id = 1;\n optional int64 total_minor = 2;\n}';
test('additive optional evolution is safe', () => validate(original.replace('\n}', '\n optional string currency = 3;\n}'), original));
test('money, presence and field reuse fail closed', () => {
  for (const broken of [original.replace('int64', 'double'), original.replace('optional string', 'string'), original.replace('event_id', 'rental_id')]) {
    assert.throws(() => validate(broken, original));
  }
});
test('removed field requires reservation', () => {
  const removed = original.replace(' optional int64 total_minor = 2;', '');
  assert.throws(() => validate(removed, original));
  validate(removed.replace('\n}', '\n reserved 2;\n}'), original);
});
