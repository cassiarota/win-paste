import SwiftUI

/// Cloud-sync setup panel: server, account, VIP key, sync passphrase, enable + sync-now.
struct SyncView: View {
    let engine: SyncEngine

    @State private var server = ""
    @State private var email = ""
    @State private var password = ""
    @State private var key = ""
    @State private var phrase = ""
    @State private var enabled = false
    @State private var message = ""

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                Text("云同步(跨设备)").font(.headline)
                Text("在多台设备间同步剪贴板历史。内容用你的『同步口令』端到端加密后上传,服务器只保存密文。需登录并具备 VIP 资格(用激活码开通)。密码永不参与同步。")
                    .font(.caption).foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)

                field("服务器地址", TextField("", text: $server))

                Text("账号").font(.subheadline).bold().padding(.top, 6)
                field("邮箱", TextField("", text: $email))
                field("密码", SecureField("", text: $password))
                HStack {
                    Button("登录") { run { try await engine.login(email: email, password: password); return "登录成功" } }
                    Button("注册") { run { try await engine.register(email: email, password: password); return "注册并登录成功" } }
                }

                Text("VIP / 激活码").font(.subheadline).bold().padding(.top, 6)
                field("激活码", TextField("", text: $key))
                Button("兑换激活码") {
                    run { let vip = try await engine.redeem(key: key); return vip ? "激活成功,已获得 VIP" : "激活码无效" }
                }

                Text("同步口令(端到端加密)").font(.subheadline).bold().padding(.top, 6)
                Text("所有设备请使用相同的邮箱与同步口令。口令不上传,忘记将无法解密云端数据。")
                    .font(.caption2).foregroundColor(.secondary).fixedSize(horizontal: false, vertical: true)
                field("同步口令", SecureField("", text: $phrase))
                Button("设置同步口令") {
                    do { try engine.setPassphrase(phrase); message = "同步口令已设置" }
                    catch { message = error.localizedDescription }
                }

                Toggle("开启自动同步", isOn: $enabled)
                    .onChange(of: enabled) { on in engine.enable(on) }
                    .padding(.top, 6)
                Button("立即同步") { run { try await engine.syncNow() } }

                Text(statusLine).font(.caption).foregroundColor(.secondary).padding(.top, 8)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(18)
        }
        .frame(width: 430, height: 560)
        .onAppear {
            server = engine.baseURL
            email = engine.email ?? ""
            enabled = engine.enabled
        }
    }

    private var statusLine: String {
        let login = engine.loggedIn ? "已登录:\(engine.email ?? "")" : "未登录"
        let phr = engine.hasPassphrase ? "已设置口令" : "未设置口令"
        let en = engine.enabled ? "自动同步:开" : "自动同步:关"
        return "\(login) · \(phr) · \(en)\(message.isEmpty ? "" : "\n\(message)")"
    }

    @ViewBuilder
    private func field<V: View>(_ label: String, _ control: V) -> some View {
        Text(label).font(.caption).foregroundColor(.secondary)
        control.textFieldStyle(.roundedBorder)
    }

    private func run(_ work: @escaping () async throws -> String) {
        message = "处理中…"
        Task { @MainActor in
            do { message = try await work() }
            catch { message = "失败:\(error.localizedDescription)" }
        }
    }
}
