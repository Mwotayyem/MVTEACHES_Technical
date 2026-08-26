namespace MVTeaches.Application.Integrations;

/// <summary>
/// Thrown by an integration boundary when the real provider hasn't been
/// configured yet (no credentials, no account) — §5/§29 of the master
/// engineering prompt: "never pretend a mock is a production integration".
/// Callers (background jobs, controllers) must catch this and record a
/// visible, actionable failure — never swallow it silently.
/// </summary>
public class IntegrationNotConfiguredException : Exception
{
    public string IntegrationName { get; }

    public IntegrationNotConfiguredException(string integrationName, string detail)
        : base($"{integrationName} is not configured: {detail}")
    {
        IntegrationName = integrationName;
    }
}
