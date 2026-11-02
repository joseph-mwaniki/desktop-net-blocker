# Desktop Net Blocker (Windows 11)

Native Windows 11 self-control and system-wide network restriction application based on **delayed gratification** and **impulse friction**.

---

## 1. Product Philosophy & Core Principle

The application is built on the psychological insight that impulses to visit distracting websites are intense but temporary. Making distraction removal deliberately inconvenient gives impulses time to fade.

### The Immutable Blocklist Principle
> **Once a domain has been added to the blocklist, it must NEVER be possible to remove it immediately.**
>
> **Adding a restriction is immediate.**
> **Removing a restriction requires time and deliberation.**

- **No hardcoded blocklist**: The application starts completely empty. The user chooses what to block.
- **No immediate removal buttons**: The UI provides no delete buttons, remove buttons, delete shortcuts, bulk deletions, or editing blocked domains into allowed ones.
- **Delayed Authorization Process**:
  1. User submits a domain removal request.
  2. Domain remains actively blocked.
  3. A **3-day initial waiting countdown** begins; the floating overlay appears.
  4. User can cancel at any moment (immediately applying an escalating cooldown).
  5. After 3 days, a confirmation window becomes active.
  6. User must explicitly confirm the removal.
  7. If confirmation is ignored, the request expires and the domain remains blocked.
  8. Confirming initiates an additional **3-day final delay period**.
  9. Only after the entire 6-day workflow completes without cancellation is the domain removed.
- **Escalating Cancellation Cooldowns**:
  - 1st cancellation: **7 days** cooldown before another request can be submitted.
  - 2nd cancellation: **14 days** cooldown.
  - 3rd+ cancellation: **30 days** cooldown.
- **Single Removal Request Constraint**: Only one domain removal request can be pending at a time system-wide.

---

## 2. Technology Stack & Architecture

- **C# & .NET 8.0**: High-performance modern managed runtime.
- **Windows Background Service (`DesktopNetBlocker.Service`)**:
  - Starts automatically with Windows (`start= auto`).
  - Owns the policy state machine, persistent timers, and SQLite authority.
  - Multi-layer network filtering: local DNS proxy, Windows adapter DNS redirection, hosts file fallback synchronization, and Windows Firewall anti-bypass rules.
- **WPF Native Desktop Application (`DesktopNetBlocker.UI`)**:
  - Modern Windows 11 dark acrylic styling, Segoe UI typography, and dashboard cards.
  - Dashboard, Blocklist Management, Authorization Center, and Policy Settings.
- **Floating Countdown Overlay**:
  - Translucent, always-on-top, draggable overlay window.
  - `WS_EX_NOACTIVATE` & `WS_EX_TOOLWINDOW` to ensure it never steals keyboard focus or interrupts work/gaming.
  - Dynamic live countdown (`DAYS : HOURS : MINS : SECS`) updated every second.
  - Prominent `[CANCEL REQUEST]` button.
- **SQLite Database (`Microsoft.Data.Sqlite`)**:
  - Persistent, transactional storage with WAL mode enabled.
  - Resilient against power cuts, sleep/wake, and machine reboots.
- **Windows Named Pipes (`System.IO.Pipes`)**:
  - Authenticated IPC channel between desktop UI and background service with Windows security ACLs.

---

## 3. Solution Structure

```
desktop-net-blocker/
├── src/
│   ├── DesktopNetBlocker.Core/         # Shared models, DB context, state machine, domain matcher, IPC contracts
│   ├── DesktopNetBlocker.Service/      # Windows Background Service, DNS proxy, Firewall and Hosts manager
│   └── DesktopNetBlocker.UI/           # Native Windows 11 WPF application & Floating Countdown Overlay
├── tests/
│   └── DesktopNetBlocker.Tests/        # 29 xUnit unit, integration, and acceptance tests
├── scripts/
│   ├── install-service.ps1             # Automated service installer & firewall rule configurator
│   ├── uninstall-service.ps1           # Safe uninstaller & network restoration script
│   ├── setup.bat                       # One-click Windows installer launcher
│   └── remove.bat                      # One-click Windows uninstaller launcher
├── ARCHITECTURE.md                     # Detailed architectural specification
├── SECURITY.md                         # Security analysis, threat model & recovery guide
└── README.md
```

---

## 4. Quick Start & Installation

### Requirements
- Windows 11 (or Windows 10 64-bit)
- .NET 8.0 Runtime or SDK
- Elevated Administrator privileges (for service installation and network enforcement)

### Installation
1. Open PowerShell or Command Prompt as Administrator.
2. Navigate to `scripts\` and run:
   ```cmd
   setup.bat
   ```
   Or directly via PowerShell:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1
   ```
3. The installer will:
   - Compile and publish the release binaries.
   - Register and start `DesktopNetBlockerService` with automatic startup.
   - Configure failure recovery (automatic restart on crash).
   - Configure Windows Firewall rules to prevent direct DNS bypass.
   - Create a desktop shortcut for `Desktop Net Blocker`.

---

## 5. Usage Guide

### A. Adding Domains
1. Launch `Desktop Net Blocker` from the Start Menu or Desktop.
2. Navigate to the **Blocklist** tab.
3. Enter one or more domains (e.g. `example.com`, `reddit.com`, `*.tiktok.com`).
4. Select matching mode (`Subdomain`, `Exact`, or `Wildcard`) and category.
5. Click **🔒 BLOCK DOMAIN**.
6. The restriction activates immediately across the entire system.

### B. Requesting Domain Removal
1. Navigate to the **Blocklist** tab.
2. Locate the domain and click **Request Removal**.
3. Confirm the delayed removal workflow prompt.
4. The floating overlay appears in the top-right corner with a live 3-day countdown.
5. After 3 days, navigate to the **Authorization** tab and click **CONFIRM AUTHORIZATION**.
6. The 3-day final delay begins.
7. Once the final delay elapses, the domain is permanently removed.

### C. Temporary Unlock (2 Hours)
1. In the **Blocklist** tab, click **Temporary Unlock (2h)**.
2. Follow the delayed authorization process (3 days initial + confirmation + 3 days final).
3. Access is granted for exactly 2 hours.
4. The background service automatically re-locks the domain when the timer expires.

### D. Cancelling a Request
- Click **[CANCEL REQUEST]** on the floating overlay or in the Authorization tab.
- The request terminates immediately.
- The floating overlay disappears.
- The domain remains completely blocked.
- An escalating cooldown is applied: 7 days for the 1st cancellation, 14 days for the 2nd, and 30 days for subsequent cancellations.

---

## 6. Verification & Test Suite

The codebase includes 29 comprehensive automated tests verifying all business rules, state transitions, timers, network parsers, and acceptance criteria:

```bash
dotnet test
```

Test coverage includes:
- Empty initial blocklist verification.
- RFC 1035 / 1123 domain normalization & batch parsing.
- Exact, Subdomain, and Wildcard domain matching.
- Immediate blocklist activation upon addition.
- Three-day initial waiting period enforcement.
- Mandatory confirmation step and 24-hour expiration on ignore.
- Three-day final delay before permanent removal.
- Cancellation immediate execution and cooldown escalation (7d, 14d, 30d).
- Single pending removal request constraint.
- Restart recovery and timer persistence across reboot.
- Automatic re-locking of temporary access after 2 hours.
- DNS binary packet parsing and `0.0.0.0` / `::` synthesis.
- Hosts file synchronization and cleanup.
