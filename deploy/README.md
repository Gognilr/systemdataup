# 部署说明（nginx 反向代理形态）

```
Agent ──mTLS(443)──┐
                   ├─► nginx ──HTTP──► Kestrel 127.0.0.1:5080
管理员 ──TLS(443)──┘   终结 TLS/mTLS      仅回环监听
```

## 1. 注入机密（首次或轮换后）

机密不再写入任何 `appsettings*.json`，全部走计算机级环境变量：

| 环境变量 | 对应配置键 |
| --- | --- |
| `ConnectionStrings__Default` | `ConnectionStrings:Default` |
| `Security__Jwt__SigningKey` | `Security:Jwt:SigningKey` |
| `Security__CommandSigningKey` | `Security:CommandSigningKey` |
| `Security__ClientCa__Password` | `Security:ClientCa:Password` |

以管理员身份运行仓库根目录的 `setup-env.ps1`（该文件已被 `.gitignore` 排除），然后重启服务。

## 2. 证书

需要两套证书，用途不同，**不要混用**：

- **服务端证书** `server.crt` / `server.key` —— nginx 对外提供 HTTPS，需浏览器信任。
- **客户端 CA** `client-ca.crt` —— 用于签发和校验 Agent 证书。必须与应用的
  `Security:ClientCa:CertPath`（PFX，含私钥）是同一张 CA：应用用它签发，nginx 用它校验。

`Security:ClientCa:CertPath` 留空时，应用会在 `data/dev-ca.pfx` 自动生成一张开发 CA，
口令取自 `Security__ClientCa__Password`。导出其公钥部分给 nginx：

```bash
openssl pkcs12 -in data/dev-ca.pfx -clcerts -nokeys -out client-ca.crt
```

正式环境应替换为真实 CA。

## 3. nginx

把 `nginx/backup-monitor.conf` 放入 `conf.d/`，改掉 `server_name` 与证书路径后 `nginx -t && nginx -s reload`。

## 4. 关键约束

- **Kestrel 只能监听 `127.0.0.1`。** 监听 `0.0.0.0` 会让人绕过 nginx 直连明文端口，
  同时跳过 TLS 与客户端证书校验——等于前面所有防护都不存在。
- `Security:ClientAuth:ForwardedProxies` 必须包含 nginx 的来源地址（同机为 `127.0.0.1` / `::1`），
  它同时决定了 `X-Forwarded-For` 与 `X-Client-*` 头是否被采信。留空即全部不采信。
- `Security:ClientAuth:AllowDevelopmentHeader` 生产必须为 `false`。

## 5. 验证

```bash
# 健康检查（匿名）
curl -k https://backup.example.internal/health

# 无客户端证书访问 Agent 接口 → 预期 401
curl -k https://backup.example.internal/api/v1/agent/config

# 带客户端证书 → 预期 200
curl -k --cert agent.crt --key agent.key \
     https://backup.example.internal/api/v1/agent/config

# 伪造证书头（不经 nginx，直连回环）→ 预期 401 且日志出现"疑似伪造"告警
curl -H 'X-Client-Verify: SUCCESS' -H 'X-Client-Fingerprint: aabb...' \
     http://127.0.0.1:5080/api/v1/agent/config
```

最后一条只有在 Kestrel 错误地监听了对外地址时才可能从外部发起——它同时是一次配置自检。
