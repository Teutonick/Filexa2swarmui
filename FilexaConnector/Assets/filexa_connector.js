(function () {
  async function callFilexaApi(route, body) {
    return await new Promise((resolve, reject) => {
      if (typeof genericRequest !== "function") {
        reject(new Error("SwarmUI genericRequest API is not available"));
        return;
      }
      genericRequest(route, body || {}, resolve, 0, (error) => {
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
      const data = await callFilexaApi("GetFilexaConnectorConfig", {});
      if (updateFields) {
        document.getElementById("filexa_api_url").value = data.api_url || "";
        document.getElementById("filexa_swarm_url").value = data.swarm_url || "http://127.0.0.1:7801";
        document.getElementById("filexa_enabled").checked = !!data.enabled;
        document.getElementById("filexa_debug_logging").checked = !!data.debug_logging;
      }
      status.textContent = renderStatus(data);
    } catch (error) {
      status.textContent = `Could not read connector status: ${error.message}`;
    }
  }

  window.filexaConnectorLoad = async function () {
    await refreshStatus({ updateFields: true });
  };

  window.filexaConnectorRefreshStatus = async function () {
    await refreshStatus({ updateFields: false });
  };

  window.filexaConnectorSave = async function () {
    const status = document.getElementById("filexa_status");
    try {
      const data = await callFilexaApi("SaveFilexaConnectorConfig", {
        api_url: document.getElementById("filexa_api_url").value,
        token: document.getElementById("filexa_token").value,
        swarm_url: document.getElementById("filexa_swarm_url").value,
        enabled: document.getElementById("filexa_enabled").checked,
        debug_logging: document.getElementById("filexa_debug_logging").checked,
      });
      document.getElementById("filexa_token").value = "";
      status.textContent = renderStatus(data);
    } catch (error) {
      status.textContent = `Save failed: ${error.message}`;
    }
  };

  window.filexaConnectorDisconnect = async function () {
    const status = document.getElementById("filexa_status");
    try {
      await callFilexaApi("DisconnectFilexaConnector", {});
      status.textContent = "Disconnected.";
      window.filexaConnectorLoad();
    } catch (error) {
      status.textContent = `Disconnect failed: ${error.message}`;
    }
  };

  document.addEventListener("DOMContentLoaded", () => {
    if (document.getElementById("filexa_status")) {
      window.filexaConnectorLoad();
      window.clearInterval(window.filexaConnectorStatusTimer);
      window.filexaConnectorStatusTimer = window.setInterval(window.filexaConnectorRefreshStatus, 2000);
    }
  });
})();
