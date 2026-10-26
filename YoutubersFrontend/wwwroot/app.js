// Lithuanian alphabet order, numbers compared by value
const nameCollator =
    new Intl.Collator("lt", { sensitivity: "base", numeric: true });

const state = {
    channels: [],
    search: "",
    selectedTags: new Set(),
    loaded: false
};

// ---------- helpers ----------

function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
}

// case and diacritics insensitive, so "zaidimai" finds "žaidimai"
function normalize(value) {
    return String(value ?? "")
        .normalize("NFD")
        .replace(/\p{Diacritic}/gu, "")
        .toLowerCase();
}

// Lithuanian noun form for a number: 1 kanalas, 2 kanalai, 10 kanalų
function pluralLt(count, one, few, many) {
    const mod10 = count % 10;
    const mod100 = count % 100;

    if (mod10 === 1 && mod100 !== 11)
        return one;

    if (mod10 >= 2 && (mod100 < 11 || mod100 > 19))
        return few;

    return many;
}

// ---------- data ----------

async function loadChannels() {

    let data;

    try {
        const response =
            await fetch("/api/channels");

        data =
            await response.json();
    } catch {
        // backend not reachable yet, try again shortly
        setTimeout(loadChannels, 3000);
        return;
    }

    state.loaded = true;

    document.getElementById("summary").textContent =
        `${data.totalChannels} ${pluralLt(data.totalChannels, "kanalas", "kanalai", "kanalų")}`;

    renderProgress(data);

    const channels = [...data.channels]
        .sort((a, b) => nameCollator.compare(a.name, b.name));

    // skip re-rendering when nothing changed between polls
    const changed =
        channels.length !== state.channels.length
        || channels.some((channel, i) => channel.channelId !== state.channels[i].channelId);

    if (changed) {
        state.channels = channels;
        renderTags();
        renderChannels();
    } else if (state.channels.length === 0) {
        renderChannels();
    }

    const neverRefreshed =
        new Date(data.lastRefresh).getUTCFullYear() < 2000;

    // keep pulling channels scraped so far until the refresh is done
    if (data.isRefreshing || neverRefreshed)
        setTimeout(loadChannels, 3000);
}

// ---------- rendering ----------

function renderProgress(data) {

    const progress =
        document.getElementById("progress");

    if (!data.isRefreshing) {
        progress.innerHTML = "";
        return;
    }

    const { scraped, total } = data.progress;

    const percent =
        total > 0 ? Math.round(scraped / total * 100) : 0;

    progress.innerHTML = `
        <div class="container pb-3">
            <div class="d-flex justify-content-between small text-body-secondary mb-1">
                <span>Renkami kanalų duomenys...</span>
                <span>Surinkta ${scraped} / ${total}</span>
            </div>
            <div class="progress" role="progressbar" aria-valuenow="${percent}" aria-valuemin="0" aria-valuemax="100">
                <div class="progress-bar progress-bar-striped progress-bar-animated"></div>
            </div>
        </div>
    `;

    // set from script, the content security policy blocks inline style attributes
    progress.querySelector(".progress-bar").style.width = `${percent}%`;
}

function renderTags() {

    const counts = new Map();

    for (const channel of state.channels)
        for (const tag of channel.tags)
            counts.set(tag, (counts.get(tag) ?? 0) + 1);

    // selected tags stay visible even if no channel has them right now
    for (const tag of state.selectedTags)
        if (!counts.has(tag))
            counts.set(tag, 0);

    const tags = [...counts.entries()]
        .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0], "lt"));

    document.getElementById("tags").innerHTML = tags
        .map(([tag, count]) => {
            const active = state.selectedTags.has(tag);

            return `
                <button type="button"
                        class="btn tag-chip ${active ? "active" : ""}"
                        data-tag="${escapeHtml(tag)}"
                        aria-pressed="${active}">
                    ${escapeHtml(tag)}<span class="count">${count}</span>
                </button>
            `;
        })
        .join("");

    document.getElementById("clearFilters").classList.toggle(
        "d-none", state.selectedTags.size === 0 && state.search === "");
}

function filteredChannels() {

    const search = normalize(state.search.trim());

    return state.channels.filter(channel =>
        // a channel must have every selected category
        [...state.selectedTags].every(tag => channel.tags.includes(tag))
        && (search === "" || normalize(channel.name).includes(search)));
}

function renderChannels() {

    const channels = filteredChannels();

    let html = "";

    for (const channel of channels) {

        const url =
            `https://www.youtube.com/channel/${encodeURIComponent(channel.channelId)}`;

        const tags = channel.tags
            .map(tag => `
                <button type="button"
                        class="channel-tag ${state.selectedTags.has(tag) ? "active" : ""}"
                        data-tag="${escapeHtml(tag)}">${escapeHtml(tag)}</button>
            `)
            .join("");

        html += `
        <div class="col">
            <div class="card channel-card">
                <div class="card-body d-flex gap-3">
                    <a href="${url}" target="_blank" rel="noopener">
                        <img src="${escapeHtml(channel.iconUrl)}"
                             alt=""
                             loading="lazy"
                             referrerpolicy="no-referrer"
                             class="channel-avatar">
                    </a>

                    <div class="min-w-0 flex-grow-1">
                        <a href="${url}" target="_blank" rel="noopener"
                           class="channel-name mb-2">${escapeHtml(channel.name)}</a>
                        <div class="d-flex flex-wrap gap-1">${tags}</div>
                    </div>
                </div>
            </div>
        </div>
    `;
    }

    document.getElementById("channels").innerHTML = html;

    const filtering =
        state.selectedTags.size > 0 || state.search.trim() !== "";

    document.getElementById("resultInfo").textContent =
        filtering
            ? `Rodomi kanalai: ${channels.length} iš ${state.channels.length}`
            : "";

    const empty =
        document.getElementById("empty");

    if (channels.length === 0) {
        empty.textContent =
            state.channels.length === 0
                ? (state.loaded ? "Kanalų dar nėra – jie atsiras čia, kai tik bus surinkti." : "Įkeliami kanalai...")
                : "Pagal paiešką kanalų nerasta.";

        empty.classList.remove("d-none");
    } else {
        empty.classList.add("d-none");
    }
}

// ---------- filters ----------

function toggleTag(tag) {

    if (state.selectedTags.has(tag))
        state.selectedTags.delete(tag);
    else
        state.selectedTags.add(tag);

    renderTags();
    renderChannels();
}

// tag chips in the filter bar and on cards both toggle the filter
document.addEventListener("click", event => {
    const button = event.target.closest("[data-tag]");

    if (button)
        toggleTag(button.dataset.tag);
});

document.getElementById("search").addEventListener("input", event => {
    state.search = event.target.value;
    renderTags();
    renderChannels();
});

document.getElementById("clearFilters").addEventListener("click", () => {
    state.selectedTags.clear();
    state.search = "";
    document.getElementById("search").value = "";
    renderTags();
    renderChannels();
});

renderChannels();
loadChannels();
