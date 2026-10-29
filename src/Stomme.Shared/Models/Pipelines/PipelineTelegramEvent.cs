#nullable enable
using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>One message a Telegram bot got, as a "Telegram message received" trigger hands it on.</summary>
    public sealed record PipelineTelegramMessage(
        long UpdateId,
        string Kind,
        long MessageId,
        DateTimeOffset Date,
        string Text,
        long ChatId,
        string ChatType,
        string? ChatTitle,
        string? ChatUsername,
        long? FromId,
        string? FromUsername,
        string? FromFirstName,
        string? FromLastName,
        long? ReplyToMessageId);

    /// <summary>
    /// What a "Telegram message received" trigger hands the pipeline it starts (n8n's Telegram Trigger): the message, as
    /// JSON text, read with an expression - <c>{{ message.text }}</c>, <c>{{ message.chat.id }}</c>, <c>{{ message.from.username }}</c>
    /// - and answered with Send chat message to <c>{{ message.chat.id }}</c>, in reply to <c>{{ message.messageId }}</c>. ONE
    /// shape, written by the server from the Telegram connector's record and by the editor for the sample an author tests with.
    /// </summary>
    public static class PipelineTelegramEvent
    {
        /// <summary>The trigger's step type.</summary>
        public const string StepType = "Stomme.TelegramMessageReceived";

        /// <summary>A message in a chat with the bot, a group it is in.</summary>
        public const string MessageKind = "message";

        /// <summary>A post in a channel the bot is an administrator of.</summary>
        public const string ChannelPostKind = "channel_post";

        private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>The message as a run reads it.</summary>
        public static string Write(PipelineTelegramMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            var json = new JsonObject
            {
                ["updateId"] = message.UpdateId,
                ["kind"] = message.Kind,
                ["messageId"] = message.MessageId,
                ["date"] = message.Date.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["text"] = message.Text,
                ["chat"] = new JsonObject
                {
                    ["id"] = message.ChatId,
                    ["type"] = message.ChatType,
                    ["title"] = message.ChatTitle,
                    ["username"] = message.ChatUsername,
                },
                ["from"] = message.FromId is { } fromId
                    ? new JsonObject
                    {
                        ["id"] = fromId,
                        ["username"] = message.FromUsername,
                        ["firstName"] = message.FromFirstName,
                        ["lastName"] = message.FromLastName,
                    }
                    : null, // a channel post has no sender
                ["replyToMessageId"] = message.ReplyToMessageId,
            };
            return json.ToJsonString(Readable);
        }

        /// <summary>A message to test with in the editor, before any has come: a person asking the bot about an order.</summary>
        public static string Sample(DateTimeOffset now) => Write(new PipelineTelegramMessage(
            UpdateId: 900001, Kind: MessageKind, MessageId: 42, Date: now, Text: "Where is order 1001?",
            ChatId: 123456789, ChatType: "private", ChatTitle: null, ChatUsername: "ada_customer",
            FromId: 123456789, FromUsername: "ada_customer", FromFirstName: "Ada", FromLastName: "Lovelace", ReplyToMessageId: null));
    }
}
