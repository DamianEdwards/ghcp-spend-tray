using GHCPSpendTray.App.Native;
using GHCPSpendTray.App.Platform;
using GHCPSpendTray.App.UI;
using GHCPSpendTray.Core;
using Microsoft.UI.Reactor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace GHCPSpendTray.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool packageSmoke = args.Contains("--package-smoke-test", StringComparer.Ordinal);
        bool smoke = packageSmoke || args.Contains("--smoke-test", StringComparer.Ordinal);
        bool emptyDemo = args.Contains("--demo-empty", StringComparer.Ordinal);
        bool unlimitedDemo = args.Contains("--demo-unlimited", StringComparer.Ordinal);
        bool usageStatesDemo = args.Contains("--demo-usage-states", StringComparer.Ordinal);
        bool demo = smoke || emptyDemo || unlimitedDemo || usageStatesDemo || args.Contains("--demo", StringComparer.Ordinal);
        bool verifyTelemetry = args.Contains("--verify-telemetry", StringComparer.Ordinal);
        string[] bootstrapArgs = args.Where(a => a is not "--smoke-test" and not "--package-smoke-test" and
            not "--demo" and not "--demo-empty" and not "--demo-unlimited" and not "--demo-usage-states" and not "--verify-telemetry").ToArray();
        try
        {
            if (verifyTelemetry && (!demo || smoke || !bootstrapArgs.Contains("--portable", StringComparer.Ordinal)))
                throw new ArgumentException("--verify-telemetry requires --portable --data-dir <isolated-directory> and --demo (or a demo variant), and cannot be used with smoke tests.");
            if (verifyTelemetry && string.IsNullOrWhiteSpace(TelemetryConfiguration.ConnectionString))
                throw new ArgumentException("Telemetry verification requires a build with GHCP_APPLICATIONINSIGHTS_CONNECTION_STRING configured.");
            if (packageSmoke && (!PackageContext.IsPackaged ||
                Windows.ApplicationModel.Package.Current.Id.Name != "GHCPSpendTray.Development" ||
                bootstrapArgs.Contains("--portable", StringComparer.Ordinal)))
                throw new ArgumentException("Packaged smoke testing requires the isolated GHCPSpendTray.Development package.");
            if (demo && !packageSmoke && !bootstrapArgs.Contains("--portable", StringComparer.Ordinal))
                throw new ArgumentException("Demo and smoke modes require --portable --data-dir <isolated-directory>.");
            using var runtime = Bootstrap.Start(bootstrapArgs);
            if (runtime is null) return 0;
            Diagnostics.Initialize(runtime.DataDirectory);
            if (smoke)
            {
                File.WriteAllText(Path.Combine(runtime.DataDirectory, "native-smoke-progress.txt"), "");
                RecordSmokePhase(runtime.DataDirectory, "Bootstrap complete; initializing Reactor");
            }
            runtime.Diagnostic += ex => Diagnostics.Record($"Instance coordination failed ({ex.GetType().Name}).");
            var smokeTime = smoke ? new SmokeTimeProvider() : null;
            var smokeUpdates = smoke ? new SmokeStoreUpdates() : null;
            using IApplicationController controller = demo ? new DemoController(runtime.DataDirectory, emptyDemo, smokeTime, unlimitedDemo,
                telemetry: new(UI.UI.Version, "demo", TrackedAllowed: verifyTelemetry, SyntheticVerification: verifyTelemetry),
                usageStates: usageStatesDemo) :
                new ApplicationController(runtime.DataDirectory, runtime.IsPortable,
                    telemetry: new(UI.UI.Version, PackageContext.InstallationSource));
            int smokeExit = 0;
            ReactorShell? shell = null;
            ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
            try
            {
                ReactorApp.Run(context =>
                {
                    if (smoke) RecordSmokePhase(runtime.DataDirectory, "Reactor initialized; creating tray shell");
                    shell = new ReactorShell(controller, storeUpdates: !demo, updateService: smokeUpdates);
                    Application.Current.UnhandledException += (_, e) =>
                    {
                        Diagnostics.Record($"Reactor dispatch failed ({e.Exception.GetType().Name}).");
                        if (smoke)
                        {
                            smokeExit = 1;
                            File.WriteAllText(Path.Combine(runtime.DataDirectory, "native-smoke-result.txt"),
                                $"FAIL: Reactor dispatch: {e.Exception.GetType().Name}\n");
                            ReactorApp.Exit(1);
                        }
                        else shell.Session.SetError("A user-interface operation failed. See the diagnostic log.");
                        e.Handled = true;
                    };
                    runtime.RegisterActivationCallback(() => shell.Session.Post(shell.ShowFlyout));
                    shell.Start(!smoke && !runtime.IsStartup);
                    runtime.SignalReady();
                    if (smoke) RecordSmokePhase(runtime.DataDirectory, "Tray shell ready; starting synthetic smoke");
                    if (smoke) _ = RunSmokeAsync(shell, runtime.DataDirectory, smokeTime!, smokeUpdates!, code => smokeExit = code);
                });
            }
            finally { shell?.Dispose(); }
            return smokeExit;
        }
        catch (Exception ex)
        {
            Diagnostics.Record($"Application startup failed ({ex.GetType().Name}).");
            // Bootstrap errors are locally produced and contain no authentication payloads.
            if (!smoke)
                Win32.MessageBox(0, ex.Message, "GHCPSpendTray could not start", Win32.MB_ICONERROR);
            return 1;
        }
    }

    private static void RecordSmokePhase(string directory, string phase) =>
        File.AppendAllText(Path.Combine(directory, "native-smoke-progress.txt"),
            $"{DateTimeOffset.UtcNow:O} {phase}{Environment.NewLine}");

    private static async Task RunSmokeAsync(ReactorShell shell, string directory, SmokeTimeProvider time,
        SmokeStoreUpdates updates, Action<int> setExit)
    {
        try
        {
            if (PackageContext.IsPackaged)
            {
                RecordSmokePhase(directory, "Checking package startup task and data directory");
                var startup = await StartupRegistration.CreateAsync();
                if (startup.Enabled)
                    throw new InvalidOperationException("Fresh development package unexpectedly enables login startup.");
                if (!InstallationPaths.SamePath(directory, PackageContext.DataDirectory))
                    throw new InvalidOperationException("Packaged smoke test did not use package-local storage.");
            }
            RecordSmokePhase(directory, "Testing tray mouse/keyboard activation and settings navigation");
            await WaitForUI(shell, "controller initialization", () => shell.Session.Initialized && !shell.Session.Busy);
            await OnUI(shell, () =>
            {
                if (shell.Flyout is not null) throw new InvalidOperationException("Hidden startup unexpectedly opened a window.");
                SendTraySelection(shell, 0x201);
                SendTraySelection(shell, Win32.NIN_SELECT);
                SendTraySelection(shell, 0x202);
            });
            await WaitForFlyoutVisibility(shell, true, "after mouse single-click");
            await OnUI(shell, () =>
            {
                AssertFlyoutVisible(shell, "after mouse single-click");
                SendTraySelection(shell, Win32.NIN_SELECT);
            });
            await WaitForFlyoutVisibility(shell, false, "after mouse click on open flyout");
            await OnUI(shell, () => SendTraySelection(shell, Win32.NIN_KEYSELECT));
            await WaitForFlyoutVisibility(shell, true, "after keyboard selection");
            await OnUI(shell, () => SendTraySelection(shell, Win32.NIN_KEYSELECT));
            await WaitForFlyoutVisibility(shell, false, "after keyboard selection toggled closed");
            await OnUI(shell, () =>
            {
                SendTraySelection(shell, Win32.NIN_SELECT);
                SendTraySelection(shell, 0x203);
                SendTraySelection(shell, Win32.NIN_SELECT);
                Win32.SendMessage(shell.TrayHandle, 0x113, 1, 0);
            });
            await OnUI(shell, () =>
            {
                if (Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0 || shell.SettingsWindow is not null)
                    throw new InvalidOperationException("Double-click must remain two ordinary toggles, without opening settings.");
                SendTraySelection(shell, Win32.NIN_KEYSELECT);
            });
            await WaitForFlyoutVisibility(shell, true, "before opening settings from the gear");
            await WaitForFlyoutUI(shell, "settings gear");
            await InvokeButtonAsync(shell, "OpenSettings", flyout: true);
            await WaitForSettingsUI(shell, "usage settings from the gear");
            bool empty = shell.Session.Dashboard.Accounts.Count == 0;
            if (!empty)
            {
                await OnUI(shell, () =>
                {
                    string key = shell.Session.Dashboard.Accounts[0].Key;
                    if (Find(shell.SettingsWindow!, key + "_DailyConsumption") is null ||
                        Find(shell.Flyout!, key + "_CompactDailyConsumption") is null)
                        throw new InvalidOperationException("Synthetic daily consumption is missing from Usage or the popup.");
                    if (shell.Session.Dashboard.Accounts.Count > 1 &&
                        Find(shell.SettingsWindow!, shell.Session.Dashboard.Accounts[1].Key + "_DailyConsumptionStatus") is null)
                        throw new InvalidOperationException("Connected daily usage without reports did not explain its missing data.");
                    if (Find(shell.SettingsWindow!, key + "_DailyPlot") is not Canvas plot ||
                        !plot.Children.OfType<TextBlock>().Any(t => t.Text == "USD"))
                        throw new InvalidOperationException("The daily chart has no USD axis.");
                    var rectangles = plot.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().ToArray();
                    var accent = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
                    if (!rectangles.Any(r => r.Fill is SolidColorBrush fill && fill.Color == accent && r.Opacity == 1) ||
                        !rectangles.Any(r => r.Fill is SolidColorBrush fill && fill.Color == accent && Math.Abs(r.Opacity - 0.2) < 0.0001) ||
                        !rectangles.Any(r => r.StrokeDashArray is { Count: > 0 }))
                        throw new InvalidOperationException("The daily chart is missing accent-color columns or dashed translucent estimates.");
                });
                for (int update = 0; update < 2; update++)
                {
                    await OnUI(shell, shell.Session.Notify);
                    await WaitForSettingsUI(shell, "daily chart after dashboard rerender");
                    await OnUI(shell, () =>
                    {
                        string key = shell.Session.Dashboard.Accounts[0].Key;
                        foreach (var (window, suffix) in new[]
                        {
                            (shell.SettingsWindow!, "_DailyPlot"), (shell.Flyout!, "_CompactDailyPlot")
                        })
                        {
                            if (Find(window, key + suffix) is not Canvas updatedPlot ||
                                !updatedPlot.Children.OfType<TextBlock>().Any(t => t.Text == "USD") ||
                                !updatedPlot.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().Any())
                                throw new InvalidOperationException("Daily chart drawing was cleared by a dashboard rerender.");
                        }
                    });
                }
            }
            if (!empty)
            {
                RecordSmokePhase(directory, "Testing first-account popup fit with daily usage and forecast risk states");
                await SmokeFirstAccountPopupAsync(shell);
            }
            await OnUI(shell, () =>
            {
                if (Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0)
                    throw new InvalidOperationException("Settings did not dismiss the flyout.");
                if (shell.Session.Page != SettingsPage.Usage || Find(shell.SettingsWindow!, "UsagePage") is null)
                    throw new InvalidOperationException("Settings did not open on the usage page.");
                AssertBackAccelerators(shell.SettingsWindow!);
                if (shell.Session.Dashboard.Accounts.Count == 0)
                {
                    if (Find(shell.SettingsWindow!, "UsageAddAccount") is not Button { IsEnabled: true } add ||
                        Find(shell.SettingsWindow!, "UsageEmptyMessage") is not TextBlock { Text: "Add an account to see your usage" })
                        throw new InvalidOperationException("Usage empty state did not offer account setup.");
                    if (Find(shell.SettingsWindow!, "UsageTotal") is not null ||
                        Find(shell.SettingsWindow!, "TrayUsageDetails") is not null ||
                        Find(shell.SettingsWindow!, "RefreshUsage") is not null)
                        throw new InvalidOperationException("Empty usage showed consumption details or refresh controls.");
                }
                else
                {
                    if (Find(shell.SettingsWindow!, "UsageTotal") is not TextBlock { Text: "$42.75" })
                        throw new InvalidOperationException("Usage summary did not render.");
                    if (Find(shell.SettingsWindow!, "RefreshUsage") is not Button refresh || !refresh.IsEnabled)
                        throw new InvalidOperationException("Usage refresh control did not render.");
                }
                if (Find(shell.SettingsWindow!, "AccountHistory") is not null)
                    throw new InvalidOperationException("Usage page still displayed sampled spending history.");
            });
            await InvokeButtonAsync(shell, empty ? "UsageAddAccount" : "RefreshUsage");
            await WaitForSettingsUI(shell, empty ? "empty Usage account setup" : "Usage refresh");
            await OnUI(shell, () =>
            {
                if (empty)
                {
                    if (shell.Session.Page != SettingsPage.Accounts || !shell.Session.ShowAddForm ||
                        Find(shell.SettingsWindow!, "AccountOnboarding") is null)
                        throw new InvalidOperationException("Usage empty-state action did not open account setup.");
                    shell.Session.TryGoBack();
                    shell.Session.Navigate(SettingsPage.Usage);
                }
            });
            await WaitForSettingsUI(shell, "Usage after empty-state navigation");
            if (!empty) await SmokeUsageOverviewAsync(shell);
            RecordSmokePhase(directory, "Testing Usage disclosures");
            await SmokeUsageDisclosureAsync(shell);
            await SmokePeriodEstimateAsync(shell, time, phase => RecordSmokePhase(directory, phase));
            await SmokeFormValidationAsync(shell);
            await SmokeDirtyFormsAsync(shell);
            RecordSmokePhase(directory, "Testing flyout focus transitions and account onboarding");
            await OnUI(shell, () => SendTraySelection(shell, Win32.NIN_KEYSELECT));
            await WaitForFlyoutVisibility(shell, true, "after opening from settings");
            await WaitForFlyoutUI(shell, "flyout from settings");
            await OnUI(shell, () =>
            {
                AssertFlyoutVisible(shell, "after opening from settings");
                // Queue a deactivation, then reactivate before its deferred dismissal runs.
                shell.SettingsWindow!.Activate();
                shell.ShowFlyout();
            });
            await WaitForFlyoutUI(shell, "rapid focus transition");
            await OnUI(shell, () =>
            {
                AssertFlyoutVisible(shell, "after rapid focus transition");
                if (!shell.Session.Initialized) throw new InvalidOperationException("Controller did not initialize.");
                var flyout = shell.Flyout ?? throw new InvalidOperationException("Flyout did not open.");
                if (shell.Session.Dashboard.Accounts.Count == 0)
                {
                    if (Find(flyout, "AddFirstAccount") is not Button connect)
                        throw new InvalidOperationException("Empty-state connect action did not render.");
                }
                else if (Find(flyout, "TotalConsumption") is not TextBlock { Text: "$42.75" })
                    throw new InvalidOperationException("Reactor cost display did not render.");
                else
                {
                    if (Find(flyout, "AccountAvatar-" + shell.Session.Dashboard.Accounts[0].Key) is not PersonPicture)
                        throw new InvalidOperationException("Flyout account picture did not render.");
                    if (Find(flyout, "OpenSettings") is not Button settings) throw new InvalidOperationException("Settings gear is missing.");
                }
            });
            await InvokeButtonAsync(shell, empty ? "AddFirstAccount" : "OpenSettings", flyout: true);
            await WaitForSettingsUI(shell, "flyout settings/account setup action");
            await OnUI(shell, () =>
            {
                if (shell.SettingsWindow is null) throw new InvalidOperationException("Settings action did not open a window.");
                if (shell.Session.Dashboard.Accounts.Count != 0) shell.Session.Navigate(SettingsPage.Accounts);
            });
            await WaitForSettingsUI(shell, "accounts list");
            await OnUI(shell, () =>
            {
                if (shell.Session.Dashboard.Accounts.Count == 0) return;
                if (Find(shell.SettingsWindow!, "AccountAvatar-" + shell.Session.Dashboard.Accounts[0].Key) is not PersonPicture)
                    throw new InvalidOperationException("Settings account picture did not render.");
                shell.Session.AddAccount();
            });
            await WaitForSettingsUI(shell, "add-account deep link");
            await OnUI(shell, () =>
            {
                var settings = shell.SettingsWindow ?? throw new InvalidOperationException("Settings did not open.");
                if (Find(settings, "AccountOnboarding") is null ||
                    Find(settings, "ChangeSignInHost") is not Button changeHost ||
                    Find(settings, "AccountIdentityConfirmation") is not null)
                    throw new InvalidOperationException("Add-account deep link did not render.");
            });
            await InvokeButtonAsync(shell, "ChangeSignInHost");
            await WaitForSettingsUI(shell, "change sign-in host");
            await OnUI(shell, () =>
            {
                var settings = shell.SettingsWindow!;
                if (Find(settings, "AccountHostSelection") is not ComboBox || !shell.Session.EditingHost)
                    throw new InvalidOperationException("Change host did not expose host-specific sign-in settings.");
                if (Find(settings, "AccountBack") is not Button back)
                    throw new InvalidOperationException("Onboarding Back action did not render.");
            });
            await InvokeButtonAsync(shell, "AccountBack");
            await WaitForSettingsUI(shell, "onboarding Back");
            await OnUI(shell, () =>
            {
                if (shell.Session.ShowAddForm || shell.Session.CanGoBack ||
                    Find(shell.SettingsWindow!, "AccountOnboarding") is not null)
                    throw new InvalidOperationException("Onboarding Back did not return to the accounts list.");
                AssertBackAccelerators(shell.SettingsWindow!);
                shell.Session.Navigate(SettingsPage.Notifications);
            });
            await WaitForSettingsUI(shell, "Notifications navigation");
            if (shell.Session.Dashboard.Accounts.Count > 0)
            {
                await OnUI(shell, () => shell.Session.EditAccount(shell.Session.Dashboard.Accounts[0].Key));
                await WaitForSettingsUI(shell, "account details");
                await OnUI(shell, () =>
                {
                    var settings = shell.SettingsWindow!;
                    if (Find(settings, "AccountConsumption") is not TextBlock { Text: "$26.25" })
                        throw new InvalidOperationException("Account summary did not render.");
                    if (Find(settings, "AccountAvatar-" + shell.Session.Dashboard.Accounts[0].Key) is not PersonPicture)
                        throw new InvalidOperationException("Account detail picture did not render.");
                    if (Find(settings, "AdvancedAccountDetails") is not Expander { IsExpanded: false } advanced)
                        throw new InvalidOperationException("Advanced account details must start collapsed.");
                    if (Find(settings, "AccountHistory") is not null)
                        throw new InvalidOperationException("Sampled spending history must not appear as a chart.");
                    if (Find(settings, "AccountDiagnosticsTable") is not null)
                        throw new InvalidOperationException("Advanced data is displayed before disclosure.");
                    if (new ExpanderAutomationPeer(advanced).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                        throw new InvalidOperationException("Advanced disclosure is not accessible.");
                    expand.Expand();
                });
                await WaitForSettingsUI(shell, "expanded account diagnostics");
                await OnUI(shell, () =>
                {
                    if (Find(shell.SettingsWindow!, "DetailCredits") is not TextBlock { Text: "2,625" })
                        throw new InvalidOperationException("Expanded account details did not expose labeled data.");
                    if (Find(shell.SettingsWindow!, "AccountBack") is not Button back)
                        throw new InvalidOperationException("Account details Back action did not render.");
                });
                await InvokeButtonAsync(shell, "AccountBack");
                await WaitForSettingsUI(shell, "account details Back");
                await OnUI(shell, () =>
                {
                    if (shell.Session.SelectedAccount is not null || shell.Session.CanGoBack)
                        throw new InvalidOperationException("Account details Back did not return to accounts.");
                    shell.Session.Navigate(SettingsPage.Notifications);
                });
                await WaitForSettingsUI(shell, "Notifications after account Back");
            }
            RecordSmokePhase(directory, "Testing tray settings, preview and callback lifecycle");
            await SmokeTraySettingsAsync(shell);
            RecordSmokePhase(directory, "Testing settings close/reopen and native notification/credentials");
            await OnUI(shell, () => shell.SettingsWindow!.NativeWindow.Close());
            await WaitForUI(shell, "settings close", () => shell.SettingsWindow is null);
            await OnUI(shell, () =>
            {
                if (shell.SettingsWindow is not null || shell.Session.CanGoBack)
                    throw new InvalidOperationException("Closing settings retained its window or Back target.");
                shell.ShowSettings(SettingsPage.Notifications);
            });
            await WaitForSettingsUI(shell, "reopened Notifications settings");
            if (!empty) await SmokeUsageDeepLinksAsync(shell);
            if (empty) await SmokeSingleAccountPopupAsync(shell);
            RecordSmokePhase(directory, "Testing synthetic Store update About controls");
            await SmokeStoreUpdatesAsync(shell, updates);
            await OnUI(shell, () =>
            {
                AssertBackAccelerators(shell.SettingsWindow!);
                if (shell.Session.Page != SettingsPage.About) throw new InvalidOperationException("Settings navigation failed.");
                SmokeCredentials();
                if (shell.Session.TestNotification?.Invoke() != true) throw new InvalidOperationException("Shell notification rejected.");
                RecordSmokePhase(directory, "All synthetic smoke assertions passed; exiting Reactor");
                File.WriteAllText(Path.Combine(directory, "native-smoke-result.txt"),
                    "PASS: fixed Save/Cancel footer, dirty Back/page/close/quit prompts and safe draft decisions, " +
                    "inline settings validation borders, field messages and accessible help text, correction and override reset, " +
                    "immediate semantic mouse/keyboard tray toggle, raw mouse callbacks ignored, no double-click shortcut, " +
                    "popup settings gear, hide/reopen and focus transitions, synchronized automation and committed UI layout, " +
                    "Reactor cost flyout, usage-first settings without sampled chart, independent collapsed account diagnostics, " +
                    "per-account estimate opt-in/off/unavailable, UTC disclosure and unchanged observed totals/tray, " +
                    "account avatars and diagnostics, add-account deep link, " +
                    "account Back controls and scoped keyboard accelerator registration, native controls, " +
                    "tray style/mode/selection controls, per-account callback mapping, retired callbacks ignored, neutral access icon, " +
                    "unsaved live preview pixel parity, Save isolation and reusable image-buffer lifecycle, " +
                    "simulated TaskbarCreated recovery and display-change repaint, " +
                    "synthetic Store latest/update About controls, native update notification opens About, consent cancellation and retry, " +
                    "Shell notification submission and isolated Credential Manager round-trip.\n" +
                    "No live account access, installation, or startup writes.\n");
                shell.Exit();
            });
        }
        catch (Exception ex)
        {
            setExit(1);
            RecordSmokePhase(directory, $"Smoke failed ({ex.GetType().Name})");
            File.WriteAllText(Path.Combine(directory, "native-smoke-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}\n");
            ReactorApp.UIDispatcher?.TryEnqueue(() => ReactorApp.Exit(1));
        }
    }
    private sealed class SmokeStoreUpdates : IStoreUpdates
    {
        internal bool Available { get; set; }
        internal int Installs { get; private set; }
        public Task<bool> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(Available);
        public Task<StoreInstallResult> InstallAsync(nint owner, Action<StoreUpdateProgress> progress,
            CancellationToken cancellationToken)
        {
            if (owner == 0) throw new InvalidOperationException("Synthetic Store request has no owner HWND.");
            Installs++;
            return Task.FromResult(StoreInstallResult.Canceled);
        }
        public void Restart() => throw new InvalidOperationException("Synthetic canceled update must not restart.");
    }
    private static async Task SmokeStoreUpdatesAsync(ReactorShell shell, SmokeStoreUpdates updates)
    {
        await OnUI(shell, () => shell.ShowSettings(SettingsPage.About));
        await WaitForSettingsUI(shell, "Store latest-version About");
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, "StoreUpdateStatus") is not TextBlock { Text: "You're running the latest version." } ||
                Find(shell.SettingsWindow!, "InstallStoreUpdate") is not null)
                throw new InvalidOperationException("Current Store app must show latest-version status without an Update button.");
            updates.Available = true;
            shell.Session.StoreUpdates!.Check(force: true);
        });
        await WaitForSettingsUI(shell, "Store update available");
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, "StoreUpdateStatus") is not TextBlock { Text: "An update is available." } ||
                Find(shell.SettingsWindow!, "InstallStoreUpdate") is not Button { Content: "Update", IsEnabled: true })
                throw new InvalidOperationException("Available Store update must render its enabled Update action.");
            shell.Session.Navigate(SettingsPage.Usage);
            shell.SettingsWindow!.NativeWindow.Close();
        });
        await WaitForUI(shell, "settings closed before notification click", () => shell.SettingsWindow is null);
        await OnUI(shell, () =>
        {
            var icon = shell.TrayIcons.Single(icon => icon.NotificationAccount == "store-update");
            Win32.SendMessage(shell.TrayHandle, Win32.WM_TRAY, 0,
                (nint)((icon.Id << 16) | (uint)Win32.NIN_BALLOONUSERCLICK));
        });
        await WaitForSettingsUI(shell, "native Store notification opens About");
        await OnUI(shell, () =>
        {
            if (shell.Session.Page != SettingsPage.About ||
                Find(shell.SettingsWindow!, "InstallStoreUpdate") is not Button { IsEnabled: true })
                throw new InvalidOperationException("Clicking the Store update notification must open About with its Update action.");
        });
        await InvokeButtonAsync(shell, "InstallStoreUpdate");
        await WaitForSettingsUI(shell, "Store consent cancellation");
        await OnUI(shell, () =>
        {
            if (updates.Installs != 1 || shell.Session.StoreUpdates!.Updating ||
                Find(shell.SettingsWindow!, "StoreUpdateStatus") is not TextBlock { Text: "Update canceled. You can try again." } ||
                Find(shell.SettingsWindow!, "InstallStoreUpdate") is not Button { IsEnabled: true })
                throw new InvalidOperationException("Canceled Store consent must leave a retryable Update action.");
            shell.Session.Navigate(SettingsPage.Usage);
        });
        await WaitForSettingsUI(shell, "Store update in-app notice");
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, "ViewStoreUpdate") is not Button { IsEnabled: true })
                throw new InvalidOperationException("An available update must be discoverable outside About.");
        });
        await InvokeButtonAsync(shell, "ViewStoreUpdate");
        await WaitForSettingsUI(shell, "Store update notice opens About");
        await OnUI(shell, () =>
        {
            if (shell.Session.Page != SettingsPage.About)
                throw new InvalidOperationException("Update notice did not open About.");
            updates.Available = false;
            shell.Session.StoreUpdates!.Check(force: true);
        });
        await WaitForSettingsUI(shell, "Store update cleared externally");
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, "StoreUpdateStatus") is not TextBlock { Text: "You're running the latest version." } ||
                Find(shell.SettingsWindow!, "InstallStoreUpdate") is not null)
                throw new InvalidOperationException("A cleared Store update must remove the Update action.");
        });
    }
    private sealed class SmokeTimeProvider : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task SmokeUsageDeepLinksAsync(ReactorShell shell)
    {
        var keys = shell.Session.Dashboard.Accounts.Select(a => a.Key).Reverse().ToArray();
        foreach (string key in keys)
        {
            await OnUI(shell, shell.ShowFlyout);
            await WaitForFlyoutUI(shell, "account Usage link in popup");
            await InvokeButtonAsync(shell, key + "_ViewUsage", flyout: true);
            await WaitForSettingsUI(shell, "account Usage destination");
            await OnUI(shell, () =>
            {
                var panel = Find(shell.SettingsWindow!, key + "_UsagePanel") as FrameworkElement;
                var scroll = Find(shell.SettingsWindow!, "SettingsScroll");
                RecordSmokePhase(shell.Session.Controller.DataDirectory,
                    $"Usage destination key={key}, target={shell.Session.UsageAccountKey}, panel={panel?.GetType().Name}, " +
                    $"scroll={scroll?.GetType().Name}, top={(scroll is UIElement element ? panel?.TransformToVisual(element).TransformPoint(default).Y : null)}.");
            });
            await WaitForUI(shell, "account Usage panel brought into view", () =>
            {
                var window = shell.SettingsWindow!;
                if (shell.Session.Page != SettingsPage.Usage || shell.Session.UsageAccountKey != key ||
                    shell.Session.SelectedAccount is not null || shell.Session.ShowAddForm ||
                    Find(window, key + "_UsagePanel") is not FrameworkElement panel ||
                    Find(window, "SettingsScroll") is not ScrollView scroll)
                    return false;
                double top = panel.TransformToVisual(scroll).TransformPoint(default).Y;
                return top >= -1 && top < scroll.ActualHeight - 40;
            });
            await OnUI(shell, () =>
            {
                if (Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0)
                    throw new InvalidOperationException("Account Usage navigation did not dismiss the popup.");
            });
        }
        await OnUI(shell, () => shell.Session.Navigate(SettingsPage.Notifications));
        await WaitForSettingsUI(shell, "Notifications after popup Usage links");
    }

    private static async Task SmokeFirstAccountPopupAsync(ReactorShell shell)
    {
        var demo = (DemoController)shell.Session.Controller;
        var account = shell.Session.Dashboard.Accounts[0];
        var preferences = demo.AccountSettings(account.Key);
        try
        {
            await demo.SaveAccountAsync(account.Key, preferences.DisplayName, preferences.Thresholds,
                preferences.SpendIncrementUsd, showPeriodEstimate: true);
            await WaitForUI(shell, "sample forecast for popup fit", () =>
                shell.Session.Dashboard.Accounts[0].PeriodEstimate?.EstimatedConsumptionUsd is > 0);
            decimal projected = shell.Session.Dashboard.Accounts[0].PeriodEstimate!.EstimatedConsumptionUsd!.Value;
            await OnUI(shell, shell.ShowFlyout);
            await WaitForFlyoutUI(shell, "popup before late daily-data sizing");
            await OnUI(shell, () =>
            {
                var size = shell.Flyout!.AppWindow.Size;
                shell.Flyout.AppWindow.Resize(new(size.Width, (int)(590 * size.Width / 400d)));
            });
            foreach (decimal? fraction in new decimal?[] { null, 0.8m, 0.97m, 1.2m })
            {
                await demo.SaveAccountAsync(account.Key, preferences.DisplayName, preferences.Thresholds,
                    preferences.SpendIncrementUsd, showPeriodEstimate: fraction is not null,
                    customBudgetUsd: fraction is { } value ? decimal.Round(projected / value, 2) : null,
                    updateCustomBudget: true);
                await WaitForFlyoutUI(shell, "first complete account with daily chart");
                await OnUI(shell, () =>
                {
                    var window = shell.Flyout!;
                    if (Find(window, "FlyoutBodyScroll") is not ScrollView scroll ||
                        Find(window, account.Key + "_FlyoutAccount") is not FrameworkElement card ||
                        Find(window, account.Key + "_CompactDailyPlot") is not FrameworkElement plot ||
                        Find(window, account.Key + "_ViewUsage") is not Button details)
                        throw new InvalidOperationException("The multi-account popup is missing its daily chart or Details action.");
                    if (scroll.VerticalOffset > 1 || scroll.ScrollableHeight <= 0)
                        throw new InvalidOperationException("The multi-account popup must start at the first account and retain scrolling for later accounts.");
                    foreach (var element in new[] { card, plot, details })
                    {
                        double top = element.TransformToVisual(scroll).TransformPoint(default).Y;
                        if (top < -1 || top + element.ActualHeight > scroll.ViewportHeight + 1)
                            throw new InvalidOperationException($"First-account popup content is clipped (forecast={fraction}, top={top:F1}, height={element.ActualHeight:F1}, viewport={scroll.ViewportHeight:F1}).");
                    }
                    if (plot.ActualHeight < 96 || Find(window, "TotalConsumption") is not TextBlock { FontSize: >= 28 })
                        throw new InvalidOperationException("The compact popup sacrificed chart height or total readability.");
                });
            }
        }
        finally
        {
            await demo.SaveAccountAsync(account.Key, preferences.DisplayName, preferences.Thresholds,
                preferences.SpendIncrementUsd, preferences.ShowPeriodEstimate,
                account.CustomBudgetUsd, updateCustomBudget: true);
            await OnUI(shell, () => shell.Flyout!.Hide());
        }
    }

    private static async Task SmokeSingleAccountPopupAsync(ReactorShell shell)
    {
        var demo = (DemoController)shell.Session.Controller;
        await demo.AddExampleAccountAsync();
        await WaitForUI(shell, "single synthetic account", () => shell.Session.Dashboard.Accounts.Count == 1);
        string key = shell.Session.Dashboard.Accounts[0].Key;
        foreach (bool estimate in new[] { false, true })
        {
            await demo.SaveAccountAsync(key, "", "", showPeriodEstimate: estimate);
            await OnUI(shell, shell.ShowFlyout);
            await WaitForFlyoutUI(shell, "single account with daily chart and optional estimate");
            await OnUI(shell, () =>
            {
                if (Find(shell.Flyout!, key + "_CompactDailyConsumption") is null)
                    throw new InvalidOperationException("Single-account popup is missing its daily chart.");
                if (Find(shell.Flyout!, "TotalConsumption") is not null)
                    throw new InvalidOperationException("Single-account popup repeats the account total.");
                var scroll = Find(shell.Flyout!, "FlyoutBodyScroll");
                double overflow = scroll switch
                {
                    ScrollView modern => modern.ScrollableHeight,
                    ScrollViewer classic => classic.ScrollableHeight,
                    _ => throw new InvalidOperationException("Popup body scroll control is missing.")
                };
                if (overflow > 1)
                    throw new InvalidOperationException($"Single-account popup requires {overflow:F1} DIPs of vertical scrolling (estimate={estimate}).");
            });
        }
        await OnUI(shell, () => shell.Flyout!.Hide());
    }

    private static async Task SmokeDirtyFormsAsync(ReactorShell shell)
    {
        if (shell.Session.Dashboard.Accounts.Count == 0) return;
        var account = shell.Session.Dashboard.Accounts[0];
        await OnUI(shell, () => shell.Session.EditAccount(account.Key));
        await WaitForSettingsUI(shell, "fixed account actions");
        double footerY = 0;
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            var root = (FrameworkElement)window.NativeWindow.Content;
            var footer = (FrameworkElement)Find(window, "SettingsFormActions")!;
            if (Find(window, "SaveAccount") is not Button { IsEnabled: false } ||
                Find(window, "CancelSettingsChanges") is not Button { IsEnabled: false })
                throw new InvalidOperationException("Clean forms must have visible, disabled Save and Cancel actions.");
            for (DependencyObject? parent = VisualTreeHelper.GetParent(footer); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (ReferenceEquals(parent, Find(window, "SettingsScroll")))
                    throw new InvalidOperationException("Save and Cancel must not be inside scrolling form content.");
            footerY = footer.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()).Y;
            ((ScrollView)Find(window, "SettingsScroll")!).ScrollTo(0, double.MaxValue,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        });
        await WaitForUI(shell, "account form actually scrolled", () =>
            Find(shell.SettingsWindow!, "SettingsScroll") is ScrollView { VerticalOffset: > 0 });
        await WaitForSettingsUI(shell, "scrolled account form");
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            var root = (FrameworkElement)window.NativeWindow.Content;
            var footer = (FrameworkElement)Find(window, "SettingsFormActions")!;
            double y = footer.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point()).Y;
            if (Math.Abs(y - footerY) > 1 || y + footer.ActualHeight > root.ActualHeight + 1)
                throw new InvalidOperationException("Scrolling must leave the form action footer visible and stationary.");
            ((TextBox)Find(window, "DisplayName")!).Text = "Unsaved native draft";
        });
        await WaitForSettingsUI(shell, "dirty account status");
        await InvokeButtonAsync(shell, "AccountBack");
        await ChooseDialog("Keep editing");
        await OnUI(shell, () =>
        {
            if (shell.Session.SelectedAccount != account.Key || shell.Session.DisplayName != "Unsaved native draft" ||
                Find(shell.SettingsWindow!, "SettingsDraftStatus") is not TextBlock { Text: "Unsaved changes" })
                throw new InvalidOperationException("Keep editing must preserve the form and its dirty draft.");
            var navigation = FindNode<NavigationView>((DependencyObject)shell.SettingsWindow!.NativeWindow.Content)!;
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => Equals(item.Tag, "General"));
        });
        await ChooseDialog("Keep editing");
        await OnUI(shell, () =>
        {
            var navigation = FindNode<NavigationView>((DependencyObject)shell.SettingsWindow!.NativeWindow.Content)!;
            if (shell.Session.Page != SettingsPage.Accounts ||
                navigation.SelectedItem is not NavigationViewItem item || !Equals(item.Tag, "Accounts"))
                throw new InvalidOperationException("Canceled navigation must keep both content and selected page on Accounts.");
        });
        await InvokeButtonAsync(shell, "CancelSettingsChanges");
        await WaitForSettingsUI(shell, "canceled account draft");
        await OnUI(shell, () =>
        {
            if (shell.Session.HasUnsavedChanges || shell.Session.DisplayName != shell.Session.Controller.AccountSettings(account.Key).DisplayName)
                throw new InvalidOperationException("The fixed Cancel action must restore saved account settings.");
            ((ComboBox)Find(shell.SettingsWindow!, "BudgetBasis")!).SelectedIndex = 1;
        });
        await WaitForSettingsUI(shell, "budget input before guarded Save");
        await OnUI(shell, () => ((TextBox)Find(shell.SettingsWindow!, "CustomBudget")!).Text = "test");
        await WaitForUI(shell, "invalid budget draft becomes dirty", () => shell.Session.CustomBudget == "test" && shell.Session.HasUnsavedChanges);
        await InvokeButtonAsync(shell, "AccountBack");
        await ChooseDialog("Save");
        await OnUI(shell, () =>
        {
            if (shell.Session.SelectedAccount != account.Key || !shell.Session.HasUnsavedChanges ||
                Find(shell.SettingsWindow!, "CustomBudgetError") is not TextBlock ||
                shell.Session.Dashboard.Accounts[0].CustomBudgetUsd is not null)
                throw new InvalidOperationException("Save from the leave prompt must retain and highlight an invalid draft without navigating.");
        });
        await InvokeButtonAsync(shell, "CancelSettingsChanges");
        await WaitForSettingsUI(shell, "invalid guarded draft canceled");
        string originalName = shell.Session.Controller.AccountSettings(account.Key).DisplayName;
        foreach (string name in new[] { "Saved native draft", originalName })
        {
            await OnUI(shell, () => ((TextBox)Find(shell.SettingsWindow!, "DisplayName")!).Text = name);
            await WaitForUI(shell, "account name draft becomes dirty", () => shell.Session.DisplayName == name && shell.Session.HasUnsavedChanges);
            await InvokeButtonAsync(shell, "AccountBack");
            await ChooseDialog("Save");
            await OnUI(shell, () =>
            {
                if (shell.Session.SelectedAccount is not null || shell.Session.Controller.AccountSettings(account.Key).DisplayName != name)
                    throw new InvalidOperationException("Save from the leave prompt must persist the draft before navigating Back.");
                shell.Session.EditAccount(account.Key);
            });
            await WaitForSettingsUI(shell, "saved account reopened");
        }
        await OnUI(shell, () =>
        {
            shell.Session.Navigate(SettingsPage.General);
        });
        await WaitForSettingsUI(shell, "fixed global actions");
        await OnUI(shell, () => ((TextBox)Find(shell.SettingsWindow!, "PollMinutes")!).Text = "20");
        await WaitForUI(shell, "global draft becomes dirty", () => shell.Session.HasUnsavedChanges);
        await OnUI(shell, shell.Exit);
        await ChooseDialog("Keep editing");
        var settings = shell.SettingsWindow!;
        await OnUI(shell, RequestSettingsClose);
        await ChooseDialog("Keep editing");
        await OnUI(shell, () =>
        {
            if (!ReferenceEquals(shell.SettingsWindow, settings) || shell.Session.PollMinutes != "20")
                throw new InvalidOperationException("Keep editing must cancel Settings close and app quit without losing the draft.");
            RequestSettingsClose();
        });
        await ChooseDialog("Discard");
        await WaitForUI(shell, "discarded settings window closes", () => shell.SettingsWindow is null);
        await OnUI(shell, () =>
        {
            if (shell.Session.Controller.Settings.PollMinutes != 10)
                throw new InvalidOperationException("Discard must not save the global draft.");
            shell.ShowSettings(SettingsPage.Usage);
        });
        await WaitForSettingsUI(shell, "settings reopened after protected close");

        void RequestSettingsClose() => Win32.SendMessage(
            WinRT.Interop.WindowNative.GetWindowHandle(settings.NativeWindow), Win32.WM_CLOSE, 0, 0);

        async Task ChooseDialog(string label)
        {
            await WaitForUI(shell, "unsaved changes dialog opens", () => LiveDialog() is { IsLoaded: true, ActualWidth: > 0 });
            try
            {
                await InvokeButtonAsync(shell, "Unsaved changes: " + label, findButton: () =>
                    LiveDialog() is { } dialog ? FindDialogButton(dialog, label) : null);
            }
            catch (TimeoutException ex)
            {
                string details = "";
                await OnUI(shell, () =>
                {
                    if (LiveDialog() is { } dialog)
                        details = $"primary={dialog.PrimaryButtonText}, secondary={dialog.SecondaryButtonText}, close={dialog.CloseButtonText}; " +
                            string.Join("; ", DialogControls(dialog));
                });
                throw new TimeoutException("Unsaved changes dialog did not expose the requested action: " + details, ex);
            }
            await WaitForUI(shell, "unsaved changes decision completes", () => !shell.Session.HasPendingNavigation && LiveDialog() is null);
            if (shell.SettingsWindow is not null) await WaitForSettingsUI(shell, "unsaved changes decision renders");
        }
        ContentDialog? LiveDialog()
        {
            if (shell.SettingsWindow?.NativeWindow.Content is not FrameworkElement root) return null;
            return VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .Select(popup => popup.Child is { } child ? FindNode<ContentDialog>(child) : null)
                .FirstOrDefault(dialog => dialog is not null);
        }
        static T? FindNode<T>(DependencyObject node) where T : DependencyObject
        {
            if (node is T result) return result;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (FindNode<T>(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
            return null;
        }
        static Button? FindDialogButton(DependencyObject node, string label)
        {
            if (node is Button button && (Equals(button.Content, label) ||
                new ButtonAutomationPeer(button).GetName() == label)) return button;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                if (FindDialogButton(VisualTreeHelper.GetChild(node, i), label) is { } child) return child;
            return null;
        }
        static IEnumerable<string> DialogControls(DependencyObject node)
        {
            if (node is Button button)
                yield return $"button={new ButtonAutomationPeer(button).GetName()}, name={button.Name}, " +
                    $"enabled={button.IsEnabled}, loaded={button.IsLoaded}, size={button.ActualWidth}x{button.ActualHeight}";
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (string value in DialogControls(VisualTreeHelper.GetChild(node, i))) yield return value;
        }
    }

    private static async Task SmokeFormValidationAsync(ReactorShell shell)
    {
        if (shell.Session.Dashboard.Accounts.Count == 0) return;
        var account = shell.Session.Dashboard.Accounts[0];
        await OnUI(shell, () => shell.Session.EditAccount(account.Key));
        await WaitForSettingsUI(shell, "budget validation preferences");
        await OnUI(shell, () => ((ComboBox)Find(shell.SettingsWindow!, "BudgetBasis")!).SelectedIndex = 1);
        await WaitForSettingsUI(shell, "custom budget input");
        TextBox? budgetInput = null;
        await OnUI(shell, () =>
        {
            budgetInput = (TextBox)Find(shell.SettingsWindow!, "CustomBudget")!;
            budgetInput.Text = "test";
        });
        await WaitForSettingsUI(shell, "invalid budget draft");
        await InvokeButtonAsync(shell, "SaveAccount");
        await WaitForSettingsUI(shell, "inline budget validation");
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "CustomBudgetError") is not TextBlock message || !message.Text.Contains("greater than $0") ||
                Find(window, "CustomBudgetValidationBorder") is not Border { BorderThickness.Left: 2, BorderBrush: not null } ||
                Find(window, "CustomBudget") is not TextBox input || AutomationProperties.GetHelpText(input) != message.Text ||
                Find(window, "SettingsValidationFeedback") is null || shell.Session.Busy ||
                shell.Session.Dashboard.Accounts[0].CustomBudgetUsd is not null)
                throw new InvalidOperationException("Invalid budget must have a visible field border, local error and accessible help without saving.");
            input.Text = "50";
        });
        await WaitForSettingsUI(shell, "corrected budget validation");
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "CustomBudgetError") is not null ||
                Find(window, "CustomBudgetValidationBorder") is not Border { BorderThickness.Left: 0 } ||
                Find(window, "SettingsValidationFeedback") is not null ||
                Find(window, "CustomBudget") is not TextBox input || AutomationProperties.GetHelpText(input) != "" ||
                !ReferenceEquals(input, budgetInput))
                throw new InvalidOperationException("Correction must clear validation without replacing the active input. " +
                    $"error={Find(window, "CustomBudgetError") is not null}, " +
                    $"border={(Find(window, "CustomBudgetValidationBorder") as Border)?.BorderThickness.Left}, " +
                    $"summary={Find(window, "SettingsValidationFeedback") is not null}, " +
                    $"help={(Find(window, "CustomBudget") is TextBox value ? AutomationProperties.GetHelpText(value) : "missing")}, " +
                    $"retained={ReferenceEquals(Find(window, "CustomBudget"), budgetInput)}.");
            input.Text = "test";
        });
        await WaitForSettingsUI(shell, "invalid budget again");
        await InvokeButtonAsync(shell, "SaveAccount");
        await WaitForSettingsUI(shell, "budget override reset");
        await OnUI(shell, () => ((ComboBox)Find(shell.SettingsWindow!, "BudgetBasis")!).SelectedIndex = 0);
        await WaitForSettingsUI(shell, "API allocation reset");
        await OnUI(shell, () =>
        {
            if (shell.Session.HasFieldErrors || Find(shell.SettingsWindow!, "CustomBudgetError") is not null ||
                Find(shell.SettingsWindow!, "SettingsValidationFeedback") is not null)
                throw new InvalidOperationException("An inactive budget must not retain its validation error.");
            shell.Session.Navigate(SettingsPage.Usage);
        });
        await WaitForSettingsUI(shell, "Usage after validation");
    }

    private static async Task SmokePeriodEstimateAsync(ReactorShell shell, SmokeTimeProvider time, Action<string> phase)
    {
        if (shell.Session.Dashboard.Accounts.Count == 0) return;
        phase("Estimates: loading account preferences");
        var account = shell.Session.Dashboard.Accounts[0];
        string key = account.Key;
        var start = new DateTimeOffset(time.Now.Year, time.Now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var trayBefore = shell.Session.Dashboard.Tray!.RollUp;
        string amount = UI.UI.ApproximateMoney(26.25m * (start.AddMonths(1) - start).Days / 3m);
        await OnUI(shell, () => shell.Session.EditAccount(key));
        await WaitForSettingsUI(shell, "account estimate preferences");
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "PeriodEstimate") is not null || Find(window, "PeriodEstimateDisclosure") is not null ||
                Find(window, "ShowPeriodEstimate") is not CheckBox { IsChecked: false } preference)
                throw new InvalidOperationException("Period estimate must be off by default and absent, not zero.");
            time.Now = start.AddDays(3);
            preference.IsChecked = true;
        });
        await WaitForSettingsUI(shell, "draft estimate opt-in");
        await OnUI(shell, () =>
        {
            if (shell.Session.Controller.AccountSettings(key).ShowPeriodEstimate ||
                Find(shell.SettingsWindow!, "PeriodEstimate") is not null)
                throw new InvalidOperationException("Draft forecast opt-in changed saved usage before Save.");
        });
        await InvokeButtonAsync(shell, "SaveAccount");
        phase("Estimates: saving opt-in and checking account row");
        await WaitForAccountSave();
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            AssertEstimateRow(window, "", amount);
            AssertForecastThresholds(window, shell.Session.Dashboard.Accounts[0]);
            if (Find(window, "PeriodEstimateWarning") is null ||
                Find(window, "PeriodEstimateDisclosure") is not null ||
                Find(window, "AccountConsumption") is not TextBlock { Text: "$26.25" } ||
                shell.Session.Dashboard.ConsumptionUsd != 42.75m || shell.Session.Dashboard.Accounts[0].Percent != 105m ||
                shell.Session.Dashboard.Tray!.RollUp != trayBefore)
                throw new InvalidOperationException("Estimate row, collapsed disclosure or observed usage contract failed.");
            ExpandDetails(window, "AdvancedAccountDetails");
        });
        await WaitForSettingsUI(shell, "expanded estimate details");
        phase("Estimates: checking expanded account details");
        await OnUI(shell, () =>
        {
            AssertEstimateDisclosure(shell.SettingsWindow!, "", amount);
            shell.Session.Navigate(SettingsPage.Usage);
        });
        await WaitForSettingsUI(shell, "Usage estimate row");
        phase("Estimates: checking Usage row");
        await OnUI(shell, () =>
        {
            AssertEstimateRow(shell.SettingsWindow!, key + "_", amount);
            if (Find(shell.SettingsWindow!, "example.ghe.com:2_PeriodEstimate") is not null)
                throw new InvalidOperationException("Usage estimates are missing or not independently opt-in.");
            ExpandDetails(shell.SettingsWindow!, key + "_AdvancedAccountDetails");
        });
        await WaitForSettingsUI(shell, "expanded Usage estimate details");
        phase("Estimates: checking expanded Usage details");
        await OnUI(shell, () =>
        {
            AssertEstimateDisclosure(shell.SettingsWindow!, key + "_", amount);
            shell.ShowFlyout();
        });
        await WaitForFlyoutVisibility(shell, true, "with account estimate enabled");
        phase("Estimates: checking flyout row");
        await WaitForFlyoutUI(shell, "flyout estimate row");
        await OnUI(shell, () =>
        {
            AssertEstimateRow(shell.Flyout!, key + "_Flyout_", amount, rightAlignAmount: true);
            if (Find(shell.Flyout!, "example.ghe.com:2_Flyout_PeriodEstimate") is not null ||
                Find(shell.Flyout!, "TotalConsumption") is not TextBlock { Text: "$42.75" })
                throw new InvalidOperationException("Flyout estimate changed observed totals or another account.");
            time.Now = start.AddHours(12);
            shell.Session.EditAccount(key);
            shell.Session.Refresh();
        });
        phase("Estimates: refreshing unavailable observation");
        await WaitForIdle();
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            AssertEstimateRow(window, "", "Unavailable");
            if (Find(window, "PeriodEstimateAmount") is not TextBlock { Text: "Unavailable" } ||
                Find(window, "PeriodEstimateContext") is not TextBlock reason || !reason.Text.Contains("24 hours") ||
                Find(window, "PeriodEstimateWarning") is not null || Find(window, "PeriodEstimateDisclosure") is not null ||
                Find(window, "ShowPeriodEstimate") is not CheckBox { IsChecked: true } preference)
                throw new InvalidOperationException("Unavailable forecast lost its opt-in, reason or collapsed disclosure.");
            preference.IsChecked = false;
            ExpandDetails(window, "AdvancedAccountDetails");
        });
        await WaitForSettingsUI(shell, "expanded unavailable estimate reason");
        phase("Estimates: checking expanded unavailable reason");
        await OnUI(shell, () =>
        {
            AssertEstimateDisclosure(shell.SettingsWindow!, "", "Unavailable");
            if (Find(shell.SettingsWindow!, "PeriodEstimateUnavailable") is not TextBlock reason || !reason.Text.Contains("24 hours"))
                throw new InvalidOperationException("Expanded estimate details lost the unavailable reason.");
        });
        await WaitForSettingsUI(shell, "draft estimate disable");
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, "PeriodEstimate") is null)
                throw new InvalidOperationException("Draft disable hid the forecast before Save.");
        });
        await InvokeButtonAsync(shell, "SaveAccount");
        phase("Estimates: saving explicit disable");
        await WaitForAccountSave();
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, "PeriodEstimate") is not null ||
                Find(shell.SettingsWindow!, "PeriodEstimateDisclosure") is not null)
                throw new InvalidOperationException("Explicit disable did not omit the estimate and disclosure.");
            time.Now = DateTimeOffset.UtcNow;
            shell.Session.Refresh();
        });
        await WaitForIdle();
        await OnUI(shell, () => shell.Session.Navigate(SettingsPage.Usage));
        await WaitForSettingsUI(shell, "disabled Usage estimate");
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, key + "_PeriodEstimate") is not null)
                throw new InvalidOperationException("Disabled forecast remained in Usage.");
            shell.ShowFlyout();
        });
        await WaitForFlyoutVisibility(shell, true, "after disabling the account estimate");
        phase("Estimates: checking disabled flyout row");
        await WaitForFlyoutUI(shell, "disabled flyout estimate");
        await OnUI(shell, () =>
        {
            if (Find(shell.Flyout!, key + "_Flyout_PeriodEstimate") is not null)
                throw new InvalidOperationException("Disabled forecast remained in the flyout.");
            shell.ShowSettings(SettingsPage.Usage);
        });

        static void ExpandDetails(ReactorWindow window, string id)
        {
            if (Find(window, id) is not Expander advanced ||
                new ExpanderAutomationPeer(advanced).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                throw new InvalidOperationException("Estimate disclosure is not keyboard/screen-reader accessible.");
            expand.Expand();
        }
        static void AssertForecastThresholds(ReactorWindow window, AccountView source)
        {
            if (Find(window, "ForecastTrack") is not Canvas track)
                throw new InvalidOperationException("Enabled finite-target estimate has no forecast track.");
            double originalWidth = track.Width;
            try
            {
                foreach (double width in new[] { 220d, 620d })
                {
                    track.Width = width;
                    ((FrameworkElement)window.NativeWindow.Content).UpdateLayout();
                    foreach (decimal projected in new[] { 94.99m, 95m, 99.99m, 100m, 150m })
                    {
                        var fixture = source with
                        {
                            ConsumptionUsd = 60, Percent = 60, AllocationUsd = 100, CustomBudgetUsd = 100,
                            PeriodEstimate = new(EstimatedConsumptionUsd: projected)
                        };
                        UI.ForecastTrack.Apply(track, fixture, "");
                        track.UpdateLayout();
                        AssertEstimateRow(window, "", UI.UI.EstimateAmount(fixture.PeriodEstimate));
                        var extension = (Microsoft.UI.Xaml.Shapes.Line)Find(window, "ForecastExtension")!;
                        var marker = (Microsoft.UI.Xaml.Shapes.Line)Find(window, "ForecastMarker")!;
                        var baseline = AssertTrackingTarget(window, "", 100, "budget");
                        var brush = (SolidColorBrush)extension.Stroke;
                        var expected = projected >= 100
                            ? (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                            : projected >= 95 ? (SolidColorBrush)Application.Current.Resources["SystemFillColorCautionBrush"]
                            : new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent));
                        if (brush.Color != expected.Color || Math.Abs(extension.X1 - (baseline.X1 + 0.6 * (baseline.X2 - baseline.X1))) > 0.1 ||
                            Math.Abs(marker.X1 - (baseline.X1 + (double)Math.Min(1, projected / 100) * (baseline.X2 - baseline.X1))) > 0.1)
                            throw new InvalidOperationException("Forecast threshold color, abutting extension, or clamped marker is incorrect.");
                    }
                    foreach (decimal used in new[] { 0m, 60m, 94.99m, 95m, 99.99m, 100m, 110m })
                    {
                        var fixture = source with
                        {
                            ConsumptionUsd = used, Percent = used, AllocationUsd = 100, CustomBudgetUsd = 100,
                            PeriodEstimate = new(EstimatedConsumptionUsd: 150)
                        };
                        UI.ForecastTrack.Apply(track, fixture, "");
                        track.UpdateLayout();
                        AssertActualCallout(window, used, "");
                        UI.ForecastTrack.Apply(track, fixture with { PeriodEstimate = null }, "");
                        track.UpdateLayout();
                        AssertActualCallout(window, used, "");
                        if (Find(window, "ForecastMarker") is not null || Find(window, "PeriodEstimateAmount") is not null)
                            throw new InvalidOperationException("Disabling the forecast retained projected native drawing.");
                    }
                    foreach (decimal target in new[] { 0.01m, 1500.25m, 10000m, decimal.MaxValue })
                    {
                        var fixture = source with
                        {
                            ConsumptionUsd = target * 0.6m, Percent = 60, AllocationUsd = target, CustomBudgetUsd = null,
                            PeriodEstimate = new(EstimatedConsumptionUsd: target * 0.9m)
                        };
                        UI.ForecastTrack.Apply(track, fixture, "");
                        track.UpdateLayout();
                        AssertTrackingTarget(window, "", target, "API limit");
                        AssertEstimateRow(window, "", UI.UI.EstimateAmount(fixture.PeriodEstimate));
                    }
                }
            }
            finally
            {
                track.Width = originalWidth;
                ((FrameworkElement)window.NativeWindow.Content).UpdateLayout();
                UI.ForecastTrack.Apply(track, source, "");
                track.UpdateLayout();
            }
        }

        static void AssertActualCallout(ReactorWindow window, decimal used, string prefix)
        {
            if (Find(window, prefix + "ForecastTrack") is not Canvas track ||
                Find(window, prefix + "AccountConsumption") is not TextBlock amount || amount.Text != UI.UI.Money(used) ||
                Find(window, prefix + "ActualUsageCallout") is not FrameworkElement callout ||
                Find(window, prefix + "ActualUsageMarker") is not Microsoft.UI.Xaml.Shapes.Line marker ||
                Find(window, prefix + "ActualUsageLine") is not Microsoft.UI.Xaml.Shapes.Line usageLine ||
                Find(window, prefix + "UsageBaseline") is not Microsoft.UI.Xaml.Shapes.Line baseline ||
                amount.FontSize < 21 || AutomationProperties.GetLabeledBy(amount) is not TextBlock { Text: "used" } ||
                Canvas.GetTop(callout) + callout.ActualHeight > marker.Y1 + 1 ||
                Canvas.GetLeft(callout) < 0 || Canvas.GetLeft(callout) + callout.ActualWidth > baseline.X2 + 3)
                throw new InvalidOperationException("Actual usage callout did not retain its exact large amount above the solid marker.");
            AssertActualMarker(track, marker, usageLine);
            AssertTrackingTarget(window, prefix, 100, "budget");
            double expected = baseline.X1 + (double)Math.Min(1, used / 100) * (baseline.X2 - baseline.X1);
            if (Math.Abs(marker.X1 - expected) > 0.1)
                throw new InvalidOperationException("Actual usage marker does not match actual percentage.");
            var icon = Find(window, prefix + "ActualUsageWarning");
            if (used < 95 && icon is not null || used >= 95 && icon is not FontIcon)
                throw new InvalidOperationException("Actual warning icon incorrectly follows the forecast.");
            if (icon is FontIcon warning)
            {
                string expectedGlyph = used >= 100 ? "\uE711" : "\uE7BA";
                var expectedBrush = (SolidColorBrush)Application.Current.Resources[used >= 100
                    ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"];
                if (warning.Glyph != expectedGlyph || warning.Foreground is not SolidColorBrush brush ||
                    brush.Color != expectedBrush.Color)
                    throw new InvalidOperationException("Actual usage warning/error glyph or color is incorrect.");
            }
        }

        static void AssertActualMarker(Canvas track, Microsoft.UI.Xaml.Shapes.Line marker, Microsoft.UI.Xaml.Shapes.Line usageLine)
        {
            if (marker.Y1 >= usageLine.Y1 - usageLine.StrokeThickness / 2 ||
                marker.Y2 <= usageLine.Y1 + usageLine.StrokeThickness / 2 ||
                track.Children.OfType<Microsoft.UI.Xaml.Shapes.Line>().Any(line =>
                    line != marker && Canvas.GetZIndex(line) >= Canvas.GetZIndex(marker)))
                throw new InvalidOperationException("Actual usage marker does not cross above the horizontal strokes at a higher drawing priority.");
            if (marker.Stroke is not SolidColorBrush markerBrush ||
                markerBrush.Color != new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground))
                throw new InvalidOperationException("Actual usage marker lost its theme-aware neutral color.");
        }

        static Microsoft.UI.Xaml.Shapes.Line AssertTrackingTarget(ReactorWindow window, string prefix,
            decimal? expectedAmount = null, string? expectedLabel = null)
        {
            if (Find(window, prefix + "ForecastTrack") is not Canvas track ||
                Find(window, prefix + "UsageBaseline") is not Microsoft.UI.Xaml.Shapes.Line baseline ||
                Find(window, prefix + "TrackingTarget") is not FrameworkElement column ||
                Find(window, prefix + "TrackingTargetAmount") is not TextBlock amount ||
                Find(window, prefix + "TrackingTargetLabel") is not TextBlock label ||
                AutomationProperties.GetLabeledBy(amount) != label || label.Text is not ("budget" or "API limit") ||
                amount.FontSize < 16 || label.FontSize >= amount.FontSize ||
                Canvas.GetLeft(column) < baseline.X2 + 10 ||
                Canvas.GetLeft(column) + column.ActualWidth > track.ActualWidth + 1 ||
                Canvas.GetTop(column) + column.ActualHeight > track.ActualHeight + 1 ||
                ToolTipService.GetToolTip(column) is not string tooltip ||
                !tooltip.EndsWith(label.Text == "budget" ? " custom budget" : " API limit", StringComparison.Ordinal) ||
                AutomationProperties.GetName(column) != tooltip ||
                expectedAmount is { } value && amount.Text != UI.UI.TrackingTargetAmount(value) ||
                expectedLabel is not null && label.Text != expectedLabel)
                throw new InvalidOperationException("The fixed tracking target is missing, mislabeled, or overlapping the usage track.");
            var origin = amount.TransformToVisual(track).TransformPoint(default);
            if (Math.Abs(origin.Y + amount.ActualHeight / 2 - baseline.Y1) > 1)
                throw new InvalidOperationException("The fixed target amount is not aligned with the horizontal track.");
            var labelOrigin = label.TransformToVisual(track).TransformPoint(default);
            if (Math.Abs(origin.X + amount.ActualWidth / 2 - labelOrigin.X - label.ActualWidth / 2) > 1 ||
                amount.HorizontalAlignment != HorizontalAlignment.Center || label.HorizontalAlignment != HorizontalAlignment.Center)
                throw new InvalidOperationException("The fixed target label is not centered beneath its amount.");
            return baseline;
        }

        static void AssertEstimateRow(ReactorWindow window, string prefix, string expected, bool rightAlignAmount = false)
        {
            if (Find(window, prefix + "ForecastTrack") is Canvas track &&
                Find(window, prefix + "ForecastExtension") is not null)
            {
                if (Find(window, prefix + "ForecastExtension") is not Microsoft.UI.Xaml.Shapes.Line { StrokeDashArray.Count: > 0 } dashed ||
                    Find(window, prefix + "ForecastMarker") is not Microsoft.UI.Xaml.Shapes.Line marker ||
                    Find(window, prefix + "UsageBaseline") is not Microsoft.UI.Xaml.Shapes.Line baseline ||
                    Find(window, prefix + "ForecastCallout") is not FrameworkElement callout ||
                    Find(window, prefix + "PeriodEstimateAmount") is not TextBlock projectedAmount || projectedAmount.Text != expected ||
                    Find(window, prefix + "PeriodEstimateLabel") is not TextBlock { Text: "est. total" } projectedLabel ||
                    AutomationProperties.GetLabeledBy(projectedAmount) != projectedLabel ||
                    ToolTipService.GetToolTip(callout) is null ||
                    dashed.X1 > dashed.X2 || marker.X1 < 0 || marker.X1 > baseline.X2 + 1 ||
                    Canvas.GetLeft(callout) < 0 || Canvas.GetLeft(callout) + callout.ActualWidth > baseline.X2 + 3 ||
                    Canvas.GetTop(callout) < marker.Y2 || Canvas.GetTop(callout) + callout.ActualHeight > track.ActualHeight + 1 ||
                    dashed.StrokeDashArray.Count != 2 || dashed.StrokeDashArray[0] <= dashed.StrokeDashArray[1])
                    throw new InvalidOperationException("Forecast track, marker, callout, accessibility, or bounds are invalid.");
                if (Find(window, prefix + "ActualUsageMarker") is not Microsoft.UI.Xaml.Shapes.Line actualMarker ||
                    Find(window, prefix + "ActualUsageLine") is not Microsoft.UI.Xaml.Shapes.Line usageLine)
                    throw new InvalidOperationException("Actual usage marker is missing beside the forecast.");
                AssertActualMarker(track, actualMarker, usageLine);
                AssertTrackingTarget(window, prefix);
                var calloutAmountOrigin = projectedAmount.TransformToVisual(callout).TransformPoint(default);
                var calloutLabelOrigin = projectedLabel.TransformToVisual(callout).TransformPoint(default);
                double expectedLeft = Math.Clamp(marker.X1 - callout.ActualWidth / 2, 0,
                    Math.Max(0, baseline.X2 + 2 - callout.ActualWidth));
                if (projectedLabel.FontSize >= projectedAmount.FontSize ||
                    projectedLabel.FontSize < 11 ||
                    projectedAmount.Foreground is not SolidColorBrush mainText ||
                    mainText.Color != new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground) ||
                    projectedLabel.Foreground is not SolidColorBrush secondaryText ||
                    secondaryText.Color != ((SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]).Color ||
                    projectedAmount.Opacity != 1 || projectedLabel.Opacity != 1 ||
                    calloutLabelOrigin.Y < calloutAmountOrigin.Y + projectedAmount.ActualHeight - 1 ||
                    Math.Abs(calloutLabelOrigin.X + projectedLabel.ActualWidth / 2 - (calloutAmountOrigin.X + projectedAmount.ActualWidth / 2)) > 1 ||
                    Math.Abs(Canvas.GetLeft(callout) - expectedLeft) > 1)
                    throw new InvalidOperationException("Forecast subtitle is not centered beneath the amount or aligned with the marker.");
                if (ForecastTextContrast(projectedAmount) < 4.5 || ForecastTextContrast(projectedLabel) < 4.5)
                    throw new InvalidOperationException("Forecast text does not meet 4.5:1 contrast against its panel.");
                return;
            }

            static double ForecastTextContrast(TextBlock text)
            {
                var layers = new List<SolidColorBrush>();
                for (DependencyObject? parent = VisualTreeHelper.GetParent(text); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                {
                    Brush? background = parent switch
                    {
                        Border border => border.Background,
                        Panel panel => panel.Background,
                        Control control => control.Background,
                        _ => null
                    };
                    if (background is SolidColorBrush layer) layers.Add(layer);
                }
                var baseColor = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                var surface = (R: baseColor.R / 255d, G: baseColor.G / 255d, B: baseColor.B / 255d);
                foreach (var layer in layers.AsEnumerable().Reverse()) surface = Composite(layer, surface);
                var ink = Composite((SolidColorBrush)text.Foreground, surface);
                double first = Luminance(ink), second = Luminance(surface);
                return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);

                static (double R, double G, double B) Composite(SolidColorBrush brush, (double R, double G, double B) background)
                {
                    double alpha = brush.Color.A / 255d * brush.Opacity;
                    return (brush.Color.R / 255d * alpha + background.R * (1 - alpha),
                        brush.Color.G / 255d * alpha + background.G * (1 - alpha),
                        brush.Color.B / 255d * alpha + background.B * (1 - alpha));
                }
                static double Luminance((double R, double G, double B) color)
                {
                    static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                    return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
                }
            }
            if (Find(window, prefix + "PeriodEstimate") is not FrameworkElement row ||
                Find(window, prefix + "PeriodEstimateLabel") is not TextBlock { Text: "Estimated at reset" } label ||
                Find(window, prefix + "PeriodEstimateAmount") is not TextBlock amount || amount.Text != expected ||
                AutomationProperties.GetLabeledBy(amount) != label ||
                ToolTipService.GetToolTip(row) is null)
                throw new InvalidOperationException($"Compact estimate ({prefix}) label, amount, help or accessibility association is missing: " +
                    $"row={Find(window, prefix + "PeriodEstimate") is not null}, " +
                    $"label={(Find(window, prefix + "PeriodEstimateLabel") as TextBlock)?.Text ?? "missing"}, " +
                    $"amount={(Find(window, prefix + "PeriodEstimateAmount") as TextBlock)?.Text ?? "missing"}, expected={expected}, " +
                    $"labeledBy={(Find(window, prefix + "PeriodEstimateAmount") is TextBlock value ? AutomationProperties.GetLabeledBy(value) is not null : false)}, " +
                    $"help={(Find(window, prefix + "PeriodEstimate") is FrameworkElement estimate ? ToolTipService.GetToolTip(estimate) is not null : false)}.");
            var labelOrigin = label.TransformToVisual(row).TransformPoint(new(0, 0));
            var amountOrigin = amount.TransformToVisual(row).TransformPoint(new(0, 0));
            double labelGap = amountOrigin.X - (labelOrigin.X + label.ActualWidth);
            bool aligned = rightAlignAmount
                ? Math.Abs(amountOrigin.X + amount.ActualWidth - row.ActualWidth) <= 1
                : Math.Abs(labelGap - 12) <= 1 && Math.Abs(labelOrigin.X) <= 1;
            if (row.ActualWidth <= 0 || Math.Abs(labelOrigin.Y - amountOrigin.Y) > 1 ||
                amountOrigin.X < labelOrigin.X + label.ActualWidth ||
                !aligned ||
                amount.FontSize >= 21)
                throw new InvalidOperationException(rightAlignAmount
                    ? "Flyout estimate is not a secondary row with the amount at the right edge."
                    : "Settings estimate is not a secondary row with the amount 12 pixels beside the left-aligned label.");
        }
        static void AssertEstimateDisclosure(ReactorWindow window, string prefix, string expected)
        {
            if (Find(window, prefix + "PeriodEstimateDisclosure") is null ||
                Find(window, prefix + "PeriodEstimateMethod") is not TextBlock method || !method.Text.Contains("not an invoice") ||
                Find(window, prefix + "PeriodEstimateDetailAmount") is not TextBlock amount || amount.Text != expected ||
                Find(window, prefix + "PeriodEstimateDaily") is null ||
                Find(window, prefix + "PeriodEstimateStart") is not TextBlock boundary || !boundary.Text.EndsWith("00:00:00 UTC") ||
                Find(window, prefix + "PeriodEstimateReset") is null ||
                Find(window, prefix + "PeriodEstimateObserved") is null)
                throw new InvalidOperationException("Expanded Advanced information lost estimate method, amount or UTC diagnostics.");
        }

        async Task WaitForAccountSave()
        {
            await WaitForIdle();
            await OnUI(shell, () =>
            {
                if (shell.Session.Notice != "Account settings saved.")
                    throw new InvalidOperationException("Account estimate preference was not saved.");
            });
        }
        async Task WaitForIdle()
        {
            await WaitForSettingsUI(shell, "account estimate operation");
            await OnUI(shell, () =>
            {
                if (shell.Session.Error is { } error) throw new InvalidOperationException(error);
            });
        }
    }
    private static async Task SmokeUsageOverviewAsync(ReactorShell shell)
    {
        var original = default(Windows.Graphics.SizeInt32);
        await OnUI(shell, () => original = shell.SettingsWindow!.AppWindow.Size);
        try
        {
            foreach (int width in new[] { 1100, 640, 1100 })
            {
                double scale = 1;
                await OnUI(shell, () =>
                {
                    var root = (FrameworkElement)shell.SettingsWindow!.NativeWindow.Content;
                    scale = root.XamlRoot.RasterizationScale;
                    shell.SettingsWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), original.Height));
                });
                await WaitForUI(shell, $"compact Usage overview at {width} DIPs", () =>
                    Math.Abs(shell.SettingsWindow!.AppWindow.Size.Width - width * scale) <= 2 &&
                    LayoutReady(shell.SettingsWindow) &&
                    Math.Abs(((FrameworkElement)shell.SettingsWindow.NativeWindow.Content).ActualWidth - width) <= 32);
                await OnUI(shell, () =>
                {
                    var window = shell.SettingsWindow!;
                    var overview = Find(window, "UsageOverview") as FrameworkElement ??
                        throw new InvalidOperationException("Usage overview is missing.");
                    var amount = (FrameworkElement)Find(window, "UsageOverviewAmount")!;
                    var actions = (FrameworkElement)Find(window, "UsageOverviewActions")!;
                    var amountOrigin = amount.TransformToVisual(overview).TransformPoint(default);
                    var actionsOrigin = actions.TransformToVisual(overview).TransformPoint(default);
                    if (overview.ActualHeight > (width > 640 ? 152 : 190))
                        throw new InvalidOperationException($"Usage overview exceeds its compact height budget at {width} DIPs: {overview.ActualHeight:F1}.");
                    if (width > 640 && actionsOrigin.X < amountOrigin.X + amount.ActualWidth - 1)
                        throw new InvalidOperationException("Wide Usage overview is not using horizontal space.");
                    if (width == 640 && actionsOrigin.Y < amountOrigin.Y + amount.ActualHeight - 1)
                        throw new InvalidOperationException("Narrow Usage overview did not reflow.");
                    if (Find(window, "RefreshUsage") is not Button { IsEnabled: true } refresh ||
                        refresh.TransformToVisual(overview).TransformPoint(default).X + refresh.ActualWidth > overview.ActualWidth + 1)
                        throw new InvalidOperationException("Responsive Usage overview clipped its refresh action.");
                    if (Find(window, "TrayUsageDetails") is not null)
                        throw new InvalidOperationException("Usage overview repeats the per-account breakdown.");
                });
            }
        }
        finally
        {
            await OnUI(shell, () => shell.SettingsWindow!.AppWindow.Resize(original));
            await WaitForSettingsUI(shell, "restored Usage overview size");
        }
    }

    private static async Task SmokeUsageDisclosureAsync(ReactorShell shell)
    {
        if (shell.Session.Dashboard.Accounts.Count == 0) return;
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            foreach (var account in shell.Session.Dashboard.Accounts)
            {
                if (Find(window, account.Key + "_AdvancedAccountDetails") is not Expander { IsExpanded: false } ||
                    Find(window, account.Key + "_AccountDiagnosticsTable") is not null ||
                    Find(window, account.Key + "_AccountSummary") is null)
                    throw new InvalidOperationException("Usage account diagnostics must start collapsed with consumption still visible.");
            }
            if (Find(window, "github.com:1_AdvancedAccountDetails") is not Expander advanced ||
                new ExpanderAutomationPeer(advanced).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                throw new InvalidOperationException("Usage account disclosure is not accessible.");
            expand.Expand();
        });
        await WaitForSettingsUI(shell, "expanded Usage diagnostics");
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "github.com:1_DetailCredits") is not TextBlock { Text: "2,625" } ||
                Find(window, "example.ghe.com:2_AdvancedAccountDetails") is not Expander { IsExpanded: false } ||
                Find(window, "example.ghe.com:2_AccountDiagnosticsTable") is not null)
                throw new InvalidOperationException("Usage disclosure did not expose just the selected account's diagnostics.");
            shell.Session.Refresh();
        });
        await WaitForSettingsUI(shell, "refreshed Usage disclosure");
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "github.com:1_AdvancedAccountDetails") is not Expander { IsExpanded: true } advanced ||
                Find(window, "github.com:1_DetailCredits") is not TextBlock { Text: "2,625" } ||
                new ExpanderAutomationPeer(advanced).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                throw new InvalidOperationException("Refreshing usage lost the account disclosure state.");
            expand.Collapse();
        });
        await WaitForSettingsUI(shell, "collapsed Usage diagnostics");
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "github.com:1_AdvancedAccountDetails") is not Expander { IsExpanded: false } ||
                Find(window, "github.com:1_AccountDiagnosticsTable") is not null ||
                Find(window, "github.com:1_AccountConsumption") is not TextBlock { Text: "$26.25" })
                throw new InvalidOperationException("Collapsing diagnostics hid consumption or retained advanced fields.");
        });
    }
    private static void AssertBackAccelerators(ReactorWindow window)
    {
        if (window.NativeWindow.Content is not UIElement root ||
            root.KeyboardAcceleratorPlacementMode != Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden ||
            root.KeyboardAccelerators.Count != 2 ||
            !root.KeyboardAccelerators.Any(a => a.Key == Windows.System.VirtualKey.Left &&
                a.Modifiers == Windows.System.VirtualKeyModifiers.Menu && a.ScopeOwner == root) ||
            !root.KeyboardAccelerators.Any(a => a.Key == Windows.System.VirtualKey.GoBack &&
                a.Modifiers == Windows.System.VirtualKeyModifiers.None && a.ScopeOwner == root))
            throw new InvalidOperationException("Settings Back accelerators are missing, incorrectly scoped, or exposing a window-wide tooltip.");
    }
    private static nint FlyoutHwnd(ReactorShell shell) =>
        WinRT.Interop.WindowNative.GetWindowHandle((shell.Flyout ??
            throw new InvalidOperationException("Flyout has not been created.")).NativeWindow);

    private static void AssertFlyoutVisible(ReactorShell shell, string stage)
    {
        var hwnd = FlyoutHwnd(shell);
        // Synthetic Shell callbacks do not carry Explorer's foreground permission.
        // Assert actual visibility even when Windows legitimately refuses focus.
        if (Win32.IsWindowVisible(hwnd) == 0)
            throw new InvalidOperationException($"Tray activation failed {stage}: native visible={Win32.IsWindowVisible(hwnd) != 0}, foreground={Win32.GetForegroundWindow() == hwnd}.");
    }

    private static async Task WaitForFlyoutVisibility(ReactorShell shell, bool expected, string stage)
    {
        await WaitForUI(shell, $"flyout {(expected ? "visible" : "hidden")} {stage}",
            () => (shell.Flyout is not null && Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0) == expected);
    }

    private static Task WaitForSettingsUI(ReactorShell shell, string stage) =>
        WaitForUI(shell, stage, () => LayoutReady(shell.SettingsWindow) && shell.Session.Initialized && !shell.Session.Busy &&
            shell.SettingsRenderedRevision == shell.Session.Revision);

    private static Task WaitForFlyoutUI(ReactorShell shell, string stage) =>
        WaitForUI(shell, stage, () => LayoutReady(shell.Flyout) && shell.Session.Initialized && !shell.Session.Busy &&
            shell.FlyoutRenderedRevision == shell.Session.Revision);

    private static bool LayoutReady(ReactorWindow? window)
    {
        if (window?.NativeWindow.Content is not FrameworkElement { IsLoaded: true } root) return false;
        root.UpdateLayout();
        return root.ActualWidth > 0 && root.ActualHeight > 0 && TreeLoaded(root);
    }

    private static bool TreeLoaded(DependencyObject node)
    {
        if (node is FrameworkElement { IsLoaded: false }) return false;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (!TreeLoaded(VisualTreeHelper.GetChild(node, i))) return false;
        return true;
    }

    private static async Task WaitForUI(ReactorShell shell, string stage, Func<bool> ready)
    {
        RecordSmokePhase(shell.Session.Controller.DataDirectory, $"Waiting for {stage}");
        var elapsed = Stopwatch.StartNew();
        string state = "";
        do
        {
            var remaining = TimeSpan.FromSeconds(5) - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            bool complete = false;
            try
            {
                await OnUI(shell, () =>
                {
                    complete = ready();
                    state = $"page={shell.Session.Page}, initialized={shell.Session.Initialized}, busy={shell.Session.Busy}, " +
                        $"revision={shell.Session.Revision}, settingsRender={shell.SettingsRenderedRevision}, " +
                        $"flyoutRender={shell.FlyoutRenderedRevision}, addForm={shell.Session.ShowAddForm}, " +
                        $"back={shell.Session.CanGoBack}, settings={shell.SettingsWindow is not null}, " +
                        $"flyoutVisible={shell.Flyout is not null && Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0}, " +
                        $"foreground=0x{Win32.GetForegroundWindow():X}, error={shell.Session.Error ?? "none"}";
                }).WaitAsync(remaining);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"UI dispatch stalled at {stage}: {state}", ex);
            }
            if (complete) return;
            await Task.Delay(25);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(5));
        string renderFailure = "";
        if (shell.SettingsRenderedRevision < 0 && shell.SettingsWindow is { } failedWindow)
            await OnUI(shell, () =>
            {
                if (failedWindow.NativeWindow.Content is DependencyObject root)
                    renderFailure = " | Rendered text: " + string.Join(" | ", TextInTree(root));
            });
        throw new TimeoutException($"UI readiness timed out at {stage} after {elapsed.Elapsed.TotalSeconds:F2}s: {state}{renderFailure}");

        static IEnumerable<string> TextInTree(DependencyObject node)
        {
            if (node is TextBlock text) yield return text.Text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                foreach (string fragment in TextInTree(VisualTreeHelper.GetChild(node, i))) yield return fragment;
        }
    }

    private static async Task SmokeTraySettingsAsync(ReactorShell shell)
    {
        await OnUI(shell, () => shell.ShowSettings(SettingsPage.General));
        await WaitForSettingsUI(shell, "General tray controls");
        nint installedImage = 0;
        await OnUI(shell, () =>
        {
            AssertTrayPreview(shell);
            SmokePreviewBuffer();
            installedImage = shell.TrayIcons.Single().ImageHandle;
            if (Find(shell.SettingsWindow!, "TrayStyle") is not ComboBox style ||
                Find(shell.SettingsWindow!, "TrayMode") is not ComboBox mode)
                throw new InvalidOperationException("Native tray settings controls did not render.");
            style.SelectedIndex = 1;
            mode.SelectedIndex = 1;
        });
        await WaitForSettingsUI(shell, "draft tray style/mode");
        await OnUI(shell, () =>
        {
            AssertTrayPreview(shell);
            if (shell.TrayIcons.Count != 1 || shell.TrayIcons.Single().ImageHandle != installedImage ||
                shell.Session.Controller.Settings is not { TrayStyle: TrayIconStyle.Pie, TrayMode: TrayDisplayMode.RollUp })
                throw new InvalidOperationException("Draft preview changed the installed tray before Save.");
        });
        await InvokeButtonAsync(shell, "SaveGeneralSettings");
        await WaitForTraySave();
        uint retired = 0;
        await OnUI(shell, () =>
        {
            if (shell.Session.Controller.Settings is not { TrayStyle: TrayIconStyle.Percentage, TrayMode: TrayDisplayMode.PerAccount } ||
                shell.TrayIcons.Count != Math.Max(1, shell.Session.Dashboard.Accounts.Count))
                throw new InvalidOperationException("Tray preferences did not reach the Shell.");
            var icon = shell.TrayIcons.First();
            if (icon.AccountKey is not null)
            {
                retired = icon.Id;
                SendTraySelection(shell, Win32.NIN_KEYSELECT, icon.Id);
            }
            // Only this app's icons are re-added; Explorer and other apps are untouched.
            Win32.SendMessage(shell.TrayHandle, Win32.RegisterWindowMessage("TaskbarCreated"), 0, 0);
            Win32.SendMessage(shell.TrayHandle, 0x7E, 0, 0);
        });
        await WaitForSettingsUI(shell, "per-account selection and taskbar recovery");
        await OnUI(shell, () =>
        {
            if (retired != 0 && shell.Session.SelectedAccount != shell.TrayIcons.First().AccountKey)
                throw new InvalidOperationException("Per-account callback opened the wrong identity.");
            shell.ShowSettings(SettingsPage.General);
        });
        await WaitForSettingsUI(shell, "tray account selections");
        (uint Id, nint Image)[] installedIcons = [];
        bool hasTrayAccounts = false;
        await OnUI(shell, () =>
        {
            hasTrayAccounts = shell.Session.Dashboard.Accounts.Count > 0;
            installedIcons = shell.TrayIcons.Select(icon => (icon.Id, icon.ImageHandle)).ToArray();
            foreach (var account in shell.Session.Dashboard.Accounts)
            {
                if (Find(shell.SettingsWindow!, "TrayAccount-" + account.Key) is not CheckBox selection)
                    throw new InvalidOperationException("Account inclusion checkbox is missing.");
                selection.IsChecked = false;
            }
        });
        await WaitForSettingsUI(shell, "draft tray exclusions");
        await OnUI(shell, () =>
        {
            AssertTrayPreview(shell);
            if (!shell.TrayIcons.Select(icon => (icon.Id, icon.ImageHandle)).SequenceEqual(installedIcons))
                throw new InvalidOperationException("Draft exclusions changed installed tray icons.");
            if (!hasTrayAccounts && (shell.Session.HasUnsavedChanges ||
                Find(shell.SettingsWindow!, "SaveGeneralSettings") is not Button { IsEnabled: false } ||
                Find(shell.SettingsWindow!, "CancelSettingsChanges") is not Button { IsEnabled: false }))
                throw new InvalidOperationException("Empty account exclusions created a draft or enabled Save/Cancel.");
        });
        if (hasTrayAccounts)
        {
            await InvokeButtonAsync(shell, "SaveGeneralSettings");
            await WaitForTraySave();
        }
        await OnUI(shell, () =>
        {
            if (shell.TrayIcons.Count != 1 || shell.TrayIcons.First().AccountKey is not null ||
                shell.Session.Dashboard.Tray?.RollUp.Percent is not null)
                throw new InvalidOperationException("Empty selection did not retain a neutral access icon.");
            if (retired != 0)
            {
                int revision = shell.Session.Revision;
                SendTraySelection(shell, Win32.NIN_KEYSELECT, retired);
                if (shell.Session.Revision != revision)
                    throw new InvalidOperationException("A retired callback was processed.");
            }
            SendTraySelection(shell, Win32.NIN_KEYSELECT);
        });
        await WaitForFlyoutVisibility(shell, true, "with no selected tray accounts");
        await OnUI(shell, () => shell.ShowSettings(SettingsPage.General));
        await WaitForSettingsUI(shell, "restore tray preferences");
        await OnUI(shell, () =>
        {
            ((ComboBox)Find(shell.SettingsWindow!, "TrayStyle")!).SelectedIndex = 0;
            ((ComboBox)Find(shell.SettingsWindow!, "TrayMode")!).SelectedIndex = 0;
            foreach (var account in shell.Session.Dashboard.Accounts)
                ((CheckBox)Find(shell.SettingsWindow!, "TrayAccount-" + account.Key)!).IsChecked = true;
        });
        await WaitForSettingsUI(shell, "draft restored tray preferences");
        await InvokeButtonAsync(shell, "SaveGeneralSettings");
        await WaitForTraySave();
        Image? retiredPreview = null;
        await OnUI(shell, () =>
        {
            AssertTrayPreview(shell);
            retiredPreview = (Image)Find(shell.SettingsWindow!, "TrayPreviewIcon-rollup")!;
            if (shell.TrayIcons.Count != 1 || shell.Session.Controller.Settings.TrayStyle != TrayIconStyle.Pie)
                throw new InvalidOperationException("Restoring the roll-up pie failed.");
            shell.Session.Navigate(SettingsPage.Notifications);
        });
        await WaitForSettingsUI(shell, "unmounted tray preview");
        await OnUI(shell, () =>
        {
            if (retiredPreview!.Source is not null)
                throw new InvalidOperationException("Unmounted preview retained its WinUI pixel buffer.");
        });

        async Task WaitForTraySave()
        {
            await WaitForSettingsUI(shell, "tray settings save");
            await OnUI(shell, () =>
            {
                if (shell.Session.Error is { } error) throw new InvalidOperationException(error);
                if (shell.Session.Notice != "Settings saved.") throw new InvalidOperationException("Tray settings were not saved.");
            });
        }
    }

    private static void AssertTrayPreview(ReactorShell shell)
    {
        var preview = shell.Session.PreviewTray();
        foreach (var icon in preview.Icons)
        {
            string id = "TrayPreviewIcon-" + (icon.AccountKey ?? "rollup");
            if (Find(shell.SettingsWindow!, id) is not Image { Source: WriteableBitmap bitmap } image ||
                image.Width != 16 || image.Height != 16)
                throw new InvalidOperationException("Native-size live preview image is missing.");
            byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.ReadExactly(bytes);
            var palette = TrayIconRenderer.SystemPalette();
            var expected = TrayIconRenderer.Pixels(icon, preview.Style, bitmap.PixelWidth, palette);
            if (!MemoryMarshal.Cast<byte, uint>(bytes.AsSpan()).SequenceEqual(expected))
                throw new InvalidOperationException("Preview pixels differ from the actual tray renderer.");
            if (image.Parent is not Border { Background: SolidColorBrush swatch } ||
                swatch.Color != Windows.UI.Color.FromArgb(255, (byte)(palette.Background >> 16),
                    (byte)(palette.Background >> 8), (byte)palette.Background))
                throw new InvalidOperationException("Transparent preview lacks its taskbar-colored background swatch.");
            if (!AutomationProperties.GetName(image).Contains(icon.Details, StringComparison.Ordinal))
                throw new InvalidOperationException("Preview lacks accessible identity and availability details.");
        }
    }

    private static void SmokePreviewBuffer()
    {
        var image = new Image();
        var indicator = new TrayIndicator(null, "Synthetic", 50, 1, 2, "Partial", "Partial");
        TrayPreviewImage.Apply(image, indicator, TrayIconStyle.Pie);
        var bitmap = image.Source;
        uint gdi = Win32.GetGuiResources(Win32.GetCurrentProcess(), 0);
        uint user = Win32.GetGuiResources(Win32.GetCurrentProcess(), 1);
        for (int i = 0; i < 300; i++)
        {
            var next = indicator with { Percent = i % 3 == 0 ? null : i, IncludedAccounts = i % 3 == 0 ? 0 : 1 };
            var style = i % 2 == 0 ? TrayIconStyle.Pie : TrayIconStyle.Percentage;
            TrayPreviewImage.Apply(image, next, style);
            if (!ReferenceEquals(bitmap, image.Source))
                throw new InvalidOperationException("Same-size preview updates accumulate WinUI bitmaps.");
        }
        if (Win32.GetGuiResources(Win32.GetCurrentProcess(), 0) > gdi + 2 ||
            Win32.GetGuiResources(Win32.GetCurrentProcess(), 1) > user + 2)
            throw new InvalidOperationException("Preview generation leaked native handles.");
    }

    private static void SendTraySelection(ReactorShell shell, int notification, uint id = 1) =>
        Win32.SendMessage(shell.TrayHandle, Win32.WM_TRAY, 0, (nint)((id << 16) | (uint)notification));

    private static async Task InvokeButtonAsync(ReactorShell shell, string id, bool flyout = false, Func<Button?>? findButton = null)
    {
        Button? FindButton() => findButton is not null ? findButton() : (flyout ? shell.Flyout : shell.SettingsWindow) is { } window
            ? Find(window, id) as Button : null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Button? button = null;
        void Clicked(object sender, RoutedEventArgs args) => completion.TrySetResult();
        try
        {
            await WaitForUI(shell, $"automation button {id} loaded and enabled", () =>
            {
                if (FindButton() is not { IsEnabled: true, IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } ready)
                    return false;
                if (new ButtonAutomationPeer(ready).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
                    throw new InvalidOperationException($"Button {AutomationProperties.GetAutomationId(ready)} is not ready " +
                        "for its accessible invoke action.");
                // Reactor can replace the button between dispatcher turns. Check readiness,
                // subscribe and invoke the same instance without yielding to another render.
                button = ready;
                // Invoke queues Click. Wait for the event, not merely for Invoke to return.
                button.Click += Clicked;
                invoke.Invoke();
                return true;
            });
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Accessible button {id} invocation did not deliver Click.", ex);
            }
        }
        finally
        {
            if (button is not null) await OnUI(shell, () => button.Click -= Clicked);
        }
    }
    private static Task OnUI(ReactorShell shell, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Probe after normal-priority automation, model and Reactor work has drained.
        if (ReactorApp.UIDispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) != true) throw new InvalidOperationException("Smoke UI dispatcher rejected an operation.");
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static FrameworkElement? Find(ReactorWindow window, string id)
    {
        if (window.NativeWindow.Content is not DependencyObject root) return null;
        return FindInTree(root, id);
    }
    private static FrameworkElement? FindInTree(DependencyObject node, string id)
    {
        if (node is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindInTree(VisualTreeHelper.GetChild(node, i), id) is { } child) return child;
        }
        return null;
    }
    private static void SmokeCredentials()
    {
        var vault = new CredentialVault();
        string target = "GHCPSpendTray/test/" + Guid.NewGuid().ToString("N");
        try
        {
            vault.Write(target, "{\"version\":1,\"test\":\"synthetic-not-a-token\"}");
            if (vault.Read(target) != "{\"version\":1,\"test\":\"synthetic-not-a-token\"}")
                throw new InvalidOperationException("Credential Manager round-trip mismatch.");
        }
        finally { vault.Delete(target); }
        if (vault.Read(target) is not null) throw new InvalidOperationException("Test credential cleanup failed.");
    }
}
