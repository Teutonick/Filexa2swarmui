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
The extension uses `SixLabors.ImageSharp` for optional JPEG conversion and relies on the ImageSharp
version already restored by SwarmUI's extension props. Do not add a second ImageSharp package
reference inside this extension; duplicate references can make SwarmUI restore fail.

## Behavior

- The connector only makes outgoing HTTPS/HTTP requests to Filexa.
- It never requires exposing the user's SwarmUI port to the internet.
- It does not delete local SwarmUI outputs.
- It polls lazily every 10 seconds, sends task status updates while enabled, and only runs
  generation when Filexa returns a task.
- It can convert generated images to JPEG at 80% quality before sending them back to Filexa. If a normal direct
  result upload stalls, it automatically falls back to 50 KB binary chunks, then to 8 KB paced
  JSON/base64 chunks, and finally to a safe 4 KB JSON/base64 mode without long per-mode retry loops,
  so unstable networks, MTU issues, or bad routes are less likely to strand the connector on one job.
  JSON/base64 upload uses `Connection: close` and pauses between chunks. When a JSON/base64 mode
  succeeds, the connector caches that local upload mode for several hours and tries the normal direct
  path again after the cache expires.
- The `Cancel active task` button asks Filexa to cancel the current task and returns the connector
  to polling for new tasks.

## Troubleshooting FAQ

### I want to update or reset the extension, but old data is still shown.

If SwarmUI still shows old extension data after an update, delete
`SwarmUI\src\bin\extensions\SwarmExtensionFilexa2SwarmUIConnector` and restart SwarmUI.

### Where do I change model code, steps, and cfg?

Open Filexa and go to Local generation -> SwarmUI settings.

### The result from my PC does not upload back to Filexa.

If the process hangs after the file is ready, you may see a line like this in the SwarmUI
terminal: `[Info] [Filexa2SwarmUI Connector] Upload attempt 1/1: bytes=1772384, mime=image/png`.
The likely cause is network configuration: network, MTU, or route. Try a virtual private network
or another network path. Keep `Convert images to JPEG 80% before upload` enabled in the extension
settings.

### Everything is stuck, the suggested tips do not help, the extension does not react, and Filexa is waiting without errors.

Restarting SwarmUI usually fixes this; cancel the task in Filexa with `/cancel` first. If that
still does not help, delete the extension and install the latest version from Git again.

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
