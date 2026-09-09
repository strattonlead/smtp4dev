using System.Text.Json;
using Rnwood.Smtp4dev.Controllers;
using Rnwood.Smtp4dev.Server.Settings;
using Xunit;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// Every settings write through the API rewrites the editable settings file from a
    /// deserialized copy of itself, assigning only the fields the update carries. The listener
    /// collections are not among those fields, so they survive a write only because the
    /// serializer round trips them.
    ///
    /// If the source generated context ever stops covering them the failure is silent and total:
    /// the next settings write drops the listener configuration, and the server comes back on one
    /// port with whatever TLS mode the scalar options happen to say. A hosted deployment writes
    /// settings constantly - that is how mailboxes and validation expressions are applied - so
    /// this would be hit within seconds and look like the listeners spontaneously disappearing.
    /// </summary>
    public class SettingsFileListenerRoundTripTests
    {
        [Fact]
        public void TheEditableSettingsFile_RoundTripsTheListenerCollections()
        {
            var original = new SettingsFile
            {
                ServerOptions = new ServerOptionsSource
                {
                    SmtpListeners =
                    [
                        new SmtpListenerOptions { Port = 587, TlsMode = TlsMode.StartTls },
                        new SmtpListenerOptions { Port = 465, TlsMode = TlsMode.ImplicitTls },
                        new SmtpListenerOptions { Port = 2525, TlsMode = TlsMode.StartTls }
                    ],
                    ImapListeners =
                    [
                        new ImapListenerOptions { Port = 143, TlsMode = TlsMode.StartTls },
                        new ImapListenerOptions { Port = 993, TlsMode = TlsMode.ImplicitTls }
                    ]
                }
            };

            string json = JsonSerializer.Serialize(original, SettingsFileSerializationContext.Default.SettingsFile);
            SettingsFile roundTripped = JsonSerializer.Deserialize(json, SettingsFileSerializationContext.Default.SettingsFile);

            Assert.Equal(original.ServerOptions.SmtpListeners, roundTripped.ServerOptions.SmtpListeners);
            Assert.Equal(original.ServerOptions.ImapListeners, roundTripped.ServerOptions.ImapListeners);
        }
    }
}
