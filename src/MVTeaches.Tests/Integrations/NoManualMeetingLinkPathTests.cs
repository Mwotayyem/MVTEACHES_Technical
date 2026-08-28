using MVTeaches.Application.Integrations;
using MVTeaches.Domain.Integrations;
using Xunit;

namespace MVTeaches.Tests.Integrations;

/// <summary>
/// Owner clarification (2026-08-29): "Teachers must never manually copy and
/// paste meeting links into MVTeaches. Meetings must be created and managed
/// by the application through the teacher's authorized provider account" —
/// and "Do not silently use a centre account, another teacher's connection,
/// or a manually entered meeting URL."
///
/// A behavioural test cannot prove the ABSENCE of a feature, so this
/// asserts it structurally instead: no surface anywhere accepts a
/// caller-supplied meeting URL or external meeting id, and no configuration
/// carries a centre-level Zoom account. These are the exact shapes the
/// superseded design had, so a regression toward it would fail here.
/// </summary>
public class NoManualMeetingLinkPathTests
{
    [Fact]
    public void No_provisioning_or_connection_api_accepts_a_caller_supplied_meeting_url()
    {
        var surfaces = new[] { typeof(IMeetingProvisioningService), typeof(ITeacherMeetingConnectionService) };

        foreach (var surface in surfaces)
        {
            foreach (var method in surface.GetMethods())
            {
                foreach (var parameter in method.GetParameters())
                {
                    var name = parameter.Name ?? string.Empty;
                    Assert.False(
                        name.Contains("meetingUrl", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("joinUrl", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("startUrl", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("externalMeetingId", StringComparison.OrdinalIgnoreCase),
                        $"{surface.Name}.{method.Name} accepts '{name}' — a manual meeting-link path.");
                }
            }
        }
    }

    [Fact]
    public void Zoom_configuration_carries_no_centre_level_account_id()
    {
        // The superseded Server-to-Server design had an AccountId here. Its
        // reappearance would mean the centre, not the teacher, owns the
        // meetings again.
        var properties = typeof(Infrastructure.Integrations.Zoom.ZoomOptions)
            .GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("AccountId", properties);
        Assert.Contains("ClientId", properties);
        Assert.Contains("RedirectUri", properties);
    }

    [Fact]
    public void The_provisioned_meeting_entity_exposes_no_settable_url_for_a_caller()
    {
        // JoinUrl is written only through MarkReady, from a provider handle —
        // there is no public setter a page or service could assign to.
        var joinUrl = typeof(ProvisionedMeeting).GetProperty(nameof(ProvisionedMeeting.JoinUrl))!;

        Assert.True(joinUrl.CanRead);
        Assert.False(joinUrl.SetMethod?.IsPublic ?? false);
    }

    [Fact]
    public void Both_supported_providers_and_no_others_are_modelled()
    {
        // Owner clarification (2026-08-29 follow-up): "Do not add more
        // meeting providers." The abstraction stays provider-neutral, but
        // the supported set is exactly Zoom and Google Meet.
        var providers = Enum.GetValues<VideoProviderType>();

        Assert.Equal(2, providers.Length);
        Assert.Contains(VideoProviderType.Zoom, providers);
        Assert.Contains(VideoProviderType.GoogleMeet, providers);
    }
}
