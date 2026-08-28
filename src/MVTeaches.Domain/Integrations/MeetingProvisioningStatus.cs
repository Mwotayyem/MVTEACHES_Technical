namespace MVTeaches.Domain.Integrations;

/// <summary>Owner clarification (2026-08-29): "store a clear operational
/// status such as unconfigured, provisioning, ready, failed, disconnected,
/// or capability-blocked" — transcribed exactly, plus two terminal states
/// this codebase's own workflows need: <see cref="Cancelled"/> for a
/// centre-cancelled session's meeting, and <see cref="Orphaned"/> for a
/// meeting left behind by a teacher reassignment whose old connection was
/// revoked before cleanup could run (flagged for admin action, never
/// silently linked to the new teacher).</summary>
public enum MeetingProvisioningStatus
{
    Unconfigured,
    Provisioning,
    Ready,
    Failed,
    Disconnected,
    CapabilityBlocked,
    Cancelled,
    Orphaned,
}
