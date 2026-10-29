#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Messaging;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// The chat connectors (n8n's Slack, Telegram, Discord and Mattermost nodes, their message sent, and its Twilio
    /// node's SMS): each sends to the address its service has, authorised as its service has it, the message as JSON (Twilio's
    /// a form); answers one record - the message's id and where it went - or the service's refusal in its own words; a test
    /// signs in. Telegram's token is in the request's path, so it is held to the characters Telegram issues and named by no
    /// message; Discord's channel and Twilio's account SID are in the path, so they are held to their shapes.
    /// </summary>
    public sealed class ChatConnectorsTests
    {
        private static readonly Dictionary<string, string> Token = new() { ["token"] = "tok-123" };

        private static Dictionary<string, string> Message(string to, string text = "Order 42 shipped", string? replyTo = null)
        {
            var parameters = new Dictionary<string, string> { [ChatMessages.ToParameter] = to, [ChatMessages.TextParameter] = text };
            if (replyTo is not null)
            {
                parameters[ChatMessages.ReplyToParameter] = replyTo;
            }

            return parameters;
        }

        private static IReadOnlyDictionary<string, object?> Single(List<ConnectorRecord> records) => Assert.Single(records).Fields;

        [Fact]
        public void Each_is_a_messaging_connector_with_the_one_operation_and_a_bearer_token_but_twilio_its_sid_and_auth_token()
        {
            foreach (IConnector connector in new IConnector[] { new SlackConnector(), new TelegramConnector(), new DiscordConnector(), new MattermostConnector(), new TwilioConnector() })
            {
                Assert.Equal((connector is TwilioConnector ? AuthenticationType.Basic : AuthenticationType.Bearer, ConnectorCategory.Messaging), (connector.AuthType, Assert.Single(connector.Categories)));
                var operation = ((IConnectorExtractor)connector).Operations[0]; // the first: what a send names, and the port's default
                Assert.Equal(ChatMessages.Operation, operation.Name);
                Assert.Equal(new[] { "to", "text", "reply_to" }, operation.Parameters.Fields.Select(field => field.Name));
                Assert.StartsWith("https://", connector.EndpointExample);
            }
        }

        [Fact]
        public async Task Slack_posts_to_chat_postMessage_with_the_bot_token_and_answers_the_ts()
        {
            var handler = new StubHandler(HttpStatusCode.OK, """{"ok":true,"channel":"C0123","ts":"1700000000.000100"}""");
            var records = await Contexts.DrainAsync(new SlackConnector(), Contexts.Extract(new StubEgress(handler), "https://slack.com/api/", ChatMessages.Operation, Message("#orders", replyTo: "1699999999.000001"), credentials: Token));

            Assert.Equal(("1700000000.000100", "C0123"), ((string?)Single(records)["id"], (string?)Single(records)["to"]));
            Assert.Equal((HttpMethod.Post, "https://slack.com/api/chat.postMessage"), (handler.Request!.Method, handler.Request.RequestUri!.ToString()));
            Assert.Equal(("Bearer", "tok-123"), (handler.Request.Headers.Authorization?.Scheme, handler.Request.Headers.Authorization?.Parameter));
            var sent = JsonNode.Parse(handler.Body!)!;
            Assert.Equal(("#orders", "Order 42 shipped", "1699999999.000001"), ((string?)sent["channel"], (string?)sent["text"], (string?)sent["thread_ts"]));
            Assert.StartsWith("application/json", handler.BodyContentType);
        }

        [Fact]
        public async Task Slacks_refusal_is_a_record_in_its_words_and_an_answer_that_is_not_slacks_is_a_failure()
        {
            var refused = await Contexts.DrainAsync(new SlackConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.OK, """{"ok":false,"error":"channel_not_found"}""")), "https://slack.com/api", ChatMessages.Operation, Message("#nowhere"), credentials: Token));
            Assert.Equal("Slack: channel_not_found", Single(refused)["refused"]);
            Assert.False(Single(refused).ContainsKey("id"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SlackConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html>")), "https://slack.com/api", ChatMessages.Operation, Message("#orders"), credentials: Token)));
        }

        [Fact]
        public async Task Telegram_sends_with_the_token_in_its_path_and_answers_the_message_id()
        {
            var handler = new StubHandler(HttpStatusCode.OK, """{"ok":true,"result":{"message_id":77,"chat":{"id":-1001234}}}""");
            var records = await Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(handler), "https://api.telegram.org", ChatMessages.Operation, Message("-1001234", replyTo: "70"), credentials: new Dictionary<string, string> { ["token"] = "123456:ABC-def_GHI" }));

            Assert.Equal(("77", "-1001234"), ((string?)Single(records)["id"], (string?)Single(records)["to"]));
            Assert.Equal("https://api.telegram.org/bot123456:ABC-def_GHI/sendMessage", handler.Request!.RequestUri!.ToString());
            Assert.Null(handler.Request.Headers.Authorization); // the token is the path's, as Telegram has it
            var sent = JsonNode.Parse(handler.Body!)!;
            Assert.Equal(("-1001234", "Order 42 shipped", 70L), ((string?)sent["chat_id"], (string?)sent["text"], (long?)sent["reply_parameters"]?["message_id"]));
        }

        [Fact]
        public async Task Telegrams_refusal_is_its_description_and_a_token_it_never_issues_never_reaches_the_path()
        {
            var refused = await Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""")), "https://api.telegram.org", ChatMessages.Operation, Message("42"), credentials: new Dictionary<string, string> { ["token"] = "1:a" }));
            Assert.Equal("Telegram: Bad Request: chat not found", Single(refused)["refused"]);

            var handler = new StubHandler(HttpStatusCode.OK, """{"ok":true}""");
            var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(handler), "https://api.telegram.org", ChatMessages.Operation, Message("42"), credentials: new Dictionary<string, string> { ["token"] = "1:a/../getUpdates?x=" })));
            Assert.Null(handler.Request); // nothing sent
            Assert.DoesNotContain("getUpdates", bad.Message);

            var notANumber = await Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(handler), "https://api.telegram.org", ChatMessages.Operation, Message("42", replyTo: "the last one"), credentials: new Dictionary<string, string> { ["token"] = "1:a" }));
            Assert.Equal("Telegram: 'the last one' is not a message id.", Single(notANumber)["refused"]);
            Assert.Null(handler.Request);
        }

        // For the "Telegram message received" trigger: the updates from an offset, never waiting, each its fields - a
        // message's and a channel post's, any other kind its id and kind only - and the newest update's id, Telegram told to
        // forget the earlier ones; a refusal (a webhook set) a record in Telegram's words.
        [Fact]
        public async Task Telegrams_updates_are_read_from_an_offset_and_its_latest_is_the_newest_id()
        {
            Assert.Equal(new[] { "message", "updates", "latest" }, new TelegramConnector().Operations.Select(operation => operation.Name));
            var handler = new StubHandler(HttpStatusCode.OK, """
                {"ok":true,"result":[
                  {"update_id":502,"channel_post":{"message_id":9,"date":1760000002,"chat":{"id":-100123,"type":"channel","title":"Orders"},"caption":"New stock"}},
                  {"update_id":501,"message":{"message_id":8,"date":1760000001,"chat":{"id":777,"type":"private","username":"ada_customer"},
                    "from":{"id":777,"is_bot":false,"first_name":"Ada","last_name":"Lovelace","username":"ada_customer"},"text":"Where is order 1001?","reply_to_message":{"message_id":7}}},
                  {"update_id":503,"edited_message":{"message_id":8,"date":1760000003,"chat":{"id":777,"type":"private"},"text":"an edit"}}]}
                """);
            var records = await Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(handler), "https://api.telegram.org", TelegramConnector.UpdatesOperation,
                new Dictionary<string, string> { ["offset"] = "501", ["limit"] = "500" }, credentials: new Dictionary<string, string> { ["token"] = "1:a" }));

            Assert.Equal("https://api.telegram.org/bot1:a/getUpdates?timeout=0&offset=501&limit=100", handler.Request!.RequestUri!.ToString()); // never waits; at most 100
            Assert.Equal(new long[] { 501, 502, 503 }, records.Select(record => (long)record.Fields["update_id"]!)); // oldest first
            var message = records[0].Fields;
            Assert.Equal(("message", 8L, 777L, "private", "Where is order 1001?", 777L, "ada_customer", "Ada", "Lovelace", 7L),
                ((string?)message["kind"], (long?)message["message_id"], (long?)message["chat_id"], (string?)message["chat_type"], (string?)message["text"], (long?)message["from_id"],
                    (string?)message["from_username"], (string?)message["from_first_name"], (string?)message["from_last_name"], (long?)message["reply_to_message_id"]));
            var post = records[1].Fields;
            Assert.Equal(("channel_post", "New stock", "Orders", false), ((string?)post["kind"], (string?)post["text"], (string?)post["chat_title"], post.ContainsKey("from_id")));
            Assert.Equal(new[] { "update_id", "kind" }, records[2].Fields.Keys); // an edit: moved past by its id

            var newest = new StubHandler(HttpStatusCode.OK, """{"ok":true,"result":[{"update_id":503,"message":{"message_id":10,"date":1,"chat":{"id":1,"type":"private"},"text":"x"}}]}""");
            var latest = await Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(newest), "https://api.telegram.org", TelegramConnector.LatestOperation, credentials: new Dictionary<string, string> { ["token"] = "1:a" }));
            Assert.Equal(503L, (long)Assert.Single(latest).Fields["update_id"]!);
            Assert.Equal("https://api.telegram.org/bot1:a/getUpdates?timeout=0&offset=-1&limit=1", newest.Request!.RequestUri!.ToString());
            var none = await Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.OK, """{"ok":true,"result":[]}""")), "https://api.telegram.org", TelegramConnector.LatestOperation, credentials: new Dictionary<string, string> { ["token"] = "1:a" }));
            Assert.Equal(0L, (long)Assert.Single(none).Fields["update_id"]!);

            var webhook = await Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.Conflict, """{"ok":false,"error_code":409,"description":"Conflict: can't use getUpdates method while webhook is active"}""")),
                "https://api.telegram.org", TelegramConnector.UpdatesOperation, credentials: new Dictionary<string, string> { ["token"] = "1:a" }));
            Assert.Equal("Telegram: Conflict: can't use getUpdates method while webhook is active", Single(webhook)["refused"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TelegramConnector(), Contexts.Extract(new StubEgress(handler), "https://api.telegram.org", TelegramConnector.UpdatesOperation,
                new Dictionary<string, string> { ["offset"] = "1&offset=-1" }, credentials: new Dictionary<string, string> { ["token"] = "1:a" })));
        }

        [Fact]
        public async Task Discord_posts_to_the_channels_messages_as_a_bot_and_answers_the_message_id()
        {
            var handler = new StubHandler(HttpStatusCode.OK, """{"id":"1100000000000000001","channel_id":"1090000000000000001","content":"Order 42 shipped"}""");
            var records = await Contexts.DrainAsync(new DiscordConnector(), Contexts.Extract(new StubEgress(handler), "https://discord.com/api/v10", ChatMessages.Operation, Message("1090000000000000001", replyTo: "1099999999999999999"), credentials: Token));

            Assert.Equal(("1100000000000000001", "1090000000000000001"), ((string?)Single(records)["id"], (string?)Single(records)["to"]));
            Assert.Equal("https://discord.com/api/v10/channels/1090000000000000001/messages", handler.Request!.RequestUri!.ToString());
            Assert.Equal(("Bot", "tok-123"), (handler.Request.Headers.Authorization?.Scheme, handler.Request.Headers.Authorization?.Parameter));
            var sent = JsonNode.Parse(handler.Body!)!;
            Assert.Equal(("Order 42 shipped", "1099999999999999999"), ((string?)sent["content"], (string?)sent["message_reference"]?["message_id"]));
        }

        [Fact]
        public async Task Discords_refusal_is_its_message_and_a_channel_that_is_not_digits_never_reaches_the_path()
        {
            var refused = await Contexts.DrainAsync(new DiscordConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.Forbidden, """{"message":"Missing Access","code":50001}""")), "https://discord.com/api/v10", ChatMessages.Operation, Message("1090000000000000001"), credentials: Token));
            Assert.Equal("Discord: Missing Access", Single(refused)["refused"]);

            var handler = new StubHandler(HttpStatusCode.OK, "{}");
            var path = await Contexts.DrainAsync(new DiscordConnector(), Contexts.Extract(new StubEgress(handler), "https://discord.com/api/v10", ChatMessages.Operation, Message("123/../../users/@me"), credentials: Token));
            Assert.Equal("Discord: '123/../../users/@me' is not a channel id (digits).", Single(path)["refused"]);
            Assert.Null(handler.Request);
        }

        [Fact]
        public async Task A_refusal_is_one_line_and_cut_and_a_message_without_text_or_somewhere_to_go_is_never_sent()
        {
            var reason = "line one\nline two " + new string('x', 400);
            var refused = await Contexts.DrainAsync(new SlackConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.OK, new JsonObject { ["ok"] = false, ["error"] = reason }.ToJsonString())), "https://slack.com/api", ChatMessages.Operation, Message("#a"), credentials: Token));
            var said = (string)Single(refused)["refused"]!;
            Assert.StartsWith("Slack: line one line two xxx", said);
            Assert.Equal("Slack: ".Length + ChatMessages.MaxReason + "...".Length, said.Length);

            var handler = new StubHandler(HttpStatusCode.OK, """{"ok":true,"ts":"1"}""");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SlackConnector(), Contexts.Extract(new StubEgress(handler), "https://slack.com/api", ChatMessages.Operation, Message(" ", "hello"), credentials: Token)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new SlackConnector(), Contexts.Extract(new StubEgress(handler), "https://slack.com/api", ChatMessages.Operation, Message("#a", "  "), credentials: Token)));
            Assert.Null(handler.Request);
        }

        [Fact]
        public async Task A_test_signs_in_and_says_as_whom_or_why_not()
        {
            var slack = new StubHandler(HttpStatusCode.OK, """{"ok":true,"user":"orders-bot","team":"Acme"}""");
            var tested = await new SlackConnector().TestConnectionAsync(Contexts.Test(new StubEgress(slack), "https://slack.com/api", credentials: Token));
            Assert.Equal((true, "Signed in as orders-bot on Acme."), (tested.Success, tested.Message));
            Assert.Equal("https://slack.com/api/auth.test", slack.Request!.RequestUri!.ToString());
            Assert.Equal("Slack: invalid_auth", (await new SlackConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.OK, """{"ok":false,"error":"invalid_auth"}""")), "https://slack.com/api", credentials: Token))).Message);

            var telegram = new StubHandler(HttpStatusCode.OK, """{"ok":true,"result":{"username":"orders_bot"}}""");
            tested = await new TelegramConnector().TestConnectionAsync(Contexts.Test(new StubEgress(telegram), "https://api.telegram.org", credentials: new Dictionary<string, string> { ["token"] = "1:a" }));
            Assert.Equal((true, "Signed in as @orders_bot."), (tested.Success, tested.Message));
            Assert.Equal("https://api.telegram.org/bot1:a/getMe", telegram.Request!.RequestUri!.ToString());

            var discord = new StubHandler(HttpStatusCode.OK, """{"username":"Orders"}""");
            tested = await new DiscordConnector().TestConnectionAsync(Contexts.Test(new StubEgress(discord), "https://discord.com/api/v10", credentials: Token));
            Assert.Equal((true, "Signed in as Orders."), (tested.Success, tested.Message));
            Assert.Equal("https://discord.com/api/v10/users/@me", discord.Request!.RequestUri!.ToString());
            Assert.Equal("Discord: 401: Unauthorized", (await new DiscordConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized","code":0}""")), "https://discord.com/api/v10", credentials: Token))).Message);

            Assert.False((await new SlackConnector().TestConnectionAsync(Contexts.Test(new StubEgress(slack), "slack.com/api", credentials: Token))).Success);

            var mattermost = new StubHandler(HttpStatusCode.OK, """{"id":"u1","username":"orders-bot"}""");
            tested = await new MattermostConnector().TestConnectionAsync(Contexts.Test(new StubEgress(mattermost), "https://chat.example.com/api/v4/", credentials: Token));
            Assert.Equal((true, "Signed in as orders-bot."), (tested.Success, tested.Message));
            Assert.Equal("https://chat.example.com/api/v4/users/me", mattermost.Request!.RequestUri!.ToString()); // typed with its /api/v4: not twice
            Assert.Equal("Mattermost: Invalid or expired session, please login again.", (await new MattermostConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.Unauthorized,
                """{"id":"api.context.session_expired.app_error","message":"Invalid or expired session, please login again.","status_code":401}""")), "https://chat.example.com", credentials: Token))).Message);

            var twilio = new StubHandler(HttpStatusCode.OK, """{"friendly_name":"Acme orders","status":"active"}""");
            tested = await new TwilioConnector().TestConnectionAsync(Contexts.Test(new StubEgress(twilio), "https://api.twilio.com", credentials: Twilio));
            Assert.Equal((true, "Signed in to Acme orders."), (tested.Success, tested.Message));
            Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{Sid}.json", twilio.Request!.RequestUri!.ToString());
            Assert.Equal("Twilio: Authenticate", (await new TwilioConnector().TestConnectionAsync(Contexts.Test(new StubEgress(new StubHandler(HttpStatusCode.Unauthorized, """{"code":20003,"message":"Authenticate","status":401}""")), "https://api.twilio.com", credentials: Twilio))).Message);
            Assert.StartsWith("The Twilio connection is missing 'password'", (await new TwilioConnector().TestConnectionAsync(Contexts.Test(new StubEgress(twilio), "https://api.twilio.com", credentials: new Dictionary<string, string> { ["username"] = Sid }))).Message);
        }

        [Fact]
        public async Task Mattermost_posts_under_the_servers_api_with_the_token_and_answers_the_post_id()
        {
            var handler = new StubHandler(HttpStatusCode.Created, """{"id":"pq1mk6xn3fdq8bw5a7ygkm1z8e","channel_id":"4xp9fdt7pbgium38k5k6w95oxh","message":"Order 42 shipped"}""");
            var records = await Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(new StubEgress(handler), "https://chat.example.com/", ChatMessages.Operation, Message("4xp9fdt7pbgium38k5k6w95oxh", replyTo: "r00tp0st00000000000000000a"), credentials: Token));

            Assert.Equal(("pq1mk6xn3fdq8bw5a7ygkm1z8e", "4xp9fdt7pbgium38k5k6w95oxh"), ((string?)Single(records)["id"], (string?)Single(records)["to"]));
            Assert.Equal((HttpMethod.Post, "https://chat.example.com/api/v4/posts"), (handler.Request!.Method, handler.Request.RequestUri!.ToString()));
            Assert.Equal(("Bearer", "tok-123"), (handler.Request.Headers.Authorization?.Scheme, handler.Request.Headers.Authorization?.Parameter));
            var sent = JsonNode.Parse(handler.Body!)!;
            Assert.Equal(("4xp9fdt7pbgium38k5k6w95oxh", "Order 42 shipped", "r00tp0st00000000000000000a"), ((string?)sent["channel_id"], (string?)sent["message"], (string?)sent["root_id"]));

            var alone = new StubHandler(HttpStatusCode.Created, """{"id":"p2"}""");
            await Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(new StubEgress(alone), "https://chat.example.com", ChatMessages.Operation, Message("4xp9fdt7pbgium38k5k6w95oxh"), credentials: Token));
            Assert.False(JsonNode.Parse(alone.Body!)!.AsObject().ContainsKey("root_id")); // no reply: no thread
        }

        [Fact]
        public async Task Mattermosts_refusal_is_its_message_and_an_answer_that_is_not_mattermosts_is_a_failure()
        {
            var refused = await Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.Forbidden,
                """{"id":"api.context.permissions.app_error","message":"You do not have the appropriate permissions.","status_code":403}""")), "https://chat.example.com", ChatMessages.Operation, Message("4xp9fdt7pbgium38k5k6w95oxh"), credentials: Token));
            Assert.Equal("Mattermost: You do not have the appropriate permissions.", Single(refused)["refused"]);

            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new MattermostConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.BadGateway, "<html>bad gateway</html>")), "https://chat.example.com", ChatMessages.Operation, Message("4xp9fdt7pbgium38k5k6w95oxh"), credentials: Token)));
        }

        // An invented account id, built at run time: written out, its shape is a Twilio account SID's and secret scanners
        // (GitHub's push protection among them) refuse the file.
        private static readonly string Sid = "AC" + string.Concat(Enumerable.Repeat("0123456789abcdef", 2));

        private static readonly Dictionary<string, string> Twilio = new() { ["username"] = Sid, ["password"] = "auth-token" };

        private static Dictionary<string, string> From(string from) => new() { [TwilioConnector.FromSetting] = from };

        [Fact]
        public async Task Twilio_sends_a_form_to_the_accounts_messages_with_its_sid_and_auth_token_and_answers_the_message_sid()
        {
            var handler = new StubHandler(HttpStatusCode.Created, """{"sid":"SM0123456789abcdef0123456789abcdef","to":"+46701740605","status":"queued"}""");
            var records = await Contexts.DrainAsync(new TwilioConnector(), Contexts.Extract(new StubEgress(handler), "https://api.twilio.com", ChatMessages.Operation, Message("+46701740605"), From("+15017122661"), Twilio));

            Assert.Equal(("SM0123456789abcdef0123456789abcdef", "+46701740605"), ((string?)Single(records)["id"], (string?)Single(records)["to"]));
            Assert.Equal((HttpMethod.Post, $"https://api.twilio.com/2010-04-01/Accounts/{Sid}/Messages.json"), (handler.Request!.Method, handler.Request.RequestUri!.ToString()));
            Assert.Equal(("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Sid + ":auth-token"))), (handler.Request.Headers.Authorization?.Scheme, handler.Request.Headers.Authorization?.Parameter));
            Assert.Equal("To=%2B46701740605&Body=Order+42+shipped&From=%2B15017122661", handler.Body);
            Assert.StartsWith("application/x-www-form-urlencoded", handler.BodyContentType);

            // To WhatsApp: the sender is WhatsApp's too, as n8n's "To WhatsApp" has it; a Messaging Service sends as itself.
            var whatsApp = new StubHandler(HttpStatusCode.Created, """{"sid":"SM1"}""");
            await Contexts.DrainAsync(new TwilioConnector(), Contexts.Extract(new StubEgress(whatsApp), "https://api.twilio.com", ChatMessages.Operation, Message("whatsapp:+46701740605"), From("+15017122661"), Twilio));
            Assert.Equal("To=whatsapp%3A%2B46701740605&Body=Order+42+shipped&From=whatsapp%3A%2B15017122661", whatsApp.Body);
            var service = new StubHandler(HttpStatusCode.Created, """{"sid":"SM2"}""");
            await Contexts.DrainAsync(new TwilioConnector(), Contexts.Extract(new StubEgress(service), "https://api.twilio.com", ChatMessages.Operation, Message("+46701740605"), From("MG0123456789abcdef0123456789abcdef"), Twilio));
            Assert.Equal("To=%2B46701740605&Body=Order+42+shipped&MessagingServiceSid=MG0123456789abcdef0123456789abcdef", service.Body);
        }

        [Fact]
        public async Task Twilios_refusal_is_its_message_a_reply_is_refused_and_a_sid_that_is_not_one_never_reaches_the_path()
        {
            var refused = await Contexts.DrainAsync(new TwilioConnector(), Contexts.Extract(new StubEgress(new StubHandler(HttpStatusCode.BadRequest,
                """{"code":21211,"message":"The 'To' number +4670 is not a valid phone number.","more_info":"https://www.twilio.com/docs/errors/21211","status":400}""")), "https://api.twilio.com", ChatMessages.Operation, Message("+4670"), From("+15017122661"), Twilio));
            Assert.Equal("Twilio: The 'To' number +4670 is not a valid phone number.", Single(refused)["refused"]);

            var handler = new StubHandler(HttpStatusCode.Created, """{"sid":"SM1"}""");
            var reply = await Contexts.DrainAsync(new TwilioConnector(), Contexts.Extract(new StubEgress(handler), "https://api.twilio.com", ChatMessages.Operation, Message("+46701740605", replyTo: "SM0"), From("+15017122661"), Twilio));
            Assert.Equal("Twilio: a text message cannot answer another one - leave In reply to empty.", Single(reply)["refused"]);

            var path = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TwilioConnector(), Contexts.Extract(new StubEgress(handler), "https://api.twilio.com", ChatMessages.Operation, Message("+46701740605"), From("+15017122661"),
                new Dictionary<string, string> { ["username"] = "AC0123456789abcdef0123456789ab/../", ["password"] = "x" })));
            Assert.StartsWith("The user name must be the account's SID", path.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(new TwilioConnector(), Contexts.Extract(new StubEgress(handler), "https://api.twilio.com", ChatMessages.Operation, Message("+46701740605"), credentials: Twilio))); // no From
            Assert.Null(handler.Request); // nothing sent
        }
    }
}
