import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

// Exercise the actual k6 lifecycle with a controllable clock and HTTP boundary.
function harness() {
  let now = 1000000, issued = 0;
  const requests = [];
  let fixtureStatus = 200;
  const sandbox = {
    __ENV: {}, Date: { now: () => now },
    Counter: class { add() {} }, Trend: class { add() {} },
    execution: { scenario: { iterationInTest: 0 } },
    check: () => true, sleep: () => {}, fail: message => { throw new Error(message); },
    http: {
      expectedStatuses: () => ({}),
      get: () => ({ status: fixtureStatus, json: () => ({ token: "token-" + ++issued, expiresAt: now / 1000 + 300 }) }),
      post: (url, body, options) => {
        requests.push({ token: options.headers.Authorization, name: options.tags.name });
        return { status: 201, timings: { duration: 1 } };
      },
    },
  };
  const source = readFileSync(new URL("../k6/rental-flow.js", import.meta.url), "utf8")
    .replace(/^import .*;\r?$/gm, "")
    .replace("export default function(data)", "function iteration(data)")
    .replace(/export /g, "");
  vm.createContext(sandbox);
  vm.runInContext(source + "\nglobalThis.lifecycle = {setup, iteration};", sandbox);
  return { lifecycle: sandbox.lifecycle, requests, issued: () => issued,
    advance: ms => { now += ms; }, breakFixture: () => { fixtureStatus = 503; } };
}

test("renews before expiry, accounts for warmup time and retains refreshed token", () => {
  const h = harness(), data = h.lifecycle.setup();
  h.advance(40000);
  h.lifecycle.iteration(data);
  assert.equal(h.issued(), 1);
  h.advance(229999);
  h.lifecycle.iteration(data);
  assert.equal(h.issued(), 1);
  h.advance(1);
  h.lifecycle.iteration(data);
  assert.equal(h.requests.at(-1).token, "Bearer token-2");
  h.advance(40000); // Workload is now beyond the original five-minute token lifetime.
  h.lifecycle.iteration(data);
  assert.equal(h.issued(), 2);
  assert.equal(h.requests.at(-1).token, "Bearer token-2");
  h.advance(230000);
  h.lifecycle.iteration(data);
  assert.equal(h.requests.at(-1).token, "Bearer token-3");
});

test("failed refresh stops the iteration instead of sending an expired token", () => {
  const h = harness(), data = h.lifecycle.setup();
  h.advance(300000);
  h.breakFixture();
  const count = h.requests.length;
  assert.throws(() => h.lifecycle.iteration(data), /fixture unavailable/);
  assert.equal(h.requests.length, count);
});

