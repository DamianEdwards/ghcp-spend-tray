using GHCPSpendTray.App.Native;
using GHCPSpendTray.App.Platform;
using GHCPSpendTray.App.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
            runtime.Diagnostic += ex => Diagnostics.Record($"Instance coordination failed ({ex.GetType().Name}).");
            using IApplicationController controller = demo ? new DemoController(runtime.DataDirectory, emptyDemo) :
                new ApplicationController(runtime.DataDirectory, runtime.IsPortable);
            int smokeExit = 0;
            ReactorShell? shell = null;
            ReactorApp.ShutdownPolicy = ShutdownPolicy.Explicit;
            try
            {
                ReactorApp.Run(context =>
                {
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
                    if (smoke) _ = RunSmokeAsync(shell, runtime.DataDirectory, code => smokeExit = code);
                });
            }
            finally { shell?.Dispose(); }
            return smokeExit;
        }
        catch (Exception ex)
        {
            Diagnostics.Record($"Application startup failed ({ex.GetType().Name}).");
            // Bootstrap errors are locally produced and contain no authentication payloads.
            Win32.MessageBox(0, ex.Message, "GHCPSpendTray could not start", Win32.MB_ICONERROR);
            return 1;
        }
    }

    private static async Task RunSmokeAsync(ReactorShell shell, string directory, Action<int> setExit)
    {
        try
        {
            if (PackageContext.IsPackaged)
            {
                var startup = await StartupRegistration.CreateAsync();
                if (startup.Enabled)
                    throw new InvalidOperationException("Fresh development package unexpectedly enables login startup.");
                if (!InstallationPaths.SamePath(directory, PackageContext.DataDirectory))
                    throw new InvalidOperationException("Packaged smoke test did not use package-local storage.");
            }
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
                if (Find(shell.SettingsWindow!, "RefreshUsage") is not Button refresh || !refresh.IsEnabled)
                    throw new InvalidOperationException("Usage refresh control did not render.");
                if (shell.Session.Dashboard.Accounts.Count == 0)
                {
                    if (Find(shell.SettingsWindow!, "UsageAddAccount") is not Button)
                        throw new InvalidOperationException("Usage empty state did not offer account setup.");
                    if (Find(shell.SettingsWindow!, "UsageTotal") is not TextBlock { Text: "Unavailable" })
                        throw new InvalidOperationException("Empty usage was shown as zero.");
                }
                else if (Find(shell.SettingsWindow!, "UsageTotal") is not TextBlock { Text: "$42.75" } ||
                    Find(shell.SettingsWindow!, "github.com:1_DetailCredits") is not TextBlock { Text: "2,625" })
                    throw new InvalidOperationException("Usage summary and account diagnostics did not render.");
                if (Find(shell.SettingsWindow!, "AccountHistory") is not null)
                    throw new InvalidOperationException("Usage page still displayed sampled spending history.");
                InvokeButton(refresh);
                SendTraySelection(shell, 0x401);
            });
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
                if (Find(settings, "AccountOnboarding") is null || Find(settings, "AccountHostSelection") is not ComboBox)
                    throw new InvalidOperationException("Add-account deep link did not render.");
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
                    shell.Session.Navigate(SettingsPage.Notifications);
                });
                await Task.Delay(300);
            }
            await OnUI(shell, () =>
            {
                if (shell.Session.Page != SettingsPage.Notifications) throw new InvalidOperationException("Settings navigation failed.");
                SmokeCredentials();
                if (shell.Session.TestNotification?.Invoke() != true) throw new InvalidOperationException("Shell notification rejected.");
                File.WriteAllText(Path.Combine(directory, "native-smoke-result.txt"),
                    "PASS: mouse/keyboard tray toggle, double-click settings without flyout flash, hide/reopen and focus transitions, " +
                    "Reactor cost flyout, usage-first settings without sampled chart, account avatars and diagnostics, add-account deep link, native controls, " +
                    "Shell notification submission and isolated Credential Manager round-trip.\n" +
                    "No live account access, installation, or startup writes.\n");
                shell.Exit();
            });
        }
        catch (Exception ex)
        {
            setExit(1);
            File.WriteAllText(Path.Combine(directory, "native-smoke-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}\n");
            ReactorApp.UIDispatcher?.TryEnqueue(() => ReactorApp.Exit(1));
        }
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

    private static void SendTraySelection(ReactorShell shell, int notification) =>
        Win32.SendMessage(shell.TrayHandle, Win32.WM_TRAY, 0, (nint)((1 << 16) | notification));

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
