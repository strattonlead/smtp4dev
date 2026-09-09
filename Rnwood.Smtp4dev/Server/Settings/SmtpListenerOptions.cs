namespace Rnwood.Smtp4dev.Server.Settings
{
    /// <summary>
    /// One SMTP listener. A server which needs submission on several ports, or on the same port
    /// under different TLS modes, configures one of these per port.
    ///
    /// This is a record so that it is value comparable: <c>Smtp4devServer</c> compares the
    /// configured set against the running set to decide which listeners to start and stop, and
    /// reference equality would restart all of them on every settings write.
    /// </summary>
    public sealed record SmtpListenerOptions
    {
        public int Port { get; set; } = 25;

        public TlsMode TlsMode { get; set; } = TlsMode.None;
    }
}
