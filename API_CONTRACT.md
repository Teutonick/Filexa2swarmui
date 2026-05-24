# Filexa2SwarmUI Connector API Contract

This contract describes the bot-side API a third-party bot/server must implement to reuse the
Filexa2SwarmUI Connector extension.

Version: 2026-05-24.

The extension has no public inbound HTTP API for bots. It polls an outbound Filexa-compatible API,
then talks to the user's local SwarmUI instance through SwarmUI's own `GetNewSession` and
`GenerateText2Image` APIs.

## Required Bot API

Implement the Filexa local connector API described in:

`../../docs/LOCAL_GENERATION_CONNECTOR_API_CONTRACT.md`

The connector calls these routes:

- `POST /local/v1/tasks/poll`
- `GET /local/v1/tasks/{job_id}/references/{index}`
- `GET /local/v1/tasks/{job_id}/references/{index}/text-chunks/{chunk_index}`
- `POST /local/v1/tasks/{job_id}/status`
- `POST /local/v1/tasks/{job_id}/result`
- `POST /local/v1/tasks/{job_id}/result/chunks/{index}`
- `POST /local/v1/tasks/{job_id}/result/text-chunks/{index}`
- `POST /local/v1/tasks/{job_id}/complete`
- `POST /local/v1/tasks/{job_id}/failure`
- `POST /local/v1/tasks/{job_id}/cancel`

All requests use `Authorization: Bearer <token>` and `X-Filexa-Connector-Version`.

## Task Contract

Supported task kinds:

- `image`
- `image_edit`

Required task fields:

```json
{
  "job_id": "0123456789abcdef0123456789abcdef",
  "kind": "image",
  "engine": "swarmui",
  "client_type": "swarmui",
  "prompt": "A neon city",
  "model": "split_files/diffusion_models/flux-2-klein-4b.safetensors",
  "params": {
    "model": "split_files/diffusion_models/flux-2-klein-4b.safetensors",
    "images": 1,
    "width": 1024,
    "height": 1024,
    "steps": 8,
    "cfgscale": 1.0,
    "seed": -1
  },
  "references": [],
  "deadline_at": "2026-05-24T12:00:00+00:00",
  "result_upload_url": "/local/v1/tasks/<job_id>/result",
  "result_chunk_upload_url": "/local/v1/tasks/<job_id>/result/chunks",
  "result_text_chunk_upload_url": "/local/v1/tasks/<job_id>/result/text-chunks",
  "result_complete_url": "/local/v1/tasks/<job_id>/complete",
  "status_url": "/local/v1/tasks/<job_id>/status",
  "failure_url": "/local/v1/tasks/<job_id>/failure",
  "cancel_url": "/local/v1/tasks/<job_id>/cancel"
}
```

Validation expectations:

- `job_id`: 32 hex characters.
- `prompt`: non-empty, max 8000 characters, no control characters except common whitespace.
- `engine` and `client_type`: `swarmui`.
- `params.model`: SwarmUI model code/path, max 200 characters, no `..`, no URLs, no shell metacharacters.
- `images`: `1..4`; Filexa currently sends `1`.
- `width` and `height`: `64..4096`.
- `steps`: `1..150`.
- `cfgscale`: `0..30`.
- `seed`: `-1..int32 max`.

## Reference Handling

For `image_edit`, provide up to four image references in the standard Filexa reference descriptor
shape. The connector downloads them, converts them to data URLs, and sends them to SwarmUI as
`promptimages`.

If direct reference download fails, the connector retries through JSON/base64 reference chunks and
caches that mode for a short period.

## Result Handling

The connector uploads one generated image:

- direct raw bytes first, capped at 40 MiB;
- optional JPEG conversion before upload;
- binary chunks of 50 KiB for compressed results up to 3 MiB;
- JSON/base64 chunks of 8 KiB and then 4 KiB safe mode;
- `/complete` if upload is disabled, impossible, or the file remains too large.

The connector does not currently send a `model_type` result metadata field. A bot should ignore
unknown metadata fields if future versions add them.

## Bot Compatibility Notes

- Return `410 Gone` when the task is no longer waiting; the connector treats it as terminal.
- Keep task URLs on the same origin as the configured API URL.
- Do not long-poll; the connector polls every 10 seconds.
- If you implement a non-Filexa bot, issue per-user bearer tokens and keep result/reference bytes
  temporary.
