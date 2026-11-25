# Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the
user.

## What stays on your computer

- Sensor readings, usage history, game sessions and benchmark results are stored locally in
  `C:\ProgramData\Nodekeeper` (service) and `%AppData%\Nodekeeper` (dashboard).
- Nodekeeper has no telemetry, analytics, accounts, ads or crash reporting.
- **Alert channels (off unless you set them up):** if you enter an ntfy topic, a Discord webhook or a Telegram bot
  on the Alerts page, that machine's service sends the alert title and text (for example "NAS: Volume almost full —
  D: is 96 % full") to that service when an alert starts or ends, and when you press **Send test**. Nothing else is
  sent. The webhook URL and bot token are stored only in that machine's `C:\ProgramData\Nodekeeper\alertas.json`.
- Apart from that, the service never contacts the internet. Two more exceptions, both plain downloads that send
  nothing about you or your PCs:
  - **Update notice** (dashboard, on by default; Settings › Updates turns it off): once a day the dashboard reads the
    public list of Nodekeeper releases from GitHub (`api.github.com/repos/acboer/Nodekeeper/releases`) to tell you
    when a new version is out. It downloads and installs nothing.
  - **Installer**: if the PawnIO sensor driver is missing and you leave **Install the PawnIO driver** checked, it
    downloads the official PawnIO installer from GitHub (`github.com/namazso/PawnIO.Setup/releases`).
- GitHub sees these requests like any web request (your IP address); see GitHub's privacy statement.

## What is shared on your local network, and only when you ask

- The Nodekeeper service listens on TCP port 47821 (HTTPS), opened in Windows Firewall for **private and domain** networks only (not public networks).
- It answers only dashboards you have paired yourself, by accepting an access request on that PC or by typing a
  one-time 6-digit code. Each paired dashboard gets its own token; you can remove a machine from the dashboard and
  revoke a dashboard on the service (`Nodekeeper.Service.exe revogar`).
- The dashboard's network speed test sends random data between your own two PCs.

## Third parties

Nodekeeper does not use third-party services. The bundled open-source libraries
([third-party notices](THIRD-PARTY-NOTICES.md)) run locally and do not send data anywhere.

## Changes

Any future feature that sends data off your network (for example an opt-in benchmark ranking) will be off by
default, explained in the app before it is enabled, and described here.
