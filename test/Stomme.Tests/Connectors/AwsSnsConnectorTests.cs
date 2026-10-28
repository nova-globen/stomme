#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.AwsSns;
using Stomme.Modules.Connectors.Shared;
using Xunit;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Amazon SNS (n8n's AWS SNS node: a topic made and deleted, a message published): every call SNS's query API - its
    /// action and version a form - signed with AWS Signature Version 4 for SNS in the connection's region; a message published
    /// with a subject and attributes to a topic by its ARN or its name (found among the topics, page by page), an SMS to a phone
    /// number, a FIFO topic's with its group; topics and a topic's subscriptions listed page by page, its attributes read (its
    /// policies left out), a topic made (FIFO by its name) and deleted, an endpoint subscribed and unsubscribed; what could go
    /// wrong refused before anything is signed or sent; an answer with a DTD never read; AWS's error code and message; a test
    /// naming the topics seen.
    /// </summary>
    public sealed class AwsSnsConnectorTests
    {
        private const string Api = "https://sns.eu-north-1.example.test";

        private const string Orders = "arn:aws:sns:eu-north-1:123456789012:orders";

        private const string Wrapped = "<{0}Response xmlns=\"http://sns.amazonaws.com/doc/2010-03-31/\"><{0}Result>{1}</{0}Result><ResponseMetadata><RequestId>r-1</RequestId></ResponseMetadata></{0}Response>";

        private static readonly Dictionary<string, string> Credentials = new() { ["access-key"] = "AKIDEXAMPLE", ["secret-key"] = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY" };

        private static readonly Dictionary<string, string> Settings = new() { ["region"] = "eu-north-1" };

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        private static AwsSnsConnector Connector() => new(new FixedClock(new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero)));

        private static string Answer(string action, string result) => string.Format(Wrapped, action, result);

        private sealed class Sns : HttpMessageHandler
        {
            public List<(string Url, string? Authorization, string? ContentType, string Body)> Requests { get; } = new();

            public Func<string, string, (HttpStatusCode, string)> Answer { get; set; } = (action, _) => (HttpStatusCode.OK, AwsSnsConnectorTests.Answer(action, ""));

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var auth) ? auth.Single() : null, request.Content?.Headers.ContentType?.ToString(), body));
                var action = body.Split('&').First(pair => pair.StartsWith("Action=", StringComparison.Ordinal))["Action=".Length..];
                var (status, answer) = Answer(action, body);
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "text/xml") };
            }
        }

        private static ConnectorExtractContext Context(Sns sns, string operation, Dictionary<string, string> parameters) =>
            Contexts.Extract(new StubEgress(sns), Api, operation, parameters, settings: Settings, credentials: Credentials);

        [Fact]
        public async Task A_message_is_published_signed_to_a_topic_by_its_arn_or_its_name_or_to_a_phone()
        {
            var sns = new Sns
            {
                Answer = (action, body) => (HttpStatusCode.OK, action switch
                {
                    "ListTopics" => body.Contains("NextToken", StringComparison.Ordinal)
                        ? Answer(action, "<Topics><member><TopicArn>" + Orders + ".fifo</TopicArn></member></Topics>")
                        : Answer(action, "<Topics><member><TopicArn>" + Orders + "</TopicArn></member></Topics><NextToken>t/1+</NextToken>"),
                    _ => Answer(action, "<MessageId>95df01b4</MessageId><SequenceNumber>10000000000000003000</SequenceNumber>"),
                }),
            };
            var sent = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sns, AwsSnsConnector.PublishOperation, new()
            {
                ["topic"] = Orders, ["subject"] = "Order 1042", ["message"] = "Tack för din beställning & välkommen!", ["attributes"] = "{\"source\": \"shop\", \"items\": 3}",
            })));

            var call = Assert.Single(sns.Requests);
            Assert.Equal(($"{Api}/", "application/x-www-form-urlencoded; charset=utf-8"), (call.Url, call.ContentType));
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261012/eu-north-1/sns/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=", call.Authorization, StringComparison.Ordinal);
            Assert.Equal("Action=Publish&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aeu-north-1%3A123456789012%3Aorders&Message=Tack%20f%C3%B6r%20din%20best%C3%A4llning%20%26%20v%C3%A4lkommen%21&Subject=Order%201042"
                + "&MessageAttributes.entry.1.Name=source&MessageAttributes.entry.1.Value.DataType=String&MessageAttributes.entry.1.Value.StringValue=shop"
                + "&MessageAttributes.entry.2.Name=items&MessageAttributes.entry.2.Value.DataType=Number&MessageAttributes.entry.2.Value.StringValue=3", call.Body);
            Assert.Equal(("95df01b4", "10000000000000003000", Orders), ((string?)sent.Fields["message_id"], (string?)sent.Fields["sequence_number"], (string?)sent.Fields["topic"]));

            await Contexts.DrainAsync(Connector(), Context(sns, AwsSnsConnector.PublishOperation, new() { ["topic"] = "orders.fifo", ["message"] = "hi", ["group"] = "shop-1", ["deduplication"] = "o-1042" }));
            Assert.Equal(new[]
            {
                "Action=ListTopics&Version=2010-03-31",
                "Action=ListTopics&Version=2010-03-31&NextToken=t%2F1%2B",
                "Action=Publish&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aeu-north-1%3A123456789012%3Aorders.fifo&Message=hi&MessageGroupId=shop-1&MessageDeduplicationId=o-1042",
            }, sns.Requests.Skip(1).Select(request => request.Body)); // a name found among the topics, page by page

            var sms = Assert.Single(await Contexts.DrainAsync(Connector(), Context(sns, AwsSnsConnector.PublishOperation, new() { ["phone"] = "+46701740605", ["message"] = "Din kod: 1042" })));
            Assert.Equal("Action=Publish&Version=2010-03-31&PhoneNumber=%2B46701740605&Message=Din%20kod%3A%201042", sns.Requests[^1].Body);
            Assert.Equal(("+46701740605", (string?)null), ((string?)sms.Fields["phone"], (string?)sms.Fields["topic"]));
        }

        [Fact]
        public async Task Topics_and_subscriptions_are_listed_made_and_deleted()
        {
            var sns = new Sns
            {
                Answer = (action, body) => (HttpStatusCode.OK, action switch
                {
                    "ListTopics" => body.Contains("NextToken", StringComparison.Ordinal)
                        ? Answer(action, "<Topics><member><TopicArn>" + Orders + "-c</TopicArn></member></Topics>")
                        : Answer(action, "<Topics><member><TopicArn>" + Orders + "-a</TopicArn></member><member><TopicArn>" + Orders + "-b</TopicArn></member></Topics><NextToken>t2</NextToken>"),
                    "GetTopicAttributes" => Answer(action, "<Attributes><entry><key>DisplayName</key><value>Orders</value></entry><entry><key>SubscriptionsConfirmed</key><value>2</value></entry><entry><key>HTTPSuccessFeedbackRoleArn</key><value>arn:aws:iam::123456789012:role/feedback</value></entry><entry><key>TopicArn</key><value>" + Orders + "</value></entry><entry><key>Policy</key><value>{\"Version\":\"2008-10-17\"}</value></entry></Attributes>"),
                    "CreateTopic" => Answer(action, "<TopicArn>" + Orders + ".fifo</TopicArn>"),
                    "ListSubscriptionsByTopic" => Answer(action, "<Subscriptions><member><SubscriptionArn>" + Orders + ":2bcfbf39-05c3-41de-beaa-fcfcc21c8f55</SubscriptionArn><Owner>123456789012</Owner><Protocol>email</Protocol><Endpoint>ada@example.com</Endpoint><TopicArn>" + Orders + "</TopicArn></member></Subscriptions>"),
                    "Subscribe" => Answer(action, "<SubscriptionArn>pending confirmation</SubscriptionArn>"),
                    _ => Answer(action, ""),
                }),
            };
            (string?, string) Last() => (sns.Requests[^1].Body.Split('&')[0], sns.Requests[^1].Body);
            Task<List<ConnectorRecord>> Run(string operation, Dictionary<string, string> parameters) => Contexts.DrainAsync(Connector(), Context(sns, operation, parameters));

            var topics = await Run(AwsSnsConnector.TopicsOperation, new());
            Assert.Equal(new[] { ("orders-a", Orders + "-a"), ("orders-b", Orders + "-b"), ("orders-c", Orders + "-c") }, topics.Select(topic => ((string?)topic.Fields["name"], (string?)topic.Fields["arn"])));
            Assert.Equal(2, sns.Requests.Count);
            Assert.Equal(2, (await Run(AwsSnsConnector.TopicsOperation, new() { ["max"] = "2" })).Count);
            Assert.Equal(3, sns.Requests.Count); // no page asked for past the most

            var attributes = Assert.Single(await Run(AwsSnsConnector.TopicAttributesOperation, new() { ["topic"] = Orders })).Fields;
            Assert.Equal(("Orders", 2L, Orders, false), ((string?)attributes["display_name"], (long?)attributes["subscriptions_confirmed"], (string?)attributes["topic_arn"], attributes.ContainsKey("policy"))); // a policy no record's value
            Assert.Equal("arn:aws:iam::123456789012:role/feedback", (string?)attributes["http_success_feedback_role_arn"]); // capitals in a row one word

            var made = Assert.Single(await Run(AwsSnsConnector.CreateTopicOperation, new() { ["name"] = "orders", ["display_name"] = "Shop orders", ["fifo"] = "true" }));
            Assert.Equal("Action=CreateTopic&Version=2010-03-31&Name=orders.fifo&Attributes.entry.1.key=DisplayName&Attributes.entry.1.value=Shop%20orders&Attributes.entry.2.key=FifoTopic&Attributes.entry.2.value=true", sns.Requests[^1].Body);
            Assert.Equal(("orders.fifo", Orders + ".fifo"), ((string?)made.Fields["name"], (string?)made.Fields["arn"]));
            await Run(AwsSnsConnector.CreateTopicOperation, new() { ["name"] = "orders" });
            Assert.Equal("Action=CreateTopic&Version=2010-03-31&Name=orders", sns.Requests[^1].Body);
            var gone = Assert.Single(await Run(AwsSnsConnector.DeleteTopicOperation, new() { ["topic"] = Orders }));
            Assert.Equal(("Action=DeleteTopic", "Action=DeleteTopic&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aeu-north-1%3A123456789012%3Aorders"), Last());
            Assert.Equal((Orders, true), ((string?)gone.Fields["topic"], (bool?)gone.Fields["done"]));

            var subscription = Assert.Single(await Run(AwsSnsConnector.SubscriptionsOperation, new() { ["topic"] = Orders })).Fields;
            Assert.Equal(("email", "ada@example.com", "123456789012", Orders + ":2bcfbf39-05c3-41de-beaa-fcfcc21c8f55"), ((string?)subscription["protocol"], (string?)subscription["endpoint"], (string?)subscription["owner"], (string?)subscription["subscription_arn"]));
            Assert.Equal("Action=ListSubscriptionsByTopic&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aeu-north-1%3A123456789012%3Aorders", sns.Requests[^1].Body);
            var pending = Assert.Single(await Run(AwsSnsConnector.SubscribeOperation, new() { ["topic"] = Orders, ["protocol"] = "HTTPS", ["endpoint"] = "https://example.com/hooks/sns" }));
            Assert.Equal("Action=Subscribe&Version=2010-03-31&TopicArn=arn%3Aaws%3Asns%3Aeu-north-1%3A123456789012%3Aorders&Protocol=https&Endpoint=https%3A%2F%2Fexample.com%2Fhooks%2Fsns&ReturnSubscriptionArn=true", sns.Requests[^1].Body);
            Assert.Equal(("pending confirmation", true), ((string?)pending.Fields["subscription_arn"], (bool?)pending.Fields["pending"]));
            var unsubscribed = Assert.Single(await Run(AwsSnsConnector.UnsubscribeOperation, new() { ["subscription"] = Orders + ":2bcfbf39-05c3-41de-beaa-fcfcc21c8f55" }));
            Assert.Equal("Action=Unsubscribe&Version=2010-03-31&SubscriptionArn=arn%3Aaws%3Asns%3Aeu-north-1%3A123456789012%3Aorders%3A2bcfbf39-05c3-41de-beaa-fcfcc21c8f55", sns.Requests[^1].Body);
            Assert.Equal(Orders + ":2bcfbf39-05c3-41de-beaa-fcfcc21c8f55", (string?)unsubscribed.Fields["subscription"]);
        }

        [Fact]
        public async Task What_could_go_wrong_is_refused_before_anything_is_signed_or_sent_and_awss_errors_are_its_words()
        {
            var sns = new Sns();
            async Task<string> Refused(string operation, Dictionary<string, string> parameters) =>
                (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(sns, operation, parameters)))).Message;

            Assert.Equal("Give the topic - its ARN or its name - or a phone number.", await Refused(AwsSnsConnector.PublishOperation, new() { ["message"] = "x" }));
            Assert.Equal("Give a topic or a phone number - not both.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["phone"] = "+46701740605", ["message"] = "x" }));
            Assert.Equal("'0701740605' is not a phone number - +46701740605.", await Refused(AwsSnsConnector.PublishOperation, new() { ["phone"] = "0701740605", ["message"] = "x" }));
            Assert.Equal("'my topic' is not a topic - its ARN, arn:aws:sns:eu-north-1:123456789012:orders, or its name.", await Refused(AwsSnsConnector.DeleteTopicOperation, new() { ["topic"] = "my topic" }));
            Assert.Equal("Give the topic - its ARN or its name.", await Refused(AwsSnsConnector.SubscriptionsOperation, new()));
            Assert.Equal("'arn:aws:sns:eu-north-1:1234:orders' is not a topic - its ARN, arn:aws:sns:eu-north-1:123456789012:orders, or its name.", await Refused(AwsSnsConnector.DeleteTopicOperation, new() { ["topic"] = "arn:aws:sns:eu-north-1:1234:orders" }));
            Assert.Equal("'my attr' is not an attribute's name - letters, digits, _, - and ., not AWS. or Amazon.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = "x", ["attributes"] = "{\"my attr\": \"x\"}" }));
            Assert.Equal("Give the message.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = " " }));
            Assert.Equal("The message is larger than 256 KB.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = new string('m', 256 * 1024 + 1) }));
            Assert.Equal("The subject is one line of up to 100 characters.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = "x", ["subject"] = "a\nb" }));
            Assert.Equal("The subject is one line of up to 100 characters.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = "x", ["subject"] = new string('s', 101) }));
            Assert.Equal("The attributes are not a JSON object - {\"source\": \"shop\"}.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = "x", ["attributes"] = "[1]" }));
            Assert.Equal("'Amazon.trace' is not an attribute's name - letters, digits, _, - and ., not AWS. or Amazon.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = "x", ["attributes"] = "{\"Amazon.trace\": \"x\"}" }));
            Assert.Equal("The attribute source has no value.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = "x", ["attributes"] = "{\"source\": null}" }));
            Assert.Equal("A message carries ten attributes at most - 11 given.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders, ["message"] = "x", ["attributes"] = "{" + string.Join(",", Enumerable.Range(1, 11).Select(i => $"\"a{i}\": 1")) + "}" }));
            Assert.Equal("'a b' is not a deduplication id - up to 128 letters, digits and punctuation.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders + ".fifo", ["message"] = "x", ["group"] = "g", ["deduplication"] = "a b" }));
            Assert.Equal("A FIFO topic's message needs its group.", await Refused(AwsSnsConnector.PublishOperation, new() { ["topic"] = Orders + ".fifo", ["message"] = "x" }));
            Assert.Equal("Give the topic's name.", await Refused(AwsSnsConnector.CreateTopicOperation, new()));
            Assert.Equal("'or ders' is not a topic's name - letters, digits, - and _, up to 256.", await Refused(AwsSnsConnector.CreateTopicOperation, new() { ["name"] = "or ders" }));
            Assert.Equal("'yes' is not true or false (fifo).", await Refused(AwsSnsConnector.CreateTopicOperation, new() { ["name"] = "orders", ["fifo"] = "yes" }));
            Assert.Equal("The display name is one line of up to 100 characters.", await Refused(AwsSnsConnector.CreateTopicOperation, new() { ["name"] = "orders", ["display_name"] = "a\nb" }));
            Assert.Equal("Give the protocol - http, https, email, email-json, sms, sqs, application, lambda or firehose.", await Refused(AwsSnsConnector.SubscribeOperation, new() { ["topic"] = Orders, ["protocol"] = "ftp", ["endpoint"] = "x" }));
            Assert.Equal("'http://example.com/hook' is not an endpoint for https.", await Refused(AwsSnsConnector.SubscribeOperation, new() { ["topic"] = Orders, ["protocol"] = "https", ["endpoint"] = "http://example.com/hook" }));
            Assert.Equal("'ada@' is not an endpoint for email.", await Refused(AwsSnsConnector.SubscribeOperation, new() { ["topic"] = Orders, ["protocol"] = "email", ["endpoint"] = "ada@" }));
            Assert.Equal("'0701740605' is not an endpoint for sms.", await Refused(AwsSnsConnector.SubscribeOperation, new() { ["topic"] = Orders, ["protocol"] = "sms", ["endpoint"] = "0701740605" }));
            Assert.Equal("'orders' is not an endpoint for sqs.", await Refused(AwsSnsConnector.SubscribeOperation, new() { ["topic"] = Orders, ["protocol"] = "sqs", ["endpoint"] = "orders" }));
            Assert.Equal("Give the subscription - its ARN, from Get subscriptions or Subscribe.", await Refused(AwsSnsConnector.UnsubscribeOperation, new() { ["subscription"] = Orders }));
            Assert.Equal("'0' is not a number above 0 (max).", await Refused(AwsSnsConnector.TopicsOperation, new() { ["max"] = "0" }));
            Assert.StartsWith("Amazon SNS has no operation 'list_platform_applications' - publish, topics,", await Refused("list_platform_applications", new()));
            Assert.Empty(sns.Requests); // nothing signed or sent

            var noRegion = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(sns), Api, AwsSnsConnector.TopicsOperation, new Dictionary<string, string>(), settings: new Dictionary<string, string> { ["region"] = "EU" }, credentials: Credentials)));
            Assert.Equal("'EU' is not an AWS region - eu-north-1.", noRegion.Message);
            var nameless = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Contexts.Extract(new StubEgress(sns), Api, AwsSnsConnector.TopicsOperation, new Dictionary<string, string>(), settings: Settings, credentials: new Dictionary<string, string> { ["secret-key"] = "s" })));
            Assert.Equal("The Amazon SNS connection has no access key.", nameless.Message);
            var unnamed = await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(new Sns(), AwsSnsConnector.DeleteTopicOperation, new() { ["topic"] = "nowhere" })));
            Assert.Equal("No topic is named nowhere.", unnamed.Message);
            Assert.Empty(sns.Requests);

            var missing = new Sns { Answer = (_, _) => (HttpStatusCode.NotFound, "<ErrorResponse xmlns=\"http://sns.amazonaws.com/doc/2010-03-31/\"><Error><Type>Sender</Type><Code>NotFound</Code><Message>Topic does not exist</Message></Error><RequestId>r-2</RequestId></ErrorResponse>") };
            Assert.Equal("HTTP 404 NotFound: Topic does not exist", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(missing, AwsSnsConnector.DeleteTopicOperation, new() { ["topic"] = Orders })))).Message);
            var silent = new Sns { Answer = (_, _) => (HttpStatusCode.Forbidden, "") };
            Assert.Equal("HTTP 403.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(silent, AwsSnsConnector.TopicsOperation, new())))).Message);
            var entity = new Sns { Answer = (action, _) => (HttpStatusCode.OK, "<!DOCTYPE r [<!ENTITY x \"arn:aws:sns:eu-north-1:123456789012:planted\">]><ListTopicsResponse><ListTopicsResult><Topics><member><TopicArn>&x;</TopicArn></member></Topics></ListTopicsResult></ListTopicsResponse>") };
            Assert.Equal("Amazon SNS answered ListTopics with no XML.", (await Assert.ThrowsAsync<InvalidOperationException>(() => Contexts.DrainAsync(Connector(), Context(entity, AwsSnsConnector.TopicsOperation, new())))).Message); // a DTD never read
        }

        [Fact]
        public async Task A_test_names_the_topics_seen()
        {
            var sns = new Sns { Answer = (action, _) => (HttpStatusCode.OK, Answer(action, "<Topics><member><TopicArn>" + Orders + "</TopicArn></member><member><TopicArn>" + Orders + "-b</TopicArn></member></Topics>")) };
            var tested = await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(sns), "", settings: Settings, credentials: Credentials));
            Assert.Equal((true, "SNS in eu-north-1: 2 topics seen."), (tested.Success, tested.Message));
            Assert.Equal(("https://sns.eu-north-1.amazonaws.com/", "Action=ListTopics&Version=2010-03-31"), (sns.Requests[0].Url, sns.Requests[0].Body)); // the region's own address when none is given
            var many = new Sns { Answer = (action, _) => (HttpStatusCode.OK, Answer(action, "<Topics><member><TopicArn>" + Orders + "</TopicArn></member></Topics><NextToken>t</NextToken>")) };
            Assert.Equal("SNS in eu-north-1: 1 or more topics seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(many), "", settings: Settings, credentials: Credentials))).Message);
            var one = new Sns { Answer = (action, _) => (HttpStatusCode.OK, Answer(action, "<Topics><member><TopicArn>" + Orders + "</TopicArn></member></Topics>")) };
            Assert.Equal("SNS in eu-north-1: one topic seen.", (await Connector().TestConnectionAsync(Contexts.Test(new StubEgress(one), "", settings: Settings, credentials: Credentials))).Message);
            Assert.Equal((AuthenticationType.ApiKeyPair, "awssns"), (Connector().AuthType, Connector().ConnectorType));
            Assert.Equal(8, Connector().Operations.Count);
        }
    }
}
