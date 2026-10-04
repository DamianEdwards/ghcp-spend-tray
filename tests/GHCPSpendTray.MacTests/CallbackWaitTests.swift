import Foundation

@MainActor
enum CallbackWaitTests {
    static func run() async throws {
        let early = CallbackWait<Int>()
        early.complete(.success(7))
        guard try await early.value(timeout: .zero, failure: "Unexpected early-callback timeout.") == 7 else {
            throw AppError.message("A callback received before awaiting must return immediately.")
        }

        let later = CallbackWait<Int>()
        Task { later.complete(.success(9)); later.complete(.success(10)) }
        guard try await later.value(failure: "Expected the queued test callback.") == 9 else {
            throw AppError.message("Callback completion must resume once and ignore duplicates.")
        }
        do {
            _ = try await later.value(failure: "Unexpected second wait.")
            throw AppError.message("A fixture callback allowed multiple waits.")
        } catch AppError.message(let message) {
            guard message == "A fixture callback cannot be awaited twice." else { throw AppError.message(message) }
        }

        let missing = CallbackWait<Void>()
        do {
            try await missing.value(timeout: .zero, failure: "Synthetic missing callback.")
            throw AppError.message("A missing callback ignored its deadline.")
        } catch AppError.message(let message) {
            guard message == "Synthetic missing callback." else { throw AppError.message(message) }
        }
        missing.complete(.success(()))

        let cancelled = CallbackWait<Void>()
        let wait = Task { try await cancelled.value(failure: "Cancellation did not wake the waiter.") }
        Task { wait.cancel() }
        do {
            try await wait.value
            throw AppError.message("A cancelled callback wait returned success.")
        } catch is CancellationError {}
        cancelled.complete(.success(()))
        print("PASS: callback-driven fixture readiness/completion, early callbacks, duplicate completion, deadlines and cancellation.")
    }
}
