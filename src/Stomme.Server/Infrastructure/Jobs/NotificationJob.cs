using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using MimeKit;
using Stomme.FeatureManagement;
using Stomme.FeatureManagement.Application;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using MailKit.Security;

namespace Stomme.Infrastructure
{
    public class NotificationJob : HostedServiceBase
    {
        // JobType = "Stomme.Infrastructure.NotificationJob, Stomme.Server"

        public NotificationJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Notification Job";
            Frequency = "m"; // minute
            Interval = 1;
            IsEnabled = false;
        }

        // job is executed for each tenant in installation
        public async override Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            string log = "";

            // get services
            var siteRepository = provider.GetRequiredService<ISiteRepository>();
            var userRepository = provider.GetRequiredService<IUserRepository>();
            var settingRepository = provider.GetRequiredService<ISettingRepository>();
            var notificationRepository = provider.GetRequiredService<INotificationRepository>();
            var features = provider.GetRequiredService<IFeatureEvaluator>();

            // The e-mail capability is gated by a feature flag (D12). It is read once per run rather than
            // per site: a flag is installation-wide, and the evaluator reads the flag store on every call.
            var emailEnabled = await features.IsEnabledAsync(FeatureCatalog.EmailNotifications);
            if (!emailEnabled)
            {
                return "Notification E-mail Is Disabled By The " + FeatureCatalog.EmailNotifications + " Feature Flag<br />";
            }

            // iterate through sites for current tenant
            List<Site> sites = (await siteRepository.GetSitesAsync()).ToList();
            foreach (Site site in sites.Where(item => !item.IsDeleted))
            {
                log += "Processing Notifications For Site: " + site.Name + "<br />";

                List<Notification> notifications = (await notificationRepository.GetNotificationsAsync(site.SiteId, -1, -1)).ToList();
                if (notifications.Count > 0)
                {
                    // get site settings
                    var settings = await settingRepository.GetSettingsAsync(EntityNames.Site, site.SiteId, EntityNames.Host, -1);

                    // The legacy per-site SMTPEnabled setting still applies, narrowing the flag to a site.
                    // It is retained for backward compatibility (an install that turned e-mail off must stay
                    // off after upgrading); the flag is the installation-wide capability switch.
                    if (settingRepository.GetSettingValue(settings, "SMTPEnabled", "True") == "True")
                    {
                        // Mail queued more than a day ago is not sent (2026-10-10): the queue keeps every undelivered row
                        // for as long as SMTP is off, so switching it on used to send everything since the install - a
                        // reset link long expired, an error notice months old - through a relay with an hourly cap.
                        var stale = NotificationQueueAge.TooOld(notifications, DateTime.UtcNow);
                        if (stale.Count > 0)
                        {
                            foreach (var old in stale)
                            {
                                NotificationQueueAge.Close(old);
                                await notificationRepository.UpdateNotificationAsync(old);
                            }

                            log += $"{stale.Count} Notification(s) Queued More Than {NotificationQueueAge.MaxAge.TotalHours:0} Hours Ago Were Not Sent<br />";
                            notifications = notifications.Except(stale).ToList();
                        }

                        if (notifications.Count == 0)
                        {
                            log += "No Notifications To Deliver<br />";
                            continue;
                        }

                        bool valid = true;
                        if (settingRepository.GetSettingValue(settings, "SMTPAuthentication", "Basic") == "Basic")
                        {
                            // basic
                            if (settingRepository.GetSettingValue(settings, "SMTPHost", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPPort", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPSender", "") == "")
                            {
                                log += "SMTP Not Configured Properly In Site Settings - Host, Port, And Sender Are All Required" + "<br />";
                                valid = false;
                            }
                        }
                        else
                        {
                            // oauth
                            if (settingRepository.GetSettingValue(settings, "SMTPHost", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPPort", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPAuthority", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPClientId", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPClientSecret", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPScopes", "") == "" ||
                                settingRepository.GetSettingValue(settings, "SMTPSender", "") == "")
                            {
                                log += "SMTP Not Configured Properly In Site Settings - Host, Port, Authority, Client ID, Client Secret, Scopes, And Sender Are All Required" + "<br />";
                                valid = false;
                            }
                        }

                        if (valid)
                        {
                            // construct SMTP Client
                            using var client = new SmtpClient();

                            try
                            {
                                var secureSocketOptions = SecureSocketOptions.Auto;
                                switch (settingRepository.GetSettingValue(settings, "SMTPSSL", "Auto"))
                                {
                                    case "None":
                                        secureSocketOptions = SecureSocketOptions.None;
                                        break;
                                    case "Auto":
                                        secureSocketOptions = SecureSocketOptions.Auto;
                                        break;
                                    case "StartTls":
                                        secureSocketOptions = SecureSocketOptions.StartTls;
                                        break;
                                    case "SslOnConnect":
                                    case "True": // legacy setting value
                                        secureSocketOptions = SecureSocketOptions.SslOnConnect;
                                        break;
                                    case "StartTlsWhenAvailable":
                                    case "False": // legacy setting value
                                        secureSocketOptions = SecureSocketOptions.StartTlsWhenAvailable;
                                        break;
                                }

                                await client.ConnectAsync(settingRepository.GetSettingValue(settings, "SMTPHost", ""),
                                        int.Parse(settingRepository.GetSettingValue(settings, "SMTPPort", "")),
                                        secureSocketOptions);
                            }
                            catch (Exception ex)
                            {
                                log += "SMTP Not Configured Properly In Site Settings - Could Not Connect To SMTP Server - " + ex.Message + "<br />";
                                valid = false;
                            }

                            if (valid)
                            {
                                if (settingRepository.GetSettingValue(settings, "SMTPAuthentication", "Basic") == "Basic")
                                {
                                    if (settingRepository.GetSettingValue(settings, "SMTPUsername", "") != "" && settingRepository.GetSettingValue(settings, "SMTPPassword", "") != "")
                                    {
                                        try
                                        {
                                            await client.AuthenticateAsync(settingRepository.GetSettingValue(settings, "SMTPUsername", ""),
                                            settingRepository.GetSettingValue(settings, "SMTPPassword", ""));
                                        }
                                        catch (Exception ex)
                                        {
                                            log += "SMTP Not Configured Properly In Site Settings - Basic Authentication Failed Using Username And Password - " + ex.Message + "<br />";
                                            valid = false;
                                        }

                                    }
                                    else
                                    {
                                        // it is possible to use basic without any authentication (not recommended)
                                    }
                                }
                                else
                                {
                                    // oauth authentication
                                    var confidentialClientApplication = ConfidentialClientApplicationBuilder.Create(settingRepository.GetSettingValue(settings, "SMTPClientId", ""))
                                        .WithAuthority(settingRepository.GetSettingValue(settings, "SMTPAuthority", ""))
                                        .WithClientSecret(settingRepository.GetSettingValue(settings, "SMTPClientSecret", ""))
                                        .Build();
                                    try
                                    {
                                        var result = await confidentialClientApplication.AcquireTokenForClient(settingRepository.GetSettingValue(settings, "SMTPScopes", "").Split(',')).ExecuteAsync();
                                        var oauth2 = new SaslMechanismOAuth2(settingRepository.GetSettingValue(settings, "SMTPSender", ""), result.AccessToken);
                                        await client.AuthenticateAsync(oauth2);
                                    }
                                    catch (Exception ex)
                                    {
                                        log += "SMTP Not Configured Properly In Site Settings - OAuth Token Could Not Be Retrieved From Authority - " + ex.Message + "<br />";
                                        valid = false;
                                    }
                                }
                            }

                            if (valid)
                            {
                                // iterate through undelivered notifications
                                int sent = 0;
                                foreach (Notification notification in notifications)
                                {
                                    var fromEmail = notification.FromEmail ?? "";
                                    var fromName = notification.FromDisplayName ?? "";
                                    var toEmail = notification.ToEmail ?? "";
                                    var toName = notification.ToDisplayName ?? "";

                                    // get sender from user information if "from" email or name is not specified and user id is available
                                    if ((string.IsNullOrEmpty(fromEmail) || string.IsNullOrEmpty(fromName)) && notification.FromUserId != null)
                                    {
                                        var user = await userRepository.GetUserAsync(notification.FromUserId.Value);
                                        if (user != null)
                                        {
                                            fromEmail = string.IsNullOrEmpty(fromEmail) ? user.Email ?? "" : fromEmail;
                                            fromName = string.IsNullOrEmpty(fromName) ? user.DisplayName ?? "" : fromName;
                                        }
                                    }
                                    fromName = string.IsNullOrEmpty(fromName) ? site.Name : fromName;

                                    // get recipient from user information if "to" email or name is not specified and user id is available
                                    if ((string.IsNullOrEmpty(toEmail) || string.IsNullOrEmpty(toName)) && notification.ToUserId != null)
                                    {
                                        var user = await userRepository.GetUserAsync(notification.ToUserId.Value);
                                        if (user != null)
                                        {
                                            toEmail = string.IsNullOrEmpty(toEmail) ? user.Email ?? "" : toEmail;
                                            toName = string.IsNullOrEmpty(toName) ? user.DisplayName ?? "" : toName;
                                        }
                                    }

                                    // create mailbox addresses
                                    MailboxAddress from = null;
                                    MailboxAddress to = null;
                                    MailboxAddress replyTo = null;
                                    var mailboxAddressValidationError = "";

                                    // always send from SMTP Sender
                                    if (MailboxAddress.TryParse(settingRepository.GetSettingValue(settings, "SMTPSender", ""), out from))
                                    {
                                        from.Name = fromName; 
                                    }
                                    else
                                    {
                                        mailboxAddressValidationError += $" Invalid Sender: {fromName} &lt;{settingRepository.GetSettingValue(settings, "SMTPSender", "")}&gt;";
                                    }

                                    // reply to
                                    if (!string.IsNullOrEmpty(fromEmail) && fromEmail != from.Address)
                                    {
                                        if (MailboxAddress.TryParse(fromEmail, out replyTo))
                                        {
                                            replyTo.Name = fromName; 
                                        }
                                    }

                                    // recipient
                                    if (MailboxAddress.TryParse(toEmail, out to))
                                    {
                                        to.Name = toName; 
                                    }
                                    else
                                    {
                                        mailboxAddressValidationError += $" Invalid Recipient: {toName} &lt;{toEmail}&gt;";
                                    }

                                    // if mailbox addresses are valid
                                    if (from != null && to != null)
                                    {
                                        // create mail message
                                        MimeMessage mailMessage = new MimeMessage();
                                        mailMessage.MessageId = NotificationMail.MessageIdFor(from);
                                        mailMessage.From.Add(from);
                                        mailMessage.To.Add(to);
                                        if (replyTo != null)
                                        {
                                            mailMessage.ReplyTo.Add(replyTo);
                                        }

                                        // subject
                                        mailMessage.Subject = notification.Subject;

                                        // body
                                        var bodyText = notification.Body;

                                        if (!bodyText.Contains('<') || !bodyText.Contains('>'))
                                        {
                                            // plain text messages should convert line breaks to HTML tags to preserve formatting
                                            bodyText = bodyText.Replace("\n", "<br />");
                                        }

                                        mailMessage.Body = new TextPart("html", System.Text.Encoding.UTF8)
                                        {
                                            Text = bodyText
                                        };

                                        // send mail
                                        try
                                        {
                                            await client.SendAsync(mailMessage);
                                            sent++;
                                            notification.IsDelivered = true;
                                            notification.DeliveredOn = DateTime.UtcNow;
                                            await notificationRepository.UpdateNotificationAsync(notification);
                                        }
                                        catch (Exception ex)
                                        {
                                            log += $"Error Sending Notification Id: {notification.NotificationId} - {ex.Message}<br />";
                                        }
                                    }
                                    else
                                    {
                                        // invalid mailbox address
                                        log += $"Notification Id: {notification.NotificationId} Has An {mailboxAddressValidationError} And Has Been Deleted<br />";
                                        notification.IsDeleted = true;
                                        await notificationRepository.UpdateNotificationAsync(notification);
                                    }
                                }

                                log += "Notifications Delivered: " + sent + "<br />";
                            }

                            await client.DisconnectAsync(true);
                        }
                    }
                    else
                    {
                        log += "SMTP Disabled In Site Settings<br />";
                    }
                }
                else
                {
                    log += "No Notifications To Deliver<br />";
                }
            }

            return log;
        }
    }

    /// <summary>
    /// How old a queued e-mail may be and still be sent: a day. A password-reset link the identity system issues expires in a
    /// day, and an older notice is news nobody is waiting for. Measured from when the mail was due (its <c>SendOn</c>, which a
    /// new notification sets to its creation), so mail scheduled for later is judged from then.
    /// </summary>
    public static class NotificationQueueAge
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        public static IReadOnlyList<Notification> TooOld(IEnumerable<Notification> queue, DateTime nowUtc) =>
            queue.Where(notification => (notification.SendOn ?? notification.CreatedOn) < nowUtc - MaxAge).ToList();

        /// <summary>
        /// An old mail closed without being sent: a system notice is marked deleted (the nightly purge takes it, as it takes an
        /// undeliverable one); a person's message stays in the inbox it was sent to - only its e-mail is given up.
        /// </summary>
        public static void Close(Notification notification)
        {
            if (notification.FromUserId is null)
            {
                notification.IsDeleted = true;
            }
            else
            {
                notification.IsDelivered = true;
                notification.DeliveredOn = null;
            }
        }
    }

    /// <summary>What the notification job puts on each mail beyond its addresses and text.</summary>
    public static class NotificationMail
    {
        /// <summary>
        /// A Message-ID at the sender's own domain (2026-10-10, infra's finding on the first mail through the relay): MimeKit's
        /// default takes the machine name - in a container a hex id like <c>deee53a6f9d6</c> - which spam filters read as a
        /// sign of a careless sender; the sender's domain (the relay's, e.g. <c>notify.example.com</c>) is the one the mail is sent for.
        /// </summary>
        public static string MessageIdFor(MailboxAddress from) =>
            MimeKit.Utils.MimeUtils.GenerateMessageId(string.IsNullOrWhiteSpace(from?.Domain) ? "localhost" : from.Domain);
    }
}
