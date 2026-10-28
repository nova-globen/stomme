# Nova Globen Stomme technical documentation

How each piece of Stomme works, written for the AI agent or developer building this app. Each document explains a
mechanism and the public surface an app uses; each skill in [`.claude/skills/`](../.claude/skills/) walks through
building one kind of piece, with how to verify it and the common mistakes.

Start with [Architecture overview](architecture/overview.md) and [Module anatomy](modules/anatomy.md), then build your
first module with the `stomme-create-module` skill.

## Architecture and operations

| Document | What it covers | Skills |
|---|---|---|
| [Architecture overview](architecture/overview.md) | What Stomme is, the app's projects and packages, the path of a request | |
| [Startup and discovery](architecture/startup-and-discovery.md) | Host startup, how modules and services are found and registered, where middleware goes in the pipeline | |
| [Configuration](architecture/configuration.md) | The settings files, the override file, feature flags and every switch | [stomme-configure-features](../.claude/skills/stomme-configure-features/SKILL.md) |
| [Install, run and upgrade](operations/install-run-upgrade.md) | The installer, unattended installs, databases, upgrading Stomme | [stomme-run-and-install](../.claude/skills/stomme-run-and-install/SKILL.md), [stomme-upgrade-stomme](../.claude/skills/stomme-upgrade-stomme/SKILL.md) |
| [Deploy](operations/deploy.md) | Publishing, containers, persistent data, reverse proxies | [stomme-deploy-app](../.claude/skills/stomme-deploy-app/SKILL.md) |

## Modules

| Document | What it covers | Skills |
|---|---|---|
| [Module anatomy](modules/anatomy.md) | What a module is, its definition, the Server/Client/Shared split, discovery | [stomme-create-module](../.claude/skills/stomme-create-module/SKILL.md), [stomme-bundle-external-module](../.claude/skills/stomme-bundle-external-module/SKILL.md) |
| [Module UI](modules/module-ui.md) | `ModuleBase`, the state a component receives, actions and routing | [stomme-build-module-ui](../.claude/skills/stomme-build-module-ui/SKILL.md) |
| [Module settings and templates](modules/settings-and-templates.md) | Placement settings, module-wide configuration, page and site templates | [stomme-module-settings](../.claude/skills/stomme-module-settings/SKILL.md) |
| [Portable, searchable, migratable](modules/portable-searchable-migratable.md) | Import and export of content, search, upgrade hooks | [stomme-module-import-export-search](../.claude/skills/stomme-module-import-export-search/SKILL.md) |
| [The application layer](modules/application-layer.md) | Commands, queries, handlers, validation, domain events | [stomme-add-command-or-query](../.claude/skills/stomme-add-command-or-query/SKILL.md) |
| [API and client services](modules/api-and-client-services.md) | Controllers, authorization, client services over HTTP | [stomme-add-api-endpoint](../.claude/skills/stomme-add-api-endpoint/SKILL.md) |

## Data

| Document | What it covers | Skills |
|---|---|---|
| [Module persistence](persistence/module-persistence.md) | Storage-blind aggregates on any database, tenant ownership, queries | [stomme-add-aggregate](../.claude/skills/stomme-add-aggregate/SKILL.md) |
| [Framework data](persistence/framework-data.md) | Sites, pages, users, settings and files as an app reads them | |
| [Datasets, dashboards, formats and cloud cost](data/datasets-dashboards-formats.md) | Registered data, classification, dashboards, formats | [stomme-build-dataset-and-dashboard](../.claude/skills/stomme-build-dataset-and-dashboard/SKILL.md) |

## UI and UX

| Document | What it covers | Skills |
|---|---|---|
| [UX standards](ui/ux-standards.md) | The bar every page meets: consistency, states, errors, light and dark, narrow screens, keyboard | [stomme-ux-review](../.claude/skills/stomme-ux-review/SKILL.md) |
| [Components and controls](ui/components-and-controls.md) | The components to build pages from | [stomme-build-module-ui](../.claude/skills/stomme-build-module-ui/SKILL.md) |
| [Themes](ui/themes.md) | Themes, containers, the colour tokens | [stomme-create-theme](../.claude/skills/stomme-create-theme/SKILL.md) |
| [Render modes](ui/render-modes.md) | Static, Interactive and Headless; Server and WebAssembly | |
| [Localization](ui/localization.md) | Resources, cultures, translated text | |

## Security

| Document | What it covers | Skills |
|---|---|---|
| [Tenancy](security/tenancy.md) | Alias, site and tenant; what is per site, per tenant and installation-wide | |
| [Identity and permissions](security/identity-and-permissions.md) | Users, roles, permission keys, checking access | [stomme-secure-a-module](../.claude/skills/stomme-secure-a-module/SKILL.md) |
| [Authentication options](security/authentication-options.md) | External sign-in (OIDC, OAuth 2.0, SAML2, Windows) and Stomme as a provider | [stomme-add-external-login](../.claude/skills/stomme-add-external-login/SKILL.md), [stomme-oidc-provider](../.claude/skills/stomme-oidc-provider/SKILL.md) |
| [Security posture](security/security-posture.md) | Fail closed, secrets, egress, what every module must do | [stomme-secure-a-module](../.claude/skills/stomme-secure-a-module/SKILL.md) |

## Platform services

| Document | What it covers | Skills |
|---|---|---|
| [Background jobs](services/jobs.md) | Jobs, scheduling, work per site | [stomme-add-background-job](../.claude/skills/stomme-add-background-job/SKILL.md) |
| [Caching and sync](services/caching-and-sync.md) | The cache, its layers and its limits | [stomme-use-cache](../.claude/skills/stomme-use-cache/SKILL.md) |
| [Events and messaging](services/events-and-messaging.md) | Domain events, the event bus, the message bus, real-time push | [stomme-publish-and-handle-events](../.claude/skills/stomme-publish-and-handle-events/SKILL.md) |
| [Logging, health and metering](services/logging-health-metering.md) | Logs, health probes and alerts, usage and quotas | |
| [Locks and collaboration](services/locks-and-collaboration.md) | Locks, editing sessions, presence | |
| [Notifications](services/notifications.md) | In-app, e-mail and webhook notifications | [stomme-send-notification](../.claude/skills/stomme-send-notification/SKILL.md) |
| [Files and folders](services/files-and-folders.md) | Storing and serving files, privacy, upload guards | [stomme-store-files](../.claude/skills/stomme-store-files/SKILL.md) |
| [Search](services/search.md) | Site search and how a module takes part | [stomme-module-import-export-search](../.claude/skills/stomme-module-import-export-search/SKILL.md) |
| [Backup and restore](services/backup-restore.md) | Backups, archive storage, restore | |
| [Data rights](services/data-rights.md) | Erasure, a person's data package, data export, classification | [stomme-support-erasure-and-export](../.claude/skills/stomme-support-erasure-and-export/SKILL.md) |

## AI, automation and integrations

| Document | What it covers | Skills |
|---|---|---|
| [The AI assistant](ai/ai-assistant.md) | Chat, agents, the site's model, calling it from a module | [stomme-use-ai-in-module](../.claude/skills/stomme-use-ai-in-module/SKILL.md) |
| [The MCP server](ai/mcp-server.md) | A site's modules as tools and resources for AI clients | [stomme-expose-module-to-mcp](../.claude/skills/stomme-expose-module-to-mcp/SKILL.md) |
| [The MCP client](ai/mcp-client.md) | Registering external MCP servers | |
| [Pipelines](pipelines/overview.md) | The workflow engine: definitions, runs, triggers, expressions | [stomme-build-pipeline-via-api](../.claude/skills/stomme-build-pipeline-via-api/SKILL.md) |
| [The pipeline editor](pipelines/editor.md) | The visual editor, testing steps, versions, n8n import | [stomme-import-n8n-workflow](../.claude/skills/stomme-import-n8n-workflow/SKILL.md) |
| [Custom pipeline steps](pipelines/custom-steps.md) | Writing a step type of the app's own | [stomme-create-pipeline-step](../.claude/skills/stomme-create-pipeline-step/SKILL.md) |
| [The pipelines HTTP API](pipelines/api.md) | Creating, publishing and running pipelines over HTTP | [stomme-build-pipeline-via-api](../.claude/skills/stomme-build-pipeline-via-api/SKILL.md) |
| [Connectors and connections](connectors/overview.md) | The connector catalogue, connections, credentials, egress | |
| [Writing a connector](connectors/writing-a-connector.md) | A connector type of the app's own | [stomme-write-connector](../.claude/skills/stomme-write-connector/SKILL.md) |
| [Using connections in code](connectors/using-connections-in-code.md) | Reading and writing through a connection from module code | [stomme-use-connection](../.claude/skills/stomme-use-connection/SKILL.md) |
