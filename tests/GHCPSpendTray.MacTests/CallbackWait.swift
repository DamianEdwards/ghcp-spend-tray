import Foundation

@MainActor
final class CallbackWait<Value: Sendable> {
    private var result: Result<Value, Error>?
    private var continuation: CheckedContinuation<Value, Error>?
    private var deadline: Task<Void, Never>?
    private var awaited = false

    func complete(_ result: Result<Value, Error>) {
        guard self.result == nil else { return }
        self.result = result
        deadline?.cancel()
        deadline = nil
        continuation?.resume(with: result)
        continuation = nil
    }

    func value(timeout: Duration = .seconds(10), failure: String) async throws -> Value {
        guard !awaited else { throw AppError.message("A fixture callback cannot be awaited twice.") }
        awaited = true
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                if let result {
                    continuation.resume(with: result)
                    return
                }
                self.continuation = continuation
                deadline = Task { [weak self] in
                    do {
                        try await Task.sleep(for: timeout)
                    } catch is CancellationError {
                        return
                    } catch {
                        self?.complete(.failure(error))
                        return
                    }
                    self?.complete(.failure(AppError.message(failure)))
                }
            }
        } onCancel: {
            Task { @MainActor [weak self] in self?.complete(.failure(CancellationError())) }
        }
    }
}
