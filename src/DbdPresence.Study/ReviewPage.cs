using System.Text;
using DbdPresence.Vision.Text;

namespace DbdPresence.Study;

/// <summary>A range shown on the page, with its small pictures as data addresses.</summary>
/// <param name="Segment">The range.</param>
/// <param name="Thumbnails">Small PNG pictures, as <c>data:image/png;base64,...</c>.</param>
/// <param name="Icons">Close-ups of the owner's own state icon cell in the same pictures (full resolution, for the optional icon answer), or none.</param>
internal sealed record PageSegment(PlanSegment Segment, IReadOnlyList<string> Thumbnails, IReadOnlyList<string>? Icons = null);

/// <summary>A match shown on the blind re-check page.</summary>
/// <param name="StartIndex">The match's first range.</param>
/// <param name="Thumbnails">A strip of small pictures spread over the match.</param>
internal sealed record PageMatch(int StartIndex, IReadOnlyList<string> Thumbnails);

/// <summary>What one review page shows.</summary>
internal sealed record ReviewPageModel(string Mode, string SessionId, string AnswerFileName, IReadOnlyList<PageSegment> Segments, IReadOnlyList<PageMatch> Matches, ReviewAnswers? Prefill);

/// <summary>
/// Builds the single-file review page the owner opens in a browser (PLAN_V2 D25: pictures stay on this PC). The page is plain HTML with a little script,
/// loads nothing from anywhere, and saves the owner's answers as a file in the browser's downloads folder; the tool reads that file. Thumbnails are
/// embedded, so the file holds game pictures: it is a session file and is deleted with the session after 30 days.
/// </summary>
internal static class ReviewPage
{
    /// <summary>Builds the page.</summary>
    public static string Build(ReviewPageModel model, Vocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vocabulary);
        var data = JsonText.Compact(w =>
        {
            w.WriteStartObject();
            w.WriteString("mode", model.Mode);
            w.WriteString("session", model.SessionId);
            w.WriteString("answerFile", model.AnswerFileName);
            w.WriteStartArray("scenes");
            foreach (var (id, text) in ReviewVocabulary.Scenes)
            {
                w.WriteStartArray();
                w.WriteStringValue(id);
                w.WriteStringValue(text);
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteStartArray("ownStatuses");
            foreach (var (id, text) in ReviewVocabulary.OwnStatusAnswers)
            {
                w.WriteStartArray();
                w.WriteStringValue(id);
                w.WriteStringValue(text);
                w.WriteEndArray();
            }

            w.WriteEndArray();
            w.WriteStartArray("survivorResults");
            foreach (var r in ReviewVocabulary.SurvivorResults)
            {
                w.WriteStringValue(r);
            }

            w.WriteEndArray();
            w.WriteStartObject("lists");
            List(w, "characters", vocabulary.Entries(VocabularyKind.Survivor).Concat(vocabulary.Entries(VocabularyKind.Killer)), withGroup: false);
            List(w, "maps", vocabulary.Entries(VocabularyKind.Map), withGroup: true);
            List(w, "killers", vocabulary.Entries(VocabularyKind.Killer), withGroup: false);
            List(w, "queues", [new VocabularyEntry(DbdPresence.Core.Detection.QueueModes.Normal, "Normal queue"), .. vocabulary.Entries(VocabularyKind.EventMode)], withGroup: false);
            w.WriteEndObject();
            w.WriteStartArray("segments");
            foreach (var s in model.Segments)
            {
                w.WriteStartObject();
                w.WriteNumber("i", s.Segment.Index);
                w.WriteNumber("from", s.Segment.FromFrame);
                w.WriteNumber("to", s.Segment.ToFrame);
                w.WriteString("start", s.Segment.StartLocal);
                w.WriteNumber("seconds", s.Segment.Seconds);
                Thumbs(w, s.Thumbnails);
                w.WriteStartArray("icons");
                foreach (var icon in s.Icons ?? [])
                {
                    w.WriteStringValue(icon);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("matches");
            foreach (var m in model.Matches)
            {
                w.WriteStartObject();
                w.WriteNumber("i", m.StartIndex);
                Thumbs(w, m.Thumbnails);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (model.Prefill is { } pre)
            {
                w.WritePropertyName("prefill");
                w.WriteRawValue(pre.ToJson());
            }

            w.WriteEndObject();
        });

        // The data is JSON from this tool; "</" is escaped so no value can close the script element early.
        var safe = data.Replace("</", "<\\/", StringComparison.Ordinal);
        return Template.Replace("__DATA__", safe, StringComparison.Ordinal);
    }

    private static void Thumbs(System.Text.Json.Utf8JsonWriter w, IReadOnlyList<string> thumbnails)
    {
        w.WriteStartArray("thumbs");
        foreach (var t in thumbnails)
        {
            w.WriteStringValue(t);
        }

        w.WriteEndArray();
    }

    private static void List(System.Text.Json.Utf8JsonWriter w, string name, IEnumerable<VocabularyEntry> entries, bool withGroup)
    {
        w.WriteStartArray(name);
        foreach (var e in entries)
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            w.WriteString("name", withGroup && e.Group is { Length: > 0 } g ? $"{e.Name} ({g})" : e.Name);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    /// <summary>Gets the page text (UTF-8). It holds no address of any kind: nothing is loaded and nothing is sent.</summary>
    internal static string Template { get; } = BuildTemplate();

    private static string BuildTemplate()
    {
        var sb = new StringBuilder(Head);
        sb.Append(Script);
        return sb.ToString();
    }

    private const string Head = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <title>DBD Presence: check your matches</title>
        <style>
        body{font-family:"Segoe UI",Arial,sans-serif;margin:16px;background:#14161a;color:#e8eaed;max-width:1100px}
        h1{font-size:20px;margin:4px 0}
        p.note{color:#aab;max-width:780px;line-height:1.4}
        .seg{border:1px solid #333;border-radius:6px;padding:8px;margin:8px 0;display:flex;gap:10px;align-items:center;flex-wrap:wrap;background:#1c1f25}
        .seg.ok{border-color:#3a7}
        .seg img,.strip img{height:90px;border-radius:3px}
        .meta{min-width:170px;font-size:13px;color:#bbc}
        select,input{font-size:14px;padding:4px;background:#0f1114;color:#e8eaed;border:1px solid #556;border-radius:4px}
        select.suggested{border-color:#d90;color:#fc6}
        label.tick{display:flex;gap:6px;align-items:center;font-size:14px}
        .facts{margin:0 0 8px 24px;padding:10px;border-left:4px solid #58f;background:#181c26;display:grid;grid-template-columns:max-content 1fr;gap:6px 10px;align-items:center}
        .facts .strip{grid-column:1 / span 2;display:flex;gap:6px;flex-wrap:wrap}
        .own{margin:0 0 8px 24px;padding:8px;border-left:4px solid #a6e;background:#1c1a26;display:flex;gap:10px;align-items:center;flex-wrap:wrap;font-size:14px}
        .own img{height:64px;border-radius:3px;background:#000}
        .err{outline:2px solid #e44}
        button{font-size:16px;padding:8px 18px;margin:8px 0;cursor:pointer}
        #bar{position:sticky;top:0;background:#14161a;padding:6px 0;z-index:2;border-bottom:1px solid #333}
        </style></head><body>
        <div id="bar"><h1 id="title"></h1><div id="status"></div></div>
        <p class="note" id="intro"></p>
        <div id="app"></div>
        <p><button id="save">Save my answers</button> <span id="msg"></span></p>
        <script>
        """;

    private const string Script = """
        "use strict";
        const D = __DATA__;
        const isLabel = D.mode === "labels";
        const CYCLE = ["MainMenu","Other","Lobby","Matchmaking","Lobby","InTrial","InMatchMenu","InTrial","Results","MainMenu"];
        const rows = [];
        const matchPanels = new Map();
        const $ = (id) => document.getElementById(id);

        function h(tag, props, ...kids) {
          const e = document.createElement(tag);
          for (const k in (props || {})) {
            if (k === "class") e.className = props[k];
            else if (k === "text") e.textContent = props[k];
            else if (k === "list" || k === "placeholder") e.setAttribute(k, props[k]);
            else e[k] = props[k];
          }
          for (const c of kids) { if (c) e.append(c); }
          return e;
        }
        const option = (v, t) => h("option", { value: v, text: t });
        const nameMap = (list) => new Map(list.map((e) => [e.name.toLowerCase(), e.id]));
        const idMap = (list) => new Map(list.map((e) => [e.id, e.name]));
        const lists = D.lists;
        const byName = { characters: nameMap(lists.characters), maps: nameMap(lists.maps), killers: nameMap(lists.killers), queues: nameMap(lists.queues) };
        const byId = { characters: idMap(lists.characters), maps: idMap(lists.maps), killers: idMap(lists.killers), queues: idMap(lists.queues) };

        function idOf(kind, text, optional) {
          const t = text.trim();
          if (t === "") return optional ? "" : null;
          if (t === "?") return "?";
          const id = byName[kind].get(t.toLowerCase());
          return id === undefined ? null : id;
        }
        function nameOf(kind, id) { return id === "?" ? "?" : (byId[kind].get(id) || ""); }

        function datalist(id, list) {
          const d = h("datalist", { id });
          for (const e of list) d.append(option(e.name, ""));
          return d;
        }

        function advance(ptr, scene) {
          if (scene === "None" || scene === "") return ptr;
          for (let i = ptr + 1; i < CYCLE.length; i++) if (CYCLE[i] === scene) return i;
          for (let i = 0; i <= ptr; i++) if (CYCLE[i] === scene) return i;
          return ptr;
        }

        function makeFacts(startIndex, thumbs, pre) {
          const role = h("select", {}, option("", "Choose..."), option("Survivor", "Survivor"), option("Killer", "Killer"), option("?", "Can't tell"));
          const ch = h("input", { type: "text", list: "dl-characters", placeholder: "Start typing a name, or ? if not shown" });
          const map = h("input", { type: "text", list: "dl-maps", placeholder: "Start typing a map, or ? if not shown" });
          const result = h("select", {}, option("", "Choose..."), ...D.survivorResults.map((r) => option(r, r)), option("?", "Can't tell"));
          const killer = h("input", { type: "text", list: "dl-killers", placeholder: "Optional: the killer, if the game shows the name" });
          const queue = h("input", { type: "text", list: "dl-queues", placeholder: "Optional: normal queue or the event, if shown" });
          if (pre) {
            role.value = pre.role; ch.value = nameOf("characters", pre.character); map.value = nameOf("maps", pre.map);
            result.value = pre.result; killer.value = nameOf("killers", pre.killer); queue.value = nameOf("queues", pre.queue);
          }
          for (const control of [role, ch, map, result, killer, queue]) {
            const clear = () => control.classList.remove("err");
            control.addEventListener("input", clear);
            control.addEventListener("change", clear);
          }
          const el = h("div", { class: "facts" });
          if (thumbs && thumbs.length) el.append(h("div", { class: "strip" }, ...thumbs.map((u) => h("img", { src: u, alt: "picture from the match" }))));
          const pair = (label, control) => { el.append(h("div", { text: label }), control); };
          pair("Your role", role);
          pair("Your character", ch);
          pair("Map", map);
          pair("Result (survivors)", result);
          pair("Killer (optional)", killer);
          pair("Queue (optional)", queue);
          const read = () => {
            const a = {
              role: role.value,
              character: idOf("characters", ch.value, false),
              map: idOf("maps", map.value, false),
              result: result.value,
              killer: idOf("killers", killer.value, true),
              queue: idOf("queues", queue.value, true),
            };
            let bad = false;
            const flag = (control, wrong) => { control.classList.toggle("err", wrong); bad = bad || wrong; };
            flag(role, a.role === "");
            flag(ch, a.character === null);
            flag(map, a.map === null);
            flag(result, a.role === "Survivor" && a.result === "");
            flag(killer, a.killer === null);
            flag(queue, a.queue === null);
            return bad ? null : a;
          };
          return { el, read };
        }

        function refresh() {
          let ptr = -1, done = 0, starts = 0;
          for (const r of rows) {
            if (isLabel) {
              if (r.start.checked) starts++;
              if (r.ok.checked) {
                done++; r.select.classList.remove("suggested"); r.dom.classList.add("ok"); ptr = advance(ptr, r.select.value);
              } else {
                r.dom.classList.remove("ok");
                if (r.touched) { r.select.classList.remove("suggested"); ptr = advance(ptr, r.select.value); }
                else { ptr = (ptr + 1) % CYCLE.length; r.select.value = CYCLE[ptr]; r.select.classList.add("suggested"); }
              }
            } else if (r.select.value !== "") { done++; }
            r.syncOwn();
          }
          $("status").textContent = isLabel
            ? done + " of " + rows.length + " screens confirmed. " + starts + " matches marked."
            : done + " of " + rows.length + " answered.";
        }

        function build() {
          $("title").textContent = isLabel ? "Check your recording" : "Second look (do this without looking at your earlier answers)";
          $("intro").textContent = isLabel
            ? "Each row is a run of pictures from your recording. Look at the pictures, make sure the screen name is right, and tick the box. Orange means only a suggestion from the usual order of screens: it is a guess, so check it. Tick \"a new match starts here\" on the first row of every match and fill in what you played. For a row in a trial you may also say what your own icon shows (optional; it teaches the app your icons). Nothing leaves this computer."
            : "A few rows are asked again, without your earlier answers, to check the labels are reliable. Pick the screen for each row and fill in the matches shown. Nothing leaves this computer.";
          const app = $("app");
          app.append(datalist("dl-characters", lists.characters), datalist("dl-maps", lists.maps), datalist("dl-killers", lists.killers), datalist("dl-queues", lists.queues));
          const preSeg = new Map(); const preMatch = new Map();
          if (D.prefill) { for (const s of D.prefill.segments) preSeg.set(s.i, s); for (const m of D.prefill.matches) preMatch.set(m.startIndex, m); }

          for (const seg of D.segments) {
            const select = h("select", {}, ...(isLabel ? [] : [option("", "Choose the screen...")]), ...D.scenes.map(([id, text]) => option(id, text)));
            const ok = h("input", { type: "checkbox" });
            const start = h("input", { type: "checkbox" });
            const meta = h("div", { class: "meta", text: seg.start + " - " + Math.round(seg.seconds) + " s - pictures " + seg.from + " to " + seg.to });
            const dom = h("div", { class: "seg" }, ...seg.thumbs.map((u) => h("img", { src: u, alt: "picture" })), meta, select);
            const row = { seg, select, ok, start, dom, touched: false, facts: null, own: null, syncOwn: () => {} };
            if (isLabel) {
              dom.append(h("label", { class: "tick" }, ok, h("span", { text: "Looks right" })), h("label", { class: "tick" }, start, h("span", { text: "A new match starts here" })));
              const pre = preSeg.get(seg.i);
              if (pre) { select.value = pre.scene; ok.checked = true; start.checked = pre.matchStart; row.touched = true; }
            }
            select.addEventListener("change", () => { row.touched = true; refresh(); });
            ok.addEventListener("change", () => { row.touched = true; refresh(); });
            const wrap = h("div", {}, dom);
            if (isLabel) {
              const own = h("select", {}, option("", "Not answered"), ...D.ownStatuses.map(([id, text]) => option(id, text)));
              const ownBox = h("div", { class: "own" }, ...(seg.icons || []).map((u) => h("img", { src: u, alt: "your icon" })),
                h("span", { text: "Optional, only if you played survivor: what is your own icon (left of your name, first row of the list)? Answer only if it is the same in all these close-ups." }), own);
              const previous = preSeg.get(seg.i);
              if (previous && previous.ownStatus) own.value = previous.ownStatus;
              row.own = own;
              row.syncOwn = () => {
                const trial = select.value === "InTrial";
                if (trial && !ownBox.isConnected) wrap.append(ownBox);
                if (!trial && ownBox.isConnected) { ownBox.remove(); own.value = ""; }
              };
              select.addEventListener("change", row.syncOwn);
            }
            if (isLabel) {
              const showFacts = () => {
                if (start.checked && !row.facts) { row.facts = makeFacts(seg.i, null, preMatch.get(seg.i)); wrap.append(row.facts.el); }
                if (!start.checked && row.facts) { row.facts.el.remove(); row.facts = null; }
                refresh();
              };
              start.addEventListener("change", showFacts);
              if (start.checked) showFacts();
            }
            rows.push(row);
            app.append(wrap);
          }
          if (!isLabel) {
            app.append(h("h2", { text: "Matches asked again" }));
            for (const m of D.matches) {
              const panel = makeFacts(m.i, m.thumbs, null);
              matchPanels.set(m.i, panel);
              app.append(panel.el);
            }
          }
          refresh();
        }

        $("save").addEventListener("click", () => {
          const out = { schemaVersion: 1, kind: D.mode, sessionId: D.session, segments: [], matches: [] };
          let bad = 0;
          for (const r of rows) {
            const scene = r.select.value;
            const missing = scene === "" || (isLabel && !r.ok.checked);
            r.dom.classList.toggle("err", missing);
            if (missing) { bad++; continue; }
            const entry = { i: r.seg.i, scene, matchStart: isLabel && r.start.checked };
            if (isLabel && scene === "InTrial" && r.own.value !== "") entry.ownStatus = r.own.value;
            out.segments.push(entry);
          }
          const panels = isLabel ? rows.filter((r) => r.start.checked && r.facts).map((r) => [r.seg.i, r.facts]) : [...matchPanels.entries()];
          for (const [i, panel] of panels) {
            const a = panel.read();
            if (a === null) { bad++; continue; }
            out.matches.push(Object.assign({ startIndex: i }, a));
          }
          if (bad > 0) { $("msg").textContent = (bad === 1 ? "1 thing still needs" : bad + " things still need") + " an answer (marked in red)."; return; }
          const blob = new Blob([JSON.stringify(out)], { type: "application/json" });
          const link = h("a", { href: URL.createObjectURL(blob), download: D.answerFile });
          document.body.append(link);
          link.click();
          link.remove();
          $("msg").textContent = "Saved to your Downloads folder as " + D.answerFile + ". Go back to the DBD Presence window and press Enter.";
        });

        build();
        </script></body></html>
        """;
}
