import http from "k6/http";
import crypto from "k6/crypto";
import { check, sleep } from "k6";

const BASE_URL = __ENV.BASE_URL || "http://rental-operations:8200";
const PATH = "/api/Rental/create";
const SIGNING_KEY = __ENV.GATEWAY_IDENTITY_SIGNING_KEY;
const KEY_ID = __ENV.GATEWAY_IDENTITY_SIGNING_KEY_ID || "local-v1";
const SUBJECT = __ENV.SLO_DRILL_SUBJECT || "slo-drill-rider";
const AUDIENCE = "projecty.rental-operations";
const VUS = Number.parseInt(__ENV.VUS || "5", 10);
const DURATION = __ENV.DURATION || "6m";

if (!SIGNING_KEY || SIGNING_KEY.length < 32) {
  throw new Error("GATEWAY_IDENTITY_SIGNING_KEY must contain at least 32 characters");
}

export const options = {
  scenarios: {
    availabilityBurnDrill: {
      executor: "constant-vus",
      vus: VUS,
      duration: DURATION,
      gracefulStop: "5s",
    },
  },
  thresholds: {
    checks: ["rate>0.99"],
  },
  discardResponseBodies: true,
};

function identityHeaders(method, path) {
  const issuedAt = Math.floor(Date.now() / 1000).toString();
  const roles = "Rider";
  const canonical = [
    "v1",
    KEY_ID,
    SUBJECT,
    roles,
    issuedAt,
    method,
    path,
    AUDIENCE,
  ].join("\n");
  const signature = crypto.hmac(
    "sha256",
    SIGNING_KEY,
    canonical,
    "base64rawurl",
  );

  return {
    "Content-Type": "application/json",
    "x-identity-key-id": KEY_ID,
    "x-identity-subject": SUBJECT,
    "x-identity-roles": roles,
    "x-identity-issued-at": issuedAt,
    "x-identity-signature": `v1=${signature}`,
  };
}

export default function () {
  const startsAt = new Date(Date.now() + 8 * 86400000).toISOString();
  const predictedEndsAt = new Date(Date.now() + 1 * 86400000).toISOString();
  const payload = JSON.stringify({
    motocycleLicencePlate: "ABC1D23",
    startDate: startsAt,
    predictedEndDate: predictedEndsAt,
  });

  const response = http.post(`${BASE_URL}${PATH}`, payload, {
    headers: identityHeaders("POST", PATH),
    tags: { name: "POST /api/Rental/create", drill: "availability-burn" },
  });

  check(response, {
    "controlled invalid period returns 400": (result) => result.status === 400,
  });
  sleep(0.2);
}
