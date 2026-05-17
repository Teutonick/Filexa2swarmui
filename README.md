# Filexa2SwarmUI Connector

Connects SwarmUI to Filexa local generation so Telegram users can run T2I and I2I jobs on this PC.
The connector tab shows live polling, active job, elapsed time, latest upload status, and optional debug logging.

Bot: https://t.me/WorkOnBigFilesBot

Not affiliated with, endorsed by, or sponsored by SwarmUI.

## Layout

- `Filexa2SwarmUIConnector/` - SwarmUI extension source.
- `README.md` - installation and usage guide.
- `LICENSE` - source code license.
- `NOTICE.md` - legal notices and disclaimers.
- `SECURITY.md` - vulnerability reporting policy.

Prebuilt binaries are not distributed in this repository.

## Install For A SwarmUI User
The extension is designed to work with https://t.me/WorkOnBigFilesBot only.

1. Install SwarmUI from the official project:
   https://github.com/mcmonkeyprojects/SwarmUI
2. Launch SwarmUI once and finish its first-run setup.
3. Open the SwarmUI Flux.2 Klein recommendation:
   https://github.com/mcmonkeyprojects/SwarmUI/blob/master/docs/Model%20Support.md#flux2-klein
4. Download a Flux.2 Klein checkpoint. Prefer SwarmUI's built-in `Utilities` -> `Model Downloader`,
   or place the model file into `SwarmUI\Models\diffusion_models`. For the lowest entry barrier
   start with Klein 4B distilled: it is the smaller/faster option; SwarmUI recommends `Steps=8`
   and `CFG Scale=1` for distilled Klein models. Klein 9B is heavier; the KV-cache variant is
   mainly for users with much more VRAM.
5. Restart SwarmUI after the model is available. SwarmUI will autodownload smaller text
   encoder/VAE dependencies when needed.
6. Restart SwarmUI, open the Generate tab, select Flux.2 Klein, and verify one local text-to-image
   generation before connecting the bot.
7. Copy `Filexa2SwarmUIConnector` into your SwarmUI folder:
   `SwarmUI/src/Extensions/Filexa2SwarmUIConnector`.
8. Restart SwarmUI or run the SwarmUI update/build script so the extension compiles.
9. Open the new `Filexa2SwarmUI Connector` tab.
10. Paste the Filexa API URL and token shown by the Telegram bot. Save the token if you might
    reuse it; the tab hides it after saving.
11. In the bot settings, set the SwarmUI model code, steps, and cfg if the defaults do not match.
12. Enable the connector and keep SwarmUI running.

The connector uses SwarmUI's normal local API (`GetNewSession`, `GenerateText2Image`) and sends
all traffic outbound from the user's PC to Filexa. No public SwarmUI port is required.

SwarmUI compiles extensions inside its own source tree. This repository provides the connector source code only. SwarmUI compiles the
extension inside the user's local SwarmUI installation during restart/update.
Prebuilt binaries are not distributed in this repository.

## Behavior

- The connector only makes outgoing HTTPS/HTTP requests to Filexa.
- It never requires exposing the user's SwarmUI port to the internet.
- It does not delete local SwarmUI outputs.
- It polls lazily every 10 seconds, sends heartbeat/status updates while enabled, and only runs
  generation when Filexa returns a task.

**Important: the developer and the bot do not have access to the user's computer. All operations
with third-party software, downloaded models, and local configuration are performed by the user at
their own risk. The developer is not responsible for output quality, software breakage, hardware
damage, data loss, or any other loss caused by these actions.**


## Legal Notice

This repository contains only the Filexa2SwarmUI Connector source code.

The connector is licensed under the MIT License. The Filexa bot/API service is
provided under separate Filexa Terms of Use and Privacy Policy:
https://teutonick.github.io/bot-legal-docs/privacy

This connector is not part of SwarmUI and is not affiliated with or endorsed by
the SwarmUI project. SwarmUI, AI models, model weights, checkpoints, drivers,
and other runtime components are third-party software and may have their own
licenses and restrictions.

Users are solely responsible for installing SwarmUI, selecting and licensing
models, securing their API tokens, operating their local computer, reviewing
generated outputs, and complying with applicable laws and third-party terms.

The connector makes outbound HTTP/HTTPS requests to the configured Filexa API
endpoint. It does not require exposing the user's local SwarmUI port to the
public internet.

The connector stores the Filexa API URL and token in the local SwarmUI extension
configuration file. Anyone with access to this local file may be able to read
the token. Keep your SwarmUI installation and user account secure.


## Security Notice
Security issues should be reported privately according to SECURITY.md.
