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
        await ctx.act("POST", "/route/order", { station: ctx.ord.station, trains: btn.dataset.order === "set" ? ctx.seq : null });
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
    const ord = (diagram.orders || []).find((o) => o.station === m.stationId);
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

  // ---- 順序の設定画面（駅・区間を選んで並べ替える。並べ替えている間は描き直さない） ----
  function orderEditor({ el, act }) {
    let diagram = null;
    let pick = "";     // "st:TH71" / "sec:TH64|TH63"
    let dirty = false; // 並べ替え中（決定まで描き直さない）
    const ctx = { act, seq: [], sseq: [], ord: null, sec: null, redraw: () => draw(), done: () => { dirty = false; } };

    function options() {
      const st = (diagram.orders || []).map((o) => `<option value="st:${esc(o.station)}" ${pick === "st:" + o.station ? "selected" : ""}>駅: ${esc(o.name)}（${o.trains.length}本${o.fixedOrder ? "・指定中" : ""}）</option>`);
      const sc = (diagram.sections || []).map((s) => `<option value="sec:${esc(s.a)}|${esc(s.b)}" ${pick === `sec:${s.a}|${s.b}` ? "selected" : ""}>単線: ${esc(s.name)}（${s.trains.length}本${s.fixedOrder ? "・指定中" : ""}）</option>`);
      return st.concat(sc).join("");
    }

    function draw() {
      if (!diagram) return;
      ctx.ord = pick.startsWith("st:") ? (diagram.orders || []).find((o) => o.station === pick.slice(3)) : null;
      ctx.sec = pick.startsWith("sec:") ? (diagram.sections || []).find((s) => `${s.a}|${s.b}` === pick.slice(4)) : null;
      el.innerHTML = `<div class="row"><select data-pick-order>${`<option value="">駅・区間を選ぶ</option>` + options()}</select>
          ${dirty ? `<span class="muted">並べ替え中（「この順で決定」で反映）</span>` : ""}</div>
        ${ctx.ord ? stationOrderHtml(ctx.ord, ctx.seq) : ctx.sec ? sectionOrderHtml(ctx.sec, ctx.sseq) : ""}`;
    }

    function load() {
      ctx.seq = ctx.ord ? ctx.ord.trains.map((t) => t.train) : [];
      ctx.sseq = ctx.sec ? ctx.sec.trains.map((t) => t.train) : [];
    }

    el.addEventListener("change", async (ev) => {
      const s = ev.target.closest("select[data-pick-order]");
      if (s) { pick = s.value; dirty = false; draw(); load(); draw(); return; }
      await handleTrackChange(ev, ctx);
    });
    el.addEventListener("click", async (ev) => {
      const btn = ev.target.closest("button");
      if (!btn) return;
      if (btn.dataset.up || btn.dataset.down || btn.dataset.sup || btn.dataset.sdown) dirty = true;
      await handleOrderEvent(ev, ctx);
    });

    return {
      update(d) {
        diagram = d;
        if (dirty || el.contains(document.activeElement) && document.activeElement.tagName === "SELECT") return;
        ctx.ord = pick.startsWith("st:") ? (d.orders || []).find((o) => o.station === pick.slice(3)) : null;
        ctx.sec = pick.startsWith("sec:") ? (d.sections || []).find((s) => `${s.a}|${s.b}` === pick.slice(4)) : null;
        load();
        // 見込み時刻・状態は毎秒変わるので、比べるときは除く（押している途中のボタンが消えないように）
        const html = JSON.stringify([pick, ctx.ord, ctx.sec], (k, v) => (k === "eta" || k === "status" ? undefined : v));
        if (el.dataset.key === html) return;
        el.dataset.key = html;
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
  // 上の「全部・上りのみ・下りのみ」で、表示する列車を絞る
  function diagramView({ box, tip, filter, act }) {
    const DG = { w: 1000, left: 92, right: 12, top: 18, rowPx: 17, pastMin: 10, futureMin: 30 };
    let diagram = null;
    let dragging = null;
    let shown = "全部";
    try { shown = localStorage.getItem("gokkoDiagramFilter") || "全部"; } catch (e) {}

    function drawFilter() {
      if (!filter) return;
      filter.innerHTML = ["全部", "上りのみ", "下りのみ"].map((f) =>
        `<button data-filter="${f}" class="${f === shown ? "primary" : ""}">${f}</button>`).join(" ");
    }
    if (filter) filter.addEventListener("click", (ev) => {
      const b = ev.target.closest("button[data-filter]");
      if (!b) return;
      shown = b.dataset.filter;
      try { localStorage.setItem("gokkoDiagramFilter", shown); } catch (e) {}
      drawFilter();
      box.dataset.html = "";
      render(diagram);
    });
    drawFilter();

    function layout(d) {
      const ys = d.stations.map((s) => s.y);
      const minY = Math.min(...ys), maxY = Math.max(...ys);
      const h = (maxY - minY) / 2 * DG.rowPx;
      const now = new Date(d.now).getTime();
      const t0 = now - DG.pastMin * 60000, t1 = now + DG.futureMin * 60000;
      return {
        height: DG.top + h + 24, now, t0, t1,
        x: (t) => DG.left + (new Date(t).getTime() - t0) / (t1 - t0) * (DG.w - DG.left - DG.right),
        t: (x) => t0 + (x - DG.left) / (DG.w - DG.left - DG.right) * (t1 - t0),
        y: (z) => DG.top + (z - minY) / 2 * DG.rowPx
      };
    }

    const visible = (tr) => shown === "全部" || (shown === "上りのみ" ? tr.direction === "上り" : tr.direction === "下り");

    function render(d) {
      if (!d) return;
      diagram = d;
      if (dragging || popup) return; // ずらしている間・◆の窓を開いている間は描き直さない
      const L = layout(d);
      const parts = [`<svg viewBox="0 0 ${DG.w} ${L.height}" width="100%" style="display:block">`];
      for (const s of d.stations) {
        const y = L.y(s.y);
        parts.push(`<line x1="${DG.left}" x2="${DG.w - DG.right}" y1="${y}" y2="${y}" stroke="var(--line)" stroke-width="${s.control ? 1.6 : 0.6}"/>`);
        parts.push(`<text x="${DG.left - 6}" y="${y + 4}" text-anchor="end" font-size="11" fill="${s.control ? "var(--text)" : "var(--muted)"}" font-weight="${s.control ? 600 : 400}">${esc(s.name)}</text>`);
      }
      const first = Math.ceil(L.t0 / 60000) * 60000;
      for (let t = first; t <= L.t1; t += 60000) {
        const x = L.x(t), five = new Date(t).getMinutes() % 5 === 0;
        parts.push(`<line x1="${x}" x2="${x}" y1="${DG.top - 6}" y2="${L.height - 18}" stroke="var(--line)" stroke-width="${five ? 1 : 0.4}"/>`);
        if (five) parts.push(`<text x="${x}" y="${L.height - 4}" text-anchor="middle" font-size="10" fill="var(--muted)">${hhmm(t)}</text>`);
      }
      const nx = L.x(L.now);
      parts.push(`<line x1="${nx}" x2="${nx}" y1="${DG.top - 10}" y2="${L.height - 18}" stroke="var(--danger)" stroke-width="1.2"/>`);
      d.trains.forEach((tr, ti) => {
        if (!visible(tr)) return;
        const color = TYPE_COLORS[tr.type] || "var(--text)";
        const pts = (arr) => arr.map((p) => `${L.x(p.t).toFixed(1)},${L.y(p.y).toFixed(1)}`).join(" ");
        if (tr.history.length > 1) parts.push(`<polyline points="${pts(tr.history)}" fill="none" stroke="${color}" stroke-width="2"/>`);
        if (tr.forecast.length > 1) parts.push(`<polyline points="${pts(tr.forecast)}" fill="none" stroke="${color}" stroke-width="1.6" stroke-dasharray="5 4"/>`);
        const cur = tr.forecast[0];
        parts.push(`<text x="${L.x(cur.t) + 6}" y="${L.y(cur.y) - 5}" font-size="11" font-weight="600" fill="${color}">${esc(tr.train)}</text>`);
        tr.markers.forEach((m, mi) => {
          const x = L.x(m.t), y = L.y(m.y);
          const fill = m.held ? "#c62828" : !m.editable ? "#999" : m.departAt ? "#e0a800" : "#3aa0d8";
          const trackText = m.track == null ? "通" : String(m.track);
          const trackColor = m.trackSource === "集中" || m.trackSource === "駅扱い" ? "#e07000" : m.trackSource === "待避" ? "#b04fd0" : color;
          const dep = m.departAt ? `・発車時刻 ${hhmm(new Date(m.departAt).getTime(), true)}` : "";
          const tipText = `${tr.train} ${m.name}: ${m.track == null ? "通過" : m.track + "番線"}（${m.trackSource}）・${m.source} ${hhmm(new Date(m.t).getTime(), true)}${dep}（${m.status}）`
            + "　押すと番線・駅扱い・抑止・順序" + (m.draggable ? "　横にずらすと発車時刻（ダブルクリックで解除）" : "");
          parts.push(`<text x="${x + 8}" y="${y + 4}" font-size="11" font-weight="700" fill="${trackColor}" pointer-events="none">${esc(trackText)}</text>`);
          parts.push(`<path d="M${x},${y - 7} L${x + 6},${y} L${x},${y + 7} L${x - 6},${y} Z" fill="${fill}" stroke="var(--panel, #fff)" stroke-width="1"
            data-train="${esc(tr.train)}" data-station="${esc(m.stationId)}" data-ti="${ti}" data-mi="${mi}" data-drag="${m.draggable ? 1 : 0}"
            data-x="${x}" data-tip="${esc(tipText)}" style="cursor:${m.draggable ? "ew-resize" : "pointer"}"/>`);
        });
      });
      parts.push(`</svg>`);
      if (!d.trains.some(visible)) parts.push(`<div class="muted">表示する列車はいません</div>`);
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
        const x = Math.max(DG.left, Math.min(DG.w - DG.right, svgX(ev)));
        dragging.el.setAttribute("transform", `translate(${x - dragging.x0},0)`);
        dragging.x = x;
        if (tip) tip.textContent = `${dragging.label} → ${hhmm(layout(diagram).t(x), true)} 発に変更（離すと決定）`;
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
  function queuePanel({ el, api }) {
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
            <span class="muted">（取れずに待っているもの。上から順に取ります）</span>
            <button data-log="1" style="margin-left:auto">${showLog ? "記録を閉じる" : "進路の記録を見る"}</button></div>
          ${q.length === 0 ? `<div class="muted">待っている進路はありません</div>` : `<table>${q.map((x, i) =>
            `<tr><td class="muted">${i + 1}</td><td><b>${esc(x.train)}</b></td><td>${esc(x.station)}</td><td>${esc(x.kind)}</td><td class="muted">${esc(x.wait)}</td></tr>`).join("")}</table>`}
          ${showLog ? `<div style="margin-top:8px"><b>進路の記録</b> <span class="muted">（新しい順。サーバーの logs フォルダにも日ごとに残ります）</span>
            <pre style="max-height:240px;overflow:auto;font-size:12px;margin:4px 0 0">${esc(logLines.join("\n"))}</pre></div>` : ""}`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  // 継続乗務申請（運転画面を出たあとも残している在線。5分で消える）と、出現待ちの列車
  function continuePanel({ el, act }) {
    el.addEventListener("click", async (ev) => {
      const b = ev.target.closest("button[data-ghost]");
      if (!b) return;
      if (!confirm(`${b.dataset.train} の継続乗務申請を消して、残している在線をなくしますか？`)) return;
      await act("POST", "/continue/delete", { id: b.dataset.ghost });
    });
    return {
      update(d) {
        const c = (d && d.continues) || [], a = (d && d.appear) || [];
        const left = (t) => Math.max(0, Math.round((Date.parse(t) - Date.now()) / 1000));
        const html = `<div class="row" style="margin-bottom:4px"><b>継続乗務申請</b>
            <span class="muted">（運転画面を出たあとも在線を残している列車。同じ番線から次のシナリオを始めると、その列番で続けます）</span></div>
          ${c.length === 0 ? `<div class="muted">継続乗務申請はありません</div>` : `<table>${c.map((x) =>
            `<tr><td><b>${esc(x.train)}</b></td><td class="muted">${esc(x.circuits.join(" "))}</td>
              <td>${hhmm(Date.parse(x.since), true)} から・あと${Math.floor(left(x.until) / 60)}分${left(x.until) % 60}秒</td>
              <td><button data-ghost="${esc(x.id)}" data-train="${esc(x.train)}">消す</button></td></tr>`).join("")}</table>`}
          <div class="row" style="margin:8px 0 4px"><b>出現待ち</b>
            <span class="muted">（他の列車の在線・進路の上に現れた列車。先の列車が抜けるまで、サーバーの列車として扱いません）</span></div>
          ${a.length === 0 ? `<div class="muted">出現待ちの列車はいません</div>` : `<table>${a.map((x) =>
            `<tr><td><b>${esc(x.train)}</b></td><td class="muted">${esc(x.reason)}</td></tr>`).join("")}</table>`}`;
        if (el.dataset.key === html) return;
        el.dataset.key = html;
        el.innerHTML = html;
      }
    };
  }

  window.GokkoDia = { openPopup, closePopup, isOpen: () => !!popup, orderEditor, trainEditor, diagramView, queuePanel, continuePanel, TYPE_COLORS, esc, hhmm };
})();
