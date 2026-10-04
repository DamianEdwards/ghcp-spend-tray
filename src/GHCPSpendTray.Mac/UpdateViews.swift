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
            Button(updates.actionTitle) { updates.checkForUpdates() }
                .disabled(!updates.enabled || !updates.state.canCheck)
            if updates.enabled {
                Toggle("Automatically check for updates", isOn: Binding(
                    get: { updates.state.automaticallyChecks }, set: { updates.setAutomaticallyChecks($0) }))
                Toggle("Automatically download and install updates on quit", isOn: Binding(
                    get: { updates.state.automaticallyDownloads }, set: { updates.setAutomaticallyDownloads($0) }))
                    .disabled(!updates.state.automaticallyChecks)
                Text("Checks run daily. Downloaded updates can also be installed using Install and Relaunch.")
                    .font(.caption).foregroundStyle(.secondary)
                if let date = updates.state.lastChecked {
                    Text("Last checked: \(date.formatted(date: .abbreviated, time: .shortened))")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            if let error = updates.state.error { Text(error).font(.caption).foregroundStyle(.red) }
            Text(updates.explanation).font(.caption).foregroundStyle(.secondary)
        }.frame(maxWidth: 470)
    }
}
