(function () {
  async function callFilexaApi(route, body) {
    return await new Promise((resolve, reject) => {
      if (typeof genericRequest !== "function") {
        reject(new Error("SwarmUI genericRequest API is not available"));
        return;
      }
      genericRequest(route, body || {}, resolve, undefined, (error) => {
        reject(new Error(String(error || "request failed")));
      });
    });
  }

  function formatSeconds(value) {
    const seconds = Number(value || 0);
    if (!Number.isFinite(seconds) || seconds <= 0) {
      return "-";
    }
    return `${seconds.toFixed(seconds < 10 ? 1 : 0)}s`;
  }

  function elapsedSeconds(startedAt) {
    const start = Date.parse(startedAt || "");
    if (!Number.isFinite(start)) {
      return 0;
    }
    return Math.max(0, (Date.now() - start) / 1000);
  }

  function renderStatus(data) {
    const lines = [
      `Status: ${data.status || "unknown"}`,
      `Last event: ${data.last_event || "-"}`,
      `Token saved: ${data.has_token ? "yes" : "no"}`,
      `Debug logging: ${data.debug_logging ? "on" : "off"}`,
      `JPEG 80% conversion before upload: ${data.compress_images_before_upload ? "on" : "off"}`,
      `Result upload to bot: ${data.keep_result_on_pc_only ? "off" : "on"}`,
      `Polls: ${data.poll_count || 0}`,
    ];
    if (data.active_job_id) {
      lines.push(`Active job: ${data.active_job_id}`);
      lines.push(`Kind: ${data.active_kind || "-"}`);
      lines.push(`Elapsed: ${formatSeconds(elapsedSeconds(data.started_at_utc))}`);
      if (data.active_prompt_preview) {
        lines.push(`Prompt: ${data.active_prompt_preview}`);
      }
    }
    if (data.last_duration_seconds) {
      lines.push(`Last duration: ${formatSeconds(data.last_duration_seconds)}`);
    }
    if (data.upload_mode_hint) {
      lines.push(`Upload mode cache: ${data.upload_mode_hint}`);
    }
    if (data.last_error) {
      lines.push(`Last error: ${data.last_error}`);
    }
    return lines.join("\n");
  }

  async function refreshStatus({ updateFields } = { updateFields: false }) {
    const status = document.getElementById("filexa_status");
    if (!status) {
      return;
    }
    try {
      const data = await callFilexaApi("GetFilexa2SwarmUIConnectorConfig", {});
      if (updateFields) {
        document.getElementById("filexa_api_url").value = data.api_url || "";
        document.getElementById("filexa_swarm_url").value = data.swarm_url || "http://127.0.0.1:7801";
        document.getElementById("filexa_enabled").checked = !!data.enabled;
        document.getElementById("filexa_debug_logging").checked = !!data.debug_logging;
        document.getElementById("filexa_compress_images_before_upload").checked = data.compress_images_before_upload !== false;
        document.getElementById("filexa_keep_result_on_pc_only").checked = !!data.keep_result_on_pc_only;
      }
      status.textContent = renderStatus(data);
    } catch (error) {
      status.textContent = `Could not read connector status: ${error.message}`;
    }
  }

  window.filexa2SwarmUIConnectorLoad = async function () {
    await refreshStatus({ updateFields: true });
  };

  window.filexa2SwarmUIConnectorRefreshStatus = async function () {
    await refreshStatus({ updateFields: false });
  };

  window.filexa2SwarmUIConnectorSave = async function () {
    const status = document.getElementById("filexa_status");
    try {
      const data = await callFilexaApi("SaveFilexa2SwarmUIConnectorConfig", {
        api_url: document.getElementById("filexa_api_url").value,
        token: document.getElementById("filexa_token").value,
        swarm_url: document.getElementById("filexa_swarm_url").value,
        enabled: document.getElementById("filexa_enabled").checked,
        debug_logging: document.getElementById("filexa_debug_logging").checked,
        compress_images_before_upload: document.getElementById("filexa_compress_images_before_upload").checked,
        keep_result_on_pc_only: document.getElementById("filexa_keep_result_on_pc_only").checked,
      });
      document.getElementById("filexa_token").value = "";
      status.textContent = renderStatus(data);
    } catch (error) {
      status.textContent = `Save failed: ${error.message}`;
    }
  };

  window.filexa2SwarmUIConnectorDisconnect = async function () {
    const status = document.getElementById("filexa_status");
    try {
      await callFilexaApi("DisconnectFilexa2SwarmUIConnector", {});
      status.textContent = "Disconnected.";
      window.filexa2SwarmUIConnectorLoad();
    } catch (error) {
      status.textContent = `Disconnect failed: ${error.message}`;
    }
  };

  window.filexa2SwarmUIConnectorCancelTask = async function () {
    const status = document.getElementById("filexa_status");
    try {
      const data = await callFilexaApi("CancelFilexa2SwarmUIConnectorTask", {});
      status.textContent = renderStatus(data);
    } catch (error) {
      status.textContent = `Cancel failed: ${error.message}`;
    }
  };

  document.addEventListener("DOMContentLoaded", () => {
    if (document.getElementById("filexa_status")) {
      window.filexa2SwarmUIConnectorLoad();
      window.clearInterval(window.filexa2SwarmUIConnectorStatusTimer);
      window.filexa2SwarmUIConnectorStatusTimer = window.setInterval(window.filexa2SwarmUIConnectorRefreshStatus, 2000);
    }
  });
})();
