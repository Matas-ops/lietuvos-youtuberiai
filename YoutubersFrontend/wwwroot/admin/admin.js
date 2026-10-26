const LEVELS = { Trace: 0, Debug: 1, Information: 2, Warning: 3, Error: 4, Critical: 5 };
const LEVEL_LABELS = { Trace: "TRACE", Debug: "DEBUG", Information: "INFO", Warning: "WARN", Error: "ERROR", Critical: "CRIT" };
const MAX_LOGS = 2000;

const state = {
    lastLogId: 0,
    logs: [],
    statusTimer: null,
    logTimer: null
};

const $ = id => document.getElementById(id);

const dashboardVisible = () => !$("dashboardView").classList.contains("d-none");

// ---------- helpers ----------

function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
}

function isSecureContextForLogin() {
    return location.protocol === "https:"
        || ["localhost", "127.0.0.1", "[::1]"].includes(location.hostname);
}

async function api(path, options = {}) {
    const response = await fetch(path, {
        credentials: "same-origin",
        cache: "no-store",
        ...options
    });

    let body = null;

    try {
        body = await response.json();
    } catch {
    }

    if (response.status === 401 && path !== "/api/admin/login") {
        showLogin();
        throw new Error("Unauthorized");
    }

    return { response, body };
}

// ---------- views ----------

function showLogin() {
    stopPolling();

    $("dashboardView").classList.add("d-none");
    $("logout").classList.add("d-none");
    $("loginView").classList.remove("d-none");

    const secure = isSecureContextForLogin();

    $("insecureWarning").classList.toggle("d-none", secure);
    $("password").disabled = !secure;
    $("loginButton").disabled = !secure;

    if (secure)
        $("password").focus();
}

function showDashboard() {
    $("loginView").classList.add("d-none");
    $("dashboardView").classList.remove("d-none");
    $("logout").classList.remove("d-none");

    startPolling();
}

// ---------- login ----------

$("loginForm").addEventListener("submit", async event => {
    event.preventDefault();

    const input = $("password");
    const error = $("loginError");

    error.classList.add("d-none");

    // the password goes straight into the request body and the field is cleared right away
    const body = JSON.stringify({ password: input.value });
    input.value = "";

    $("loginButton").disabled = true;

    try {
        const { response, body: result } = await api("/api/admin/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body
        });

        if (response.ok) {
            showDashboard();
            return;
        }

        let message = result?.error ?? `Prisijungti nepavyko (${response.status})`;

        if (response.status === 429) {
            const retryAfter = response.headers.get("Retry-After");

            if (retryAfter)
                message += ` (${retryAfter} s)`;
        }

        error.textContent = message;
        error.classList.remove("d-none");
        input.focus();
    } catch {
        error.textContent = "Nepavyko pasiekti serverio";
        error.classList.remove("d-none");
    } finally {
        $("loginButton").disabled = false;
    }
});

$("logout").addEventListener("click", async () => {
    try {
        await api("/api/admin/logout", { method: "POST" });
    } catch {
    }

    state.lastLogId = 0;
    state.logs = [];
    $("logView").innerHTML = "";

    showLogin();
});

// ---------- status & refresh ----------

async function loadStatus() {

    let data;

    try {
        ({ body: data } = await api("/api/admin/status"));
    } catch {
        // network hiccup, keep trying unless we got logged out
        if (dashboardVisible())
            state.statusTimer = setTimeout(loadStatus, 10000);

        return;
    }

    if (!data) {
        state.statusTimer = setTimeout(loadStatus, 10000);
        return;
    }

    $("statChannels").textContent = data.totalChannels;

    const lastRefresh = new Date(data.lastRefresh);

    $("statLastRefresh").textContent =
        lastRefresh.getUTCFullYear() < 2000 ? "Niekada" : lastRefresh.toLocaleString("lt-LT");

    const { scraped, total } = data.progress;

    $("statState").textContent =
        data.isRefreshing ? `Renkama ${scraped} / ${total}` : "Laukia";

    $("refreshButton").disabled = data.isRefreshing;

    $("refreshProgress").classList.toggle("d-none", !data.isRefreshing);
    $("refreshProgress").querySelector(".progress-bar").style.width =
        `${total > 0 ? Math.round(scraped / total * 100) : 0}%`;

    // poll faster while something is happening
    state.statusTimer = setTimeout(loadStatus, data.isRefreshing ? 3000 : 10000);
}

$("refreshButton").addEventListener("click", async () => {

    const message = $("refreshMessage");

    $("refreshButton").disabled = true;

    try {
        const { response, body } = await api("/api/admin/refresh", { method: "POST" });

        message.className = `small mt-2 ${response.ok ? "text-success" : "text-danger"}`;
        message.textContent = response.ok ? "Atnaujinimas pradėtas" : (body?.error ?? `Nepavyko (${response.status})`);
    } catch {
        return;
    }

    clearTimeout(state.statusTimer);
    loadStatus();
});

// ---------- logs ----------

async function loadLogs() {

    try {
        const { body } = await api(`/api/admin/logs?afterId=${state.lastLogId}`);

        if (Array.isArray(body) && body.length > 0) {
            state.logs.push(...body);
            state.lastLogId = body[body.length - 1].id;

            if (state.logs.length > MAX_LOGS)
                state.logs.splice(0, state.logs.length - MAX_LOGS);

            renderLogs();
        }
    } catch {
        if (!dashboardVisible())
            return;
    }

    state.logTimer = setTimeout(loadLogs, 3000);
}

function logVisible(entry) {
    const minLevel = Number($("levelFilter").value);
    const search = $("logSearch").value.trim().toLowerCase();

    return (LEVELS[entry.level] ?? 0) >= minLevel
        && (search === ""
            || entry.message.toLowerCase().includes(search)
            || entry.category.toLowerCase().includes(search));
}

function renderLogs() {

    const view = $("logView");

    const visible = state.logs.filter(logVisible);

    view.innerHTML = visible
        .map(entry => {
            const time = new Date(entry.time).toLocaleTimeString("lt-LT");
            const category = entry.category.split(".").pop();

            return `
                <div class="log-line">
                    <span class="log-time">${escapeHtml(time)}</span>
                    <span class="log-level level-${escapeHtml(entry.level)}" title="${escapeHtml(entry.level)}">${escapeHtml(LEVEL_LABELS[entry.level] ?? entry.level)}</span>
                    <span class="log-message"><span class="log-category" title="${escapeHtml(entry.category)}">${escapeHtml(category)}:</span> ${escapeHtml(entry.message)}</span>
                    ${entry.exception ? `<div class="log-exception">${escapeHtml(entry.exception)}</div>` : ""}
                </div>
            `;
        })
        .join("");

    $("logCount").textContent =
        `Įrašai: ${visible.length} iš ${state.logs.length}`;

    if ($("autoScroll").checked)
        view.scrollTop = view.scrollHeight;
}

$("levelFilter").addEventListener("change", renderLogs);
$("logSearch").addEventListener("input", renderLogs);

// ---------- polling ----------

function startPolling() {
    stopPolling();
    loadStatus();
    loadLogs();
}

function stopPolling() {
    clearTimeout(state.statusTimer);
    clearTimeout(state.logTimer);
}

// an existing session cookie skips the login form
(async () => {
    try {
        const { response } = await api("/api/admin/status");

        if (response.ok)
            showDashboard();
        else
            showLogin();
    } catch {
        showLogin();
    }
})();
