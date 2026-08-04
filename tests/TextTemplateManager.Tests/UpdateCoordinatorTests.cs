using TextTemplateManager.Services.System;
using Xunit;

namespace TextTemplateManager.Tests;

/// <summary>The update state machine. These are the paths that were previously only observable by
/// actually running the app and waiting — an unattended install firing at the wrong moment, a toast
/// going nowhere, a failed release reinstalling itself forever.
///
/// Serialised: the coordinator reads and writes the one persisted update-state file.</summary>
[Collection(nameof(UpdateCoordinatorTests))]
[CollectionDefinition(nameof(UpdateCoordinatorTests), DisableParallelization = true)]
public class UpdateCoordinatorTests : IDisposable
{
    private readonly FakeUpdateHost _host;
    private readonly UpdateCoordinator _sut;

    public UpdateCoordinatorTests()
    {
        UpdateState.Clear();
        _host = new FakeUpdateHost(TestEnvironment.NewScratchDir());
        _sut = new UpdateCoordinator(_host);
    }

    public void Dispose() => UpdateState.Clear();

    // ================= policy =================

    [Fact]
    public async Task Policy_disabled_blocks_the_check_entirely()
    {
        _host.UpdatesAllowed = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Equal(0, _host.ChecksRun);
        Assert.Equal(UpdateStatusKind.None, _host.Status);
    }

    [Fact]
    public async Task Policy_disabled_tells_the_user_only_when_they_asked()
    {
        _host.UpdatesAllowed = false;

        await _sut.RunCheckAsync(UpdateTrigger.Auto);
        Assert.Empty(_host.Messages);

        await _sut.RunCheckAsync(UpdateTrigger.User);
        Assert.Single(_host.Messages);
    }

    [Fact]
    public async Task Beta_is_requested_only_when_both_the_setting_and_policy_allow_it()
    {
        _host.AllowBetaUpdates = true;
        _host.BetaAllowed = false;
        await _sut.RunCheckAsync(UpdateTrigger.User);
        Assert.False(_host.BetaRequested);

        _host.BetaAllowed = true;
        await _sut.RunCheckAsync(UpdateTrigger.User);
        Assert.True(_host.BetaRequested);
    }

    // ================= failures are silent unless asked for =================

    [Fact]
    public async Task An_unreachable_server_is_reported_only_on_a_user_check()
    {
        _host.CheckThrows = new HttpRequestException("no network");

        await _sut.RunCheckAsync(UpdateTrigger.Auto);
        Assert.Empty(_host.Messages);

        await _sut.RunCheckAsync(UpdateTrigger.User);
        Assert.Single(_host.Messages);
    }

    [Fact]
    public async Task A_failed_download_clears_the_status_and_does_not_arm_anything()
    {
        _host.Available = FakeUpdateHost.Release();
        _host.DownloadThrows = new IOException("disk full");

        await _sut.RunCheckAsync(UpdateTrigger.User);

        Assert.Equal(UpdateStatusKind.None, _host.Status);
        Assert.Null(_sut.ReadyInstallerPath);
        Assert.Null(_sut.PendingAutoInstallPath);
    }

    [Fact]
    public async Task A_download_yielding_nothing_leaves_no_ready_state()
    {
        _host.Available = FakeUpdateHost.Release();
        _host.DownloadReturnsNull = true;

        await _sut.RunCheckAsync(UpdateTrigger.User);

        Assert.Equal(UpdateStatusKind.None, _host.Status);
        Assert.Null(_sut.ReadyInstallerPath);
    }

    [Fact]
    public async Task A_check_already_running_is_not_started_twice()
    {
        _host.Available = FakeUpdateHost.Release();
        _host.CheckGate = new TaskCompletionSource();

        var first = _sut.RunCheckAsync(UpdateTrigger.Auto);   // parked inside the check
        await _sut.RunCheckAsync(UpdateTrigger.Auto);         // must bail out on the busy guard
        _host.CheckGate.SetResult();
        await first;

        Assert.Equal(1, _host.ChecksRun);
    }

    // ================= up to date =================

    [Fact]
    public async Task Being_up_to_date_clears_state_sweeps_installers_and_dismisses_toasts()
    {
        _host.Available = FakeUpdateHost.Release();
        await _sut.RunCheckAsync(UpdateTrigger.Auto);
        Assert.NotNull(_sut.ReadyInstallerPath);

        _host.Available = null;
        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Null(_sut.ReadyInstallerPath);
        Assert.Null(_sut.KnownTag);
        Assert.Equal(UpdateStatusKind.None, _host.Status);
        Assert.Contains(null, _host.CleanCalls);        // swept with keep: null
        Assert.Equal(1, _host.NotificationsCleared);
        Assert.Null(UpdateState.Load());
    }

    // ================= auto-download off =================

    [Fact]
    public async Task With_auto_download_off_an_update_is_surfaced_but_not_fetched()
    {
        _host.AutoCheckUpdates = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Equal(0, _host.DownloadsRun);
        Assert.Equal(UpdateStatusKind.Available, _host.Status);
        Assert.Null(_sut.ReadyInstallerPath);
    }

    [Fact]
    public async Task With_auto_download_off_an_already_downloaded_installer_stays_ready()
    {
        // The regression this guards: a manual download followed by a background check used to reset the
        // state to "Available" and throw away the staged installer.
        _host.Available = FakeUpdateHost.Release();
        await _sut.RunCheckAsync(UpdateTrigger.User);        // downloads it
        Assert.Equal(UpdateStatusKind.Ready, _host.Status);

        _host.AutoCheckUpdates = false;
        await _sut.RunCheckAsync(UpdateTrigger.Auto);        // the periodic check

        Assert.Equal(UpdateStatusKind.Ready, _host.Status);
        Assert.NotNull(_sut.ReadyInstallerPath);
    }

    [Fact]
    public async Task A_user_check_downloads_even_when_auto_download_is_off()
    {
        _host.AutoCheckUpdates = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.User);

        Assert.Equal(1, _host.DownloadsRun);
    }

    // ================= announcing: dialog vs toast vs balloon =================

    [Fact]
    public async Task A_visible_window_gets_a_dialog_not_a_toast()
    {
        _host.WindowVisible = true;
        _host.WindowForeground = true;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Single(_host.Prompts);
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public async Task A_visible_but_background_window_also_flashes_the_taskbar()
    {
        _host.WindowVisible = true;
        _host.WindowForeground = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Equal(1, _host.TaskbarFlashes);
    }

    [Fact]
    public async Task A_hidden_window_gets_a_toast_not_an_invisible_dialog()
    {
        // The bug this pins down: showing a ContentDialog on a tray-hidden window left the user with
        // nothing on screen while the app waited for an answer.
        _host.WindowVisible = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Empty(_host.Prompts);
        Assert.Equal(new[] { UpdateNote.InstallReady }, _host.Toasts);
    }

    [Fact]
    public async Task When_toasts_are_unavailable_a_tray_balloon_is_used_instead()
    {
        // Toast registration genuinely fails for an unpackaged app; without this fallback a tray-only
        // session would be told about the update nowhere at all.
        _host.WindowVisible = false;
        _host.NotificationsAvailable = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Empty(_host.Toasts);
        Assert.Single(_host.Balloons);
    }

    [Fact]
    public async Task Notifications_turned_off_suppress_the_toast_and_the_balloon_alike()
    {
        // "Off" has to mean silent, not "fall back to the tray balloon" — a balloon is still a
        // notification, so leaving that path open would ignore the setting.
        _host.WindowVisible = false;
        _host.NotificationsEnabled = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Empty(_host.Toasts);
        Assert.Empty(_host.Balloons);
        Assert.Equal(UpdateStatusKind.Ready, _host.Status);   // still ready, just not announced
    }

    [Fact]
    public async Task Notifications_turned_off_do_not_suppress_the_dialog_on_a_visible_window()
    {
        // The setting governs notifications, not the in-app prompt.
        _host.WindowVisible = true;
        _host.WindowForeground = true;
        _host.NotificationsEnabled = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Single(_host.Prompts);
    }

    [Fact]
    public async Task Notifications_turned_off_silence_the_installing_toast()
    {
        _host.NotificationsEnabled = false;
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();

        await _sut.TryAutoInstallAsync();

        Assert.Empty(_host.InstallingToasts);
        Assert.Single(_host.Launches);   // the install itself still happens
    }

    [Fact]
    public async Task A_passive_detection_raises_no_dialog_on_a_visible_window()
    {
        // Auto-download off: the top-right button already says it, so a modal would just be noise.
        _host.AutoCheckUpdates = false;
        _host.WindowVisible = true;
        _host.WindowForeground = true;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Empty(_host.Prompts);
    }

    [Fact]
    public async Task The_same_release_is_announced_only_once()
    {
        _host.WindowVisible = false;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);
        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Single(_host.Toasts);
    }

    [Fact]
    public async Task A_stable_release_is_announced_even_though_its_beta_shares_the_version_number()
    {
        // Tag comparison, not numeric: v1.3.2-beta and v1.3.2 are the same Version, so a numeric check
        // treated the stable follow-up as already seen and silently never announced it.
        _host.WindowVisible = false;
        _host.Available = FakeUpdateHost.Release("v1.3.2-beta", "TextTemplateManager-Setup-1.3.2-beta.exe");
        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        _host.Available = FakeUpdateHost.Release("v1.3.2");
        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.Equal(2, _host.Toasts.Count);
    }

    [Fact]
    public async Task A_toast_triggered_download_always_reports_back()
    {
        // "Download now" arrives when the release is already known, so a first-seen-only rule would
        // download and then show the user nothing at all.
        _host.AutoCheckUpdates = false;
        _host.WindowVisible = false;
        _host.Available = FakeUpdateHost.Release();
        await _sut.RunCheckAsync(UpdateTrigger.Auto);       // detected; one toast
        _host.Toasts.Clear();

        await _sut.OnNotificationActionAsync("download");

        Assert.Equal(1, _host.DownloadsRun);
        Assert.Equal(new[] { UpdateNote.InstallReady }, _host.Toasts);
    }

    // ================= notification actions =================

    [Fact]
    public async Task The_install_button_on_a_toast_installs_the_staged_release()
    {
        _host.WindowVisible = false;
        _host.Available = FakeUpdateHost.Release();
        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        await _sut.OnNotificationActionAsync("install");

        Assert.Single(_host.Launches);
        Assert.True(_host.Launches[0].Hidden);      // hidden window -> come back to the tray
        Assert.Equal(1, _host.Shutdowns);
    }

    [Fact]
    public async Task Installing_from_a_toast_after_the_installer_vanished_re_checks_instead()
    {
        await _sut.OnNotificationActionAsync("install");

        Assert.Empty(_host.Launches);
        Assert.Equal(1, _host.ChecksRun);
    }

    [Fact]
    public async Task The_open_button_surfaces_the_window()
    {
        await _sut.OnNotificationActionAsync("open");

        Assert.Equal(1, _host.WindowSurfaced);
    }

    // ================= unattended install: the safe moment =================

    private async Task ArmAutoInstallAsync()
    {
        _host.AutoInstallUpdates = true;
        _host.Available = FakeUpdateHost.Release();
        _host.IdleSeconds = 0;                       // user is active, so nothing fires yet
        await _sut.RunCheckAsync(UpdateTrigger.Auto);
    }

    [Fact]
    public async Task Auto_install_arms_but_waits_while_the_user_is_active()
    {
        await ArmAutoInstallAsync();

        Assert.NotNull(_sut.PendingAutoInstallPath);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task Auto_install_fires_once_the_session_is_really_idle()
    {
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();

        await _sut.TryAutoInstallAsync();

        Assert.Single(_host.Launches);
        Assert.Equal(1, _host.Shutdowns);
        Assert.Single(_host.InstallingToasts);
    }

    [Fact]
    public async Task Background_alone_is_not_enough_to_install()
    {
        // The whole point of the idle gate: this app lives in the tray, so "not foreground" is its normal
        // state and would mean installing while the user works in another app.
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();
        _host.IdleSeconds = UpdateCoordinator.SafeIdleSeconds - 1;

        await _sut.TryAutoInstallAsync();

        Assert.Empty(_host.Launches);
        Assert.NotNull(_sut.PendingAutoInstallPath);     // still armed for later
    }

    [Fact]
    public async Task An_open_quick_paste_window_blocks_the_install()
    {
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();
        _host.PasteWindowVisible = true;

        await _sut.TryAutoInstallAsync();

        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task The_window_being_in_use_blocks_the_install()
    {
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();
        _host.WindowForeground = true;

        await _sut.TryAutoInstallAsync();

        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task A_sync_write_in_progress_blocks_the_install()
    {
        // Ending the process mid sync-write is how a file ends up truncated or duplicated as a cloud
        // conflict copy.
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();
        _host.WritesQuiet = false;

        await _sut.TryAutoInstallAsync();

        Assert.Empty(_host.Launches);
        Assert.NotNull(_sut.PendingAutoInstallPath);
    }

    [Fact]
    public async Task Turning_auto_install_off_cancels_an_armed_install()
    {
        await ArmAutoInstallAsync();
        Assert.NotNull(_sut.PendingAutoInstallPath);

        _host.AutoInstallUpdates = false;
        await _sut.OnAutoInstallSettingChangedAsync();
        _host.AtSafeMoment();
        await _sut.TryAutoInstallAsync();

        Assert.Null(_sut.PendingAutoInstallPath);
        Assert.Empty(_host.Launches);
        Assert.Null(UpdateState.Load());
    }

    [Fact]
    public async Task Turning_auto_install_on_picks_up_an_already_downloaded_update()
    {
        _host.Available = FakeUpdateHost.Release();
        _host.WindowVisible = false;
        await _sut.RunCheckAsync(UpdateTrigger.Auto);      // downloaded, awaiting confirmation
        Assert.Null(_sut.PendingAutoInstallPath);

        _host.AutoInstallUpdates = true;
        _host.AtSafeMoment();
        await _sut.OnAutoInstallSettingChangedAsync();

        Assert.Single(_host.Launches);
    }

    [Fact]
    public async Task An_installer_deleted_after_arming_disarms_instead_of_launching()
    {
        await ArmAutoInstallAsync();
        File.Delete(_sut.PendingAutoInstallPath!);
        _host.AtSafeMoment();

        await _sut.TryAutoInstallAsync();

        Assert.Empty(_host.Launches);
        Assert.Null(_sut.PendingAutoInstallPath);
    }

    [Fact]
    public async Task Data_is_persisted_and_writes_drained_before_the_installer_runs()
    {
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();

        await _sut.TryAutoInstallAsync();

        Assert.Equal(1, _host.PersistCalls);
        Assert.Equal(1, _host.DrainCalls);
    }

    // ================= the retry cap =================

    [Fact]
    public async Task Each_unattended_attempt_is_counted_before_the_installer_launches()
    {
        // Counted beforehand because the app is gone by the time a failure could be observed.
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();

        await _sut.TryAutoInstallAsync();

        Assert.Equal(1, UpdateState.Load()!.Attempts);
    }

    [Fact]
    public async Task A_release_that_never_applies_stops_being_retried_after_the_cap()
    {
        _host.AutoInstallUpdates = true;
        _host.Available = FakeUpdateHost.Release();

        // Simulate repeated launches that leave the installed version unchanged.
        for (int i = 0; i < UpdateState.MaxAttempts; i++)
        {
            _host.IdleSeconds = 0;
            await _sut.RunCheckAsync(UpdateTrigger.Auto);
            _host.AtSafeMoment();
            await _sut.TryAutoInstallAsync();
        }
        int launchesBeforeCap = _host.Launches.Count;

        _host.IdleSeconds = 0;
        await _sut.RunCheckAsync(UpdateTrigger.Auto);
        _host.AtSafeMoment();
        await _sut.TryAutoInstallAsync();

        Assert.Equal(UpdateState.MaxAttempts, launchesBeforeCap);
        Assert.Equal(launchesBeforeCap, _host.Launches.Count);   // no further attempt
        Assert.True(_sut.AutoInstallBlocked);
    }

    [Fact]
    public async Task Hitting_the_cap_hands_the_update_to_the_user()
    {
        _host.WindowVisible = false;
        new UpdateState { Tag = "v1.3.2", FromVersion = "1.3.1", Attempts = UpdateState.MaxAttempts }.Save();
        _host.AutoInstallUpdates = true;
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.True(_sut.AutoInstallBlocked);
        Assert.Equal(new[] { UpdateNote.AutoInstallFailed }, _host.Toasts);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task A_restart_does_not_grant_a_capped_release_another_attempt()
    {
        // The cap is read off disk precisely so a fresh process can't quietly reset it.
        new UpdateState { Tag = "v1.3.2", FromVersion = "1.3.1", Attempts = UpdateState.MaxAttempts }.Save();
        _host.AutoInstallUpdates = true;
        _host.Available = FakeUpdateHost.Release();

        var restarted = new UpdateCoordinator(_host);
        await restarted.RunCheckAsync(UpdateTrigger.Auto);
        _host.AtSafeMoment();
        await restarted.TryAutoInstallAsync();

        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task A_failing_installer_launch_blocks_further_attempts_and_notifies()
    {
        _host.WindowVisible = false;
        _host.InstallerLaunches = false;
        await ArmAutoInstallAsync();
        _host.AtSafeMoment();
        _host.Toasts.Clear();

        await _sut.TryAutoInstallAsync();

        Assert.Equal(0, _host.Shutdowns);
        Assert.True(_sut.AutoInstallBlocked);
        Assert.Contains(UpdateNote.AutoInstallFailed, _host.Toasts);
        Assert.Equal(UpdateStatusKind.Ready, _host.Status);
    }

    // ================= resuming at next launch =================

    [Fact]
    public async Task An_install_deferred_from_a_previous_session_runs_at_launch()
    {
        _host.AutoInstallUpdates = true;
        string asset = "TextTemplateManager-Setup-1.3.2.exe";
        Directory.CreateDirectory(_host.InstallerDir);
        File.WriteAllText(Path.Combine(_host.InstallerDir, asset), "installer");
        new UpdateState { Tag = "v1.3.2", Asset = asset, FromVersion = "1.3.1", Attempts = 0 }.Save();

        await _sut.ResumeDeferredInstallAsync();

        Assert.Single(_host.Launches);
        Assert.Equal(1, _host.Shutdowns);
        Assert.Equal(0, _host.PersistCalls);   // nothing to flush at launch
    }

    [Fact]
    public async Task A_hidden_autostart_resumes_hidden_and_a_user_launch_resumes_visible()
    {
        _host.AutoInstallUpdates = true;
        string asset = "TextTemplateManager-Setup-1.3.2.exe";
        Directory.CreateDirectory(_host.InstallerDir);
        File.WriteAllText(Path.Combine(_host.InstallerDir, asset), "installer");
        new UpdateState { Tag = "v1.3.2", Asset = asset, FromVersion = "1.3.1" }.Save();

        _sut.LaunchedHidden = true;
        await _sut.ResumeDeferredInstallAsync();
        Assert.True(_host.Launches[0].Hidden);

        UpdateState.Clear();
        File.WriteAllText(Path.Combine(_host.InstallerDir, asset), "installer");
        new UpdateState { Tag = "v1.3.2", Asset = asset, FromVersion = "1.3.1" }.Save();
        var visible = new UpdateCoordinator(_host) { LaunchedHidden = false };
        await visible.ResumeDeferredInstallAsync();

        Assert.False(_host.Launches[1].Hidden);
    }

    [Fact]
    public async Task A_successful_update_is_detected_at_launch_and_cleans_up()
    {
        // The installed version no longer matches what the attempt started from, so it applied.
        new UpdateState { Tag = "v1.3.2", Asset = "x.exe", FromVersion = "1.3.1", Attempts = 1 }.Save();
        _host.InstalledVersion = "1.3.2";

        await _sut.ResumeDeferredInstallAsync();

        Assert.Null(UpdateState.Load());
        Assert.Contains(null, _host.CleanCalls);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task A_capped_release_is_not_reinstalled_at_launch()
    {
        _host.AutoInstallUpdates = true;
        new UpdateState
        {
            Tag = "v1.3.2",
            Asset = "x.exe",
            FromVersion = "1.3.1",
            Attempts = UpdateState.MaxAttempts,
        }.Save();

        await _sut.ResumeDeferredInstallAsync();

        Assert.Empty(_host.Launches);
        Assert.True(_sut.AutoInstallBlocked);
    }

    [Fact]
    public async Task Nothing_resumes_when_auto_install_is_off()
    {
        _host.AutoInstallUpdates = false;
        new UpdateState { Tag = "v1.3.2", Asset = "x.exe", FromVersion = "1.3.1" }.Save();

        await _sut.ResumeDeferredInstallAsync();

        Assert.Empty(_host.Launches);
    }

    // ================= user-driven install =================

    [Fact]
    public async Task A_user_check_offers_the_install_and_honours_declining_it()
    {
        _host.Available = FakeUpdateHost.Release();
        _host.PromptAnswer = false;

        await _sut.RunCheckAsync(UpdateTrigger.User);

        Assert.Single(_host.Prompts);
        Assert.Empty(_host.Launches);
        Assert.NotNull(_sut.ReadyInstallerPath);      // still staged for later
    }

    [Fact]
    public async Task Accepting_the_prompt_installs_and_relaunches_visibly()
    {
        _host.Available = FakeUpdateHost.Release();
        _host.PromptAnswer = true;

        await _sut.RunCheckAsync(UpdateTrigger.User);

        Assert.Single(_host.Launches);
        Assert.False(_host.Launches[0].Hidden);       // the user asked for it; bring the window back
        Assert.Equal(1, _host.Shutdowns);
    }

    [Fact]
    public async Task A_user_install_of_a_vanished_installer_says_so_instead_of_failing_silently()
    {
        _host.Available = FakeUpdateHost.Release();
        await _sut.RunCheckAsync(UpdateTrigger.Auto);
        string staged = _sut.ReadyInstallerPath!;
        File.Delete(staged);

        await _sut.InstallAsync(staged, unattended: false, flush: true, relaunchHidden: false);

        Assert.Empty(_host.Launches);
        Assert.Single(_host.Messages);
        Assert.Equal(UpdateStatusKind.None, _host.Status);
    }

    [Fact]
    public async Task The_busy_flag_is_raised_during_a_check_and_cleared_afterwards()
    {
        _host.Available = FakeUpdateHost.Release();

        await _sut.RunCheckAsync(UpdateTrigger.Auto);

        Assert.False(_sut.Busy);
        Assert.False(_host.BusySet);
    }

    [Fact]
    public async Task The_updating_label_replaces_the_download_label_while_installing()
    {
        _host.Available = FakeUpdateHost.Release();
        _host.PromptAnswer = true;

        await _sut.RunCheckAsync(UpdateTrigger.User);

        Assert.Equal("Updating…", _host.BusyText);
    }
}
