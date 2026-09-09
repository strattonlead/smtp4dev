using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Esprima.Ast;

namespace Rnwood.Smtp4dev.Server.Settings
{
    /// <summary>
    /// Settings for the server. These are re-read whenever the settings change, so most of them are read at the
    /// point of use and take effect immediately.
    ///
    /// The exceptions are the options which the SMTP and IMAP listeners read while they are being created.
    /// Changing one of those restarts the relevant listener, which terminates every session which is in
    /// progress. Those options are listed in <c>Smtp4devServer.SmtpListenerConfig</c> and
    /// <c>ImapServer.ImapListenerConfig</c>; add a new option to the relevant one if the listener needs to be
    /// recreated for it to take effect.
    /// </summary>
    public record ServerOptions
    {
        private string database = "database.db";

        public string Urls { get; set; }

        public int Port { get; set; } = 25;
        public bool AllowRemoteConnections { get; set; } = true;
        public string BindAddress { get; set; }

        public string Database { get => database?.Trim('"'); set => database = value; }
        public int NumberOfMessagesToKeep { get; set; } = 100;
        public int NumberOfSessionsToKeep { get; set; } = 100;

        public string BasePath { get; set; } = "/";

        public TlsMode TlsMode { get; set; } = TlsMode.None;

        public TlsMode Pop3TlsMode { get; set; } = TlsMode.None;

        [AllowNull]
        public string SslProtocols { get; set; } = null;

        public string TlsCipherSuites { get; set; } = "";

        public string TlsCertificateStoreThumbprint { get; set; }
        public string TlsCertificate { get; set; }
        public string TlsCertificatePrivateKey { get; set; }

        public string TlsCertificatePassword { get; set; } = "";

        public string HostName { get; set; } = Dns.GetHostName();

        public int? ImapPort { get; set; } = 143;
        public int? Pop3Port { get; set; } = 110;

        /// <summary>
        /// The SMTP listeners to start. When this is not set, a single listener is derived from
        /// <see cref="Port"/> and <see cref="TlsMode"/>, so an unmodified upstream configuration
        /// behaves exactly as it did before this option existed.
        /// </summary>
        public SmtpListenerOptions[] SmtpListeners { get; set; } = null;

        /// <summary>
        /// The IMAP listeners to start. When this is not set, a single listener is derived from
        /// <see cref="ImapPort"/> with TLS disabled, which is what IMAP has always done.
        /// </summary>
        public ImapListenerOptions[] ImapListeners { get; set; } = null;

        public bool RecreateDb { get; set; }

        public bool LockSettings { get; set; } = false;

        public bool DisableMessageSanitisation { get; set; } = false;
        public string CredentialsValidationExpression { get; set; }
        public bool AuthenticationRequired { get; set; } = false;
        public bool SecureConnectionRequired { get; set; } = false;

        public bool SmtpAllowAnyCredentials { get; set; }
        public string RecipientValidationExpression { get; set; }

        public string MessageValidationExpression { get; set; }

        public string CommandValidationExpression { get; set; }
        public bool DisableIPv6 { get; set; } = false;

        public UserOptions[] Users { get; set; } = [];

        public bool WebAuthenticationRequired { get; set; } = false;
        
        /// <summary>
        /// DEPRECATED: Use mailbox configuration with AuthenticatedUsers property instead for better flexibility.
        /// When true, authenticated SMTP sessions will deliver messages to the user's DefaultMailbox, 
        /// bypassing all mailbox routing rules including header filters.
        /// For more flexible routing that allows header-based filtering even for authenticated users,
        /// configure mailboxes with the AuthenticatedUsers property and position them in the desired order.
        /// </summary>
        [Obsolete("Use mailbox configuration with AuthenticatedUsers property instead for better flexibility")]
        public bool DeliverMessagesToUsersDefaultMailbox { get; set; } = true;

        public string SmtpEnabledAuthTypesWhenNotSecureConnection { get; set; } = "PLAIN,LOGIN,CRAM-MD5";

        public string SmtpEnabledAuthTypesWhenSecureConnection { get; set; } = "PLAIN,LOGIN,CRAM-MD5";

        public MailboxOptions[] Mailboxes { get; set; } = [];

        public string HtmlValidateConfig { get; set; }

        public bool DisableHtmlValidation { get; set; } = false;

        public bool DisableHtmlCompatibilityCheck { get; set; } = false;

        public long? MaxMessageSize { get; set; }
        
        public bool ValidateBareLineFeed { get; set; } = false;

        public bool Pop3SecureConnectionRequired { get; set; } = false;

        public string DeliverToStdout { get; set; } = "";

        public int? ExitAfterMessages { get; set; }

        /// <summary>
        /// OAuth2/XOAUTH2 Identity Provider (IDP) Authority URL for token validation.
        /// Example: https://login.microsoftonline.com/common/v2.0 for Azure AD
        /// When SmtpAllowAnyCredentials is false, tokens will be validated against this IDP.
        /// </summary>
        public string OAuth2Authority { get; set; }

        /// <summary>
        /// OAuth2/XOAUTH2 Audience value for token validation.
        /// The token must be issued for this audience to be accepted.
        /// </summary>
        public string OAuth2Audience { get; set; }

        /// <summary>
        /// OAuth2/XOAUTH2 Issuer value for token validation.
        /// The token must be issued by this issuer to be accepted.
        /// If not specified, issuer validation is performed using the authority's discovery document.
        /// </summary>
        public string OAuth2Issuer { get; set; }

        /// <summary>
        /// The SMTP listeners which should be running, whether they came from
        /// <see cref="SmtpListeners"/> or from the single port and TLS mode which preceded it.
        /// </summary>
        public IReadOnlyList<SmtpListenerOptions> ResolveSmtpListeners() =>
            SmtpListeners is { Length: > 0 }
                ? SmtpListeners
                : new[] { new SmtpListenerOptions { Port = Port, TlsMode = TlsMode } };

        /// <summary>
        /// The IMAP listeners which should be running. Empty when IMAP is disabled.
        /// </summary>
        public IReadOnlyList<ImapListenerOptions> ResolveImapListeners()
        {
            if (ImapListeners is { Length: > 0 })
            {
                return ImapListeners;
            }

            return ImapPort.HasValue
                ? new[] { new ImapListenerOptions { Port = ImapPort.Value, TlsMode = TlsMode.None } }
                : Array.Empty<ImapListenerOptions>();
        }

        /// <summary>
        /// Whether any listener needs a TLS certificate. Asked by
        /// <see cref="CertificateHelper.GetTlsCertificate"/>, which must not skip resolving a
        /// certificate just because the scalar <see cref="TlsMode"/> is None while a listener
        /// entry asks for TLS.
        /// </summary>
        public bool RequiresTlsCertificate() =>
            ResolveSmtpListeners().Any(listener => listener.TlsMode != TlsMode.None)
            || ResolveImapListeners().Any(listener => listener.TlsMode != TlsMode.None)
            || Pop3TlsMode != TlsMode.None;
    }

}
