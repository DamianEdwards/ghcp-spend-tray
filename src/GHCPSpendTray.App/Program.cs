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

namespace GHCPSpendTray.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool packageSmoke = args.Contains("--package-smoke-test", StringComparer.Ordinal);
        bool smoke = packageSmoke || args.Contains("--smoke-test", StringComparer.Ordinal);
        bool emptyDemo = args.Contains("--demo-empty", StringComparer.Ordinal);
        bool demo = smoke || emptyDemo || args.Contains("--demo", StringComparer.Ordinal);
        string[] bootstrapArgs = args.Where(a => a is not "--smoke-test" and not "--package-smoke-test" and not "--demo" and not "--demo-empty").ToArray();
        try
        {
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
            using IApplicationController controller = demo ? new DemoController(runtime.DataDirectory, emptyDemo, smokeTime) :
                new ApplicationController(runtime.DataDirectory, runtime.IsPortable);
            int smokeExit = 0;
            ReactorShell? shell = null;
            ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
            try
            {
                ReactorApp.Run(context =>
                {
                    if (smoke) RecordSmokePhase(runtime.DataDirectory, "Reactor initialized; creating tray shell");
                    shell = new ReactorShell(controller);
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
                    if (smoke) _ = RunSmokeAsync(shell, runtime.DataDirectory, smokeTime!, code => smokeExit = code);
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

    private static async Task RunSmokeAsync(ReactorShell shell, string directory, SmokeTimeProvider time, Action<int> setExit)
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
            await Task.Delay(1800);
            await OnUI(shell, () =>
            {
                if (shell.Flyout is not null) throw new InvalidOperationException("Hidden startup unexpectedly opened a window.");
                SendTraySelection(shell, 0x400);
            });
            await WaitForFlyoutVisibility(shell, true, "after mouse single-click");
            await OnUI(shell, () =>
            {
                AssertFlyoutVisible(shell, "after mouse single-click");
                SendTraySelection(shell, 0x400);
            });
            await WaitForFlyoutVisibility(shell, false, "after mouse click on open flyout");
            await OnUI(shell, () => SendTraySelection(shell, 0x401));
            await WaitForFlyoutVisibility(shell, true, "after keyboard selection");
            await OnUI(shell, () => SendTraySelection(shell, 0x401));
            await WaitForFlyoutVisibility(shell, false, "after keyboard selection toggled closed");
            await OnUI(shell, () =>
            {
                SendTraySelection(shell, 0x400);
                SendTraySelection(shell, 0x203);
                SendTraySelection(shell, 0x400);
                if (Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0 || shell.SettingsWindow is null)
                    throw new InvalidOperationException("Double-click flashed the flyout instead of opening settings.");
            });
            await Task.Delay(checked((int)Win32.GetDoubleClickTime()) + 150);
            await OnUI(shell, () =>
            {
                if (Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0)
                    throw new InvalidOperationException("Double-click's pending single click opened the flyout.");
                shell.Session.Navigate(SettingsPage.Notifications);
                SendTraySelection(shell, 0x400);
                SendTraySelection(shell, 0x400);
                if (shell.Session.Page != SettingsPage.Usage ||
                    Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0)
                    throw new InvalidOperationException("Two mouse selections did not open usage settings.");
                shell.ShowSettings(SettingsPage.Usage);
            });
            await Task.Delay(checked((int)Win32.GetDoubleClickTime()) + 150);
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
                    InvokeButton(add);
                    if (shell.Session.Page != SettingsPage.Accounts || !shell.Session.ShowAddForm)
                        throw new InvalidOperationException("Usage empty-state action did not open account setup.");
                    shell.Session.TryGoBack();
                    shell.Session.Navigate(SettingsPage.Usage);
                }
                else
                {
                    if (Find(shell.SettingsWindow!, "UsageTotal") is not TextBlock { Text: "$42.75" })
                        throw new InvalidOperationException("Usage summary did not render.");
                    if (Find(shell.SettingsWindow!, "RefreshUsage") is not Button refresh || !refresh.IsEnabled)
                        throw new InvalidOperationException("Usage refresh control did not render.");
                    InvokeButton(refresh);
                }
                if (Find(shell.SettingsWindow!, "AccountHistory") is not null)
                    throw new InvalidOperationException("Usage page still displayed sampled spending history.");
            });
            RecordSmokePhase(directory, "Testing Usage disclosures");
            await SmokeUsageDisclosureAsync(shell);
            await SmokePeriodEstimateAsync(shell, time, phase => RecordSmokePhase(directory, phase));
            RecordSmokePhase(directory, "Testing flyout focus transitions and account onboarding");
            await OnUI(shell, () => SendTraySelection(shell, 0x401));
            await Task.Delay(250);
            await OnUI(shell, () =>
            {
                AssertFlyoutVisible(shell, "after opening from settings");
                // Queue a deactivation, then reactivate before its deferred dismissal runs.
                shell.SettingsWindow!.Activate();
                shell.ShowFlyout();
            });
            await Task.Delay(250);
            await OnUI(shell, () =>
            {
                AssertFlyoutVisible(shell, "after rapid focus transition");
                if (!shell.Session.Initialized) throw new InvalidOperationException("Controller did not initialize.");
                var flyout = shell.Flyout ?? throw new InvalidOperationException("Flyout did not open.");
                if (shell.Session.Dashboard.Accounts.Count == 0)
                {
                    if (Find(flyout, "AddFirstAccount") is not Button connect)
                        throw new InvalidOperationException("Empty-state connect action did not render.");
                    InvokeButton(connect);
                }
                else if (Find(flyout, "TotalConsumption") is not TextBlock { Text: "$42.75" })
                    throw new InvalidOperationException("Reactor cost display did not render.");
                else
                {
                    if (Find(flyout, "AccountAvatar-" + shell.Session.Dashboard.Accounts[0].Key) is not PersonPicture)
                        throw new InvalidOperationException("Flyout account picture did not render.");
                    if (Find(flyout, "OpenSettings") is not Button settings) throw new InvalidOperationException("Settings gear is missing.");
                    InvokeButton(settings);
                }
            });
            await Task.Delay(700);
            await OnUI(shell, () =>
            {
                if (shell.SettingsWindow is null) throw new InvalidOperationException("Settings action did not open a window.");
                if (shell.Session.Dashboard.Accounts.Count != 0) shell.Session.Navigate(SettingsPage.Accounts);
            });
            await Task.Delay(500);
            await OnUI(shell, () =>
            {
                if (shell.Session.Dashboard.Accounts.Count == 0) return;
                if (Find(shell.SettingsWindow!, "AccountAvatar-" + shell.Session.Dashboard.Accounts[0].Key) is not PersonPicture)
                    throw new InvalidOperationException("Settings account picture did not render.");
                shell.Session.AddAccount();
            });
            await Task.Delay(500);
            await OnUI(shell, () =>
            {
                var settings = shell.SettingsWindow ?? throw new InvalidOperationException("Settings did not open.");
                if (Find(settings, "AccountOnboarding") is null ||
                    Find(settings, "ChangeSignInHost") is not Button changeHost ||
                    Find(settings, "AccountIdentityConfirmation") is not null)
                    throw new InvalidOperationException("Add-account deep link did not render.");
                InvokeButton(changeHost);
            });
            await Task.Delay(300);
            await OnUI(shell, () =>
            {
                var settings = shell.SettingsWindow!;
                if (Find(settings, "AccountHostSelection") is not ComboBox || !shell.Session.EditingHost)
                    throw new InvalidOperationException("Change host did not expose host-specific sign-in settings.");
                if (Find(settings, "AccountBack") is not Button back)
                    throw new InvalidOperationException("Onboarding Back action did not render.");
                InvokeButton(back);
            });
            await Task.Delay(300);
            await OnUI(shell, () =>
            {
                if (shell.Session.ShowAddForm || shell.Session.CanGoBack ||
                    Find(shell.SettingsWindow!, "AccountOnboarding") is not null)
                    throw new InvalidOperationException("Onboarding Back did not return to the accounts list.");
                AssertBackAccelerators(shell.SettingsWindow!);
                shell.Session.Navigate(SettingsPage.Notifications);
            });
            await Task.Delay(500);
            if (shell.Session.Dashboard.Accounts.Count > 0)
            {
                await OnUI(shell, () => shell.Session.EditAccount(shell.Session.Dashboard.Accounts[0].Key));
                await Task.Delay(500);
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
                await Task.Delay(500);
                await OnUI(shell, () =>
                {
                    if (Find(shell.SettingsWindow!, "DetailCredits") is not TextBlock { Text: "2,625" })
                        throw new InvalidOperationException("Expanded account details did not expose labeled data.");
                    if (Find(shell.SettingsWindow!, "AccountBack") is not Button back)
                        throw new InvalidOperationException("Account details Back action did not render.");
                    InvokeButton(back);
                });
                await Task.Delay(300);
                await OnUI(shell, () =>
                {
                    if (shell.Session.SelectedAccount is not null || shell.Session.CanGoBack)
                        throw new InvalidOperationException("Account details Back did not return to accounts.");
                    shell.Session.Navigate(SettingsPage.Notifications);
                });
                await Task.Delay(300);
            }
            RecordSmokePhase(directory, "Testing tray settings, preview and callback lifecycle");
            await SmokeTraySettingsAsync(shell);
            RecordSmokePhase(directory, "Testing settings close/reopen and native notification/credentials");
            await OnUI(shell, () => shell.SettingsWindow!.NativeWindow.Close());
            await Task.Delay(300);
            await OnUI(shell, () =>
            {
                if (shell.SettingsWindow is not null || shell.Session.CanGoBack)
                    throw new InvalidOperationException("Closing settings retained its window or Back target.");
                shell.ShowSettings(SettingsPage.Notifications);
            });
            await Task.Delay(300);
            await OnUI(shell, () =>
            {
                AssertBackAccelerators(shell.SettingsWindow!);
                if (shell.Session.Page != SettingsPage.Notifications) throw new InvalidOperationException("Settings navigation failed.");
                SmokeCredentials();
                if (shell.Session.TestNotification?.Invoke() != true) throw new InvalidOperationException("Shell notification rejected.");
                RecordSmokePhase(directory, "All synthetic smoke assertions passed; exiting Reactor");
                File.WriteAllText(Path.Combine(directory, "native-smoke-result.txt"),
                    "PASS: mouse/keyboard tray toggle, double-click settings without flyout flash, hide/reopen and focus transitions, " +
                    "Reactor cost flyout, usage-first settings without sampled chart, independent collapsed account diagnostics, " +
                    "per-account estimate opt-in/off/unavailable, UTC disclosure and unchanged observed totals/tray, " +
                    "account avatars and diagnostics, add-account deep link, " +
                    "account Back controls and scoped keyboard accelerator registration, native controls, " +
                    "tray style/mode/selection controls, per-account callback mapping, retired callbacks ignored, neutral access icon, " +
                    "unsaved live preview pixel parity, Save isolation and reusable image-buffer lifecycle, " +
                    "simulated TaskbarCreated recovery and display-change repaint, " +
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
    private sealed class SmokeTimeProvider : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
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
        await Task.Delay(150);
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "PeriodEstimate") is not null || Find(window, "PeriodEstimateDisclosure") is not null ||
                Find(window, "ShowPeriodEstimate") is not CheckBox { IsChecked: false } preference)
                throw new InvalidOperationException("Period estimate must be off by default and absent, not zero.");
            time.Now = start.AddDays(3);
            preference.IsChecked = true;
        });
        await Task.Delay(100);
        await OnUI(shell, () =>
        {
            if (shell.Session.Controller.AccountSettings(key).ShowPeriodEstimate ||
                Find(shell.SettingsWindow!, "PeriodEstimate") is not null)
                throw new InvalidOperationException("Draft forecast opt-in changed saved usage before Save.");
            InvokeButton((Button)Find(shell.SettingsWindow!, "SaveAccount")!);
        });
        phase("Estimates: saving opt-in and checking account row");
        await WaitForAccountSave();
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            AssertEstimateRow(window, "", amount);
            if (Find(window, "PeriodEstimateWarning") is null ||
                Find(window, "PeriodEstimateDisclosure") is not null ||
                Find(window, "AccountConsumption") is not TextBlock { Text: "$26.25" } ||
                shell.Session.Dashboard.ConsumptionUsd != 42.75m || shell.Session.Dashboard.Accounts[0].Percent != 105m ||
                shell.Session.Dashboard.Tray!.RollUp != trayBefore)
                throw new InvalidOperationException("Estimate row, collapsed disclosure or observed usage contract failed.");
            ExpandDetails(window, "AdvancedAccountDetails");
        });
        await Task.Delay(150);
        phase("Estimates: checking expanded account details");
        await OnUI(shell, () =>
        {
            AssertEstimateDisclosure(shell.SettingsWindow!, "", amount);
            shell.Session.Navigate(SettingsPage.Usage);
        });
        await Task.Delay(100);
        phase("Estimates: checking Usage row");
        await OnUI(shell, () =>
        {
            AssertEstimateRow(shell.SettingsWindow!, key + "_", amount);
            if (Find(shell.SettingsWindow!, "example.ghe.com:2_PeriodEstimate") is not null)
                throw new InvalidOperationException("Usage estimates are missing or not independently opt-in.");
            ExpandDetails(shell.SettingsWindow!, key + "_AdvancedAccountDetails");
        });
        await Task.Delay(150);
        phase("Estimates: checking expanded Usage details");
        await OnUI(shell, () =>
        {
            AssertEstimateDisclosure(shell.SettingsWindow!, key + "_", amount);
            shell.ShowFlyout();
        });
        await WaitForFlyoutVisibility(shell, true, "with account estimate enabled");
        phase("Estimates: checking flyout row");
        await Task.Delay(100);
        await OnUI(shell, () =>
        {
            AssertEstimateRow(shell.Flyout!, key + "_Flyout_", amount);
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
        await Task.Delay(150);
        phase("Estimates: checking expanded unavailable reason");
        await OnUI(shell, () =>
        {
            AssertEstimateDisclosure(shell.SettingsWindow!, "", "Unavailable");
            if (Find(shell.SettingsWindow!, "PeriodEstimateUnavailable") is not TextBlock reason || !reason.Text.Contains("24 hours"))
                throw new InvalidOperationException("Expanded estimate details lost the unavailable reason.");
        });
        await Task.Delay(100);
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, "PeriodEstimate") is null)
                throw new InvalidOperationException("Draft disable hid the forecast before Save.");
            InvokeButton((Button)Find(shell.SettingsWindow!, "SaveAccount")!);
        });
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
        await Task.Delay(100);
        await OnUI(shell, () =>
        {
            if (Find(shell.SettingsWindow!, key + "_PeriodEstimate") is not null)
                throw new InvalidOperationException("Disabled forecast remained in Usage.");
            shell.ShowFlyout();
        });
        await WaitForFlyoutVisibility(shell, true, "after disabling the account estimate");
        phase("Estimates: checking disabled flyout row");
        await Task.Delay(100);
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
        static void AssertEstimateRow(ReactorWindow window, string prefix, string expected)
        {
            if (Find(window, prefix + "PeriodEstimate") is not FrameworkElement row ||
                Find(window, prefix + "PeriodEstimateLabel") is not TextBlock { Text: "Estimated at reset" } label ||
                Find(window, prefix + "PeriodEstimateAmount") is not TextBlock amount || amount.Text != expected ||
                AutomationProperties.GetLabeledBy(amount) != label ||
                ToolTipService.GetToolTip(row) is null)
                throw new InvalidOperationException("Compact estimate label, amount, help or accessibility association is missing.");
            var labelOrigin = label.TransformToVisual(row).TransformPoint(new(0, 0));
            var amountOrigin = amount.TransformToVisual(row).TransformPoint(new(0, 0));
            if (row.ActualWidth <= 0 || Math.Abs(labelOrigin.Y - amountOrigin.Y) > 1 ||
                amountOrigin.X < labelOrigin.X + label.ActualWidth ||
                Math.Abs(amountOrigin.X + amount.ActualWidth - row.ActualWidth) > 1 ||
                amount.FontSize >= 21)
                throw new InvalidOperationException("Estimate is not a secondary, right-aligned row like the macOS reference.");
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
            for (int attempt = 0; attempt < 100; attempt++)
            {
                bool ready = false;
                await OnUI(shell, () =>
                {
                    if (shell.Session.Error is { } error) throw new InvalidOperationException(error);
                    ready = !shell.Session.Busy;
                });
                if (ready) { await Task.Delay(100); return; }
                await Task.Delay(30);
            }
            throw new TimeoutException("Account estimate operation did not complete.");
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
        await Task.Delay(300);
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "github.com:1_DetailCredits") is not TextBlock { Text: "2,625" } ||
                Find(window, "example.ghe.com:2_AdvancedAccountDetails") is not Expander { IsExpanded: false } ||
                Find(window, "example.ghe.com:2_AccountDiagnosticsTable") is not null)
                throw new InvalidOperationException("Usage disclosure did not expose just the selected account's diagnostics.");
            shell.Session.Refresh();
        });
        await Task.Delay(300);
        await OnUI(shell, () =>
        {
            var window = shell.SettingsWindow!;
            if (Find(window, "github.com:1_AdvancedAccountDetails") is not Expander { IsExpanded: true } advanced ||
                Find(window, "github.com:1_DetailCredits") is not TextBlock { Text: "2,625" } ||
                new ExpanderAutomationPeer(advanced).GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                throw new InvalidOperationException("Refreshing usage lost the account disclosure state.");
            expand.Collapse();
        });
        await Task.Delay(300);
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
        for (int attempt = 0; attempt < 100; attempt++)
        {
            bool visible = false;
            await OnUI(shell, () => visible = shell.Flyout is not null && Win32.IsWindowVisible(FlyoutHwnd(shell)) != 0);
            if (visible == expected) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"Tray flyout did not become {(expected ? "visible" : "hidden")} {stage}.");
    }

    private static async Task SmokeTraySettingsAsync(ReactorShell shell)
    {
        await OnUI(shell, () => shell.ShowSettings(SettingsPage.General));
        await Task.Delay(200);
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
        await Task.Delay(200);
        await OnUI(shell, () =>
        {
            AssertTrayPreview(shell);
            if (shell.TrayIcons.Count != 1 || shell.TrayIcons.Single().ImageHandle != installedImage ||
                shell.Session.Controller.Settings is not { TrayStyle: TrayIconStyle.Pie, TrayMode: TrayDisplayMode.RollUp })
                throw new InvalidOperationException("Draft preview changed the installed tray before Save.");
            InvokeButton((Button)Find(shell.SettingsWindow!, "SaveGeneralSettings")!);
        });
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
                SendTraySelection(shell, 0x401, icon.Id);
            }
            // Only this app's icons are re-added; Explorer and other apps are untouched.
            Win32.SendMessage(shell.TrayHandle, Win32.RegisterWindowMessage("TaskbarCreated"), 0, 0);
            Win32.SendMessage(shell.TrayHandle, 0x7E, 0, 0);
        });
        await Task.Delay(200);
        await OnUI(shell, () =>
        {
            if (retired != 0 && shell.Session.SelectedAccount != shell.TrayIcons.First().AccountKey)
                throw new InvalidOperationException("Per-account callback opened the wrong identity.");
            shell.ShowSettings(SettingsPage.General);
        });
        await Task.Delay(200);
        (uint Id, nint Image)[] installedIcons = [];
        await OnUI(shell, () =>
        {
            installedIcons = shell.TrayIcons.Select(icon => (icon.Id, icon.ImageHandle)).ToArray();
            foreach (var account in shell.Session.Dashboard.Accounts)
            {
                if (Find(shell.SettingsWindow!, "TrayAccount-" + account.Key) is not CheckBox selection)
                    throw new InvalidOperationException("Account inclusion checkbox is missing.");
                selection.IsChecked = false;
            }
        });
        await Task.Delay(200);
        await OnUI(shell, () =>
        {
            AssertTrayPreview(shell);
            if (!shell.TrayIcons.Select(icon => (icon.Id, icon.ImageHandle)).SequenceEqual(installedIcons))
                throw new InvalidOperationException("Draft exclusions changed installed tray icons.");
            InvokeButton((Button)Find(shell.SettingsWindow!, "SaveGeneralSettings")!);
        });
        await WaitForTraySave();
        await OnUI(shell, () =>
        {
            if (shell.TrayIcons.Count != 1 || shell.TrayIcons.First().AccountKey is not null ||
                shell.Session.Dashboard.Tray?.RollUp.Percent is not null)
                throw new InvalidOperationException("Empty selection did not retain a neutral access icon.");
            if (retired != 0)
            {
                int revision = shell.Session.Revision;
                SendTraySelection(shell, 0x401, retired);
                if (shell.Session.Revision != revision)
                    throw new InvalidOperationException("A retired callback was processed.");
            }
            SendTraySelection(shell, 0x401);
        });
        await WaitForFlyoutVisibility(shell, true, "with no selected tray accounts");
        await OnUI(shell, () => shell.ShowSettings(SettingsPage.General));
        await Task.Delay(200);
        await OnUI(shell, () =>
        {
            ((ComboBox)Find(shell.SettingsWindow!, "TrayStyle")!).SelectedIndex = 0;
            ((ComboBox)Find(shell.SettingsWindow!, "TrayMode")!).SelectedIndex = 0;
            foreach (var account in shell.Session.Dashboard.Accounts)
                ((CheckBox)Find(shell.SettingsWindow!, "TrayAccount-" + account.Key)!).IsChecked = true;
            InvokeButton((Button)Find(shell.SettingsWindow!, "SaveGeneralSettings")!);
        });
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
        await Task.Delay(150);
        await OnUI(shell, () =>
        {
            if (retiredPreview!.Source is not null)
                throw new InvalidOperationException("Unmounted preview retained its WinUI pixel buffer.");
        });

        async Task WaitForTraySave()
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                bool ready = false;
                await OnUI(shell, () =>
                {
                    if (shell.Session.Error is { } error) throw new InvalidOperationException(error);
                    ready = !shell.Session.Busy && shell.Session.Notice == "Settings saved.";
                });
                if (ready) { await Task.Delay(100); return; }
                await Task.Delay(30);
            }
            throw new TimeoutException("Tray settings save did not complete.");
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

    private static void InvokeButton(Button button)
    {
        if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Button does not expose its accessible invoke action.");
        invoke.Invoke();
    }
    private static Task OnUI(ReactorShell shell, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.Session.Post(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        });
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
