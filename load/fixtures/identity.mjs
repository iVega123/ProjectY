// Test-only issuer. Never included by the application or production Compose.
import http from "node:http";
import { generateKeyPairSync, sign, randomUUID } from "node:crypto";
const { publicKey, privateKey } = generateKeyPairSync("ed25519");
const key = { ...publicKey.export({ format: "jwk" }), kid: "load-only", alg: "EdDSA", use: "sig" };
const encode = value => Buffer.from(JSON.stringify(value)).toString("base64url");
http.createServer((request, response) => {
  response.setHeader("content-type", "application/json");
  if (request.url === "/jwks") return response.end(JSON.stringify({ keys: [key] }));
  if (request.url === "/token") {
    const now = Math.floor(Date.now() / 1000);
    const body = encode({ alg: "EdDSA", kid: key.kid, typ: "JWT" }) + "." +
      encode({ iss: "projecty.identity", aud: "projecty.rental-operations", sub: "load-rider",
        roles: ["Rider"], iat: now, exp: now + 300, jti: randomUUID() });
    return response.end(JSON.stringify({ token: body + "." + sign(null, Buffer.from(body), privateKey).toString("base64url") }));
  }
  response.statusCode = request.url === "/health" ? 200 : 404;
  response.end("{}");
}).listen(8080, "0.0.0.0");
