using Stomme.Connectivity;
using Stomme.Infrastructure;
using Stomme.Modules.Connectors.ActiveDirectory;
using Stomme.Modules.Connectors.AzureVirtualMachines;
using Stomme.Modules.Connectors.CiscoDnaCenter;
using Stomme.Modules.Connectors.Cost;
using Stomme.Modules.Connectors.Cylance;
using Stomme.Modules.Connectors.EntraId;
using Stomme.Modules.Connectors.Entuity;
using Stomme.Modules.Connectors.FlexeraOne;
using Stomme.Modules.Connectors.FlexNetManager;
using Stomme.Modules.Connectors.Ftp;
using Stomme.Modules.Connectors.Intune;
using Stomme.Modules.Connectors.PostgreSql;
using Stomme.Modules.Connectors.Qualys;
using Stomme.Modules.Connectors.Rapid7;
using Stomme.Modules.Connectors.RedHatSatellite;
using Stomme.Modules.Connectors.RestApi;
using Stomme.Modules.Connectors.ServiceNow;
using Stomme.Modules.Connectors.SqlServer;
using Stomme.Modules.Connectors.SuseManager;
using Stomme.Modules.Connectors.Taegis;
using Stomme.Modules.Connectors.Tenable;
using Stomme.Modules.Connectors.Xml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Connectors.Startup
{
    /// <summary>
    /// The Connectors family's composition (BL-96): every in-tree connector registered as a singleton <see cref="IConnector"/>
    /// for the host's catalog, which refuses a duplicate type key at composition (D110). The family is ONE Stomme module:
    /// a connector is a folder, not an <c>IModule</c> of its own (the predecessor platform's per-connector plugin modules did nothing but this
    /// one registration). An external connector registers itself the same way from its own <see cref="IServerStartup"/> -
    /// see <c>examples/Connectors/</c>.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }

        public void ConfigureServices(IServiceCollection services)
        {
            // Increment 1: the generic REST connector and PostgreSQL - the two types the demo content declares.
            services.AddSingleton<IConnector, RestApiConnector>();
            services.AddSingleton<IConnector, PostgreSqlConnector>();

            // Increment 2: the other drivers and the one host process (HostOnly).
            services.AddSingleton<IConnector, SqlServerConnector>();
            services.AddSingleton<IConnector, FtpConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Imap.ImapConnector>();
            // BL-229 - n8n's Slack, Telegram and Discord nodes' send message: the chat services a pipeline posts to.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.SlackConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.TelegramConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.DiscordConnector>();
            // BL-229 - n8n's Mattermost node's message posted and its Twilio node's SMS sent: two more for Send chat message.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.MattermostConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.TwilioConnector>();
            // BL-229 - n8n's Microsoft Teams node's channel and chat message: Send chat message to Teams.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.TeamsConnector>();
            // BL-229 - n8n's WhatsApp Business Cloud node's text message: Send chat message to WhatsApp, through Meta's Cloud API.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.WhatsAppConnector>();
            // BL-229 - n8n's GitHub node: a repository's issues, a release, a file and a workflow.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GitHub.GitHubConnector>();
            // BL-229 - n8n's Google Calendar node: events and a calendar's availability.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleCalendar.GoogleCalendarConnector>();
            // BL-229 - n8n's Trello node: cards and their comments.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Trello.TrelloConnector>();
            // BL-229 - n8n's Asana node: tasks and their comments.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Asana.AsanaConnector>();
            // BL-229 - n8n's ClickUp node: tasks and their comments.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.ClickUp.ClickUpConnector>();
            // BL-229 - n8n's Stripe node: customers, charges and the balance.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Stripe.StripeConnector>();
            // BL-229 - n8n's Salesforce node: records of any object by SOQL, and their writes.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Salesforce.SalesforceConnector>();
            // BL-229 - n8n's Shopify node: a shop's orders and products, over Shopify's GraphQL Admin API.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Shopify.ShopifyConnector>();
            // BL-229 - n8n's Pipedrive node: deals, persons, organizations, activities and products, and notes.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Pipedrive.PipedriveConnector>();
            // BL-229 - n8n's Zendesk node: tickets, users and organizations.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Zendesk.ZendeskConnector>();
            // BL-229 - n8n's Supabase node: a project's table rows, through its REST API.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Supabase.SupabaseConnector>();
            // BL-229 - n8n's Mailchimp node: an audience's members and the account's campaigns.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Mailchimp.MailchimpConnector>();
            // BL-229 - n8n's WooCommerce node: a store's orders, products and customers.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.WooCommerce.WooCommerceConnector>();
            // BL-229 - n8n's Linear node: a workspace's issues, comments and links.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Linear.LinearConnector>();
            // BL-229 - n8n's GitLab node: a project's issues, merge requests, releases, files and pipelines.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GitLab.GitLabConnector>();
            // BL-229 - n8n's Freshdesk node: a helpdesk's tickets and contacts.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Freshdesk.FreshdeskConnector>();
            // BL-229 - n8n's Monday.com node: a board's items.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Monday.MondayConnector>();
            // BL-229 - n8n's Todoist node: tasks, comments and projects.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Todoist.TodoistConnector>();
            // BL-229 - n8n's WordPress node: a site's posts, pages and users.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.WordPress.WordPressConnector>();
            // BL-229 - n8n's SendGrid node: contacts, lists and mail.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.SendGrid.SendGridConnector>();
            // BL-229 - n8n's Google Tasks node: a list's tasks.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleTasks.GoogleTasksConnector>();
            // BL-229 - n8n's Microsoft To Do node: a user's lists and tasks.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MicrosoftToDo.MicrosoftToDoConnector>();
            // BL-229 - n8n's Microsoft Excel 365 node: workbooks, sheets and tables.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MicrosoftExcel.MicrosoftExcelConnector>();
            // BL-229 - n8n's Google Contacts node: a Google account's contacts.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleContacts.GoogleContactsConnector>();
            // BL-229 - n8n's Google Docs node: documents created, read and written.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleDocs.GoogleDocsConnector>();
            // BL-229 - n8n's Odoo node: any model's records over JSON-RPC or JSON-2.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Odoo.OdooConnector>();
            // BL-229 - n8n's Brevo node (Sendinblue): contacts, attributes, transactional e-mail and senders.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Brevo.BrevoConnector>();
            // BL-229 - n8n's ActiveCampaign node: contacts, lists, tags, deals, accounts and e-commerce records.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.ActiveCampaign.ActiveCampaignConnector>();
            // BL-229 - n8n's Zoho CRM node: any module's records.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.ZohoCrm.ZohoCrmConnector>();
            // BL-229 - n8n's Intercom node: users, leads and companies, on Intercom's API 2.x.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Intercom.IntercomConnector>();
            // BL-229 - n8n's Zoom node: meetings, registrants, recordings and users, by a server-to-server OAuth app.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Zoom.ZoomConnector>();
            // BL-229 - n8n's Mailgun node: an e-mail sent, and the domain's events and suppressions.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Mailgun.MailgunConnector>();
            // BL-229 - n8n's Webflow node: a collection's items, and the sites and collections they belong to.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Webflow.WebflowConnector>();
            // BL-229 - n8n's YouTube node: channels, playlists, playlist items, videos and video categories.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.YouTube.YouTubeConnector>();
            // BL-229 - n8n's LinkedIn node: a post made as a member or an organization, changed, read and deleted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.LinkedIn.LinkedInConnector>();
            // BL-229 - n8n's Reddit node: posts, comments, the profile, subreddits and users.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Reddit.RedditConnector>();
            // BL-229 - n8n's AWS SES node: e-mail sent plain or from a template, templates and custom verification e-mails.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsSes.AwsSesConnector>();
            // BL-229 - n8n's AWS SQS node: a message sent; messages received and deleted, queues read and purged.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsSqs.AwsSqsConnector>();
            // BL-229 - n8n's AWS SNS node: a topic made and deleted, a message published; topics and subscriptions kept.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsSns.AwsSnsConnector>();
            // BL-229 - n8n's AWS DynamoDB node: items written, got, updated, deleted, queried and scanned; tables read.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsDynamoDb.AwsDynamoDbConnector>();
            // BL-229 - n8n's AWS Lambda node: a function invoked, waited for or queued as an event; functions read.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsLambda.AwsLambdaConnector>();
            // BL-229 - n8n's NocoDB node: a table's rows read, got, created, updated, upserted, counted and deleted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.NocoDb.NocoDbConnector>();
            // BL-229 - n8n's Grist node: a table's rows read, created, updated, upserted and deleted, and a SELECT over a document.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Grist.GristConnector>();
            // BL-229 - n8n's Coda node: a table's or a view's rows read, created, upserted, updated and deleted, its buttons pushed,
            // its columns, a doc's formulas and controls read.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Coda.CodaConnector>();
            // BL-229 - n8n's Ghost node: a site's posts and pages read, created, updated and deleted, its tags and members read.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Ghost.GhostConnector>();
            // BL-229 - n8n's Clockify node: a workspace's clients, projects, tags, tasks and time entries, its users, the workspaces.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Clockify.ClockifyConnector>();
            // BL-229 - n8n's Harvest node: an account's clients, contacts, projects, tasks, users, invoices, estimates, expenses, time.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Harvest.HarvestConnector>();
            // BL-229 - n8n's Strapi node: a content type's entries read, got, created, updated and deleted, a single type's content.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Strapi.StrapiConnector>();
            // BL-229 - n8n's Contentful node: a space's entries, assets, content types and locales read.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Contentful.ContentfulConnector>();
            // BL-229 - n8n's Elasticsearch node: an index's documents searched, got, created, updated and deleted, its indices kept.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Elasticsearch.ElasticsearchConnector>();
            // BL-229 - n8n's DeepL node: a text, or a list of texts, translated; the languages, the usage and the glossaries.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.DeepL.DeepLConnector>();
            // BL-229 - n8n's PagerDuty node: incidents created, got, read and updated, their notes, the log entries, users, who is on call.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.PagerDuty.PagerDutyConnector>();
            // BL-229 - n8n's Bitly node: a link shortened, got and updated; a group's links, a link's clicks, the groups and the user.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Bitly.BitlyConnector>();
            // BL-229 - n8n's Facebook Graph API node: a node or its edge read, posted to and deleted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.FacebookGraph.FacebookGraphConnector>();
            // BL-229 - n8n's Zulip node: messages sent, got, searched, updated and deleted, streams and users kept.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Zulip.ZulipConnector>();
            // BL-229 - n8n's MailerLite node: subscribers created, got, read and updated; groups and fields kept.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MailerLite.MailerLiteConnector>();
            // BL-229 - n8n's Mautic node: contacts, companies, segments, campaigns and emails.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Mautic.MauticConnector>();
            // BL-229 - n8n's Netlify node: sites and deploys; builds, a deploy published again, forms' submissions.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Netlify.NetlifyConnector>();
            // BL-229 - n8n's Rocket.Chat node: messages posted, read, updated and deleted; channels, users and subscriptions.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.RocketChat.RocketChatConnector>();
            // BL-229 - n8n's Jenkins node: jobs built, copied and created, their builds and logs, the instance quieted and restarted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Jenkins.JenkinsConnector>();
            // BL-229 - n8n's ConvertKit node, Kit now: subscribers through forms, sequences and tags; the custom fields.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Kit.KitConnector>();
            // BL-229 - n8n's Matrix node: messages sent and read, rooms made, joined and left, people invited and kicked.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Matrix.MatrixConnector>();
            // BL-229 - n8n's Sentry.io node: its events, issues, organizations, projects, releases and teams.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Sentry.SentryConnector>();
            // BL-229 - n8n's Discourse node: its posts, categories, groups, group members and users.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Discourse.DiscourseConnector>();
            // BL-229 - n8n's Zammad node: its tickets, users, organizations and groups.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Zammad.ZammadConnector>();
            // BL-229 - n8n's Pushover node: a message pushed to a user's devices.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Pushover.PushoverConnector>();
            // BL-229 - n8n's Gotify node: its messages sent, read and deleted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Gotify.GotifyConnector>();
            // BL-229 - n8n's UptimeRobot node: its account, monitors, alert contacts, maintenance windows and status pages.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.UptimeRobot.UptimeRobotConnector>();
            // BL-229 - n8n's Grafana node: its dashboards, teams, team members and the organization's users.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Grafana.GrafanaConnector>();
            // BL-229 - n8n's Okta node: its users, and their groups.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Okta.OktaConnector>();
            // BL-229 - n8n's Splunk node: its searches, reports, fired alerts and users.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Splunk.SplunkConnector>();
            // BL-229 - n8n's Bitwarden node: an organization's collections, events, groups and members.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Bitwarden.BitwardenConnector>();
            // BL-229 - n8n's G Suite Admin node: a Google Workspace's users, groups and ChromeOS devices.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleWorkspaceAdmin.GoogleWorkspaceAdminConnector>();
            // BL-229 - n8n's Mailjet node: an e-mail sent with a body or a template, and an SMS sent on a token of its own.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Mailjet.MailjetConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Mailjet.MailjetSmsConnector>();
            // BL-229 - n8n's Microsoft Graph Security node: a tenant's secure scores and their control profiles, a profile updated.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MicrosoftGraphSecurity.MicrosoftGraphSecurityConnector>();
            // BL-229 - n8n's Microsoft Dynamics CRM node: an environment's accounts - here any table - over the Dataverse Web API.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MicrosoftDynamics.MicrosoftDynamicsConnector>();
            // BL-229 - n8n's Google BigQuery node: a query run, its rows typed by the schema, and rows inserted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleBigQuery.GoogleBigQueryConnector>();
            // BL-229 - n8n's Freshservice node: an IT service desk's tickets, changes, problems, releases, assets and people.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Freshservice.FreshserviceConnector>();
            // BL-229 - n8n's Microsoft SharePoint node: a site's lists and their items over Microsoft Graph.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MicrosoftSharePoint.MicrosoftSharePointConnector>();
            // BL-229 - n8n's HaloPSA node: a service desk's clients, sites, users and tickets.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.HaloPsa.HaloPsaConnector>();
            // BL-229 - n8n's Snowflake node: a query run, rows inserted and updated over an account's SQL API.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Snowflake.SnowflakeConnector>();
            // BL-229 - n8n's Help Scout node: a shared inbox's conversations and threads, its customers and inboxes.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.HelpScout.HelpScoutConnector>();
            // BL-229 - n8n's AWS S3 and S3 nodes: a bucket's objects downloaded into and uploaded from the run's pipeline folder.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsS3.AwsS3Connector>();
            // BL-229 - n8n's Google Drive node: files, folders and shared drives, a file downloaded into and uploaded from the run's folder.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleDrive.GoogleDriveConnector>();
            // BL-229 - n8n's Microsoft OneDrive node: a drive's files and folders, a file downloaded into and uploaded from the run's folder.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MicrosoftOneDrive.MicrosoftOneDriveConnector>();
            // BL-229 - n8n's MySQL node: a statement, its values bound; a write answers what it changed.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MySql.MySqlDatabaseConnector>();
            // BL-229 - n8n's Redis node: keys, hashes, lists and channels, the one server pinned and nothing else dialled.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Redis.RedisConnector>();
            // BL-229 - n8n's MongoDB node: a collection's documents, direct to the one server the egress checked.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MongoDb.MongoDbConnector>();
            // BL-229 - n8n's FTP node over SFTP: the FTP connector's file operations, by a password or a key, the server's host key checked.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Sftp.SftpConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Sftp.SftpKeyConnector>();
            // BL-229 - n8n's SSH node: a command run, a file downloaded or uploaded - a type of its own, so a connection made to
            // move files is never one that runs commands.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Ssh.SshConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Ssh.SshKeyConnector>();
            // BL-229 - n8n's Dropbox node: files and folders listed, searched, moved; a file downloaded into and uploaded from the run's folder.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Dropbox.DropboxConnector>();
            // BL-229 - n8n's Baserow node: a table's rows, one or up to 200 at once, by a database token.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Baserow.BaserowConnector>();
            // BL-229 - n8n's RabbitMQ node: a message published to a queue or an exchange, a queue's messages taken and counted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.RabbitMq.RabbitMqConnector>();
            // BL-229 - n8n's MQTT node: a message published on a topic, and the messages on a topic filter received for a while.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Mqtt.MqttConnector>();
            // BL-229 - n8n's Nextcloud node: files and folders over WebDAV, shares and users over OCS.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Nextcloud.NextcloudConnector>();
            // BL-229 - n8n's Azure Storage node: containers and blobs, signed with the account's key.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AzureStorage.AzureStorageConnector>();
            // BL-229 - n8n's Google Cloud Storage node: buckets and objects, by an OAuth client's refresh token.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleCloudStorage.GoogleCloudStorageConnector>();
            // BL-229 - n8n's Box node: files and folders, as a server app (Box's Client Credentials Grant).
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Box.BoxConnector>();
            // BL-229 - n8n's AWS Textract, Rekognition and Comprehend nodes: documents, images and text read by AWS's own models.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsTextract.AwsTextractConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsRekognition.AwsRekognitionConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsComprehend.AwsComprehendConnector>();
            // BL-229 - n8n's AWS Transcribe node: speech in an S3 recording turned into text.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsTranscribe.AwsTranscribeConnector>();
            // BL-229 - n8n's AWS Cognito node: a user pool's users and groups administered.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsCognito.AwsCognitoConnector>();
            // BL-229 - n8n's Google Translate node: texts translated, their languages detected.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleTranslate.GoogleTranslateConnector>();
            // BL-229 - n8n's AWS Certificate Manager node: an account's certificates, requested too.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsCertificateManager.AwsCertificateManagerConnector>();
            // BL-229 - n8n's Google Slides node: presentations, slides, placeholders replaced and thumbnails.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleSlides.GoogleSlidesConnector>();
            // BL-229 - n8n's Metabase node: questions and their results, databases, metrics and alerts.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Metabase.MetabaseConnector>();
            // BL-229 - n8n's PostHog node: events, people and their feature flags.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.PostHog.PostHogConnector>();
            // BL-229 - n8n's AWS IAM node: an account's users and groups.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsIam.AwsIamConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AwsElb.AwsElbConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleAnalytics.GoogleAnalyticsConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Hunter.HunterConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.HighLevel.HighLevelConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.OpenWeatherMap.OpenWeatherMapConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleBusinessProfile.GoogleBusinessProfileConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Vonage.VonageConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.CircleCi.CircleCiConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Misp.MispConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.UrlScanIo.UrlScanIoConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.ElasticSecurity.ElasticSecurityConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.TheHive.TheHiveConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Yourls.YourlsConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Wekan.WekanConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Rundeck.RundeckConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Taiga.TaigaConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.QuickChart.QuickChartConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.MonicaCrm.MonicaCrmConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.InvoiceNinja.InvoiceNinjaConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.ErpNext.ErpNextConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Amqp.AmqpConnector>();
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.SeaTable.SeaTableConnector>();
            // BL-229 - n8n's LDAP node: a directory's entries searched, compared, created, updated, renamed and deleted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Ldap.LdapConnector>();
            // BL-229 - n8n's Home Assistant node: a home's configuration, services, states, events, logs, templates and cameras.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.HomeAssistant.HomeAssistantConnector>();
            // BL-229 - n8n's Azure Cosmos DB node: a database's containers and items, and its SQL, signed with the account's key.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.AzureCosmosDb.AzureCosmosDbConnector>();
            // BL-229 - n8n's Google Cloud Firestore node: a database's collections and documents, and its structured queries.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleFirestore.GoogleFirestoreConnector>();
            // BL-229 - n8n's Google Chat node: messages sent (Send chat message's too), read, edited and deleted, spaces and members.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Messaging.GoogleChatConnector>();
            // BL-229 - n8n's most used node: Google Sheets, its Get Rows and Append Row.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.GoogleSheets.GoogleSheetsConnector>();
            // BL-229 - n8n's Airtable node: its search, create and update.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Airtable.AirtableConnector>();
            // BL-229 - n8n's Notion node: a database's pages got, created and updated.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Notion.NotionConnector>();
            // BL-229 - n8n's Gmail node: messages got, sent, replied to and marked as read.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Gmail.GmailConnector>();
            // BL-229 - n8n's Microsoft Outlook node: messages got, sent and replied to.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Outlook.OutlookConnector>();
            // BL-229 - n8n's HubSpot node: CRM records searched, created, updated and upserted.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.HubSpot.HubSpotConnector>();
            // BL-229 - n8n's Jira Software node: issues searched, created, updated, commented on and moved.
            services.AddSingleton<IConnector, Stomme.Modules.Connectors.Jira.JiraConnector>();
            services.AddSingleton<IConnector, ActiveDirectoryConnector>();
            services.AddSingleton<IConnector, XmlConnector>();

            // Increment 3: the OAuth family over the egress - one token provider shape, one throttling policy, no client of their own.
            services.AddSingleton<IConnector, IntuneConnector>();
            services.AddSingleton<IConnector, EntraIdConnector>();
            services.AddSingleton<IConnector, AzureVirtualMachinesConnector>();
            services.AddSingleton<IConnector, TaegisConnector>();
            services.AddSingleton<IConnector, CylanceConnector>();
            services.AddSingleton<IConnector, FlexNetManagerConnector>();
            services.AddSingleton<IConnector, FlexeraOneConnector>();

            // Increment 4: the REST-derived vendor connectors and the six cost probes - one shape, one batch.
            services.AddSingleton<IConnector, QualysConnector>();
            services.AddSingleton<IConnector, Rapid7Connector>();
            services.AddSingleton<IConnector, ServiceNowConnector>();
            services.AddSingleton<IConnector, SuseManagerConnector>();
            services.AddSingleton<IConnector, RedHatSatelliteConnector>();
            services.AddSingleton<IConnector, EntuityConnector>();
            services.AddSingleton<IConnector, CiscoDnaCenterConnector>();
            services.AddSingleton<IConnector, AzureCostConnector>();
            services.AddSingleton<IConnector, DatadogCostConnector>();
            services.AddSingleton<IConnector, GithubCostConnector>();
            services.AddSingleton<IConnector, M365CostConnector>();
            services.AddSingleton<IConnector, SalesforceCostConnector>();
            services.AddSingleton<IConnector, SnowflakeCostConnector>();

            // Increment 5: the last three - two configuration-only cost checks and the Tenable export.
            services.AddSingleton<IConnector, AwsCostConnector>();
            services.AddSingleton<IConnector, GcpCostConnector>();
            services.AddSingleton<IConnector, TenableConnector>();
        }
    }
}
