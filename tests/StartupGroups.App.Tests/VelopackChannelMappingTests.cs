using StartupGroups.App.Services;

namespace StartupGroups.App.Tests;

/// <summary>
/// Locks in the <see cref="VelopackUpdateService.ToVelopackChannel"/> contract.
/// CI scripts (release.yml, ci.yml's canary publish) and the Velopack
/// channel feed names depend on these exact strings — silent regressions
/// here would orphan users on the wrong update feed.
/// </summary>
public sealed class VelopackChannelMappingTests
{
    [Fact]
    public void Stable_MapsToNull()
    {
        // Stable rides Velopack's default channel ("win"), so we return null
        // rather than the literal "stable". Existing v0.2.x users shipped
        // without ExplicitChannel — passing "stable" here would orphan them
        // on a renamed feed they aren't subscribed to.
        VelopackUpdateService.ToVelopackChannel(UpdateChannel.Stable).Should().BeNull();
    }

    [Fact]
    public void Canary_MapsToCanaryString()
    {
        // Must match the --channel arg in ci.yml's `vpk pack` and `vpk upload`
        // steps, otherwise the published feed (releases.canary.json) and the
        // client's lookup don't agree.
        VelopackUpdateService.ToVelopackChannel(UpdateChannel.Canary).Should().Be("canary");
    }

    [Fact]
    public void EnumHasOnlyTwoMembers()
    {
        // Guard rail: re-adding Beta/Nightly silently would break the
        // "no backwards compat code" pact. If you intentionally add a
        // third channel, update this count along with ToVelopackChannel,
        // the channel pickers, and ci.yml.
        Enum.GetValues<UpdateChannel>().Should().HaveCount(2);
    }
}
