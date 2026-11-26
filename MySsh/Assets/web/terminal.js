(function () {
  "use strict";

  const container = document.getElementById("terminal");

  function postToHost(kind, payload) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({ kind: kind, payload: payload });
    }
  }

  window.addEventListener("error", function (ev) {
    postToHost("js_error", (ev && ev.message) ? String(ev.message) : "Ошибка скрипта терминала");
  });

  if (!container || typeof Terminal === "undefined") {
    postToHost("js_error", "Не найден контейнер #terminal или не загружен xterm.js");
    return;
  }

  const term = new Terminal({
    cursorBlink: true,
    fontFamily: "Consolas, 'Cascadia Mono', monospace",
    fontSize: 14,
    scrollback: 5000,
    convertEol: false,
    theme: {
      background: "#1e2024",
      foreground: "#e8ebf0",
      cursor: "#e8ebf0",
      selectionBackground: "#3a4a63"
    }
  });

  term.attachCustomKeyEventHandler(function (ev) {
    if (ev.type !== "keydown") {
      return true;
    }

    const ctrl = ev.ctrlKey && !ev.altKey;
    const ctrlShift = ev.ctrlKey && ev.shiftKey && !ev.altKey;
    const shiftInsert = ev.shiftKey && !ev.ctrlKey && !ev.altKey && ev.code === "Insert";

    if (ctrlShift && ev.code === "KeyC") {
      const selected = term.getSelection();
      if (selected) {
        postToHost("copy", selected);
      }
      ev.preventDefault();
      ev.stopPropagation();
      return false;
    }

    if ((ctrlShift && ev.code === "KeyV") || shiftInsert) {
      postToHost("paste_request", null);
      ev.preventDefault();
      ev.stopPropagation();
      return false;
    }

    if (ctrl && !ev.shiftKey && ev.code === "KeyC") {
      if (term.hasSelection()) {
        postToHost("copy", term.getSelection());
        term.clearSelection();
        ev.preventDefault();
        ev.stopPropagation();
        return false;
      }
      return true;
    }

    if (ctrl && !ev.shiftKey && ev.code === "KeyV") {
      postToHost("paste_request", null);
      ev.preventDefault();
      ev.stopPropagation();
      return false;
    }

    return true;
  });

  term.open(container);

  // --- Контекстное меню по правому клику (Копировать / Вставить) ---
  const menu = document.createElement("div");
  menu.id = "myssh-context-menu";
  menu.style.cssText = [
    "position:fixed",
    "z-index:9999",
    "display:none",
    "min-width:165px",
    "padding:4px 0",
    "background:#252830",
    "border:1px solid #3c414d",
    "border-radius:6px",
    "box-shadow:0 8px 24px rgba(0,0,0,0.45)",
    "font-family:'Segoe UI',sans-serif",
    "font-size:13px",
    "color:#e8ebf0",
    "user-select:none"
  ].join(";");

  function createMenuItem(label, onClick) {
    const item = document.createElement("div");
    item.textContent = label;
    item.style.cssText = [
      "padding:7px 14px",
      "cursor:pointer",
      "display:flex",
      "align-items:center",
      "justify-content:space-between"
    ].join(";");
    item.addEventListener("mouseenter", function () {
      if (item.dataset.disabled === "1") return;
      item.style.background = "#345280";
    });
    item.addEventListener("mouseleave", function () {
      item.style.background = "transparent";
    });
    item.addEventListener("click", function (ev) {
      ev.stopPropagation();
      if (item.dataset.disabled === "1") return;
      hideMenu();
      onClick();
    });
    return item;
  }

  function setItemDisabled(item, disabled) {
    item.dataset.disabled = disabled ? "1" : "0";
    item.style.opacity = disabled ? "0.45" : "1";
    item.style.cursor = disabled ? "default" : "pointer";
    if (disabled) {
      item.style.background = "transparent";
    }
  }

  const copyItem = createMenuItem("Копировать", function () {
    const selected = term.getSelection();
    if (!selected) return;
    postToHost("copy", selected);
    term.focus();
  });

  const pasteItem = createMenuItem("Вставить", function () {
    postToHost("paste_request", null);
    term.focus();
  });

  menu.appendChild(copyItem);
  menu.appendChild(pasteItem);
  document.body.appendChild(menu);

  function hideMenu() {
    menu.style.display = "none";
  }

  function showMenu(x, y) {
    setItemDisabled(copyItem, !term.hasSelection());

    menu.style.left = "0px";
    menu.style.top = "0px";
    menu.style.display = "block";

    const rect = menu.getBoundingClientRect();
    const maxX = Math.max(4, window.innerWidth - rect.width - 6);
    const maxY = Math.max(4, window.innerHeight - rect.height - 6);

    menu.style.left = Math.min(x, maxX) + "px";
    menu.style.top = Math.min(y, maxY) + "px";
  }

  container.addEventListener("contextmenu", function (ev) {
    ev.preventDefault();
    ev.stopPropagation();
    showMenu(ev.clientX, ev.clientY);
  });

  document.addEventListener("click", function (ev) {
    if (!menu.contains(ev.target)) {
      hideMenu();
    }
    term.focus();
  });

  window.addEventListener("blur", hideMenu);
  window.addEventListener("keydown", function (ev) {
    if (ev.key === "Escape") {
      hideMenu();
    }
  });

  window.__mySshPasteText = function (text) {
    if (!text) return;
    const normalized = String(text).replace(/\r\n/g, "\r").replace(/\n/g, "\r");
    postToHost("input", normalized);
  };

  // --- Подсветка отправленных команд ---
  // Работает в обычном shell; автоматически отключается в полноэкранных
  // приложениях (mc/vim), где xterm переключается в alternate screen buffer.
  let markTypedCommands = true;
  let buffer = "";

  function currentLineIsSafeForHighlight() {
    return markTypedCommands && term.buffer.active.type !== "alternate";
  }

  function highlightTypedLine() {
    const active = term.buffer.active;
    const lineIndex = active.viewportY + active.cursorY;
    const line = active.getLine(lineIndex);
    if (!line) return;

    const lineText = line.translateToString(true).trimEnd();
    if (!lineText) return;

    // Подсвечиваем только текущую строку ввода и сразу сбрасываем цвет (39 = default fg),
    // чтобы жёлтый цвет не переносился на вывод команды и следующие строки.
    term.write("\r\x1b[2K\x1b[1;33m" + lineText + "\x1b[39m\x1b[22m");
  }

  term.onData(function (data) {
    if (currentLineIsSafeForHighlight()) {
      for (const ch of data) {
        if (ch === "\r" || ch === "\n") {
          if (buffer.trim().length > 0) {
            highlightTypedLine();
          }
          buffer = "";
        } else if (ch === "\u0003" || ch === "\u0015") {
          // Ctrl+C / Ctrl+U сбрасывают текущую набираемую строку
          buffer = "";
        } else if (ch === "\u007f" || ch === "\b") {
          buffer = buffer.slice(0, -1);
        } else if (ch >= " " || ch === "\t") {
          buffer += ch;
        }
      }
    } else {
      buffer = "";
    }

    postToHost("input", data);
  });

  // --- Приём вывода от C# (Base64 от сырых байтов) ---
  function base64TextToBytes(b64) {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) {
      bytes[i] = bin.charCodeAt(i);
    }
    return bytes;
  }

  window.__mySshWriteText64 = function (b64) {
    term.write(base64TextToBytes(b64));
  };

  window.__mySshWrite = function (b64) {
    term.write(base64TextToBytes(b64));
  };

  window.__mySshWriteText = function (text) {
    term.write("\r\n\x1b[90m" + text + "\x1b[0m\r\n");
  };

  // --- Расчёт cols/rows по размерам контейнера и ресайз PTY ---
  let lastCols = 0;
  let lastRows = 0;
  let resizeTimer = null;

  function measureCellSize() {
    const core = term._core;
    const dims = core && core._renderService && core._renderService.dimensions;
    if (dims && dims.css && dims.css.cell && dims.css.cell.width > 0 && dims.css.cell.height > 0) {
      return {
        width: dims.css.cell.width,
        height: dims.css.cell.height
      };
    }

    const probe = document.createElement("span");
    probe.textContent = "W";
    probe.style.position = "absolute";
    probe.style.visibility = "hidden";
    probe.style.fontFamily = term.options.fontFamily;
    probe.style.fontSize = term.options.fontSize + "px";
    container.appendChild(probe);
    const rect = probe.getBoundingClientRect();
    container.removeChild(probe);
    return {
      width: rect.width || 8.4,
      height: rect.height || 17
    };
  }

  function fitAndReportSize(forceSend) {
    const style = window.getComputedStyle(container);
    const padX = (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
    const padY = (parseFloat(style.paddingTop) || 0) + (parseFloat(style.paddingBottom) || 0);

    const availableWidth = Math.max(0, container.clientWidth - padX);
    const availableHeight = Math.max(0, container.clientHeight - padY);
    if (availableWidth === 0 || availableHeight === 0) return;

    const cell = measureCellSize();
    const cols = Math.max(20, Math.floor(availableWidth / cell.width));
    const rows = Math.max(5, Math.floor(availableHeight / cell.height));

    if (cols !== term.cols || rows !== term.rows) {
      term.resize(cols, rows);
    }

    if (forceSend || cols !== lastCols || rows !== lastRows) {
      lastCols = cols;
      lastRows = rows;
      postToHost("resize", { cols: cols, rows: rows });
    }
  }

  window.__mySshForceResize = function () {
    fitAndReportSize(true);
  };

  function scheduleFit() {
    if (resizeTimer !== null) {
      clearTimeout(resizeTimer);
    }
    resizeTimer = setTimeout(function () {
      fitAndReportSize(false);
    }, 30);
  }

  if (typeof ResizeObserver !== "undefined") {
    const resizeObserver = new ResizeObserver(scheduleFit);
    resizeObserver.observe(container);
  }
  window.addEventListener("resize", scheduleFit);

  requestAnimationFrame(function () {
    fitAndReportSize(true);
    postToHost("ready", null);
    term.focus();

    setTimeout(function () {
      fitAndReportSize(true);
    }, 150);
  });
})();