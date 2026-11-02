# Architecture Specification: Desktop Net Blocker

## 1. System Overview

Desktop Net Blocker is decoupled into three primary architectural components:

```
┌────────────────────────────────────────────────────────┐
│               Desktop Application (WPF)                │
│  - Dashboard  - Blocklist  - Authorization  - Settings │
│  - Floating Countdown Overlay (No-Activate Topmost)   │
└───────────────────────────▲────────────────────────────┘
                            │ Windows Named Pipes (JSON IPC)
                            │ \\.\pipe\DesktopNetBlockerServicePipe
┌───────────────────────────▼────────────────────────────┐
│          Windows Background Service Worker             │
│  - State Machine Authority  - Persistent Wall-Clock    │
│  - SQLite Database Context  - Restart Recovery Engine  │
└───────────────────────────▲────────────────────────────┘
                            │ Rule Synchronization
┌───────────────────────────▼────────────────────────────┐
│             Network Enforcement Engine                 │
│  1. Local DNS Proxy (127.0.0.1:53 & ::1:53 UDP/TCP)    │
│  2. Adapter DNS Client Redirection (DHCP -> Loopback)  │
│  3. Windows Firewall Anti-Bypass Rules (UDP/TCP 53/853)│
│  4. Kernel-Level Hosts File Synchronization Fallback   │
└────────────────────────────────────────────────────────┘
```

---

## 2. Delayed Authorization State Machine

The authorization state machine governs all restriction changes (domain removal, temporary unlock, and policy modifications).

```
 ┌────────┐
 │ LOCKED │ ◄─────────────────────────────────────────────┐
 └───┬────┘                                               │
     │ SubmitRemovalRequest()                             │
     ▼                                                    │
 ┌─────────┐   CancelRequest()                            │
 │ PENDING │ ───────────────────────────────────────┐     │
 └───┬─────┘                                        │     │
     │ InitialDeadline Passed (3 Days)              │     │
     ▼                                              │     │
 ┌──────────────┐   Confirmation Expired (24h)      │     │
 │ CONFIRMATION │ ────────────────────────────┐     │     │
 └───┬──────────┘                             │     │     │
     │ ConfirmRequest()                       │     ▼     │
     ▼                                        │ ┌───────────┐
 ┌─────────────┐   CancelRequest()            │ │ CANCELLED │
 │ FINAL_DELAY │ ─────────────────────────────┼►│  (Apply   │
 └───┬─────────┘                              │ │ Cooldown) │
     │ FinalDeadline Passed (3 Days)          │ └───────────┘
     ▼                                        ▼
 ┌────────────┐                         ┌─────────┐
 │ AUTHORIZED │                         │ EXPIRED │
 └────────────┘                         └─────────┘
```

### State Definitions

| State | Domain Filtering Status | Timers Active | Available Actions |
| :--- | :--- | :--- | :--- |
| **`LOCKED`** | Blocked | None | Submit Removal, Submit Temp Unlock |
| **`PENDING`** | Blocked | Initial Countdown (3 Days) | `CancelRequest` |
| **`CONFIRMATION`**| Blocked | Confirmation Window (24h) | `ConfirmRequest`, `CancelRequest` |
| **`FINAL_DELAY`** | Blocked | Final Countdown (3 Days) | `CancelRequest` |
| **`AUTHORIZED`** | Removed (Permanent) or Unlocked (2h) | Temp Unlock Timer (2h if unlock) | None |
| **`CANCELLED`** | Blocked | Resulting Cooldown Timer | None until cooldown expires |
| **`EXPIRED`** | Blocked | None | Submit new request |

---

## 3. Multi-Layer Network Filtering & Anti-Bypass

### Layer 1: Local DNS Proxy (`DnsProxyServer`)
- Listens on loopback IP `127.0.0.1:53` and `::1:53`.
- Intercepts RFC 1035 DNS datagrams.
- For matched blocked domains:
  - Responds immediately with `0.0.0.0` for Type A (IPv4).
  - Responds with `::` for Type AAAA (IPv6).
  - Responds with `NODATA` for Type 65 (HTTPS/SVCB) to suppress encrypted browser transport negotiations.
- For permitted domains:
  - Forwards to upstream secure DNS (`1.1.1.1` / `8.8.8.8`) and relays answer to client.

### Layer 2: Network Adapter Redirection (`AdapterDnsManager`)
- Directs active Windows network adapters (Wi-Fi and Ethernet) to use `127.0.0.1` and `::1` as primary/secondary DNS servers.
- Restores automatic DHCP DNS client configuration upon uninstallation.

### Layer 3: Windows Firewall Anti-Bypass Rules (`FirewallPolicyManager`)
- Blocks all outbound UDP and TCP port 53 traffic for all system applications **except** `DesktopNetBlocker.Service.exe`. This prevents browsers or CLI tools from directly querying external DNS servers like `8.8.8.8`.
- Blocks outbound TCP port 853 to prevent DNS-over-TLS (DoT) bypass.
- Blocks outbound TCP port 443 to well-known public DoH resolver IPs (`1.1.1.1`, `8.8.8.8`, `9.9.9.9`, etc.) to prevent browsers from establishing DoH tunnels.

### Layer 4: Hosts File Kernel-Level Fallback (`HostsFileManager`)
- Maintains a synchronized, tagged block in `%SystemRoot%\System32\drivers\etc\hosts`:
  ```
  # BEGIN DESKTOP_NET_BLOCKER
  0.0.0.0 example.com
  :: example.com
  0.0.0.0 www.example.com
  :: www.example.com
  # END DESKTOP_NET_BLOCKER
  ```
- Serves as an immediate kernel-level fallback intercepting socket connections before network resolution.

---

## 4. SQLite Data Model

- **`blocked_domains`**:
  - `id` (TEXT, PK): Unique domain record identifier.
  - `domain` (TEXT, UNIQUE): Normalized lowercase domain.
  - `matching_mode` (INTEGER): `0` = Exact, `1` = Subdomain, `2` = Wildcard.
  - `category` (TEXT): User-defined category label.
  - `created_at` (TEXT): ISO 8601 UTC timestamp.
  - `status` (INTEGER): `0` = Active, `1` = SuspendedTemporaryUnlock, `2` = Removed.
  - `policy_version` (INTEGER): Monotonic policy increment.

- **`authorization_requests`**:
  - `id` (TEXT, PK): Request GUID.
  - `request_type` (INTEGER): `0` = Removal, `1` = TemporaryUnlock, `2` = PolicyModification.
  - `domain_id` (TEXT): Foreign reference to `blocked_domains.id`.
  - `target_domain` (TEXT): Display domain name.
  - `created_at` (TEXT): Request creation timestamp (UTC).
  - `initial_deadline` (TEXT): UTC timestamp for initial 3-day countdown.
  - `confirmation_deadline` (TEXT): UTC timestamp for confirmation expiration.
  - `confirmed_at` (TEXT): Timestamp when user confirmed.
  - `final_deadline` (TEXT): UTC timestamp for final 3-day delay.
  - `status` (INTEGER): Current state machine status.
  - `cancellation_count` (INTEGER): Sequence count of cancellations for this domain.

- **`cancellation_history`**:
  - Records cancellation timestamp, resulting cooldown duration, and `next_allowed_request_at` deadline.

- **`temporary_access`**:
  - Records 2-hour granted access sessions and auto-expiration deadlines.

- **`restriction_events`**:
  - Immutable audit trail of additions, requests, confirmations, cancellations, and blocks.
