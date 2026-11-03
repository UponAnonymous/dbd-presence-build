// Access & features (FR-ONL-06, FR-ONL-08, FR-ADM-08; PLAN_V2 §6.4): who can get access (invite-only or public), and
// what the signed configuration tells every app: switches that turn capture and detection off, the emergency switch
// for online features, an announcement banner and the minimum supported version. Every saved change raises the config
// version. The switches can only turn things off (SAFETY_AND_PRIVACY §2.3): unticking one hands the decision back to
// each user's own settings; nothing here can turn capture or detection on.

import type { AccessMode, Settings, SettingsPatch, SettingsState } from "../api.ts";
import type { View, ViewContext } from "../context.ts";
import { el, replaceChildren } from "../dom.ts";
import { describeError } from "../errors.ts";
import { count } from "../format.ts";
import { badge, button, card, errorState, field, loading, notice, pageHeader, setBusy } from "../ui.ts";

export const BANNER_MAX = 280;
const VERSION = /^\d{1,5}\.\d{1,5}\.\d{1,5}$/;

interface ModeCopy {
  title: string;
  summary: string;
  details: string;
  confirmTitle: string;
  confirmBody: string[];
}

export const MODES: Record<AccessMode, ModeCopy> = {
  "invite-only": {
    title: "Invite-only",
    summary: "New accounts need an invite code before they can use online features.",
    details: "Anyone can still sign in with Discord, but until they redeem a code their account waits for an invite. Accounts that already have access keep it.",
    confirmTitle: "Switch to invite-only?",
    confirmBody: [
      "From now on, new accounts need an invite code before they can use online features.",
      "Accounts that already have access keep it, including those that joined while access was public. Revoke users one by one if you need to remove access.",
    ],
  },
  public: {
    title: "Public",
    summary: "Anyone who signs in with Discord gets access straight away, without an invite.",
    details: "Accounts waiting for an invite get access the next time they sign in or redeem. Unused invite codes keep working but are no longer needed.",
    confirmTitle: "Open access to everyone?",
    confirmBody: [
      "Anyone with a Discord account who signs in will get access at once, without an invite.",
      "Accounts already waiting for an invite get access the next time they sign in. Switching back to invite-only later does not remove access granted in the meantime.",
    ],
  },
};

function render(container: HTMLElement, ctx: ViewContext): void {
  const modeBody = el("div", { className: "card-body" });
  const featuresBody = el("div", { className: "card-body" });
  const message = el("div", { className: "inline-message" });
  const version = el("p", { className: "page-meta muted", attrs: { "aria-live": "polite" } });
  let state: SettingsState | null = null;

  replaceChildren(
    container,
    pageHeader("Access & features", "Who can get access to the online features, and what the signed configuration tells every app: feature switches, the emergency switch, a banner and the minimum version."),
    version,
    message,
    card("Who can get access", modeBody),
    el("div", { className: "stack features" }, featuresBody),
  );

  function setState(next: SettingsState): void {
    state = next;
    version.textContent = `Signed configuration version ${next.configVersion}. Apps fetch it when they start and refresh it regularly; a saved change reaches them within minutes of their next refresh.`;
  }

  async function load(): Promise<void> {
    replaceChildren(modeBody, loading("Loading the current settings…"));
    replaceChildren(featuresBody);
    try {
      setState(await ctx.api.getSettingsState());
      drawMode(state!.settings.accessMode);
      drawFeatures();
    } catch (error) {
      ctx.reportError(error);
      replaceChildren(modeBody, errorState(error, () => void load(), ctx.reload));
    }
  }

  /** Saves a patch; returns false (and shows why) when it failed. */
  async function save(patch: SettingsPatch, trigger: HTMLButtonElement, what: string, slot: HTMLElement): Promise<boolean> {
    setBusy(trigger, true, "Saving…");
    try {
      setState(await ctx.api.saveSettings(patch));
      return true;
    } catch (error) {
      setBusy(trigger, false);
      ctx.reportError(error);
      const failure = describeError(error);
      replaceChildren(slot, notice("danger", `${what} was not changed: ${failure.title}`, failure.detail, { live: true }));
      return false;
    }
  }

  function drawMode(current: AccessMode, done: HTMLElement | null = null): void {
    let selected: AccessMode = current;
    const saveButton = button("Save access mode", { variant: "primary", attrs: { disabled: true } });
    const options = (Object.keys(MODES) as AccessMode[]).map((mode) => {
      const copy = MODES[mode];
      const input = el("input", { className: "radio", attrs: { type: "radio", name: "access-mode", value: mode, id: `mode-${mode}`, checked: mode === current, "aria-describedby": `mode-${mode}-text` } });
      input.addEventListener("change", () => {
        selected = mode;
        saveButton.disabled = selected === current;
      });
      return el(
        "label",
        { className: "choice", attrs: { for: `mode-${mode}` } },
        input,
        el(
          "span",
          { className: "choice-body" },
          el("span", { className: "choice-title" }, copy.title, mode === current ? badge("info", "Current") : null),
          el("span", { className: "choice-text", attrs: { id: `mode-${mode}-text` } }, el("span", { className: "choice-summary", text: copy.summary }), el("span", { className: "choice-details", text: copy.details })),
        ),
      );
    });
    const slot = el("div", { className: "inline-message" }, done);
    saveButton.addEventListener("click", async () => {
      if (selected === current) return;
      const copy = MODES[selected];
      const ok = await ctx.confirm({ title: copy.confirmTitle, body: [...copy.confirmBody, "The change takes effect immediately and is recorded in the audit log."], confirmLabel: `Switch to ${copy.title.toLowerCase()}`, danger: selected === "public" });
      if (!ok) return;
      if (await save({ accessMode: selected }, saveButton, "The access mode", slot)) {
        const mode = state!.settings.accessMode;
        drawMode(mode, notice("success", `Access is now ${MODES[mode].title.toLowerCase()}`, null, { live: true }));
        ctx.announce(`Access mode changed to ${MODES[mode].title.toLowerCase()}.`);
      }
    });
    replaceChildren(
      modeBody,
      slot,
      el("fieldset", { className: "choices" }, el("legend", { className: "visually-hidden", text: "Access mode" }), ...options),
      el("div", { className: "form-actions" }, saveButton),
    );
  }

  function drawFeatures(): void {
    if (!state) return;
    const settings = state.settings;
    replaceChildren(featuresBody, emergencyCard(settings), switchesCard(settings), bannerCard(settings), minimumCard(settings));
  }

  function done(text: string): void {
    replaceChildren(message, notice("success", text, `Signed configuration version ${state!.configVersion}.`, { live: true }));
    ctx.announce(text);
  }

  function emergencyCard(settings: Settings): HTMLElement {
    const slot = el("div", { className: "inline-message" });
    const on = settings.emergencyStop;
    const toggle = button(on ? "Turn online features back on" : "Turn online features off", { variant: on ? "primary" : "danger" });
    toggle.addEventListener("click", async () => {
      const ok = await ctx.confirm(
        on
          ? { title: "Turn online features back on?", body: ["Apps resume pack sync and the other online features at their next configuration refresh. Users' own settings apply again."], confirmLabel: "Turn back on" }
          : {
              title: "Turn online features off for everyone?",
              body: [
                "Every app stops pack sync and the other online features at its next configuration refresh. Discord presence keeps working.",
                "Use it when something online is going wrong and must stop at once. It stays off until you turn it back on here.",
              ],
              confirmLabel: "Turn online features off",
              danger: true,
            },
      );
      if (!ok) return;
      if (await save({ emergencyStop: !on }, toggle, "The emergency switch", slot)) {
        drawFeatures();
        done(on ? "Online features are back on" : "Online features are off for everyone");
      }
    });
    return el(
      "section",
      { className: `card card-emergency${on ? " card-emergency-on" : ""}`, attrs: { "aria-labelledby": "emergency-title" } },
      el("h2", { className: "card-title", attrs: { id: "emergency-title" } }, "Emergency switch ", on ? badge("danger", "Online features off") : badge("success", "Online features on")),
      el("p", { className: "muted", text: "Turns pack sync and the other online features off in every app. Local Discord presence keeps working, as it does when the service is down." }),
      slot,
      el("div", { className: "form-actions" }, toggle),
    );
  }

  function switchesCard(settings: Settings): HTMLElement {
    const slot = el("div", { className: "inline-message" });
    const make = (id: string, label: string, hint: string, checked: boolean) => {
      const input = el("input", { className: "checkbox", attrs: { type: "checkbox", id, checked, "aria-describedby": `${id}-hint` } });
      return { input, root: el("div", { className: "check-field" }, input, el("div", { className: "check-text" }, el("label", { className: "field-label", text: label, attrs: { for: id } }), el("p", { className: "field-hint", text: hint, attrs: { id: `${id}-hint` } }))) };
    };
    const capture = make("flag-capture", "Turn screen capture off in every app", "Capture stops at the next configuration refresh, whatever each user chose.", settings.disableCapture);
    const detection = make("flag-detection", "Turn automatic detection off in every app", "Detection stops at the next refresh. Users can still choose their role and character by hand.", settings.disableDetection);
    const saveButton = button("Save switches", { variant: "primary", attrs: { disabled: true } });
    const sync = () => (saveButton.disabled = capture.input.checked === settings.disableCapture && detection.input.checked === settings.disableDetection);
    capture.input.addEventListener("change", sync);
    detection.input.addEventListener("change", sync);
    saveButton.addEventListener("click", async () => {
      const patch: SettingsPatch = { disableCapture: capture.input.checked, disableDetection: detection.input.checked };
      const lines: string[] = [];
      if (patch.disableCapture !== settings.disableCapture) lines.push(patch.disableCapture ? "Screen capture is turned off in every app." : "The capture switch is released: each user's own setting applies again (capture stays off for anyone who has not turned it on).");
      if (patch.disableDetection !== settings.disableDetection) lines.push(patch.disableDetection ? "Automatic detection is turned off in every app." : "The detection switch is released: each user's own setting applies again.");
      const ok = await ctx.confirm({ title: "Save the feature switches?", body: [...lines, "Apps apply this at their next configuration refresh."], confirmLabel: "Save switches" });
      if (!ok) return;
      if (await save(patch, saveButton, "The switches", slot)) {
        drawFeatures();
        done("Feature switches saved");
      }
    });
    return card(
      "Feature switches",
      el("p", { className: "muted", text: "These switches can only turn features off. Unticking one does not turn anything on: it hands the choice back to each user, and capture and detection stay off unless a user turned them on." }),
      slot,
      el("div", { className: "checks" }, capture.root, detection.root),
      el("div", { className: "form-actions" }, saveButton),
    );
  }

  function bannerCard(settings: Settings): HTMLElement {
    const slot = el("div", { className: "inline-message" });
    const area = el("textarea", { className: "input textarea", attrs: { rows: 3, maxlength: BANNER_MAX } });
    area.value = settings.banner ?? "";
    const banner = field("banner-text", "Message", area, `Plain text, up to ${BANNER_MAX} characters. Shown at the top of every app. Leave it empty for no banner.`);
    const counter = el("p", { className: "field-hint counter", attrs: { "aria-live": "polite" } });
    const saveButton = button("Save banner", { variant: "primary", attrs: { disabled: true } });
    const clear = button("Remove banner", { attrs: { disabled: settings.banner === null } });
    const update = () => {
      counter.textContent = `${count(area.value.length)} of ${BANNER_MAX} characters`;
      saveButton.disabled = (area.value.trim() === "" ? null : area.value.trim()) === settings.banner;
    };
    area.addEventListener("input", update);
    update();
    const apply = async (value: string | null, trigger: HTMLButtonElement) => {
      banner.error.textContent = "";
      if (value !== null && value.length > BANNER_MAX) {
        banner.error.textContent = `Shorten it to ${BANNER_MAX} characters.`;
        area.focus();
        return;
      }
      if (await save({ banner: value }, trigger, "The banner", slot)) {
        drawFeatures();
        done(value === null ? "Banner removed" : "Banner saved");
      }
    };
    saveButton.addEventListener("click", () => void apply(area.value.trim() === "" ? null : area.value.trim(), saveButton));
    clear.addEventListener("click", async () => {
      const ok = await ctx.confirm({ title: "Remove the banner?", body: ["Apps stop showing it at their next configuration refresh."], confirmLabel: "Remove banner" });
      if (ok) await apply(null, clear);
    });
    return card(
      "Announcement banner",
      slot,
      settings.banner !== null ? el("div", { className: "banner-current" }, el("p", { className: "field-label", text: "Shown now" }), el("p", { className: "banner-preview", text: settings.banner })) : el("p", { className: "muted", text: "No banner is shown." }),
      banner.root,
      counter,
      el("div", { className: "form-actions" }, clear, saveButton),
    );
  }

  function minimumCard(settings: Settings): HTMLElement {
    const slot = el("div", { className: "inline-message" });
    const input = el("input", { className: "input input-narrow mono", attrs: { type: "text", value: settings.minimumVersion, maxlength: 17, inputmode: "decimal", autocomplete: "off", spellcheck: "false" } });
    const minimum = field("minimum-version", "Version", input, "Three numbers, for example 0.4.0. Older apps tell their user to update.");
    const saveButton = button("Save minimum version", { variant: "primary", attrs: { disabled: true } });
    input.addEventListener("input", () => (saveButton.disabled = input.value.trim() === settings.minimumVersion));
    saveButton.addEventListener("click", async () => {
      minimum.error.textContent = "";
      input.removeAttribute("aria-invalid");
      const value = input.value.trim();
      if (!VERSION.test(value)) {
        minimum.error.textContent = "Enter three numbers separated by dots, for example 0.4.0.";
        input.setAttribute("aria-invalid", "true");
        input.focus();
        return;
      }
      const ok = await ctx.confirm({ title: `Require version ${value} or later?`, body: ["Apps older than this tell their user to update at their next configuration refresh."], confirmLabel: "Save minimum version" });
      if (!ok) return;
      if (await save({ minimumVersion: value }, saveButton, "The minimum version", slot)) {
        drawFeatures();
        done(`Minimum version is now ${value}`);
      }
    });
    return card("Minimum supported version", slot, minimum.root, el("div", { className: "form-actions" }, saveButton));
  }

  void load();
}

export const accessView: View = { render };
