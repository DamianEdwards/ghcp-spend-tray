import Foundation
import Network

@MainActor
final class LoopbackAppcastServer {
    private let listener: NWListener
    private let feed: Data
    private let readiness = CallbackWait<URL>()
    private let report: (String) -> Void
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var requests: [ObjectIdentifier: Data] = [:]
    private var stopped = false
    private(set) var servedFeeds = 0
    var failed: ((Error) -> Void)?

    init(feed: Data, report: @escaping (String) -> Void) throws {
        self.feed = feed
        self.report = report
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
    }

    func start() async throws -> URL {
        listener.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in self?.stateChanged(state) }
        }
        listener.newConnectionHandler = { [weak self] connection in
            Task { @MainActor in
                guard let self, !self.stopped else { connection.cancel(); return }
                self.accept(connection)
            }
        }
        report("Starting native listener bound exclusively to 127.0.0.1.")
        listener.start(queue: .main)
        return try await readiness.value(failure: "Timed out awaiting native loopback listener readiness.")
    }

    func stop() {
        guard !stopped else { return }
        stopped = true
        listener.stateUpdateHandler = nil
        listener.newConnectionHandler = nil
        listener.cancel()
        for connection in connections.values { connection.cancel() }
        connections.removeAll()
        requests.removeAll()
        readiness.complete(.failure(CancellationError()))
        report("Native listener stopped; all fixture connections cancelled.")
    }

    private func stateChanged(_ state: NWListener.State) {
        guard !stopped else { return }
        report("Listener state: \(state).")
        switch state {
        case .ready:
            guard let port = listener.port,
                  let url = URL(string: "http://127.0.0.1:\(port.rawValue)/appcast.xml") else {
                fail(AppError.message("The native listener has no valid loopback endpoint."))
                return
            }
            readiness.complete(.success(url))
        case .failed(let error):
            fail(error)
        case .cancelled:
            fail(AppError.message("The native loopback listener was unexpectedly cancelled."))
        default:
            break
        }
    }

    private func accept(_ connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        connections[id] = connection
        requests[id] = Data()
        connection.stateUpdateHandler = { [weak self] state in
            Task { @MainActor in
                guard let self, !self.stopped else { return }
                if case .failed(let error) = state {
                    self.fail(error)
                    self.close(connection)
                }
            }
        }
        connection.start(queue: .main)
        receive(connection)
    }

    private func receive(_ connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] data, _, complete, error in
            Task { @MainActor in
                guard let self, !self.stopped else { return }
                self.received(data, complete: complete, error: error, from: connection)
            }
        }
    }

    private func received(_ data: Data?, complete: Bool, error: NWError?, from connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        guard var request = requests[id] else { return }
        if let error { fail(error); close(connection); return }
        if let data { request.append(data) }
        guard request.count <= 16384 else {
            fail(AppError.message("The synthetic HTTP request exceeds the fixture header limit."))
            close(connection)
            return
        }
        requests[id] = request
        guard request.range(of: Data("\r\n\r\n".utf8)) != nil else {
            if complete {
                fail(AppError.message("The synthetic HTTP connection closed before sending complete headers."))
                close(connection)
            } else {
                receive(connection)
            }
            return
        }
        guard let header = String(data: request, encoding: .utf8),
              let line = header.components(separatedBy: "\r\n").first,
              line == "GET /appcast.xml HTTP/1.1" || line == "GET /appcast.xml HTTP/1.0" else {
            fail(AppError.message("The synthetic listener received an unexpected HTTP request."))
            close(connection)
            return
        }
        report("Serving /appcast.xml: \(feed.count) bytes.")
        var response = Data("HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\nContent-Length: \(feed.count)\r\nConnection: close\r\n\r\n".utf8)
        response.append(feed)
        connection.send(content: response, isComplete: true, completion: .contentProcessed { [weak self] error in
            Task { @MainActor in
                guard let self, !self.stopped else { return }
                if let error {
                    self.fail(error)
                } else {
                    self.servedFeeds += 1
                    self.report("HTTP response completed.")
                }
                self.close(connection)
            }
        })
    }

    private func close(_ connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        connection.stateUpdateHandler = nil
        connection.cancel()
        connections.removeValue(forKey: id)
        requests.removeValue(forKey: id)
    }

    private func fail(_ error: Error) {
        report("Native server failed: \(error.localizedDescription).")
        readiness.complete(.failure(error))
        failed?(error)
    }
}
