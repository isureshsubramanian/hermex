namespace Hermex;

/// <summary>Base type for all exceptions raised by Hermex.</summary>
public class HermexException : Exception
{
    public HermexException(string message) : base(message) { }
    public HermexException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when <see cref="HermexOptions"/> contains an invalid value.</summary>
public sealed class HermexConfigurationException : HermexException
{
    public HermexConfigurationException(string message) : base(message) { }
}

/// <summary>
/// Thrown when the SMTP listener cannot bind to a usable port. When
/// <see cref="HermexOptions.AutoResolvePortConflict"/> is enabled this is only
/// raised after every probed port in the range was already in use.
/// </summary>
public sealed class HermexPortConflictException : HermexException
{
    public HermexPortConflictException(int configuredPort, IReadOnlyList<int> probedPorts, Exception? innerException)
        : base(BuildMessage(configuredPort, probedPorts), innerException ?? new Exception("Address already in use."))
    {
        ConfiguredPort = configuredPort;
        ProbedPorts = probedPorts;
    }

    /// <summary>The port originally requested through configuration.</summary>
    public int ConfiguredPort { get; }

    /// <summary>Every port that was probed before giving up.</summary>
    public IReadOnlyList<int> ProbedPorts { get; }

    private static string BuildMessage(int configuredPort, IReadOnlyList<int> probedPorts)
    {
        if (probedPorts.Count <= 1)
        {
            return $"Hermex could not bind the SMTP listener to port {configuredPort} because it is already in use. " +
                   $"Either free the port, change HermexOptions.SmtpPort, or enable HermexOptions.AutoResolvePortConflict.";
        }

        return $"Hermex could not start the SMTP listener. Ports {probedPorts[0]}-{probedPorts[^1]} are all in use. " +
               $"Change HermexOptions.SmtpPort to a free port or raise HermexOptions.MaxPortProbeAttempts.";
    }
}
