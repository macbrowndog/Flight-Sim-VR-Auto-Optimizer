class IngamePanelFlightDeckToolsDashboard extends TemplateElement {
  constructor() {
    super(...arguments);
    this.socket = null;
    this.reconnectTimer = null;
    this.started = false;
    this.lastTimestamp = null;
    this.fpsHistory = [];
    this.systemCpuHistory = [];
    this.simCpuHistory = [];
    this.serverStutters = 0;
    this.serverSpikes = 0;
    this.counterBaseline = { stutters: 0, spikes: 0 };
    this.panelHeightFitted = false;
  }

  connectedCallback() {
    super.connectedCallback();
    if (document.readyState === "complete") this.startPanel();
    else window.addEventListener("load", () => this.startPanel(), { once: true });
  }

  disconnectedCallback() {
    this.stopConnection();
    if (super.disconnectedCallback) super.disconnectedCallback();
  }

  startPanel() {
    if (this.started) return;
    this.started = true;
    const ui = document.querySelector("ingame-ui");
    if (ui) {
      ui.setAttribute("title", "VR OPTIMIZER");
      new MutationObserver(() => {
        if (ui.getAttribute("title") !== "VR OPTIMIZER") ui.setAttribute("title", "VR OPTIMIZER");
      }).observe(ui, { attributes: true, attributeFilter: ["title"] });
      ui.addEventListener("onResizeElement", () => this.drawGraphs());
      ui.addEventListener("panelActive", () => setTimeout(() => this.fitPanelHeight(), 100));
    }
    document.addEventListener("dataStorageReady", () => setTimeout(() => this.fitPanelHeight(), 100));
    const resetStutters = document.getElementById("reset-stutters");
    if (resetStutters) resetStutters.addEventListener("click", () => this.resetStutters());
    const resetSpikes = document.getElementById("reset-spikes");
    if (resetSpikes) resetSpikes.addEventListener("click", () => this.resetSpikes());
    window.addEventListener("resize", () => this.drawGraphs());
    this.connect();
    setTimeout(() => this.fitPanelHeight(), 500);
  }

  connect() {
    this.stopConnection(false);
    this.setLinkState(false, "CONNECTING");
    try {
      this.socket = new WebSocket("ws://127.0.0.1:48624/dashboard");
      this.socket.addEventListener("open", () => this.setLinkState(true, "OPTIMIZER LINK"));
      this.socket.addEventListener("message", event => this.receive(event.data));
      this.socket.addEventListener("close", () => this.connectionLost());
      this.socket.addEventListener("error", () => {
        if (this.socket) this.socket.close();
      });
    } catch (_) {
      this.connectionLost();
    }
  }

  connectionLost() {
    this.socket = null;
    this.setLinkState(false, "OFFLINE");
    this.renderTurboMode(false);
    this.setText("source-status", "START VR AUTO-OPTIMIZER TO ENABLE LIVE DATA");
    if (!this.reconnectTimer)
      this.reconnectTimer = setTimeout(() => {
        this.reconnectTimer = null;
        this.connect();
      }, 2000);
  }

  stopConnection(cancelReconnect = true) {
    if (this.socket) {
      const socket = this.socket;
      this.socket = null;
      socket.onclose = null;
      socket.close();
    }
    if (cancelReconnect && this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  receive(text) {
    let frame;
    try { frame = JSON.parse(text); }
    catch (_) { return; }

    this.setText("source-status", (frame.sessionActive ? "LIVE — " : "STANDBY — ") + (frame.status || "Optimizer ready"));
    this.renderTurboMode(frame.openXrTurboMode === true);
    const sample = frame.sample;
    if (sample) {
      this.setMetric("fps", sample.fps, 1);
      this.setMetric("average", sample.averageFps, 1);
      this.setMetric("one-low", sample.onePercentLowFps, 1);
      this.setMetric("frame-ms", sample.frameTimeMs, 1);
      this.setText("sim-cpu", this.number(sample.simulatorCpuPercent, 1) + "%");
      this.setText("main-thread", sample.mainThreadFrameTimeMs == null
        ? "—"
        : this.number(sample.mainThreadFrameTimeMs, 1) + " ms");
      this.setText("memory", this.integer(sample.simulatorMemoryMb) + " MB");
      this.setText("system-cpu", "SYSTEM CPU " + this.number(sample.systemCpuPercent, 1) + "%");
      this.setText("cpu-name", frame.cpuName || "CPU MODEL UNAVAILABLE");
      this.renderProcessorGroups(frame.processorGroups || [], sample.logicalProcessorUsage || []);

      if (sample.timestamp !== this.lastTimestamp) {
        this.lastTimestamp = sample.timestamp;
        this.pushHistory(this.fpsHistory, this.value(sample.fps));
        this.pushHistory(this.systemCpuHistory, this.value(sample.systemCpuPercent));
        this.pushHistory(this.simCpuHistory, this.value(sample.simulatorCpuPercent));
      }
      this.drawGraphs();
    }

    this.serverStutters = frame.stutterCount || 0;
    this.serverSpikes = frame.cpuSpikeCount || 0;
    if (this.serverStutters < this.counterBaseline.stutters) this.counterBaseline.stutters = 0;
    if (this.serverSpikes < this.counterBaseline.spikes) this.counterBaseline.spikes = 0;
    const stutters = Math.max(0, this.serverStutters - this.counterBaseline.stutters);
    const spikes = Math.max(0, this.serverSpikes - this.counterBaseline.spikes);
    this.renderCounter("stutter-alert", "stutter-card", "FRAME-TIME STUTTERS", stutters);
    this.renderCounter("spike-alert", "spike-card", "CPU SPIKE SAMPLES", spikes);
    this.fitPanelHeight();
  }

  resetStutters() {
    this.counterBaseline.stutters = this.serverStutters;
    this.renderCounter("stutter-alert", "stutter-card", "FRAME-TIME STUTTERS", 0);
  }

  resetSpikes() {
    this.counterBaseline.spikes = this.serverSpikes;
    this.renderCounter("spike-alert", "spike-card", "CPU SPIKE SAMPLES", 0);
  }

  renderTurboMode(enabled) {
    this.setText("turbo-state", "OPENXR TURBO MODE: " + (enabled ? "ON" : "OFF"));
    const state = document.getElementById("turbo-state");
    if (state) {
      state.classList.toggle("on", enabled);
      state.classList.toggle("off", !enabled);
    }
  }

  renderCounter(textId, cardId, label, count) {
    this.setText(textId, label + ": " + count);
    const card = document.getElementById(cardId);
    if (card) {
      card.classList.toggle("warning", count > 0);
      card.classList.toggle("good", count === 0);
    }
  }

  fitPanelHeight() {
    if (this.panelHeightFitted) return;
    const ui = document.querySelector("ingame-ui");
    const dashboard = document.getElementById("dashboard");
    if (!ui || !dashboard || !ui.dragDropHandler || typeof Coherent === "undefined") return;
    const header = ui.querySelector("ingame-ui-header");
    const headerHeight = header ? header.getBoundingClientRect().height : 0;
    const desired = Math.ceil(dashboard.scrollHeight + headerHeight + 18);
    const current = ui.getBoundingClientRect();
    if (desired > 100 && current.height > desired + 20) {
      ui.style.height = desired + "px";
      const panelId = ui.panelID || ui.getAttribute("panel-id");
      const resized = ui.getBoundingClientRect();
      Coherent.trigger("UPDATE_PANEL_RECT", panelId, resized.left, resized.right, resized.top, resized.top + desired);
      Coherent.trigger("UPDATE_PANEL_HEIGHT", panelId, desired);
      this.panelHeightFitted = true;
    }
  }

  renderProcessorGroups(groups, fallbackValues) {
    const container = document.getElementById("cores");
    if (!container) return;
    container.textContent = "";
    if (!groups.length && fallbackValues.length) {
      const values = fallbackValues.map(value => Math.max(0, Math.min(100, Number(value) || 0)));
      const peak = Math.max(...values);
      groups = [{
        label: "ALL LOGICAL",
        averagePercent: values.reduce((sum, value) => sum + value, 0) / values.length,
        peakPercent: peak,
        logicalProcessorCount: values.length,
        peakLogicalProcessor: values.indexOf(peak)
      }];
    }
    if (!groups.length) {
      const empty = document.createElement("div");
      empty.className = "empty";
      empty.textContent = "Waiting for CPU samples…";
      container.appendChild(empty);
      return;
    }
    groups.forEach(group => {
      const item = document.createElement("div");
      item.className = "processor-group";
      const label = document.createElement("b");
      label.textContent = group.label || "CPU";
      const average = document.createElement("span");
      average.textContent = "AVG " + this.number(group.averagePercent, 1) + "%";
      const peak = document.createElement("span");
      peak.textContent = "PEAK L" + String(group.peakLogicalProcessor).padStart(2, "0") + "  " + this.number(group.peakPercent, 1) + "%";
      item.appendChild(label);
      item.appendChild(average);
      item.appendChild(peak);
      container.appendChild(item);
    });
  }

  drawGraphs() {
    const validFps = this.fpsHistory.filter(value => value !== null);
    const fpsMaximum = Math.max(60, Math.ceil((validFps.length ? Math.max(...validFps) : 60) / 30) * 30);
    this.setText("fps-scale", "0–" + fpsMaximum);
    this.drawGraph("fps-graph", [{ values: this.fpsHistory, color: "#64df91" }], fpsMaximum);
    this.drawGraph("cpu-graph", [
      { values: this.systemCpuHistory, color: "#ffbf45" },
      { values: this.simCpuHistory, color: "#5adbe8" }
    ], 100);
  }

  drawGraph(id, series, maximum) {
    const canvas = document.getElementById(id);
    if (!canvas) return;
    const width = Math.max(1, Math.floor(canvas.clientWidth));
    const height = Math.max(1, Math.floor(canvas.clientHeight));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    const context = canvas.getContext("2d");
    context.clearRect(0, 0, width, height);
    context.strokeStyle = "#183034";
    context.lineWidth = 1;
    [0.25, 0.5, 0.75].forEach(level => {
      const y = Math.round(height * level) + 0.5;
      context.beginPath(); context.moveTo(0, y); context.lineTo(width, y); context.stroke();
    });

    series.forEach(line => {
      context.strokeStyle = line.color;
      context.lineWidth = 2;
      context.beginPath();
      let drawing = false;
      line.values.forEach((value, index) => {
        if (value === null || !Number.isFinite(value)) { drawing = false; return; }
        const x = line.values.length <= 1 ? width : index * width / (line.values.length - 1);
        const y = height - Math.max(0, Math.min(1, value / maximum)) * height;
        if (!drawing) { context.moveTo(x, y); drawing = true; }
        else context.lineTo(x, y);
      });
      context.stroke();
    });
  }

  setLinkState(online, label) {
    const element = document.getElementById("link-state");
    if (!element) return;
    element.classList.toggle("online", online);
    element.classList.toggle("offline", !online);
    element.innerHTML = "<span></span> " + label;
  }

  setMetric(id, value, digits) {
    this.setText(id, this.value(value) === null ? "—" : Number(value).toFixed(digits));
  }

  setText(id, text) {
    const element = document.getElementById(id);
    if (element) element.textContent = text;
  }

  pushHistory(history, value) {
    history.push(value);
    while (history.length > 120) history.shift();
  }

  value(value) {
    return value === null || value === undefined || !Number.isFinite(Number(value)) ? null : Number(value);
  }

  number(value, digits) {
    const number = this.value(value);
    return number === null ? "—" : number.toFixed(digits);
  }

  integer(value) {
    const number = this.value(value);
    return number === null ? "—" : Math.round(number).toLocaleString();
  }
}

window.customElements.define("ingamepanel-flightdecktools-dashboard", IngamePanelFlightDeckToolsDashboard);
checkAutoload();
