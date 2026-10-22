#!/usr/bin/env python3
"""Generates docs/04-database-tables.md from db/migrations/V001__core_schema.sql so the table reference can never drift from the schema.
Usage: python3 scripts/gen-db-docs.py
"""
import re, pathlib, sys

root = pathlib.Path(__file__).resolve().parent.parent
sql = (root / "db/migrations/V001__core_schema.sql").read_text()

PURPOSE = {
 "vendor": "Reference: ATM/device vendors and the XFS vendor profile used by agents (status layouts, event overrides).",
 "location": "Physical site of an ATM (address, geo, time zone). Shared by branches and ATMs.",
 "branch": "Bank branch / cost centre; carries the region used for row-level RBAC scoping.",
 "atm": "ATM master record (inventory). `atm_code` is the terminal id and the agent's OAuth client_id.",
 "atm_status": "Current live status: exactly one row per ATM, upserted on every telemetry message (hot table).",
 "atm_status_history": "Sampled time series of health for trends, MTBF and AI features. Monthly range partitions.",
 "atm_component": "Devices of an ATM (one row per XFS logical service): state + canonical attributes.",
 "atm_cash_unit": "Cassette / cash unit counters per ATM (feeds cash forecasting and the digital twin).",
 "atm_alert": "Alerts, de-duplicated per (ATM, fingerprint) while active; carries occurrence counters.",
 "atm_incident": "Incident (ticket) lifecycle with SLA; at most one open incident per ATM and category.",
 "deployment_package": "Signed software/config/OS-patch/agent packages available for rollout.",
 "deployment_history": "One row per (deployment, ATM): state machine, approvals (four-eyes), failure reason.",
 "app_user": "Platform users, provisioned just-in-time from the IdP (no passwords stored).",
 "role": "RBAC role.",
 "permission": "Atomic permission code (emitted in the `permissions` claim).",
 "role_permission": "Role to permission mapping.",
 "user_role": "User to role assignment with optional region scope.",
 "audit_log": "Append-only, hash-chained audit trail (PCI DSS 10.3/10.5), monthly partitions.",
 "outbox_event": "Transactional outbox: events written with the state change, relayed to Kafka.",
 "remote_command": "Signed remote commands queued for agents to pull; dual-control state machine.",
}
EXTRA_SAMPLES = {
 "atm_status_history": ["INSERT INTO atm_status_history (atm_id, recorded_at, health_score, health_level, connectivity)\nVALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc', now(), 92, 'HEALTHY', 'ONLINE');"],
 "audit_log": ["-- always through the function (it chains the hash and serialises writers):\nSELECT audit_append('1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed', 'noc.operator1', '10.1.2.3'::inet, 'atm.command.queue', 'remote_command',\n                    '5f0c0b9e-4c6e-4c0e-9a31-1b1d2f0f7c11', 'cccccccc-cccc-cccc-cccc-cccccccccccc', 'SUCCESS',\n                    '{\"type\":\"RestartApplication\",\"reason\":\"INC-2025-000123\"}'::jsonb, 'trace-0af7651916cd43dd8448eb211c80319c');"],
 "outbox_event": ["INSERT INTO outbox_event (topic, event_key, payload) VALUES\n ('atm.offline', 'cccccccc-cccc-cccc-cccc-cccccccccccc',\n  '{\"eventId\":\"8c1f...\",\"eventType\":\"atm.offline\",\"schemaVersion\":1,\"payload\":{\"atmCode\":\"ATM-000123\",\"missedHeartbeats\":3}}');"],
 "remote_command": ["INSERT INTO remote_command (atm_id, command_type, parameters, issued_by, issued_at, expires_at, nonce, key_id, signature, state)\nVALUES ('cccccccc-cccc-cccc-cccc-cccccccccccc', 'RestartWindowsService', '{\"service\":\"Spooler\"}', '1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed',\n        now(), now() + interval '15 minutes', md5(random()::text), 'cmd-2025-01', 'MEUCIQ...', 'PENDING');"],
 "user_role": [],
}
ORDER = ["vendor","location","branch","atm","atm_status","atm_status_history","atm_component","atm_cash_unit","atm_alert","atm_incident",
         "deployment_package","deployment_history","app_user","role","permission","role_permission","user_role","audit_log","outbox_event","remote_command"]
REQUESTED = {"ATM":"atm","ATMStatus":"atm_status","ATMComponent":"atm_component","ATMAlert":"atm_alert","ATMIncident":"atm_incident",
             "DeploymentPackage":"deployment_package","DeploymentHistory":"deployment_history","User":"app_user","Role":"role",
             "Permission":"permission","AuditLog":"audit_log","Location":"location","Branch":"branch","Vendor":"vendor"}

def split_top(body):
    parts, depth, cur, q = [], 0, [], False
    for ch in body:
        if ch == "'" : q = not q
        if not q:
            if ch == "(": depth += 1
            elif ch == ")": depth -= 1
            elif ch == "," and depth == 0:
                parts.append("".join(cur).strip()); cur = []; continue
        cur.append(ch)
    if "".join(cur).strip(): parts.append("".join(cur).strip())
    return parts

def strip_comments(s): return re.sub(r"--[^\n]*", "", s)

tables = {}
for m in re.finditer(r"CREATE TABLE (\w+) \((.*?)\n\)\s*(?:PARTITION BY [^;]*|WITH \([^)]*\))?;", sql, re.S):
    name, body = m.group(1), strip_comments(m.group(2))
    cols, cons = [], []
    for p in split_top(body):
        p = " ".join(p.split())
        if re.match(r"(CONSTRAINT|PRIMARY KEY|UNIQUE|CHECK|FOREIGN KEY)\b", p, re.I): cons.append(p)
        else:
            cm = re.match(r"(\w+)\s+(.*)", p)
            cols.append((cm.group(1), cm.group(2)))
    tables[name] = {"cols": cols, "cons": cons, "idx": [], "sample": []}

for m in re.finditer(r"CREATE (UNIQUE )?INDEX (\w+)\s+ON\s+(\w+)\s*(.*?);", sql):
    if m.group(3) in tables: tables[m.group(3)]["idx"].append(("UNIQUE " if m.group(1) else "") + f"`{m.group(2)}` {m.group(4)}")
for m in re.finditer(r"ALTER TABLE (\w+) ADD CONSTRAINT (\w+) (.*?);", sql):
    tables[m.group(1)]["cons"].append(f"CONSTRAINT {m.group(2)} {m.group(3)}")
for m in re.finditer(r"^(INSERT INTO (\w+).*?;)\n", sql, re.S | re.M):
    if m.group(2) in tables: tables[m.group(2)]["sample"].append(m.group(1))

def typ_and_rest(rest):
    m = re.match(r"((?:varchar|char|numeric|timestamptz|timestamp|smallint|integer|bigint|boolean|uuid|jsonb|text|inet|macaddr|bytea|date)(?:\([^)]*\))?)\s*(.*)", rest, re.I)
    return (m.group(1), m.group(2)) if m else (rest, "")

out = ["# Database tables (generated)", "",
       "> Generated from `db/migrations/V001__core_schema.sql` by `scripts/gen-db-docs.py`. Do not edit by hand.", "",
       "## Requested table to physical table", "", "| Requested | Table |", "|---|---|"]
out += [f"| {k} | `{v}` |" for k, v in REQUESTED.items()]
out += ["", "Supporting tables added by the design: `atm_status_history`, `atm_cash_unit`, `role_permission`, `user_role`, `outbox_event`, `remote_command`.", ""]

for t in ORDER:
    d = tables[t]
    out += [f"## `{t}`", "", PURPOSE.get(t, ""), "", "| Column | Type | Nullability / default / inline constraints |", "|---|---|---|"]
    for c, rest in d["cols"]:
        ty, tail = typ_and_rest(rest)
        out.append(f"| `{c}` | `{ty}` | {tail.replace('|','\\|') or '-'} |")
    out += ["", "**Constraints**", ""]
    out += [f"- `{c}`" for c in d["cons"]] or ["- (primary key only, declared inline)"]
    out += ["", "**Indexes**", ""]
    out += [f"- {i}" for i in d["idx"]] or ["- (primary key / unique constraints only)"]
    out += ["", "**Sample SQL**", "", "```sql"]
    out += [s for s in (d["sample"][:2] + EXTRA_SAMPLES.get(t, []))] or ["-- no seed rows; rows are created by the application (see docs/02-mvp-modules.md)"]
    out += ["```", ""]

(root / "docs/04-database-tables.md").write_text("\n".join(out))
print(f"wrote docs/04-database-tables.md: {len(tables)} tables parsed")
missing = [t for t in ORDER if t not in tables]
if missing: sys.exit(f"missing: {missing}")
