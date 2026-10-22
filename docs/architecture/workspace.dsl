/*
 * C4 model for ATM Nexus (Structurizr DSL).
 * Render:  structurizr-cli export -workspace docs/architecture/workspace.dsl -format mermaid   (or plantuml / dot)
 *          docker run -it --rm -p 8080:8080 -v $PWD/docs/architecture:/usr/local/structurizr structurizr/lite
 */
workspace "ATM Nexus" "Vendor-neutral ATM monitoring and management platform" {

  !identifiers hierarchical

  model {
    noc    = person "NOC operator" "Monitors the fleet, triages alerts/incidents, runs safe remote commands"
    sup    = person "Supervisor" "Approves disruptive commands and deployments (dual control)"
    eng    = person "Field engineer" "Resolves incidents on site"
    aud    = person "Auditor" "Reviews audit trail and access"

    idp    = softwareSystem "Bank IdP / Entra ID" "OIDC, MFA" "External"
    pki    = softwareSystem "Bank PKI" "Issues ATM device certificates (SCEP/EST)" "External"
    itsm   = softwareSystem "ITSM (ServiceNow / Jira SM)" "Ticketing" "External"
    siem   = softwareSystem "SIEM" "Security monitoring" "External"
    chan   = softwareSystem "Notification channels" "SMTP / SMS / Teams" "External"
    aiaz   = softwareSystem "Azure AI services" "Azure ML, AI Search, OpenAI" "External"

    atmfleet = softwareSystem "ATM fleet" "NCR, Diebold Nixdorf/Wincor, Hyosung, GRG, Hitachi, KAL, XFS4IoT devices" "External" {
      app   = container "ATM application" "APTRA / ProTopas / ProBase / Kalignite" "Vendor software"
      xfs   = container "XFS manager + service providers" "CEN/XFS 3.x (msxfs.dll) and/or XFS4IoT" "Vendor software"
      agent = container "ATM Agent" "Status, XFS events, EJ, inventory, signed command execution, store-and-forward" ".NET 9 Worker Service (win-x86)" {
        deviceMgr   = component "Device Manager" "Plugin host for device adapters" "AtmNexus.Agent.Core"
        xfsMgr      = component "XFS Service Manager" "Open/Register/Lock/Unlock/Execute/GetInfo/Close" "AtmNexus.Devices.Xfs"
        devices     = component "Device services" "CardReader, CashDispenser(bridge), Printer, PinPad, SIU" "AtmNexus.Devices.Xfs"
        events      = component "XFS event processor" "Execute/Service/User/System handlers + catalog" "AtmNexus.Devices.Xfs"
        poller      = component "Device poller + reconciler" "Status deltas -> alarms" "AtmNexus.Agent.Core"
        alarms      = component "Alarm pipeline" "Dedup, flap detection, outbox" "AtmNexus.Agent.Core"
        health      = component "Health score engine" "Weighted score + hard caps" "AtmNexus.Agent.Core"
        ej          = component "EJ collector" "Provider, parser, indexer, storage" "AtmNexus.Agent.Core"
        probase     = component "ProBase integration" "File watcher, log parser, health monitor, publisher" "AtmNexus.Agent.Core"
        commands    = component "Remote command dispatcher" "Verify signature, policy, replay; run handlers" "AtmNexus.Agent.Core"
        uploader    = component "Telemetry uploader" "Outbox drain, backoff" "AtmNexus.Agent.Core"
        cache       = component "SQLite cache" "Outbox, checkpoints, nonces" "Microsoft.Data.Sqlite"
      }
    }

    nexus = softwareSystem "ATM Nexus platform" "Monitoring, management, security, update" {
      portal = container "Web portal" "NOC dashboard, consoles, administration" "Blazor Web App + SignalR"
      gw     = container "API gateway" "TLS/mTLS, JWT pre-validation, rate limit, routing" "YARP"
      device = container "Device Service" "ATM master + live state, ingest, offline detection, twin, command queue/signing" "ASP.NET Core 9" {
        epts   = component "Endpoints" "Minimal APIs" "AtmEndpoints, AgentEndpoints"
        ingest = component "IngestTelemetryHandler" "One transaction per telemetry message" "CQRS command handler"
        cmdsvc = component "CommandService" "Queue, approve (dual control), long-poll, complete" "Application service"
        signer = component "Command signer" "ECDSA P-256 (Key Vault HSM)" "ICommandSigner"
        twin   = component "DigitalTwinBuilder" "State, health and fault models" "Pure function"
        outbox = component "Outbox relay" "FOR UPDATE SKIP LOCKED -> Kafka" "BackgroundService"
        offl   = component "Offline detector" "Heartbeat watchdog" "BackgroundService"
        repo   = component "AtmRepository" "Dapper queries" "Infrastructure"
      }
      monitoring = container "Monitoring Service" "Trends, cohort analytics, AI client with fallback" "ASP.NET Core 9"
      alert      = container "Alert Service" "Rules, escalation, maintenance windows, storm control" "ASP.NET Core 9"
      incident   = container "Incident Service" "Lifecycle, SLA, ITSM sync" "ASP.NET Core 9"
      deployment = container "Deployment Service" "Packages, rollouts, rings" "ASP.NET Core 9"
      reporting  = container "Reporting Service" "Read models, EJ search, reports" "ASP.NET Core 9"
      notification = container "Notification Service" "Routing, channels, SignalR hub" "ASP.NET Core 9"
      audit      = container "Audit Service" "Audit sink, chain verification, SIEM export" "ASP.NET Core 9"
      auth       = container "Authentication Service" "OIDC facade, enrolment, mTLS-bound tokens, RBAC claims" "ASP.NET Core 9"
      pg     = container "PostgreSQL" "System of record (schema per service)" "PostgreSQL 16" "Database"
      kafka  = container "Kafka" "Event backbone" "Apache Kafka" "Queue"
      es     = container "Elasticsearch" "EJ and log search" "Elasticsearch 8" "Database"
      blob   = container "Object storage" "Signed packages, log bundles, ML data" "Azure Blob / MinIO" "Database"
    }

    # people
    noc -> nexus.portal "Uses" "HTTPS"
    sup -> nexus.portal "Approves commands/deployments" "HTTPS"
    eng -> nexus.portal "Works incidents" "HTTPS"
    aud -> nexus.portal "Reviews audit" "HTTPS"
    nexus.portal -> idp "Authenticates users" "OIDC + PKCE"
    nexus.portal -> nexus.gw "API calls" "HTTPS (BFF)"

    # agent -> platform
    atmfleet.agent -> nexus.gw "Telemetry, EJ, inventory, command long-poll" "HTTPS mTLS, outbound only"
    atmfleet.agent -> idp "Client-credentials token with device certificate" "mTLS (RFC 8705)"
    atmfleet.agent -> atmfleet.xfs "WFS* / XFS4IoT" "P/Invoke or WebSocket"
    atmfleet.agent -> atmfleet.app "Tails journals and logs" "File I/O"
    atmfleet.xfs -> atmfleet.app "Serves devices to"
    pki -> atmfleet.agent "Issues device certificate" "SCEP/EST"

    # gateway routing
    nexus.gw -> nexus.device "Routes" "HTTP/2 mTLS"
    nexus.gw -> nexus.deployment "Routes"
    nexus.gw -> nexus.auth "Routes"
    nexus.gw -> nexus.reporting "Routes"
    nexus.gw -> nexus.incident "Routes"
    nexus.gw -> nexus.alert "Routes"

    # data
    nexus.device -> nexus.pg "Reads/writes" "SQL (Npgsql/Dapper)"
    nexus.device -> nexus.kafka "Publishes atm.* events (via outbox)" "Kafka"
    nexus.alert -> nexus.kafka "Consumes/publishes"
    nexus.incident -> nexus.kafka "Consumes/publishes"
    nexus.monitoring -> nexus.kafka "Consumes"
    nexus.deployment -> nexus.kafka "Consumes/publishes"
    nexus.reporting -> nexus.kafka "Consumes atm.ej.batch etc."
    nexus.notification -> nexus.kafka "Consumes"
    nexus.audit -> nexus.kafka "Consumes audit.record"
    nexus.reporting -> nexus.es "Indexes/searches EJ" "HTTPS"
    nexus.deployment -> nexus.blob "Stores packages"
    nexus.monitoring -> aiaz "Classify / predict / RCA" "HTTPS, circuit-broken"
    nexus.notification -> chan "Sends"
    nexus.notification -> nexus.portal "Pushes status" "SignalR"
    nexus.incident -> itsm "Syncs tickets" "REST/webhook"
    nexus.audit -> siem "Exports" "CEF/TLS"
    nexus.auth -> idp "Federates" "OIDC"

    # agent components
    atmfleet.agent.poller -> atmfleet.agent.deviceMgr "Polls"
    atmfleet.agent.deviceMgr -> atmfleet.agent.devices "Creates via adapter factories"
    atmfleet.agent.devices -> atmfleet.agent.xfsMgr "Calls"
    atmfleet.agent.xfsMgr -> atmfleet.xfs "WFS*"
    atmfleet.agent.xfsMgr -> atmfleet.agent.events "Raises raw events"
    atmfleet.agent.events -> atmfleet.agent.alarms "Domain events"
    atmfleet.agent.poller -> atmfleet.agent.alarms "Reconciled events"
    atmfleet.agent.alarms -> atmfleet.agent.cache "Persists"
    atmfleet.agent.health -> atmfleet.agent.alarms "Level changes"
    atmfleet.agent.ej -> atmfleet.agent.cache "Checkpoints, outbox"
    atmfleet.agent.probase -> atmfleet.agent.alarms "Events"
    atmfleet.agent.uploader -> atmfleet.agent.cache "Drains"
    atmfleet.agent.commands -> atmfleet.agent.deviceMgr "RestartDevice"

    # device service components
    nexus.device.epts -> nexus.device.ingest "Telemetry"
    nexus.device.epts -> nexus.device.cmdsvc "Commands"
    nexus.device.epts -> nexus.device.twin "Twin"
    nexus.device.cmdsvc -> nexus.device.signer "Signs"
    nexus.device.ingest -> nexus.device.repo "Writes"
    nexus.device.offl -> nexus.device.outbox "atm.offline (same txn)"
    nexus.device.ingest -> nexus.device.outbox "events (same txn)"

    prod = deploymentEnvironment "Production (Azure)" {
      deploymentNode "ATM" "" "Windows 10 IoT / 11 IoT" {
        containerInstance atmfleet.agent
        containerInstance atmfleet.xfs
        containerInstance atmfleet.app
      }
      deploymentNode "Azure Front Door + WAF" "" "Azure" {
        infrastructureNode "Front Door" "Global entry, WAF, Private Link" "Azure Front Door Premium"
      }
      deploymentNode "AKS (3 zones)" "" "Azure Kubernetes Service" {
        deploymentNode "Services" "" "Deployments with HPA/PDB" {
          containerInstance nexus.gw
          containerInstance nexus.portal
          containerInstance nexus.device
          containerInstance nexus.monitoring
          containerInstance nexus.alert
          containerInstance nexus.incident
          containerInstance nexus.deployment
          containerInstance nexus.reporting
          containerInstance nexus.notification
          containerInstance nexus.audit
          containerInstance nexus.auth
        }
        deploymentNode "Kafka" "" "Strimzi 3 brokers + 3 controllers" { containerInstance nexus.kafka }
      }
      deploymentNode "Azure Database for PostgreSQL" "" "Flexible Server, zone-redundant HA" { containerInstance nexus.pg }
      deploymentNode "Elastic" "" "ECK / Elastic Cloud" { containerInstance nexus.es }
      deploymentNode "Storage" "" "Blob, GRS, immutable" { containerInstance nexus.blob }
    }
  }

  views {
    systemContext nexus "Context" { include * autolayout lr }
    container nexus "Containers" { include * autolayout tb }
    container atmfleet "AtmContainers" { include * autolayout lr }
    component atmfleet.agent "AgentComponents" { include * autolayout tb }
    component nexus.device "DeviceServiceComponents" { include * autolayout tb }
    deployment nexus prod "Deployment" { include * autolayout lr }

    styles {
      element "Person" { shape person background #08427b color #ffffff }
      element "External" { background #999999 color #ffffff }
      element "Database" { shape cylinder }
      element "Queue" { shape pipe }
    }
  }
}
