# 07 - Security architecture

Scope: the platform **never stores, processes or transmits cardholder data (CHD) or sensitive authentication data (SAD)**. That is a design constraint, not an assumption: the agent masks at source, the PIN pad is monitored for status only, raw track data is available only in a test-card diagnostic mode, and the OTel collector redacts 13-19 digit runs from logs. The platform is therefore designed to sit **outside the cardholder data environment (CDE)** but is **connected to** CDE systems (ATMs), so PCI DSS v4.0 requirements for connected-to systems (segmentation, access control, logging, vulnerability management) are applied in full.

> **Implementation status.** Implemented in this repository: device-certificate mTLS token client, JWT/permission policies, per-client rate limiting, signed + dual-control commands with replay protection, PAN/track masking, hash-chained audit function, append-only trigger, hardened containers, CI scanning. Marked **[planned]**: items that need the Authentication Service, Audit Service or later agent features.

## 1. Identities and trust boundaries

| Principal | Authenticates with | Gets | Lifetime |
|---|---|---|---|
| Human user | OIDC authorization-code + **PKCE (S256)** at the bank IdP, MFA enforced by IdP | `access_token` (JWT, 10 min), rotating `refresh_token` (8 h sliding, absolute 24 h) | cookie session on the BFF; tokens never reach the browser |
| ATM agent | **mTLS device certificate** -> OAuth2 `client_credentials` (RFC 8705 `tls_client_auth`), `client_id` = ATM code | JWT scope `atm.agent` (15 min) | refreshed in-process; no secret on disk |
| Service | workload identity (AKS) + client credentials | JWT scope `svc.<name>` (5 min) | auto |
| Commands | ECDSA P-256 signature by the **command-signing key** (HSM-backed) | verified by the ATM itself | <= 15 min validity + nonce |
| Packages | detached signature by the **package-signing key** (separate HSM key) | verified by the agent before install | permanent per version |

```
ATM (DMZ-facing) --443/mTLS--> WAF/Front Door --> Gateway --mTLS mesh--> services --> PG/Kafka (private subnets, no public IP)
Operators ---------HTTPS/OIDC--> Portal (BFF) -----> Gateway
```

## 2. Authentication flows

**Users** - sequence in `docs/01-architecture.md` 6.5. Details: `state` + `nonce` validated; PKCE S256 mandatory; redirect URIs exact-match; the Blazor BFF holds tokens server-side (HttpOnly, Secure, SameSite=Strict cookie, 20 min idle / 8 h absolute); client authentication of the BFF uses `private_key_jwt` (no shared secret).

**Agents**
1. Installer verifies the package signature and that a valid device certificate (issued by the bank PKI via SCEP/EST, key in the TPM/KSP, non-exportable) is present in `LocalMachine\My`.
2. Agent opens TLS with the client certificate to `/connect/token` (`MtlsTokenProvider`); the IdP maps certificate subject/thumbprint -> ATM code -> `client_id` and checks the certificate is not revoked and the ATM is `ACTIVE`.
3. Token is cached until 60 s before expiry; every API call uses `Authorization: Bearer`. The Device Service additionally requires `client_id == body.atmCode` (an agent cannot report for another ATM - `ForbiddenException`).

## 3. Authorisation flow (RBAC)

1. Token carries `permissions: ["atm.read", "alert.ack", ...]` (computed from `user_role x role_permission`) and optional `region: "SOUTH"`.
2. Endpoint policy = **permission**, never role name: `RequireAuthorization("atm.command.execute")` (see `Endpoints.cs`, `Program.cs`).
3. Row-level scope: queries add `AND b.region_code = ANY(@regions)` when the token has region scoping.
4. Segregation of duties is **data-enforced**: `ck_cmd_four_eyes`, `ck_deploy_four_eyes`, `uq_*`, and the approver check in `CommandService.ApproveAsync`.
5. Every denial is audited (`outcome=DENIED`) and counted (`authz_denied_total` **[planned]**) - spikes page the SOC.

Seeded roles (`V001__core_schema.sql`): NOC_VIEWER, NOC_OPERATOR, FIELD_ENGINEER, SUPERVISOR, ADMIN, AUDITOR.

## 4. Token lifecycle

| Token | TTL | Storage | Revocation |
|---|---|---|---|
| User access JWT | 10 min | BFF memory | expires; back-channel logout list checked by gateway (60 s cache) |
| Refresh token | 8 h sliding / 24 h absolute, **rotated on every use**, reuse detection revokes the whole family | Auth DB (hashed, SHA-256) | on logout, role change, user disable, reuse detected |
| Agent access JWT | 15 min | agent memory only | revoke certificate -> token endpoint refuses; existing token dies <= 15 min |
| Command signature | `expiresAt` <= 15 min + nonce | `remote_command`, agent nonce table | expiry; `RemoteCommandDispatcher` rejects replays |

JWT validation (gateway **and** service, defence in depth): signature via JWKS (cached, refreshed on unknown `kid`), `iss`, `aud=atmnexus-api`, `exp/nbf` with 30 s skew, `ValidAlgorithms` restricted to RS256/ES256, `RequireSignedTokens`, `MapInboundClaims=false`.

## 5. Key rotation

| Key | Rotation | Procedure (zero downtime) |
|---|---|---|
| OIDC token-signing | every 90 d | publish new key in JWKS (**next**) -> start signing with it after 24 h -> keep old in JWKS for max token TTL + 1 h -> retire |
| **Command-signing** | every 180 d or on suspicion | agents pin **two** trusted keys (`Commands:TrustedKeys`, keyed by `keyId`): push the new public key in a *config deployment signed by the old key*, wait for >99.9% fleet coverage (metric `agent_trusted_key_count` **[planned]**), switch `CommandSigning__KeyId`, remove the old after 30 d. Emergency: signed `RevokeKey` package **[planned, Phase 2]**, or reinstall via software distribution. |
| Package-signing | 1 year | same dual-pin mechanism, separate key |
| Device certificates | 1-2 y, auto-renew at 70% lifetime via SCEP/EST | agent alarm `CERT_EXPIRING` at 30 d **[planned]** (the installer already refuses < 7 d: implemented) |
| Service TLS (mesh) | 24 h auto (SPIFFE/cert-manager) | automatic |
| DB credentials | 30 d | Key Vault rotation + CSI sync + rolling restart; dual-user rotation (alternate `app_a`/`app_b`) |
| Kafka SASL secrets | 90 d | same |

## 6. Secrets management

* **No secrets in Git, images, or environment files.** Azure Key Vault (HSM for signing keys) -> Secrets Store CSI driver -> mounted files / synced Kubernetes Secrets (`deployment/k8s/device-service.yaml`, `SecretProviderClass`). On-prem: HashiCorp Vault with the same CSI pattern.
* AKS **workload identity** (federated credentials) - pods have no stored client secrets.
* Agents hold **no shared secrets**: identity = device certificate with non-exportable key. `agent.local.json` contains configuration only; ACLs restrict it to SYSTEM/Administrators (`install-agent.ps1`).
* CI uses GitHub OIDC -> Azure federated identity; `gitleaks` blocks secrets in PRs.
* The command-signing private key is **sign-only in Key Vault Managed HSM** in production (the PEM-file signer in the repo is for dev/on-prem; swap `ICommandSigner` for a `CryptographyClient` implementation - one class).

## 7. Agent hardening

| Control | Implementation |
|---|---|
| No inbound surface | Agent opens no listening port; all traffic outbound 443 |
| Least privilege | Runs as a virtual service account; elevated only for service control (granted via SCM ACL on the specific services) |
| Signed code | Binaries signed (Trusted Signing); installer verifies Authenticode before install |
| Command safety | Allow-listed command types, validated parameters, no shell, `shutdown.exe` resolved from System32 only (`SafeProcessRunner`), idle gate, local kill-switch policy |
| Data minimisation | `PanMasker` on EJ/log/ProBase lines and log bundles; track data zeroed after parse (`SensitiveBytes`, `Array.Clear`); raw tracks only for allow-listed test BINs |
| Tamper resistance | SQLite cache holds no CHD; config file ACL; service recovery actions (implemented); agent integrity hash reported in telemetry **[planned]** |
| Resource safety | Bounded channels, outbox cap, per-call timeouts; agent can never block XFS (separate thread, `WFSExecute` only on idle commands) |
| Clock | commands tolerate 2 min skew (implemented); NTP drift reporting **[planned]** |

## 8. Network security

* Segmentation: ATM VLAN -> DMZ gateway only (443); services in private subnets; PG/Kafka/ES reachable only from the service subnets (NetworkPolicy + NSG); admin access via Bastion + JIT.
* WAF with OWASP CRS; request size limits (1 MB telemetry, 5 MB EJ/inventory, 250 MB logs); rate limits per client id.
* mTLS between services (mesh); TLS 1.2+ (1.3 preferred), strong ciphers only, HSTS; certificate pinning to the bank's private CA in the agent trust store.

## 9. PCI DSS v4.0 mapping

| Req. | Control in ATM Nexus | Evidence |
|---|---|---|
| **1** Network security controls | VLAN/DMZ segmentation, NetworkPolicy default-deny, outbound-only agents | `deployment/k8s/device-service.yaml` NetworkPolicy; firewall rule set |
| **2** Secure configurations | Chiseled non-root images, read-only FS, no default accounts, hardened Windows baseline for agent host | Dockerfile template, pod securityContext |
| **3** Protect stored account data | **CHD not stored**; PAN masked BIN6+last4 at source; track/PIN/keys never collected; EJ strict ES mapping rejects new fields; at-rest encryption (PG TDE/CMK, ES, Blob) as defence in depth | `PanMasker`, `EJIndexer`, tests `PanMaskerTests`, `EjParserTests` |
| **4** Protect data in transit | TLS 1.2+/mTLS everywhere, pinned CA | gateway/mesh config |
| **5** Malware | Agent host protected by bank EDR; agent signed; allow-listing (WDAC) | install runbook |
| **6** Secure development | SAST (CodeQL), dependency scan (`dotnet list package --vulnerable`), container scan (Trivy), secrets scan, SBOM, code review (2 approvers on `main`), peer-reviewed security tests, change control | `.github/workflows/ci.yml` |
| **7** Restrict access (need-to-know) | Permission-based RBAC, region scoping, least-privilege DB roles | `V001` seed, policies |
| **8** Identify & authenticate | OIDC + MFA, unique IDs, no shared accounts, agents = device identities, session timeouts (20 min idle) | IdP config |
| **9** Physical access | Out of platform scope; SIU tamper/door telemetry supports ATM physical controls | health model |
| **10** Log & monitor | Hash-chained append-only audit (`audit_append`), time sync, daily chain verification, SIEM export, alerting on audit failures | `audit_log`, Audit Service |
| **11** Test security | Quarterly vulnerability scans, annual pen test, ASV scans on public endpoints, file-integrity monitoring on agent binaries | runbook |
| **12** Policies / risk | Threat model (below), IR runbook, third-party risk for vendors | docs |

**Encryption summary** - transit: TLS 1.2+/mTLS; at rest: PG storage encryption with customer-managed key, Elasticsearch encrypted volumes, Blob SSE-CMK, Kafka disk encryption + SASL_SSL; field level: none needed (no CHD), but command parameters and audit `details` are scrubbed of secrets by construction.

**Logging** - what is logged: authentication, authorisation failures, privileged actions (command queue/approve/result, role changes, deployments), admin changes, audit-chain verification, agent signature failures. What is never logged: PAN, tracks, PIN blocks, keys, tokens, signatures (verified by log redaction transform + tests).

**Vulnerability management** - SLA: Critical 7 d, High 30 d, Medium 90 d (container base images rebuilt weekly; chiseled images keep the CVE surface minimal); Dependabot/Renovate; Trivy gate in CI (`--ignore-unfixed`, exit 1 on HIGH/CRITICAL).

## 10. Threat model (STRIDE summary)

| Threat | Asset | Mitigation |
|---|---|---|
| Spoofing an ATM | Telemetry integrity | mTLS device certificate bound to ATM code; token `client_id` must equal body `atmCode`; revocation on decommission |
| Spoofing the platform to an ATM | **Remote command channel** | agent verifies ECDSA signature against pinned keys; TLS cert chain pinned; commands addressed to one ATM code |
| Tampering with commands | Command parameters | canonical form includes every field; newline/`=` escaping defeats injection (`Canonical_IsStable_AndEscapesDelimiters` test) |
| Replay of a captured command | Reboot/restart | `expiresAt` + persistent nonce store (survives restart) |
| Repudiation | Who rebooted the ATM | dual control + hash-chained audit with requester/approver ids inside the signed payload |
| Information disclosure | CHD in EJ/logs | masking at source, strict ES mapping, redaction in OTel, log bundle sanitiser |
| DoS (platform) | Ingest | per-client token bucket, WAF, 1 MB limit, outbox/relay isolation, HPA |
| DoS (ATM) | Agent impact on cash dispensing | agent off the transaction path, bounded resources, idle gate, kill-switch policy |
| Elevation of privilege | Admin functions | permission claims, four-eyes, JIT admin, region scope |
| Supply-chain | Agent update | signed packages (HSM), hash verification, canary rollout with auto-rollback, SBOM/provenance |
| Rogue insider with DB access | Audit trail | append-only trigger, hash chain, off-box SIEM/WORM copy, separate DB roles |
