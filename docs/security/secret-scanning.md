# Secret scanning

Gitleaks is a blocking part of the root CI workflow. It scans every proposed
commit before the stable `CI / Required` check can pass. The scanner is pinned
to version 8.30.0; upgrades require the synthetic-token canary in CI to keep
failing as expected.

## Local feedback

Install [pre-commit](https://pre-commit.com/), then install the repository hook:

```shell
pre-commit install
```

The hook scans staged changes with the same `.gitleaks.toml` configuration and
Gitleaks version used by CI. Run it explicitly across the working tree with:

```shell
pre-commit run gitleaks --all-files
```

The hook is early feedback, not the security boundary. CI remains mandatory.

## Full-history audit

The complete history was scanned on 2026-08-29 after removing the former
repository-wide `AuthGateTests` allowlist. The redacted command was:

```shell
gitleaks git --config .gitleaks.toml --redact=100 --log-opts="--all" .
```

Gitleaks scanned 26 commits and reported 19 redacted findings, all introduced
by the original configuration commit:

- 10 findings in the four historical `appsettings.json` files were tracked
  credentials. They were removed under C3 and are covered by the revocation
  requirements in the rotation ledger.
- 9 findings were false positives: four Entity Framework primary-key GUIDs,
  three explicitly synthetic test keys, and two GitHub Actions secret
  expressions. None contains a usable credential.

A separate scan of the current tree found only four migration GUIDs and one
empty `.env.example` placeholder. They are covered by two rule-specific
allowlists that require the rule, line shape and exact path to match together.
The former allowlist for the whole `AuthGateTests` tree was removed.

Historical findings do not become safe when deleted from the current tree.
They are treated as compromised and mapped to the rotation ledger in
[`credential-rotation.md`](credential-rotation.md). CI scans proposed commits,
so these known historical findings do not make every future pull request fail.

## Exception process

Directory-wide allowlists are prohibited. A false positive may be waived only
after security review and must include all of the following in the same pull
request:

1. the affected rule and exact path or finding fingerprint;
2. evidence that the value is synthetic or otherwise not a credential;
3. an owner and an expiry date;
4. a narrowly scoped rule-specific allowlist using `condition = "AND"`, or one
   exact fingerprint in `.gitleaksignore`.

Expired exceptions fail review and must be removed or renewed explicitly.
