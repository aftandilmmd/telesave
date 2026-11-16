// Signing tool for TeleSave remote config.
//
//   swift scripts/remote-config.swift keygen [key-path]
//       Creates an Ed25519 key pair. Private key → key-path (default ~/.telesave/config-signing.key, mode 600).
//       Prints the public key for TG_CONFIG_PUBLIC_KEY.
//   swift scripts/remote-config.swift sign <config.json> [out.json] [key-path]
//       Wraps config.json into a signed envelope (default out: remote-config/config.signed.json).
//   swift scripts/remote-config.swift verify <signed.json|https-url> <public-key-base64>
//       Checks a local or hosted envelope.
import CryptoKit
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

let defaultKeyPath = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".telesave/config-signing.key").path
var args = Array(CommandLine.arguments.dropFirst())
guard let command = args.first else { fail("usage: keygen | sign | verify (see header)") }
args.removeFirst()

switch command {
case "keygen":
    let path = args.first ?? defaultKeyPath
    if FileManager.default.fileExists(atPath: path) { fail("refusing to overwrite existing key at \(path)") }
    let key = Curve25519.Signing.PrivateKey()
    let url = URL(fileURLWithPath: path)
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try key.rawRepresentation.base64EncodedString().write(to: url, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
    print("Private key: \(path)  (keep it out of git; back it up)")
    print("TG_CONFIG_PUBLIC_KEY=\(key.publicKey.rawRepresentation.base64EncodedString())")

case "sign":
    guard let input = args.first else { fail("usage: sign <config.json> [out.json] [key-path]") }
    let output = args.count > 1 ? args[1] : "remote-config/config.signed.json"
    let keyPath = args.count > 2 ? args[2] : defaultKeyPath
    let source = try Data(contentsOf: URL(fileURLWithPath: input))
    guard let object = try? JSONSerialization.jsonObject(with: source) as? [String: Any] else { fail("\(input) is not a JSON object") }
    guard object["serial"] is Int else { fail("config needs an integer \"serial\"; increase it on every change") }
    var signedObject = object
    if let hash = object["api_hash"] as? String {
        guard hash.count == 32 else { fail("api_hash must be 32 hex characters") }
        // The hosted file is public: store the hash obfuscated (must match TeleSaveCore.Obfuscator).
        let key = Array("TeleSave.obfuscation.v1".utf8)
        let bytes = Array(hash.utf8).enumerated().map { $0.element ^ key[$0.offset % key.count] }
        signedObject["api_hash"] = nil
        signedObject["api_hash_obf"] = Data(bytes).base64EncodedString()
    }
    let payload = try JSONSerialization.data(withJSONObject: signedObject, options: [.sortedKeys])
    guard let keyText = try? String(contentsOfFile: keyPath, encoding: .utf8),
          let raw = Data(base64Encoded: keyText.trimmingCharacters(in: .whitespacesAndNewlines)),
          let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: raw) else { fail("cannot read private key at \(keyPath)") }
    let signature = try key.signature(for: payload)
    let envelope = ["payload": payload.base64EncodedString(), "signature": signature.base64EncodedString()]
    let data = try JSONSerialization.data(withJSONObject: envelope, options: [.prettyPrinted, .sortedKeys])
    try data.write(to: URL(fileURLWithPath: output))
    print("Signed serial \(object["serial"]!) → \(output). Upload it to every config URL.")

case "verify":
    guard args.count == 2, let rawKey = Data(base64Encoded: args[1]),
          let publicKey = try? Curve25519.Signing.PublicKey(rawRepresentation: rawKey) else { fail("usage: verify <file|url> <public-key>") }
    let source = args[0]
    let data: Data
    if source.hasPrefix("https://"), let url = URL(string: source) {
        let semaphore = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var fetched: Data?
        URLSession.shared.dataTask(with: URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData)) { d, _, _ in
            fetched = d
            semaphore.signal()
        }.resume()
        semaphore.wait()
        guard let fetched else { fail("download failed: \(source)") }
        data = fetched
    } else {
        data = try Data(contentsOf: URL(fileURLWithPath: source))
    }
    guard let envelope = try? JSONSerialization.jsonObject(with: data) as? [String: String],
          let payload = envelope["payload"].flatMap({ Data(base64Encoded: $0) }),
          let signature = envelope["signature"].flatMap({ Data(base64Encoded: $0) }) else { fail("not a signed envelope") }
    guard publicKey.isValidSignature(signature, for: payload) else { fail("INVALID signature") }
    print("OK: valid signature")
    print(String(decoding: payload, as: UTF8.self))

default:
    fail("unknown command \(command)")
}
