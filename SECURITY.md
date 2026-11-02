# Security & Integrity Model: Desktop Net Blocker

## 1. Security Philosophy & The Administrator Boundary

A foundational requirement of Desktop Net Blocker is transparency:
> **The user has administrator privileges on Windows. The application must provide realistic tamper resistance and behavioral friction without falsely claiming to be impossible for an administrator to bypass.**

Operating systems running with administrative user privileges cannot fundamentally prevent a determined administrator from terminating a process or modifying system files with low-level debuggers or kernel drivers.

Desktop Net Blocker’s objective is **behavioral impulse friction**:
- Impulses to browse distracting sites or dismantle restrictions are impulsive, emotional, and short-lived.
- By introducing high friction (Windows background service, persistent database state, escalating cooldowns, multiple network layers), the application forces any impulsive bypass attempt to become tedious, deliberate, and time-consuming.
- By the time the user would attempt an out-of-band bypass, the emotional impulse has dissipated.

---

## 2. Anti-Tamper & Resistance Features

1. **Service Authority**:
   - The desktop GUI has **zero** deletion or editing authority over the database.
   - All state transitions, timers, and restriction updates are evaluated strictly within `DesktopNetBlocker.Service.exe`.
   - Closing or killing the desktop GUI has **zero** effect on network restrictions.

2. **Persistent Wall-Clock Timers**:
   - Timers are stored as absolute UTC deadlines (`InitialDeadline`, `FinalDeadline`, `NextAllowedRequestAt`) in SQLite.
   - Stopping the service, sleeping the computer, or rebooting Windows **does not reset** or shorten the countdowns.

3. **Single Removal Constraint & Escalating Cooldowns**:
   - Only one domain removal request can be active at a time to prevent flooding simultaneous requests.
   - Cancelling a request triggers an escalating cooldown on future requests for that domain (7 days, 14 days, 30 days).

4. **Multi-Layer Network Interception**:
   - DNS proxy running on `127.0.0.1:53` and `::1:53`.
   - Windows Firewall rules blocking direct outbound UDP/TCP 53, preventing software from querying external DNS resolvers directly.
   - Outbound port 853 blocked (preventing DNS-over-TLS).
   - Known public DoH endpoint IPs blocked on port 443 (preventing browsers from secretly tunneling queries).
   - Local `%SystemRoot%\System32\drivers\etc\hosts` synchronization providing immediate kernel-level address interception.

5. **Ethical System Integrity**:
   - Desktop Net Blocker does **not** employ malicious rootkits, deceptive file replication, or hidden bootloaders.
   - It adheres to standard Windows system management guidelines, allowing clear auditability and clean uninstallation.

---

## 3. Official System Recovery & Uninstall Procedure

If an administrator genuinely needs to remove the software or recover network settings:

1. Open PowerShell as Administrator.
2. Execute the provided uninstallation script:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-service.ps1
   ```
3. The script will safely:
   - Stop and unregister `DesktopNetBlockerService`.
   - Reset all network adapter DNS addresses back to automatic DHCP.
   - Remove Windows Firewall anti-bypass rules.
   - Remove managed blocks from `%SystemRoot%\System32\drivers\etc\hosts`.
   - Remove application binaries and desktop shortcuts.
