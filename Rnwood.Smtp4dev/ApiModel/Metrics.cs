namespace Rnwood.Smtp4dev.ApiModel
{
    /// <summary>
    /// Runtime metrics. Deliberately separate from <see cref="Server"/>, which is settings: a
    /// number that changes on its own does not belong in an object callers read, edit and post
    /// back.
    /// </summary>
    public class Metrics
    {
        /// <summary>
        /// How many connections are currently parked inside a scripted delay().
        ///
        /// Expression evaluation latency cannot detect a blocking fault, because for one the block
        /// is the latency. This is the number which says how close the server is to running out of
        /// threads, and since the expressions are global it counts blocks caused by every client.
        /// </summary>
        public int BlockedConnections { get; set; }
    }
}
