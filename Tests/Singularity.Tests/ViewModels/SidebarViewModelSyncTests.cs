using Singularity.Services;
using Singularity.ViewModels;
using Xunit;

namespace Singularity.Tests.ViewModels;

public class SidebarViewModelSyncTests
{
    [Fact]
    public void RightPanelPlayerContent_SetsActiveTabToPlayer()
    {
        var rightPanel = new RightPanelService();
        var playerVm = CreateUninitializedPlayerVm();
        using var sut = new SidebarViewModel(rightPanel, playerVm, CreateUninitializedNotificationCenter());

        rightPanel.OpenPanel(playerVm, "NOW PLAYING", "🎵");

        Assert.Equal(SidebarTab.Player, sut.ActiveTab);
        Assert.True(sut.IsPlayerTab);
    }

    [Fact]
    public void RightPanelNotificationContent_SetsActiveTabToNotifications_NotInspector()
    {
        // Regression test: NotificationCenterService previously matched none of the explicit
        // vm-type checks in SidebarViewModel's CurrentPanelVm subscription, so it fell into the
        // generic "anything else" branch and set ActiveTab to Inspector.
        var rightPanel = new RightPanelService();
        var notificationCenter = CreateUninitializedNotificationCenter();
        using var sut = new SidebarViewModel(rightPanel, CreateUninitializedPlayerVm(), notificationCenter);

        rightPanel.OpenPanel(notificationCenter, "NOTIFICATIONS", "🔔");

        Assert.Equal(SidebarTab.Notifications, sut.ActiveTab);
        Assert.True(sut.IsNotificationsTab);
        Assert.False(sut.IsInspectorTab);
        Assert.Equal(2, sut.ActiveTabIndex);
    }

    [Fact]
    public void OtherContent_SetsActiveTabToInspector_AndCanBeRestored()
    {
        var rightPanel = new RightPanelService();
        var playerVm = CreateUninitializedPlayerVm();
        using var sut = new SidebarViewModel(rightPanel, playerVm, CreateUninitializedNotificationCenter());
        var inspected = new object();

        rightPanel.OpenPanel(inspected, "TRACK INSPECTOR", "🔬");
        Assert.Equal(SidebarTab.Inspector, sut.ActiveTab);

        rightPanel.OpenPanel(playerVm, "NOW PLAYING", "🎵");
        sut.SwitchToInspectorCommand.Execute().Subscribe();

        Assert.Equal(SidebarTab.Inspector, sut.ActiveTab);
        Assert.Same(inspected, sut.CurrentContent);
    }

    [Fact]
    public void ActiveTabIndex_RoundTripsThroughAllTabs()
    {
        var rightPanel = new RightPanelService();
        using var sut = new SidebarViewModel(rightPanel, CreateUninitializedPlayerVm(), CreateUninitializedNotificationCenter());

        foreach (var (index, tab) in new[]
                 {
                     (0, SidebarTab.Inspector), (1, SidebarTab.Player), (2, SidebarTab.Notifications),
                 })
        {
            sut.ActiveTabIndex = index;
            Assert.Equal(tab, sut.ActiveTab);
            Assert.Equal(index, sut.ActiveTabIndex);
        }
    }

    private static PlayerViewModel CreateUninitializedPlayerVm()
        => (PlayerViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PlayerViewModel));

    private static NotificationCenterService CreateUninitializedNotificationCenter()
        => (NotificationCenterService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(NotificationCenterService));
}
