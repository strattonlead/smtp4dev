using System.Linq;
using Rnwood.Smtp4dev.Server.Settings;
using Xunit;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// The listener collections are additive: an unmodified upstream configuration has none of
    /// them set and must keep behaving exactly as it did, so every one of these resolvers has a
    /// fallback to the scalar option it replaces.
    /// </summary>
    public class ServerOptionsListenerTests
    {
        [Fact]
        public void ResolveSmtpListeners_FallsBackToPortAndTlsMode_WhenNoListenersAreConfigured()
        {
            var options = new ServerOptions { Port = 2525, TlsMode = TlsMode.StartTls };

            SmtpListenerOptions listener = Assert.Single(options.ResolveSmtpListeners());

            Assert.Equal(2525, listener.Port);
            Assert.Equal(TlsMode.StartTls, listener.TlsMode);
        }

        [Fact]
        public void ResolveSmtpListeners_UsesTheConfiguredListeners_WhenPresent()
        {
            var options = new ServerOptions
            {
                Port = 25,
                TlsMode = TlsMode.None,
                SmtpListeners =
                [
                    new SmtpListenerOptions { Port = 587, TlsMode = TlsMode.StartTls },
                    new SmtpListenerOptions { Port = 465, TlsMode = TlsMode.ImplicitTls },
                    new SmtpListenerOptions { Port = 2525, TlsMode = TlsMode.StartTls }
                ]
            };

            Assert.Equal(new[] { 587, 465, 2525 }, options.ResolveSmtpListeners().Select(l => l.Port));
        }

        [Fact]
        public void ResolveImapListeners_FallsBackToImapPort_WhenNoListenersAreConfigured()
        {
            var options = new ServerOptions { ImapPort = 143 };

            ImapListenerOptions listener = Assert.Single(options.ResolveImapListeners());

            Assert.Equal(143, listener.Port);
            Assert.Equal(TlsMode.None, listener.TlsMode);
        }

        [Fact]
        public void ResolveImapListeners_IsEmpty_WhenImapIsDisabled()
        {
            var options = new ServerOptions { ImapPort = null };

            Assert.Empty(options.ResolveImapListeners());
        }

        [Fact]
        public void ResolveImapListeners_UsesTheConfiguredListeners_WhenPresent()
        {
            var options = new ServerOptions
            {
                ImapPort = 143,
                ImapListeners =
                [
                    new ImapListenerOptions { Port = 143, TlsMode = TlsMode.StartTls },
                    new ImapListenerOptions { Port = 993, TlsMode = TlsMode.ImplicitTls }
                ]
            };

            Assert.Equal(new[] { 143, 993 }, options.ResolveImapListeners().Select(l => l.Port));
            Assert.Equal(TlsMode.ImplicitTls, options.ResolveImapListeners()[1].TlsMode);
        }

        [Fact]
        public void RequiresTlsCertificate_IsFalse_WhenNothingAsksForTls()
        {
            var options = new ServerOptions { TlsMode = TlsMode.None, Pop3TlsMode = TlsMode.None };

            Assert.False(options.RequiresTlsCertificate());
        }

        [Fact]
        public void RequiresTlsCertificate_IsTrue_WhenOnlyAnSmtpListenerEntryAsksForTls()
        {
            var options = new ServerOptions
            {
                TlsMode = TlsMode.None,
                Pop3TlsMode = TlsMode.None,
                SmtpListeners = [new SmtpListenerOptions { Port = 465, TlsMode = TlsMode.ImplicitTls }]
            };

            Assert.True(options.RequiresTlsCertificate());
        }

        [Fact]
        public void RequiresTlsCertificate_IsTrue_WhenOnlyAnImapListenerEntryAsksForTls()
        {
            var options = new ServerOptions
            {
                TlsMode = TlsMode.None,
                Pop3TlsMode = TlsMode.None,
                ImapListeners = [new ImapListenerOptions { Port = 993, TlsMode = TlsMode.ImplicitTls }]
            };

            Assert.True(options.RequiresTlsCertificate());
        }

        [Fact]
        public void RequiresTlsCertificate_IsTrue_WhenOnlyPop3AsksForTls()
        {
            var options = new ServerOptions { TlsMode = TlsMode.None, Pop3TlsMode = TlsMode.ImplicitTls };

            Assert.True(options.RequiresTlsCertificate());
        }
    }
}
