import SwiftUI

struct UpdateReminder: View {
    @ObservedObject var updates: AppUpdates
    var body: some View {
        if updates.state.availableVersion != nil {
            Button(updates.actionTitle) { updates.checkForUpdates() }
                .disabled(!updates.state.canCheck)
                .accessibilityIdentifier("update-available")
        }
        if let error = updates.state.error {
            Text(error).font(.caption).foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

struct UpdatePreferences: View {
    @ObservedObject var updates: AppUpdates
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if updates.enabled {
                Text(updates.statusTitle)
                    .font(.headline)
                    .accessibilityIdentifier("update-status")
                Text(updates.statusDetail)
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button(updates.actionTitle) { updates.checkForUpdates() }
                .disabled(!updates.enabled || !updates.state.canCheck)
            if updates.enabled {
                Toggle("Automatically check for updates", isOn: Binding(
                    get: { updates.state.automaticallyChecks }, set: { updates.setAutomaticallyChecks($0) }))
                Toggle("Automatically download and install updates on quit", isOn: Binding(
                    get: { updates.state.automaticallyDownloads }, set: { updates.setAutomaticallyDownloads($0) }))
                    .disabled(!updates.state.automaticallyChecks)
                Text("Automatic checks run daily. Automatic downloads are installed when you quit the app, not when you close this window.")
                    .font(.caption).foregroundStyle(.secondary)
                if let date = updates.state.lastChecked {
                    Text("Last checked: \(date.formatted(date: .abbreviated, time: .shortened))")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            if let error = updates.state.error { Text(error).font(.caption).foregroundStyle(.red) }
            if let explanation = updates.explanation {
                Text(explanation).font(.caption).foregroundStyle(.secondary)
            }
        }.frame(maxWidth: 470)
    }
}
