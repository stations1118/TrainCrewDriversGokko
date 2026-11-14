// ダイヤグラムの◆の窓と、出発順序・走行順の編集（管理画面 admin.html と、ダイヤグラムの設定画面 diasettings.html で共通）。
// 使い方: GokkoDia.openPopup({ box, train, marker, diagram, act, onClose }) / GokkoDia.orderEditor({ el, act })
//         GokkoDia.trainEditor({ el, act })（列車別設定）
(() => {
  const TYPE_COLORS = { "普通": "var(--text)", "準急": "#1e6fd9", "区急": "#18a33a", "急行": "#f08a00", "快急": "#b04fd0", "特急": "#d62828", "回送": "#888" };

  function esc(s) {
    return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  }

  function hhmm(ms, sec) {
    const d = new Date(ms);
    const p = (n) => String(n).padStart(2, "0");
    return `${p(d.getHours())}:${p(d.getMinutes())}${sec ? ":" + p(d.getSeconds()) : ""}`;
  }

  const eta = (t) => (t ? hhmm(new Date(t).getTime(), true) : "--");

  // ---- 出発順序（駅）・走行順（単線区間）の表。seq は列番の並び（▲▼で入れ替える） ----
  function stationOrderHtml(ord, seq) {
    const info = Object.fromEntries(ord.trains.map((t) => [t.train, t]));
    const rows = seq.map((tr, i) => {
      const t = info[tr] || {};
      const trackSel = t.changeable
        ? `<select data-track-train="${esc(tr)}" data-track-station="${esc(ord.station)}">
             ${t.options.map((o) => `<option value="${o}" ${o === t.track ? "selected" : ""}>${o}番線</option>`).join("")}
             <option value="">所定に戻す</option></select>`
        : `${t.track == null ? "通過" : t.track + "番線"}`;
      return `<tr>
        <td>${i + 1}</td>
        <td style="color:${TYPE_COLORS[t.type] || "inherit"}"><b>${esc(tr)}</b> ${esc(t.type || "")}</td>
        <td class="muted">${esc(t.direction || "")}・${esc(t.stop || "")}</td>
        <td>${trackSel}</td>
        <td class="muted">${eta(t.eta)}</td>
        <td style="white-space:nowrap" title="発車時刻を手で決める（この30秒前まで出発を開通させません）">${t.departable || t.departAt
          ? `発 <input type="time" step="1" data-depart-train="${esc(tr)}" data-depart-station="${esc(ord.station)}" value="${t.departAt ? hhmm(Date.parse(t.departAt), true) : ""}" style="width:7.5em">${
              t.departAt ? `<button data-depart-clear="${esc(tr)}" data-depart-station="${esc(ord.station)}" title="発車時刻の指定を外す">×</button>` : ""}` : ""}</td>
        <td style="white-space:nowrap"><button data-up="${i}" ${i === 0 ? "disabled" : ""}>▲</button><button data-down="${i}" ${i === seq.length - 1 ? "disabled" : ""}>▼</button></td>
      </tr>${t.warning ? `<tr><td></td><td colspan="5" style="color:#e07000; font-size:12px">⚠ ${esc(t.warning)}</td></tr>` : ""}`;
    }).join("");
    return `<div style="margin:4px 0"><b>${esc(ord.name)}の出発順序</b>
        <span class="muted">（${ord.fixedOrder ? "指定中" : "自動・見込み順"}${ord.loopTrack ? "、待避線 " + esc(ord.loopTrack) : ""}）</span></div>
      ${seq.length === 0 ? `<div class="muted">この駅を通る列車がいません</div>` : `<table style="font-size:12px">${rows}</table>`}
      <div class="row" style="margin:6px 0 0">
        <button data-order="set" class="primary" ${seq.length < 2 ? "disabled" : ""}>この順で決定</button>
        <button data-order="clear" ${ord.fixedOrder ? "" : "disabled"}>自動に戻す</button></div>`;
  }

  function timeToIso(v) {
    const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(v || "");
    if (!m) return null;
    const d = new Date();
    d.setHours(Number(m[1]), Number(m[2]), Number(m[3] || 0), 0);
    const now = Date.now();
    if (d.getTime() < now - 12 * 3600000) d.setDate(d.getDate() + 1);
    else if (d.getTime() > now + 12 * 3600000) d.setDate(d.getDate() - 1);
    return d.toISOString();
  }

  function sectionOrderHtml(sec, seq) {
    const info = Object.fromEntries(sec.trains.map((t) => [t.train, t]));
    const rows = seq.map((tr, i) => {
      const t = info[tr] || {};
      return `<tr>
        <td>${i + 1}</td>
        <td style="color:${TYPE_COLORS[t.type] || "inherit"}"><b>${esc(tr)}</b> ${esc(t.type || "")}</td>
        <td class="muted">${esc(t.direction || "")}・${esc(t.from || "")}から</td>
        <td class="muted">${eta(t.eta)}</td>
        <td style="white-space:nowrap"><button data-sup="${i}" ${i === 0 ? "disabled" : ""}>▲</button><button data-sdown="${i}" ${i === seq.length - 1 ? "disabled" : ""}>▼</button></td>
      </tr>`;
    }).join("");
    return `<div style="margin:4px 0"><b>${esc(sec.name)}（単線）の走行順</b>
        <span class="muted">（${sec.fixedOrder ? "指定中" : "自動・見込み順"}）</span></div>
      ${seq.length === 0 ? `<div class="muted">この区間を通る列車がいません</div>` : `<table style="font-size:12px">${rows}</table>`}
      <div class="row" style="margin:6px 0 0">
        <button data-sorder="set" class="primary" ${seq.length < 2 ? "disabled" : ""}>この順で決定</button>
        <button data-sorder="clear" ${sec.fixedOrder ? "" : "disabled"}>自動に戻す</button></div>`;
  }

  const swap = (arr, i, j) => { [arr[i], arr[j]] = [arr[j], arr[i]]; };

  // その向き（"上り" / "下り"）の列車だけにした駅の出発順序（出発順序は上下別に持つ）
  const dirOrder = (o, d) => o && { ...o, name: `${o.name} ${d}`, direction: d,
    fixedOrder: d === "上り" ? o.fixedUp : o.fixedDown, trains: o.trains.filter((t) => t.direction === d) };

  // 順序の表のボタン・番線の選択。処理したら true
  async function handleOrderEvent(ev, ctx) {
    const btn = ev.target.closest("button");
    if (btn) {
      if (btn.dataset.up || btn.dataset.down) {
        const i = Number(btn.dataset.up ?? btn.dataset.down);
        swap(ctx.seq, i, btn.dataset.up ? i - 1 : i + 1);
        ctx.redraw();
        return true;
      }
      if (btn.dataset.sup || btn.dataset.sdown) {
        const i = Number(btn.dataset.sup ?? btn.dataset.sdown);
        swap(ctx.sseq, i, btn.dataset.sup ? i - 1 : i + 1);
        ctx.redraw();
        return true;
      }
      if (btn.dataset.order) {
        ctx.done();
        await ctx.act("POST", "/route/order", { station: ctx.ord.station, direction: ctx.ord.direction, trains: btn.dataset.order === "set" ? ctx.seq : null });
        return true;
      }
      if (btn.dataset.sorder) {
        ctx.done();
        await ctx.act("POST", "/route/section-order", { a: ctx.sec.a, b: ctx.sec.b, trains: btn.dataset.sorder === "set" ? ctx.sseq : null });
        return true;
      }
    }
    return false;
  }

  async function handleTrackChange(ev, ctx) {
    const s = ev.target.closest("select[data-track-train]");
    if (!s) return false;
    ctx.done();
    await ctx.act("POST", "/route/track", { train: s.dataset.trackTrain, station: s.dataset.trackStation, track: s.value === "" ? null : Number(s.value) });
    return true;
  }

  // ---- ◆の窓 ----
  let popup = null;

  function closePopup() {
    if (popup) { popup.remove(); popup = null; }
  }

  /**
   * ◆の窓を開く。
   * box: 図の入れ物（position:relative）、anchor: クリックした◆の要素、train: 列番、marker: DiagramView の markers の1つ、
   * diagram: /diagram の結果、act(method, path, body): 実行して描き直す関数、onClose: 閉じたとき
   */
  function openPopup({ box, anchor, train, marker: m, diagram, act, onClose }) {
    closePopup();
    const r = anchor.getBoundingClientRect(), b = box.getBoundingClientRect();
    const dir = ((diagram.trains || []).find((t) => t.train === train) || {}).direction;
    const all = (diagram.orders || []).find((o) => o.station === m.stationId);
    const ord = dir ? dirOrder(all, dir) : all; // 選んだ列車の向きの出発順序だけ
    const sec = m.singleNext ? (diagram.sections || []).find((s) => (s.a === m.stationId && s.b === m.singleNext) || (s.b === m.stationId && s.a === m.singleNext)) : null;
    const ctx = {
      ord, sec, act,
      seq: ord ? ord.trains.map((t) => t.train) : [],
      sseq: sec ? sec.trains.map((t) => t.train) : [],
      redraw: () => draw(),
      done: () => { closePopup(); }
    };
    const div = document.createElement("div");
    div.dataset.gokkoPopup = "1";
    const width = 420;
    div.style.cssText = `position:absolute; left:${Math.max(0, Math.min(r.right - b.left + 6, b.width - width))}px; top:${Math.max(0, r.top - b.top - 10)}px;
      background:var(--panel); border:1px solid var(--line); border-radius:6px; padding:8px; z-index:5; box-shadow:0 2px 8px rgba(0,0,0,.2); width:${width}px; max-width:100%`;
    const sep = `style="margin:8px 0 0; border-top:1px solid var(--line); padding-top:6px"`;
    const draw = () => {
      div.innerHTML = `<div style="margin-bottom:6px"><b>${esc(train)}</b> ${esc(m.name)}
          <span class="muted">${m.track == null ? "通過" : m.track + "番線"}（${esc(m.trackSource)}）</span>
          <div class="muted" style="font-size:12px">${esc(m.status)}</div></div>
        ${m.changeable ? `<div class="row" style="margin:0"><span class="muted">番線</span>${m.options.map((o) =>
          `<button data-pick="${o}" class="${o === m.track ? "primary" : ""}">${o}番線</button>`).join("")}
          <button data-pick="">所定に戻す</button></div>` : ""}
        ${m.manual && m.manual.length ? `<div ${sep}><div class="muted" style="font-size:12px">駅扱い（連動に問題がなければ、抑止・待ちを無視して開通）</div>
          <div class="row" style="margin:4px 0 0">${m.manual.map((x) =>
            `<button data-manual="${esc(x.action)}" data-label="${esc(x.label)}">${esc(x.label)}</button>`).join("")}</div></div>` : ""}
        <div class="row" ${sep}>
          <button data-hold="arrive" class="${m.holdArrive ? "danger" : ""}" title="場内信号を出させない。開通済みでも、列車がまだ接近していなければ取り消す">${m.holdArrive ? "場内抑止中（解除）" : "場内抑止"}</button>
          <button data-hold="depart" class="${m.holdDepart ? "danger" : ""}" title="出発信号を出させない。開通済みでも、列車がまだ接近していなければ取り消す">${m.holdDepart ? "出発抑止中（解除）" : "出発抑止"}</button>
          <button class="primary" data-act="force" title="抑止を外し、安全を確認でき次第、自動で開通させる">自動開通</button>
          ${m.stopOverride === "打ち切り"
            ? `<button data-act="uncut" class="danger" title="打ち切りを外して、この先の駅も扱う">打ち切り中（解除）</button>`
            : m.terminal ? "" : `<button data-act="cut" title="この駅で運転を打ち切る（サーバーの扱いだけ。この先の駅の進路は取らない）">打ち切り</button>`}
          <button data-close="1">閉じる</button></div>
        ${ord ? `<div ${sep}>${stationOrderHtml(ord, ctx.seq)}</div>` : ""}
        ${sec ? `<div ${sep}>${sectionOrderHtml(sec, ctx.sseq)}</div>` : ""}`;
    };
    draw();
    div.addEventListener("change", async (ev) => {
      if (await handleTrackChange(ev, ctx)) onClose && onClose();
    });
    div.addEventListener("click", async (ev) => {
      ev.stopPropagation(); // 描き直しでボタンが入れ替わると、図のクリック（窓の外）と見分けられなくなるため
      const btn = ev.target.closest("button");
      if (!btn) return;
      if (btn.dataset.close) { closePopup(); onClose && onClose(); return; }
      if (await handleOrderEvent(ev, ctx)) { if (!popup) onClose && onClose(); return; }
      const body = { train, station: m.stationId };
      closePopup();
      if (btn.dataset.act === "force") await act("POST", "/route/force", body);
      else if (btn.dataset.act === "cut" || btn.dataset.act === "uncut") await act("POST", "/route/stop", { ...body, kind: btn.dataset.act === "cut" ? "打ち切り" : null });
      else if (btn.dataset.hold) {
        const on = btn.dataset.hold === "arrive" ? m.holdArrive : m.holdDepart;
        await act("POST", "/route/hold", { ...body, hold: !on, kind: btn.dataset.hold });
      } else if (btn.dataset.manual) await act("POST", "/route/manual", { ...body, action: btn.dataset.manual, label: btn.dataset.label });
      else if (btn.dataset.pick !== undefined) await act("POST", "/route/track", { ...body, track: btn.dataset.pick === "" ? null : Number(btn.dataset.pick) });
      onClose && onClose();
    });
    box.appendChild(div);
    popup = div;
    return div;
  }

  // ---- 順序の設定画面 ----
  // タブ「出発順序（上下別）」: 上り・下りを左右に並べ、それぞれ駅を選んで、その向きの列車だけを並べ替える。
  // タブ「単線区間の走行順（上下とも）」: 区間を選んで、上下の列車をまとめて並べ替える。並べ替えている間は描き直さない
  function orderEditor({ el, act }) {
    const DIRS = ["上り", "下り"];
    let diagram = null;
    let tab = "dep";
    try { tab = localStorage.getItem("gokkoOrderTab") || "dep"; } catch (e) {}
    const pick = { 上り: "", 下り: "", sec: "" };
    const seq = { 上り: [], 下り: [], sec: [] };
    const dirty = { 上り: false, 下り: false, sec: false };

    const station = (id) => (diagram && diagram.orders || []).find((o) => o.station === id) || null;
    const section = (k) => (diagram && diagram.sections || []).find((s) => `${s.a}|${s.b}` === k) || null;

    function load(col) {
      if (col === "sec") { const s = section(pick.sec); seq.sec = s ? s.trains.map((t) => t.train) : []; return; }
      const o = dirOrder(station(pick[col]), col);
      seq[col] = o ? o.trains.map((t) => t.train) : [];
    }

    function colHtml(d) {
      const opts = (diagram.orders || []).map((o) => {
        const n = o.trains.filter((t) => t.direction === d).length;
        const fixed = d === "上り" ? o.fixedUp : o.fixedDown;
        return `<option value="${esc(o.station)}" ${pick[d] === o.station ? "selected" : ""}>${esc(o.name)}（${n}本${fixed ? "・指定中" : ""}）</option>`;
      }).join("");
      const o = dirOrder(station(pick[d]), d);
      return `<div data-col="${d}" style="min-width:0">
          <div class="row"><b>${d}</b><select data-pick-col="${d}"><option value="">駅を選ぶ</option>${opts}</select>
            ${dirty[d] ? `<span class="muted">並べ替え中</span>` : ""}</div>
          ${o ? stationOrderHtml(o, seq[d]) : ""}</div>`;
    }

    function secHtml() {
      const opts = (diagram.sections || []).map((s) => `<option value="${esc(s.a)}|${esc(s.b)}" ${pick.sec === `${s.a}|${s.b}` ? "selected" : ""}>${esc(s.name)}（${s.trains.length}本${s.fixedOrder ? "・指定中" : ""}）</option>`).join("");
      const s = section(pick.sec);
      return `<div data-col="sec">
          <div class="row"><select data-pick-col="sec"><option value="">単線区間を選ぶ</option>${opts}</select>
            ${dirty.sec ? `<span class="muted">並べ替え中</span>` : ""}</div>
          ${s ? sectionOrderHtml(s, seq.sec) : ""}</div>`;
    }

    function draw() {
      if (!diagram) return;
      const tabBtn = (id, label) => `<button data-tab="${id}" class="${tab === id ? "primary" : ""}">${label}</button>`;
      el.innerHTML = `<div class="row">${tabBtn("dep", "出発順序（上下別）")}${tabBtn("sec", "単線区間の走行順（上下とも）")}</div>
        ${tab === "dep"
          ? `<div style="display:grid; gap:16px; grid-template-columns:repeat(auto-fit, minmax(320px, 1fr))">${DIRS.map(colHtml).join("")}</div>`
          : secHtml()}`;
    }

    el.addEventListener("change", async (ev) => {
      const s = ev.target.closest("select[data-pick-col]");
      if (s) { const c = s.dataset.pickCol; pick[c] = s.value; dirty[c] = false; load(c); draw(); return; }
      const inp = ev.target.closest("input[data-depart-train]");
      if (inp) {
        const time = timeToIso(inp.value);
        if (inp.value && !time) return;
        await act("POST", "/route/open-at", { train: inp.dataset.departTrain, station: inp.dataset.departStation, time });
        inp.blur();
        return;
      }
      const t = ev.target.closest("select[data-track-train]");
      if (t) {
        const c = t.closest("[data-col]")?.dataset.col;
        if (c) dirty[c] = false;
        await act("POST", "/route/track", { train: t.dataset.trackTrain, station: t.dataset.trackStation, track: t.value === "" ? null : Number(t.value) });
      }
    });

    el.addEventListener("click", async (ev) => {
      const btn = ev.target.closest("button");
      if (!btn) return;
      if (btn.dataset.tab) {
        tab = btn.dataset.tab;
        try { localStorage.setItem("gokkoOrderTab", tab); } catch (e) {}
        draw();
        return;
      }
      if (btn.dataset.departClear) { await act("POST", "/route/open-at", { train: btn.dataset.departClear, station: btn.dataset.departStation, time: null }); return; }
      const c = btn.closest("[data-col]")?.dataset.col;
      if (!c) return;
      const up = btn.dataset.up ?? btn.dataset.sup, down = btn.dataset.down ?? btn.dataset.sdown;
      if (up !== undefined || down !== undefined) {
        const i = Number(up ?? down);
        swap(seq[c], i, up !== undefined ? i - 1 : i + 1);
        dirty[c] = true;
        draw();
        return;
      }
      if (btn.dataset.sorder && c === "sec") {
        const s = section(pick.sec);
        dirty.sec = false;
        if (s) await act("POST", "/route/section-order", { a: s.a, b: s.b, trains: btn.dataset.sorder === "set" ? seq.sec : null });
        return;
      }
      if (btn.dataset.order && c !== "sec") {
        const o = station(pick[c]);
        dirty[c] = false;
        if (!o) return;
        // その向きだけを決める・自動に戻す（反対向きの順序はそのまま）
        await act("POST", "/route/order", { station: o.station, direction: c, trains: btn.dataset.order === "set" ? [...seq[c]] : null });
      }
    });

    return {
      update(d) {
        diagram = d;
        if (el.contains(document.activeElement) && ["SELECT", "INPUT"].includes(document.activeElement.tagName)) return;
        for (const c of [...DIRS, "sec"]) if (!dirty[c]) load(c);
        // 見込み時刻・状態は毎秒変わるので、比べるときは除く（押している途中のボタンが消えないように）
        const key = JSON.stringify([tab, pick, dirty, DIRS.map((x) => station(pick[x])), section(pick.sec)],
          (k, v) => (k === "eta" || k === "status" ? undefined : v));
        if (el.dataset.key === key) return;
        el.dataset.key = key;
        draw();
      }
    };
  }

  // ---- 列車別設定（列車を選んで、この先の駅ごとに番線・抑止・自動開通・駅扱いを操作する） ----
  function trainEditor({ el, act }) {
    let trains = [];
    let pick = "";
    let pressing = false; // ボタンを押している間は描き直さない（押している途中のボタンが消えないように）

    function table(t) {
      return `<div class="scroll" style="overflow-x:auto"><table>
        <thead style="white-space:nowrap"><tr><td class="muted">駅</td><td class="muted">停車</td><td class="muted">番線</td><td class="muted">決め方</td><td class="muted">状態</td><td></td></tr></thead>
        <tbody>${t.stations.map((s) => {
          const st = esc(s.stationId);
          const opts = [`<option value="">（指定なし）</option>`].concat(s.options.map((o) =>
            `<option value="${o}" ${s.track === o && s.source === "集中" ? "selected" : ""}>${o}番線</option>`)).join("");
          const manual = s.manual.length > 0
            ? `<select data-manual-station="${st}"><option value="">駅扱い…</option>${s.manual.map((x) =>
                `<option value="${esc(x.action)}">${esc(x.label)}</option>`).join("")}</select>` : "";
          return `<tr>
            <td>${esc(s.name)}</td>
            <td style="white-space:nowrap">${esc(s.stopType)} <select data-stop-station="${st}" title="臨時停車・臨時通過・打ち切り（サーバーの扱いだけ。ゲームの停車駅は変わりません）">${
              [["", "所定"], ["停車", "臨時停車"], ["通過", "臨時通過"], ["打ち切り", "打ち切り"]].map(([v, l]) =>
                `<option value="${v}" ${(s.stopOverride || "") === v ? "selected" : ""}>${l}</option>`).join("")}</select></td>
            <td>${s.track ?? "-"} ${s.options.length > 0 ? `<select data-track-station="${st}">${opts}</select>` : ""}</td>
            <td class="muted">${esc(s.source)}${s.locked ? "・確定" : ""}</td>
            <td>${esc(s.status)}</td>
            <td style="white-space:nowrap">
              <button data-hold="arrive" data-station="${st}" data-on="${s.holdArrive ? 1 : 0}" class="${s.holdArrive ? "danger" : ""}">${s.holdArrive ? "場内抑止中" : "場内抑止"}</button>
              <button data-hold="depart" data-station="${st}" data-on="${s.holdDepart ? 1 : 0}" class="${s.holdDepart ? "danger" : ""}">${s.holdDepart ? "出発抑止中" : "出発抑止"}</button>
              <button class="primary" data-force="${st}">自動開通</button>
              ${manual}
            </td></tr>`;
        }).join("")}</tbody></table></div>`;
    }

    function draw() {
      const t = trains.find((x) => x.train === pick);
      const opts = `<option value="">列車を選ぶ</option>` + trains.map((x) =>
        `<option value="${esc(x.train)}" ${x.train === pick ? "selected" : ""}>${esc(x.train)}（${esc(x.type)}・${esc(x.direction)}・${esc(x.position)}）</option>`).join("");
      el.innerHTML = `<div class="row"><select data-pick-train>${opts}</select>
          ${trains.length === 0 ? `<span class="muted">在線共有モードで運転中の列車はいません</span>` : ""}</div>${t ? table(t) : ""}`;
    }

    el.addEventListener("pointerdown", () => { pressing = true; });
    window.addEventListener("pointerup", () => { setTimeout(() => { pressing = false; }, 0); });
    el.addEventListener("change", async (ev) => {
      const s = ev.target.closest("select");
      if (!s) return;
      if (s.dataset.pickTrain !== undefined) { pick = s.value; el.dataset.key = ""; draw(); return; }
      if (!pick) return;
      if (s.dataset.manualStation && s.value) {
        await act("POST", "/route/manual", { train: pick, station: s.dataset.manualStation, action: s.value, label: s.options[s.selectedIndex].text });
      } else if (s.dataset.stopStation) {
        await act("POST", "/route/stop", { train: pick, station: s.dataset.stopStation, kind: s.value || null });
      } else if (s.dataset.trackStation) {
        await act("POST", "/route/track", { train: pick, station: s.dataset.trackStation, track: s.value === "" ? null : Number(s.value) });
      }
      s.blur();
    });
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button");
      if (!b || !pick) return;
      if (b.dataset.hold) await act("POST", "/route/hold", { train: pick, station: b.dataset.station, hold: b.dataset.on !== "1", kind: b.dataset.hold });
      else if (b.dataset.force) await act("POST", "/route/force", { train: pick, station: b.dataset.force });
    });

    return {
      update(list) {
        trains = list || [];
        if (pick && !trains.some((x) => x.train === pick)) pick = "";
        if (pressing || el.contains(document.activeElement) && document.activeElement.tagName === "SELECT") return;
        const key = JSON.stringify([pick, trains.map((x) => [x.train, x.position]), trains.find((x) => x.train === pick)]);
        if (el.dataset.key === key) return;
        el.dataset.key = key;
        draw();
      }
    };
  }

  // ---- ダイヤグラム（管理画面とダイヤグラムの設定画面で共通） ----
  // 実績（実線）と見込み（点線）。◆は進路を扱う駅を通過する（停車駅は着く）予想時刻。
  // ◆を押すと窓（番線・駅扱い・抑止・順序）。◆を横にずらすと、その駅の発車時刻を決める（スジずらし。ダブルクリックで解除）。
  // 上の「全部・上りのみ・下りのみ」で、表示する列車を絞る。
  // 「詳細表示」: 進路を扱う駅を番線ごとの段に分け、列車はその駅で使う番線の段に引く。同じ番線に同じ時間帯に2本いれば、赤く示す（番線の競合）。
  // 1分の幅・駅と駅の間隔・（詳細表示では）番線の段の高さ・表示する時間の範囲を変えられ、拡大・縮小もできる（Ctrl+ホイールでも）。
  // 設定は通常表示と詳細表示で別々に、見る人ごとにブラウザに残す。通常表示を「元に戻す」にしているときは、これまでどおり画面の幅に合わせる
  function diagramView({ box, tip, filter, act }) {
    const RIGHT = 12, TOP = 18, BOTTOM = 24;
    const DEFAULTS = {
      // 通常表示（これまでの図: 幅 1000 に過去10分・先30分。駅の間隔は 17）
      n: { minPx: (1000 - 92 - RIGHT) / 40, stationPx: 17, trackPx: 14, pastMin: 10, futureMin: 30 },
      d: { minPx: 22, stationPx: 34, trackPx: 14, pastMin: 10, futureMin: 30 }
    };
    const LIMITS = { minPx: [4, 120], stationPx: [8, 200], trackPx: [8, 60], pastMin: [0, 120], futureMin: [5, 240] };
    let view = { split: false, n: { ...DEFAULTS.n }, d: { ...DEFAULTS.d } };
    try {
      const saved = JSON.parse(localStorage.getItem("gokkoDiagramView2") || "{}");
      view = { split: !!saved.split, n: { ...DEFAULTS.n, ...saved.n }, d: { ...DEFAULTS.d, ...saved.d } };
    } catch (e) {}
    const cur = () => (view.split ? view.d : view.n);
    const isDefault = (v, def) => Object.keys(def).every((k) => v[k] === def[k]);
    let diagram = null;
    let dragging = null;
    let shown = "全部";
    try { shown = localStorage.getItem("gokkoDiagramFilter") || "全部"; } catch (e) {}
    box.style.overflowX = "auto";

    const clamp = (k, v) => Math.round(Math.max(LIMITS[k][0], Math.min(LIMITS[k][1], v)));
    function save() { try { localStorage.setItem("gokkoDiagramView2", JSON.stringify(view)); } catch (e) {} }
    function redraw() { save(); drawFilter(); box.dataset.html = ""; render(diagram); }

    const step = (k, label, unit) => `<span class="dg-set" title="${label}">${label}
        <button data-dg-step="${k}" data-dg-dir="-1">−</button><b>${Math.round(cur()[k])}</b>${unit}<button data-dg-step="${k}" data-dg-dir="1">＋</button></span>`;
    function drawFilter() {
      if (!filter) return;
      filter.innerHTML = ["全部", "上りのみ", "下りのみ"].map((f) =>
        `<button data-filter="${f}" class="${f === shown ? "primary" : ""}">${f}</button>`).join(" ")
        + `<button data-dg-detail="1" class="${view.split ? "primary" : ""}" style="margin-left:12px" title="番線ごとの段に分け、番線の競合を見る">詳細表示</button>
        <button data-dg-zoom="1.25" title="1分の幅・駅の間隔${view.split ? "・番線の段" : ""}をまとめて大きく（Ctrl+ホイールでも）">拡大</button>
        <button data-dg-zoom="0.8" title="まとめて小さく">縮小</button>
        ${step("minPx", "1分の幅", "px")} ${step("stationPx", "駅の間隔", "px")}
        ${view.split ? step("trackPx", "番線の段", "px") : ""}
        ${step("pastMin", "過去", "分")} ${step("futureMin", "先", "分")}
        <button data-dg-reset="1" title="表示の設定を最初に戻す">元に戻す</button>`;
    }
    if (filter) {
      filter.addEventListener("click", (ev) => {
        const b = ev.target.closest("button");
        if (!b) return;
        const v = cur();
        if (b.dataset.filter) {
          shown = b.dataset.filter;
          try { localStorage.setItem("gokkoDiagramFilter", shown); } catch (e) {}
        } else if (b.dataset.dgStep) {
          const k = b.dataset.dgStep, dir = Number(b.dataset.dgDir);
          const inc = k === "pastMin" || k === "futureMin" ? 5 : Math.max(1, Math.round(v[k] * 0.15));
          v[k] = clamp(k, v[k] + dir * inc);
        } else if (b.dataset.dgZoom) zoom(Number(b.dataset.dgZoom));
        else if (b.dataset.dgReset) view[view.split ? "d" : "n"] = { ...DEFAULTS[view.split ? "d" : "n"] };
        else if (b.dataset.dgDetail) view.split = !view.split;
        else return;
        redraw();
      });
    }
    function zoom(f) {
      const v = cur();
      for (const k of ["minPx", "stationPx", "trackPx"]) v[k] = clamp(k, v[k] * f);
    }
    box.addEventListener("wheel", (ev) => {
      if (!ev.ctrlKey) return;
      ev.preventDefault();
      zoom(ev.deltaY < 0 ? 1.1 : 1 / 1.1);
      redraw();
    }, { passive: false });
    drawFilter();

    // 縦の並び。駅ごとに、名前の線（y）と、番線の段（rows: 番線 → y）、上端・下端（駅間の線をつなぐところ）
    function layout(d) {
      const stations = [...d.stations].sort((a, b) => a.y - b.y);
      const c = cur();
      const left = view.split ? 118 : 92;
      const rowsOf = (s) => (view.split && s.control && s.tracks && s.tracks.length > 0 ? s.tracks : []);
      const band = {};
      let y = TOP;
      for (const s of stations) {
        const tracks = rowsOf(s);
        const rows = {};
        tracks.forEach((t, i) => { rows[t] = y + 10 + (i + 0.5) * c.trackPx; });
        const bottom = tracks.length ? y + 10 + (tracks.length - 0.5) * c.trackPx : y;
        band[s.y] = { s, y, rows, top: tracks.length ? rows[tracks[0]] : y, bottom };
        y = (tracks.length ? y + 10 + tracks.length * c.trackPx : y) + c.stationPx;
      }
      const height = y - c.stationPx + BOTTOM;
      const zones = stations.map((s) => s.y);
      const now = new Date(d.now).getTime();
      const t0 = now - c.pastMin * 60000, t1 = now + c.futureMin * 60000;
      const w = left + (c.pastMin + c.futureMin) * c.minPx + RIGHT;
      // ゾーンと番線から縦の位置。駅ちょうどなら番線の段（番線が分からなければ駅の線）、駅間なら前後の駅の端をつなぐ
      function yOf(z, track) {
        const b = band[z];
        if (b) return track != null && b.rows[String(track)] != null ? b.rows[String(track)] : b.y;
        let i = zones.findIndex((x) => x > z);
        if (i <= 0) return band[zones[i < 0 ? zones.length - 1 : 0]].y;
        const a = band[zones[i - 1]], c = band[zones[i]];
        return a.bottom + (z - zones[i - 1]) / (zones[i] - zones[i - 1]) * (c.top - a.bottom);
      }
      return {
        stations, band, left, w, height, now, t0, t1, y: yOf, minPx: c.minPx, fit: !view.split && isDefault(view.n, DEFAULTS.n), // 通常表示を元のままにしているときは、画面の幅に合わせる
        x: (t) => left + (new Date(t).getTime() - t0) / 60000 * c.minPx,
        t: (x) => t0 + (x - left) / c.minPx * 60000
      };
    }

    const visible = (tr) => shown === "全部" || (shown === "上りのみ" ? tr.direction === "上り" : tr.direction === "下り");

    // 列車ごとの、駅にいる時間（着〜発。通過は一瞬）と、その駅の番線
    function occupancy(d) {
      const list = [];
      for (const tr of d.trains) {
        const tracks = trackMap(tr);
        const pts = [...tr.history, ...tr.forecast].map((p) => ({ t: new Date(p.t).getTime(), y: p.y, track: p.row ?? tracks[p.y] }));
        let cur = null;
        for (const p of pts) {
          const atStation = Number.isInteger(p.y) && p.y % 2 === 0;
          if (cur && cur.y === p.y && cur.track === p.track) { cur.end = Math.max(cur.end, p.t); cur.start = Math.min(cur.start, p.t); continue; }
          if (cur) list.push(cur);
          cur = atStation ? { train: tr.train, tr, y: p.y, track: p.track, start: p.t, end: p.t } : null;
        }
        if (cur) list.push(cur);
      }
      return list.filter((o) => o.track != null);
    }

    // 同じ駅・同じ番線に、時間の重なる2本（番線の競合）
    function conflicts(d) {
      const occ = occupancy(d);
      const out = [];
      for (let i = 0; i < occ.length; i++)
        for (let j = i + 1; j < occ.length; j++) {
          const a = occ[i], b = occ[j];
          if (a.train === b.train || a.y !== b.y || a.track !== b.track) continue;
          const start = Math.max(a.start, b.start), end = Math.min(a.end, b.end);
          if (start <= end) out.push({ y: a.y, track: a.track, trains: [a.train, b.train], start, end });
        }
      return out;
    }

    // 段の名前: 数字なら「N番線」、入換線などは「X線」
    const rowName = (t) => (/^\d+$/.test(String(t)) ? `${t}番線` : `${t}線`);

    function trackMap(tr) {
      const m = {};
      for (const x of tr.tracks || []) m[x.y] = String(x.track);
      for (const x of tr.markers) if (x.track != null) m[x.y] = String(x.track);
      return m;
    }

    function render(d) {
      if (!d) return;
      diagram = d;
      if (dragging || popup) return; // ずらしている間・◆の窓を開いている間は描き直さない
      const L = layout(d);
      const parts = [`<svg viewBox="0 0 ${L.w} ${L.height}" ${L.fit ? `width="100%"` : `width="${L.w}" height="${L.height}"`} style="display:block">`];
      for (const s of L.stations) {
        const b = L.band[s.y];
        const rowTracks = Object.keys(b.rows);
        parts.push(`<line x1="${L.left}" x2="${L.w - RIGHT}" y1="${b.y}" y2="${b.y}" stroke="var(--line)" stroke-width="${s.control ? 1.6 : 0.6}"/>`);
        parts.push(`<text x="${L.left - 6}" y="${b.y + 4}" text-anchor="end" font-size="11" fill="${s.control ? "var(--text)" : "var(--muted)"}" font-weight="${s.control ? 600 : 400}">${esc(s.name)}</text>`);
        for (const t of rowTracks) {
          const ry = b.rows[t];
          parts.push(`<line x1="${L.left}" x2="${L.w - RIGHT}" y1="${ry}" y2="${ry}" stroke="var(--line)" stroke-width="0.5" stroke-dasharray="2 3"/>`);
          parts.push(`<text x="${L.left - 6}" y="${ry + 3.5}" text-anchor="end" font-size="10" fill="var(--muted)">${esc(rowName(t))}</text>`);
        }
      }
      const every = L.minPx >= 40 ? 1 : L.minPx >= 10 ? 5 : 10; // 時刻を書く間隔（分）
      const first = Math.ceil(L.t0 / 60000) * 60000;
      for (let t = first; t <= L.t1; t += 60000) {
        const x = L.x(t), min = new Date(t).getMinutes(), five = min % 5 === 0;
        if (L.minPx < 8 && !five) continue;
        parts.push(`<line x1="${x}" x2="${x}" y1="${TOP - 6}" y2="${L.height - 18}" stroke="var(--line)" stroke-width="${five ? 1 : 0.4}"/>`);
        if (min % every === 0) parts.push(`<text x="${x}" y="${L.height - 4}" text-anchor="middle" font-size="10" fill="var(--muted)">${hhmm(t)}</text>`);
      }
      const nx = L.x(L.now);
      parts.push(`<line x1="${nx}" x2="${nx}" y1="${TOP - 10}" y2="${L.height - 18}" stroke="var(--danger)" stroke-width="1.2"/>`);

      // 番線の競合（番線ごとに分けたときは、その番線の段に赤い帯）
      const found = view.split ? conflicts(d).filter((c) => c.end >= L.t0) : [];
      for (const c of found) {
        if (c.start > L.t1) continue;
        const x1 = Math.max(L.left, L.x(c.start)), x2 = Math.min(L.w - RIGHT, L.x(c.end));
        const cy = L.y(c.y, c.track), h = view.split ? Math.max(6, view.trackPx - 2) : 10;
        parts.push(`<rect x="${Math.min(x1, x2 - 4)}" y="${cy - h / 2}" width="${Math.max(4, x2 - x1)}" height="${h}" fill="#e53935" opacity="0.35"><title>番線の競合</title></rect>`);
      }

      d.trains.forEach((tr, ti) => {
        if (!visible(tr)) return;
        const color = TYPE_COLORS[tr.type] || "var(--text)";
        const tracks = trackMap(tr);
        const pts = (arr) => arr.map((p) => `${L.x(p.t).toFixed(1)},${L.y(p.y, p.row ?? tracks[p.y]).toFixed(1)}`).join(" ");
        if (tr.history.length > 1) parts.push(`<polyline points="${pts(tr.history)}" fill="none" stroke="${color}" stroke-width="2"/>`);
        if (tr.forecast.length > 1) parts.push(`<polyline points="${pts(tr.forecast)}" fill="none" stroke="${color}" stroke-width="1.6" stroke-dasharray="5 4"/>`);
        const cur = tr.forecast[0];
        parts.push(`<text x="${L.x(cur.t) + 6}" y="${L.y(cur.y, cur.row ?? tracks[cur.y]) - 5}" font-size="11" font-weight="600" fill="${color}">${esc(tr.train)}</text>`);
        tr.markers.forEach((m, mi) => {
          const x = L.x(m.t), y = L.y(m.y, m.track);
          const fill = m.held ? "#c62828" : !m.editable ? "#999" : m.departAt ? "#e0a800" : "#3aa0d8";
          const trackText = m.track == null ? "通" : String(m.track);
          const trackColor = m.trackSource === "集中" || m.trackSource === "駅扱い" ? "#e07000" : m.trackSource === "待避" ? "#b04fd0" : color;
          const dep = m.departAt ? `・発車時刻 ${hhmm(new Date(m.departAt).getTime(), true)}` : "";
          const tipText = `${tr.train} ${m.name}: ${m.track == null ? "通過" : m.track + "番線"}（${m.trackSource}）・${m.source} ${hhmm(new Date(m.t).getTime(), true)}${dep}（${m.status}）`
            + "　押すと番線・駅扱い・抑止・順序" + (m.draggable ? "　横にずらすと発車時刻（ダブルクリックで解除）" : "");
          if (!view.split) parts.push(`<text x="${x + 8}" y="${y + 4}" font-size="11" font-weight="700" fill="${trackColor}" pointer-events="none">${esc(trackText)}</text>`);
          parts.push(`<path d="M${x},${y - 7} L${x + 6},${y} L${x},${y + 7} L${x - 6},${y} Z" fill="${fill}" stroke="var(--panel, #fff)" stroke-width="1"
            data-train="${esc(tr.train)}" data-station="${esc(m.stationId)}" data-ti="${ti}" data-mi="${mi}" data-drag="${m.draggable ? 1 : 0}"
            data-x="${x}" data-tip="${esc(tipText)}" style="cursor:${m.draggable ? "ew-resize" : "pointer"}"/>`);
        });
      });
      parts.push(`</svg>`);
      if (!d.trains.some(visible)) parts.push(`<div class="muted">表示する列車はいません</div>`);
      if (found.length > 0) {
        const name = (y) => (L.band[y] ? L.band[y].s.name : y);
        parts.push(`<div style="color:var(--danger); margin-top:4px"><b>番線の競合</b> ` + found.map((c) =>
          `${esc(name(c.y))} ${esc(rowName(c.track))}: ${esc(c.trains.join("・"))}（${hhmm(c.start)}〜${hhmm(c.end)}）`).join("／") + `</div>`);
      }
      const html = parts.join("");
      if (box.dataset.html !== html) { box.innerHTML = html; box.dataset.html = html; }
    }

    function svgX(ev) {
      const svg = box.querySelector("svg");
      const pt = svg.createSVGPoint();
      pt.x = ev.clientX; pt.y = ev.clientY;
      return pt.matrixTransform(svg.getScreenCTM().inverse()).x;
    }

    function open(el) {
      const tr = diagram.trains[Number(el.dataset.ti)], m = tr && tr.markers[Number(el.dataset.mi)];
      if (!m) return;
      openPopup({ box, anchor: el, train: tr.train, marker: m, diagram, act, onClose: () => { box.dataset.html = ""; render(diagram); } });
    }

    box.addEventListener("pointermove", (ev) => {
      if (dragging) {
        const L = layout(diagram);
        const x = Math.max(L.left, Math.min(L.w - RIGHT, svgX(ev)));
        dragging.el.setAttribute("transform", `translate(${x - dragging.x0},0)`);
        dragging.x = x;
        if (tip) tip.textContent = `${dragging.label} → ${hhmm(L.t(x), true)} 発に変更（離すと決定）`;
      } else if (tip) {
        const m = ev.target.closest("path[data-train]");
        tip.textContent = m ? m.dataset.tip : "";
      }
    });
    box.addEventListener("pointerdown", (ev) => {
      if (ev.target.closest("[data-gokko-popup]")) return; // ◆の窓の中
      const m = ev.target.closest("path[data-train]");
      if (!m) { if (popup) { closePopup(); box.dataset.html = ""; render(diagram); } return; }
      ev.preventDefault();
      closePopup();
      m.setPointerCapture(ev.pointerId);
      dragging = { el: m, train: m.dataset.train, station: m.dataset.station, x0: Number(m.dataset.x), x: Number(m.dataset.x),
                   label: m.dataset.tip.split(":")[0], canDrag: m.dataset.drag === "1" };
    });
    box.addEventListener("pointerup", async () => {
      if (!dragging) return;
      const d = dragging;
      dragging = null;
      // 動かさずに離したら、◆の窓（クリック）
      if (Math.abs(d.x - d.x0) < 3 || !d.canDrag) {
        d.el.removeAttribute("transform");
        open(d.el);
        return;
      }
      const time = new Date(layout(diagram).t(d.x)).toISOString();
      box.dataset.html = "";
      await act("POST", "/route/open-at", { train: d.train, station: d.station, time });
    });
    box.addEventListener("dblclick", (ev) => {
      const m = ev.target.closest("path[data-train]");
      if (!m || m.dataset.drag !== "1") return;
      closePopup();
      act("POST", "/route/open-at", { train: m.dataset.train, station: m.dataset.station, time: null });
    });

    return { update: render };
  }

  // ---- 進路を取る順番（取れずに待っている場内・出発）と、進路を要求した記録 ----
  function queuePanel({ el, api, help = true }) {
    let showLog = false;
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button[data-log]");
      if (!b) return;
      showLog = !showLog;
      el.dataset.key = "";
      await refreshLog();
    });
    let logLines = [];
    async function refreshLog() {
      if (showLog) { try { logLines = (await api("GET", "/route/log")).lines; } catch (e) { logLines = [e.message]; } }
    }

    return {
      async update(d) {
        await refreshLog();
        const q = (d && d.queue) || [];
        const html = `<div class="row" style="margin-bottom:4px"><b>進路を取る順番</b>
            ${help ? `<span class="muted">（取れずに待っているもの。上から順に取ります）</span>` : ""}
            <button data-log="1" style="margin-left:auto">${showLog ? "記録を閉じる" : "進路の記録を見る"}</button></div>
          ${q.length === 0 ? `<div class="muted">待っている進路はありません</div>` : `<table>${q.map((x, i) =>
            `<tr><td class="muted">${i + 1}</td><td><b>${esc(x.train)}</b></td><td>${esc(x.station)}</td><td>${esc(x.kind)}</td><td class="muted">${esc(x.wait)}</td></tr>`).join("")}</table>`}
          ${showLog ? `<div style="margin-top:8px"><b>進路の記録</b> ${help ? `<span class="muted">（新しい順。サーバーの logs フォルダにも日ごとに残ります）</span>` : ""}
            <pre style="max-height:240px;overflow:auto;font-size:12px;margin:4px 0 0">${esc(logLines.join("\n"))}</pre></div>` : ""}`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // 継続乗務申請（運転画面を出たあとも残している在線。5分で消える）と、出現待ちの列車
  function continuePanel({ el, act, help = true }) {
    // 誤操作を防ぐため、在線を消す・参加者を切断するのどちらも「はい／いいえ」で確かめる
    el.addEventListener("click", async (ev) => {
      const g = ev.target.closest("button[data-ghost]");
      if (g) {
        if (await ask("残している在線を消しますか？", `${g.dataset.train} の継続乗務申請（残している在線）を消します。元に戻せません`))
          await act("POST", "/continue/delete", { id: g.dataset.ghost });
        return;
      }
      const k = ev.target.closest("button[data-kick]");
      if (k && await ask("この参加者を切断しますか？", `${k.dataset.train || "列番なし"} の接続を切ります（その人のクライアントは、つなぎ直すまで切れたままになります）`))
        await act("POST", "/players/kick", { id: k.dataset.kick });
    });
    return {
      update(d) {
        const c = (d && d.continues) || [], a = (d && d.appear) || [], ps = (d && d.players) || [];
        const left = (t) => Math.max(0, Math.round((Date.parse(t) - Date.now()) / 1000));
        const until = (x) => new Date(x.until).getFullYear() > 9000 ? "消すまで残す（ダイヤ運転中）"
          : `あと${Math.floor(left(x.until) / 60)}分${left(x.until) % 60}秒`;
        const html = `<div class="row" style="margin-bottom:4px"><b>継続乗務申請</b>
            ${help ? `<span class="muted">（運転画面を出たあとも在線を残している列車。同じ番線から次のシナリオを始めると、新しいシナリオの列番で始めます。ダイヤ運転中は必ず残し、消すまで残します）</span>` : ""}</div>
          ${c.length === 0 ? `<div class="muted">継続乗務申請はありません</div>` : `<table>${c.map((x) =>
            `<tr><td><b>${esc(x.train)}</b></td><td class="muted">${esc(x.circuits.join(" "))}</td>
              <td>${hhmm(Date.parse(x.since), true)} から・${until(x)}</td>
              <td><button class="danger" data-ghost="${esc(x.id)}" data-train="${esc(x.train)}">在線を消す</button></td></tr>`).join("")}</table>`}
          <div class="row" style="margin:8px 0 4px"><b>出現待ち</b>
            ${help ? `<span class="muted">（他の列車の在線・進路の上に現れた列車、ダイヤ運転中に列車番号を手入力していない列車。サーバーの列車として扱いません）</span>` : ""}</div>
          ${a.length === 0 ? `<div class="muted">出現待ちの列車はいません</div>` : `<table>${a.map((x) =>
            `<tr><td><b>${esc(x.train)}</b></td><td class="muted">${esc(x.reason)}</td></tr>`).join("")}</table>`}
          <div class="row" style="margin:8px 0 4px"><b>つながっている参加者</b>
            ${help ? `<span class="muted">（切断すると、その人のクライアントの接続を切ります）</span>` : ""}</div>
          ${ps.length === 0 ? `<div class="muted">参加者はいません</div>` : `<table>${ps.map((p) =>
            `<tr><td><b>${esc(p.train || "（列番なし）")}</b>${p.train && !p.typed ? ` <span class="muted">（シナリオの列番）</span>` : ""}</td>
              <td class="muted">${esc(p.mode)}・${p.inGame ? esc(p.position || "位置不明") : "運転画面の外"}${p.appear ? "・出現待ち" : ""}</td>
              <td><button class="danger" data-kick="${esc(p.id)}" data-train="${esc(p.train)}">切断</button></td></tr>`).join("")}</table>`}`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // ---- 運転モード（フリー運転・ダイヤ運転）の切り替え。切り替えるときは「はい／いいえ」で確かめる ----
  function modePanel({ el, act, help = true }) {
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button[data-dia]");
      if (!b) return;
      const dia = b.dataset.dia === "1";
      if (await ask(`${dia ? "ダイヤ運転" : "フリー運転"}に切り替えますか？`,
        dia ? "ダイヤ運転中は、列車番号を手入力していない列車を出現させず、継続乗務は必ず残します" : "列車番号の手入力は要らなくなり、継続乗務は5分で消えるようになります"))
        await act("POST", "/mode", { dia });
    });
    return {
      update(d) {
        const dia = !!(d && d.diaMode);
        const html = `<div class="row" style="margin:0">
            <b>運転モード</b>
            <button data-dia="0" class="${dia ? "" : "primary"}" ${dia ? "" : "disabled"}>フリー運転</button>
            <button data-dia="1" class="${dia ? "primary" : ""}" ${dia ? "disabled" : ""}>ダイヤ運転</button>
            ${help ? `<span class="muted">ダイヤ運転中は、列車番号の手入力が要り、継続乗務は必ず残します</span>` : ""}</div>`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // ---- 駅の扱い（列車扱い・集中扱いの切り替え） ----
  function stationModePanel({ el, act, help }) {
    el.addEventListener("click", (ev) => {
      const b = ev.target.closest("button[data-mode-station]");
      if (b) act("POST", "/route/station-mode", { station: b.dataset.modeStation, central: b.dataset.central === "1" });
    });
    return {
      update(d) {
        const html = (help ? `<div class="muted" style="margin-bottom:8px">集中扱いの駅は、列車ごとに「自動開通」を押すまで自動では開通させません。</div>` : "")
          + `<div class="row">${((d && d.stationModes) || []).map((s) =>
            `<button data-mode-station="${esc(s.id)}" data-central="${s.central ? "0" : "1"}" class="${s.central ? "danger" : ""}">${esc(s.name)}: ${s.central ? "集中扱い" : "列車扱い"}</button>`).join("")}</div>`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // ---- 自動退避設定（余裕・許容の遅れ） ----
  function passingPanel({ el, act, help }) {
    let editing = false;
    el.innerHTML = `<div class="row">
        <label>余裕(秒) <input data-p="margin" type="number" min="0" style="width:70px"></label>
        <label>許容の遅れ(秒) <input data-p="tolerance" type="number" min="0" style="width:70px"></label>
        <button data-p-apply="1">反映</button></div>`
      + (help ? `<div class="muted">後ろの特急に「余裕」より近く追いつかれる見込みなら待避させます。特急が見込みより「許容の遅れ」以上遅れたら、待たずに発車させます。</div>` : "");
    const margin = el.querySelector('[data-p="margin"]'), tolerance = el.querySelector('[data-p="tolerance"]');
    el.addEventListener("input", () => { editing = true; });
    el.querySelector("[data-p-apply]").addEventListener("click", async () => {
      await act("POST", "/route/passing", { marginSeconds: Number(margin.value), toleranceSeconds: Number(tolerance.value) });
      editing = false;
    });
    return {
      update(d) {
        if (editing || !d || !d.passing) return;
        margin.value = d.passing.marginSeconds;
        tolerance.value = d.passing.toleranceSeconds;
      }
    };
  }

  // ---- 確認の窓（「はい」「いいえ」）。はいなら true ----
  function ask(message, detail) {
    return new Promise((resolve) => {
      const back = document.createElement("div");
      back.style.cssText = "position:fixed; inset:0; background:rgba(0,0,0,.45); z-index:50; display:flex; align-items:center; justify-content:center; padding:16px";
      back.innerHTML = `<div role="dialog" aria-modal="true" style="background:var(--panel); color:var(--text); border:1px solid var(--line); border-radius:8px; padding:16px 18px; max-width:420px; width:100%; box-shadow:0 4px 16px rgba(0,0,0,.3)">
          ${detail ? `<div class="muted" style="margin-bottom:8px">${esc(detail)}</div>` : ""}
          <div style="font-weight:bold; font-size:16px; margin-bottom:14px">${esc(message)}</div>
          <div class="row" style="justify-content:center; margin:0">
            <button data-ans="yes" class="primary">はい</button><button data-ans="no">いいえ</button></div></div>`;
      const close = (ok) => { back.remove(); document.removeEventListener("keydown", onKey); resolve(ok); };
      const onKey = (ev) => { if (ev.key === "Escape") close(false); };
      back.addEventListener("click", (ev) => {
        const b = ev.target.closest("button[data-ans]");
        if (b) close(b.dataset.ans === "yes");
        else if (ev.target === back) close(false);
      });
      document.addEventListener("keydown", onKey);
      document.body.appendChild(back);
      back.querySelector('[data-ans="no"]').focus(); // うっかり Enter で実行しないように、いいえを選んでおく
    });
  }

  // ---- 手動進路構成（進路名を直接選んで、その列車のゲームに開通・取消させる） ----
  // confirm: true なら、取消のたびに「通告は完了したか？」を聞き、「はい」のときだけ実行する（開通は聞かない）
  function manualRoutePanel({ el, api, after, confirm = false, help = false }) {
    el.innerHTML = (help ? `<div class="muted" style="margin-bottom:8px">進路名を直接選んで、その列車のゲームに開通・取消させます。連動の確認も、列車が接近しているかの確認もしません（運営中に詰んだとき用の強制の操作）。取り消した駅は、自動で開き直さないように抑止になります。</div>` : "")
      + `<div class="row">
        <label>列車 <select data-mr="train"></select></label>
        <label>駅 <select data-mr="station"></select></label>
        <label>進路 <select data-mr="route"></select></label>
        <button data-mr-act="set" class="primary">開通</button>
        <button data-mr-act="cancel" class="danger">取消</button>
        <span data-mr="result" class="muted"></span></div>
      <div data-mr="game" class="muted"></div>`;
    const q = (k) => el.querySelector(`[data-mr="${k}"]`);
    let catalog = null, trains = [];

    function fill(sel, items) {
      const html = items.map((x) => `<option value="${esc(x.value)}">${esc(x.text)}</option>`).join("");
      if (sel.dataset.html === html) return;
      const cur = sel.value;
      sel.innerHTML = html; sel.dataset.html = html;
      if (cur && items.some((x) => x.value === cur)) sel.value = cur;
    }

    function draw() {
      if (!catalog) return;
      fill(q("train"), trains.map((t) => ({ value: t.train, text: `${t.train}（${t.direction}）` })));
      fill(q("station"), catalog.map((s) => ({ value: s.id, text: s.name })));
      const st = catalog.find((s) => s.id === q("station").value);
      fill(q("route"), st ? st.routes.map((r) => ({ value: r.name, text: `${r.name}（${r.from}→${r.to}・${r.kind}）` })) : []);
      const t = trains.find((x) => x.train === q("train").value);
      const mine = t ? t.gameRoutes.filter((g) => !st || g.interlock === st.interlock) : [];
      const html = t ? (mine.length === 0 ? `${esc(t.train)} のゲームに、この駅の進路はありません`
        : `${esc(t.train)} のゲームにある進路: ` + mine.map((g) =>
            `<button data-mr-cancel="${esc(g.route)}" data-mr-il="${esc(g.interlock)}" title="取消">${esc(g.route)} ×</button>`).join(" ")) : "";
      if (q("game").dataset.html !== html) { q("game").innerHTML = html; q("game").dataset.html = html; }
    }

    async function run(interlock, route, set) {
      const train = q("train").value;
      if (!train || !route) return;
      const st = catalog.find((s) => s.interlock === interlock);
      if (confirm && !set && !(await ask("通告は完了したか？", `${train} ${st ? st.name : interlock} ${route} を取消します`))) {
        q("result").textContent = "取りやめました";
        return;
      }
      try {
        const r = await api("POST", "/route/manual-route", { train, interlock, route, set });
        q("result").textContent = r.message;
        after && (await after());
      } catch (e) { q("result").textContent = e.message; }
    }

    el.addEventListener("change", (ev) => { if (ev.target.closest('[data-mr="train"], [data-mr="station"]')) draw(); });
    el.addEventListener("click", (ev) => {
      const b = ev.target.closest("button");
      if (!b || !catalog) return;
      if (b.dataset.mrCancel) { run(b.dataset.mrIl, b.dataset.mrCancel, false); return; }
      if (!b.dataset.mrAct) return;
      const st = catalog.find((s) => s.id === q("station").value);
      if (st) run(st.interlock, q("route").value, b.dataset.mrAct === "set");
    });

    return {
      async update(list) {
        trains = list || [];
        if (!catalog) { try { catalog = await api("GET", "/route/catalog"); } catch (e) { return; } }
        if (el.contains(document.activeElement) && document.activeElement.tagName === "SELECT") return;
        draw();
      }
    };
  }

  window.GokkoDia = { openPopup, closePopup, isOpen: () => !!popup, orderEditor, trainEditor, diagramView, queuePanel, continuePanel, modePanel, stationModePanel, passingPanel, manualRoutePanel, ask, TYPE_COLORS, esc, hhmm };
})();
