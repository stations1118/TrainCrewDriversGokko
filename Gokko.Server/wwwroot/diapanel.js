// ダイヤグラムの◆の窓と、出発順序・走行順の編集（管理画面 admin.html と、ダイヤグラムの設定画面 diasettings.html で共通）。
// 使い方: GokkoDia.openPopup({ box, train, marker, diagram, act, onClose }) / GokkoDia.orderEditor({ el, act })
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
          <button data-hold="arrive" class="${m.holdArrive ? "danger" : ""}" title="場内の進路を取らない">${m.holdArrive ? "場内抑止中（解除）" : "場内抑止"}</button>
          <button data-hold="depart" class="${m.holdDepart ? "danger" : ""}" title="出発の進路を取らない">${m.holdDepart ? "出発抑止中（解除）" : "出発抑止"}</button>
          <button class="danger" data-act="cancel" title="開通している進路を取り消して抑止にする">進路取消</button>
          <button class="primary" data-act="force" title="抑止を外し、安全を確認でき次第開通">開通</button>
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
      if (btn.dataset.act === "cancel") await act("POST", "/route/cancel", body);
      else if (btn.dataset.act === "force") await act("POST", "/route/force", body);
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

  window.GokkoDia = { openPopup, closePopup, isOpen: () => !!popup, orderEditor, TYPE_COLORS, esc, hhmm };
})();
