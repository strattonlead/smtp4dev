namespace Rnwood.Smtp4dev.Server.Settings
{
    /// <summary>
    /// One IMAP listener. Serving 143 with STARTTLS and 993 with implicit TLS at the same time
    /// needs a TLS mode per port, which a single server wide setting cannot express.
    /// </summary>
    public sealed record ImapListenerOptions
    {
        public int Port { get; set; } = 143;

        public TlsMode TlsMode { get; set; } = TlsMode.None;
    }
}
